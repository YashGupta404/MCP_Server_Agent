// Context detector (content script)
// ----------------------------------
// Runs on supported line-of-business pages (Salesforce, ServiceNow, Workday, Outlook) and figures
// out WHICH business object the user is currently looking at — its type + id — from the page URL
// (and DOM as a fallback). It sends that context to the background service worker, which caches it
// per tab so the popup / side panel can show the record's related UCEB documents.
//
// This is a plain (non-module) content script: everything lives in one file and shares one scope.

(() => {
  const ext = globalThis.browser ?? globalThis.chrome;

  // ---- per-site adapters -------------------------------------------------------------------
  // Each adapter returns { businessObjectType, businessObjectId, displayName } or null.
  // `businessObjectType` is normalized to the UCEB busObject value via SITE_TYPE_MAP below.

  // Map a raw on-page object name to the UCEB businessObjectType. Extend as new mappings are
  // configured in UCEB (see add_business_object_config / list_business_object_types).
  const SITE_TYPE_MAP = {
    // Salesforce sObject API names -> UCEB busObject
    account: "account",
    contact: "contact",
    opportunity: "opportunity",
    case: "case",
    lead: "lead",
    campaign: "campaign",
    // ServiceNow tables
    incident: "incident",
    change_request: "change_request",
    sc_req_item: "sc_req_item",
    // Workday worker/detail pages -> "employee".
    employee: "employee",
    worker: "employee",
    // Workday / Outlook otherwise fall back to their raw type name.
  };

  function normalizeType(rawType) {
    if (!rawType) return null;
    const key = String(rawType).trim().toLowerCase();
    return SITE_TYPE_MAP[key] ?? key;
  }

  // Salesforce Lightning: .../lightning/r/<Object>/<RecordId>/view
  // Classic: .../<RecordId> (15/18-char id) — best effort.
  function detectSalesforce(loc) {
    const m = loc.pathname.match(/\/lightning\/r\/([^/]+)\/([a-zA-Z0-9]{15,18})(?:\/|$)/);
    if (m) {
      return {
        rawType: m[1],
        businessObjectId: m[2],
        displayName: document.title.replace(/\s*\|\s*Salesforce.*$/i, "").trim(),
      };
    }
    // Lightning object without explicit type in path but with recordId in the hash/params.
    const idParam = new URLSearchParams(loc.search).get("recordId");
    if (idParam && /^[a-zA-Z0-9]{15,18}$/.test(idParam)) {
      return { rawType: null, businessObjectId: idParam, displayName: document.title.trim() };
    }
    return null;
  }

  // ServiceNow: scope by the human record NUMBER (e.g. CHG0040019), because the solution config maps
  // boContext.boProp = "number" (not sys_id). The classic form exposes it as a hidden input
  // `sys_original.<table>.number` whose id also encodes the table. It can render in a nested same-origin
  // iframe (the Next Experience "classic" shell), so search this document and its child frames.
  function findServiceNowNumberInput(doc) {
    try {
      const el = doc.querySelector('input[id^="sys_original."][id$=".number"]');
      if (el && el.value) return el;
    } catch {
      return null; // cross-origin frame — skip
    }
    const frames = doc.querySelectorAll ? doc.querySelectorAll("iframe") : [];
    for (const f of frames) {
      let childDoc = null;
      try {
        childDoc = f.contentDocument;
      } catch {
        childDoc = null;
      }
      if (childDoc) {
        const found = findServiceNowNumberInput(childDoc);
        if (found) return found;
      }
    }
    return null;
  }

  function detectServiceNow() {
    // ServiceNow Next Experience renders the classic record form in an <iframe> that is nested INSIDE an
    // open shadow root (web components). Neither document.querySelectorAll("iframe") nor window.frames can
    // cross a shadow boundary, so we walk the tree explicitly: at each document/shadow-root, look for the
    // hidden input, then descend into (a) every element's shadowRoot and (b) every same-origin iframe's
    // contentDocument. This reaches the `sys_original.<table>.number` input wherever ServiceNow buries it.
    let shadows = 0, iframes = 0;
    function search(root) {
      let el = null;
      try { el = root.querySelector('input[id^="sys_original."][id$=".number"]'); } catch { /* ignore */ }
      if (el && el.value) return el;
      let all;
      try { all = root.querySelectorAll("*"); } catch { return null; }
      for (const node of all) {
        if (node.tagName === "IFRAME") {
          let cd = null;
          try { cd = node.contentDocument; } catch { cd = null; }  // cross-origin -> null
          if (cd) { iframes++; const r = search(cd); if (r) return r; }
        }
        if (node.shadowRoot) { shadows++; const r = search(node.shadowRoot); if (r) return r; }
      }
      return null;
    }
    const input = search(document);
    try { console.log("[UCEB-SN]", input ? ("FOUND " + input.id + "=" + input.value) : "NOTFOUND", `shadows=${shadows} iframes=${iframes}`); } catch { /* ignore */ }
    if (!input) return null;
    const m = input.id.match(/^sys_original\.([a-z_]+)\.number$/i);
    if (!m) return null;
    return { rawType: m[1], businessObjectId: input.value.trim(), displayName: document.title.trim() };
  }

  // Workday: the Hyland "Employee Documents" experience embeds the worker's UCEB WID (a 32-hex id)
  // directly in the URL, e.g.
  //   .../d/wday/app/<app>/<app>/employeeDocuments/4bc212416f234ba1b4749e4bebe4c2eb.htmld
  // That WID is the id UCEB actually keys a worker's documents on (both list and capture), so we
  // extract it and use it verbatim. (The older "/inst/1$37/247$21" instance ref in some Workday
  // URLs is NOT the WID and does not resolve to a real worker, so it's only a last-resort fallback.)
  function detectWorkday(loc) {
    const href = loc.href;

    // 1) Explicit employeeDocuments/<wid> path — the reliable, proven key.
    const docMatch = href.match(/\/employeeDocuments\/([0-9a-fA-F]{32})(?:\.html?d?)?/i);
    if (docMatch) {
      return { rawType: "employee", businessObjectId: docMatch[1].toLowerCase(), displayName: document.title.trim() };
    }

    // 2) Any bare 32-hex WID segment elsewhere in the path/hash (other worker document views).
    const widMatch = (loc.pathname + loc.hash).match(/(?:^|[/$])([0-9a-fA-F]{32})(?:\.html?d?)?(?:[/?#]|$)/);
    if (widMatch) {
      return { rawType: "employee", businessObjectId: widMatch[1].toLowerCase(), displayName: document.title.trim() };
    }

    // 3) Normal Workday worker page (the end user's own Workday account): the URL carries NO WID —
    //    only an instance ref like "1$37/247$21" which is NOT the WID and resolves to no real worker.
    //    So we scrape the worker's Employee ID (shown on the Job Details tab) or name straight from the
    //    page and let the panel resolve the real 32-hex WID via the BFF — the user types nothing.
    return detectWorkerProfile();
  }

  const EMP_ID_PROBE_BUDGET = 1;   // one auto-scroll pass per page view (resets on nav + explicit reprobe)
  let navSeq = 0;                  // bumped on every SPA navigation (incl. back/forward)
  let empIdProbeBudget = EMP_ID_PROBE_BUDGET;
  let lastEmpIdProbeAt = 0;
  let probing = false;             // in-flight guard: never start a second scroll while one is running
  const hasEmployeeIdInDom = () =>
    /\bEmployee ID\b\s*[:#\-]?\s*[0-9]{3,}/i.test((document.body && document.body.innerText) || "");
  const hasManagerInDom = () => !!scrapeManagerName();
  // The profile is fully hydrated for an authorisation decision once BOTH the Employee ID (Job Details)
  // and the Manager field are in the DOM — they lazy-render at different scroll depths.
  const profileHydrated = () => hasEmployeeIdInDom() && hasManagerInDom();

  // Workday lazy-renders the "Job Details" (Employee ID) and the "Manager" field only once each is near
  // the viewport. This probe walks the WHOLE page DOWN in real steps so both sections render, then
  // returns to the original position. We must hydrate both before deciding access: the Employee ID
  // identifies the worker and the Manager is the authorisation signal (report-of-signed-in-user).
  async function probeForEmployeeId() {
    if (probing || profileHydrated()) return;
    probing = true;

    // Force content-visibility / contain-based lazy sections to render.
    const style = document.createElement("style");
    style.setAttribute("data-uceb-probe", "1");
    style.textContent = "*{content-visibility:visible !important;contain-intrinsic-size:auto !important;}";
    (document.head || document.documentElement).appendChild(style);

    // Pick the element that actually scrolls the page: the window, or the tallest inner scroll container.
    let scroller = document.scrollingElement || document.documentElement;
    try {
      let bestH = scroller.scrollHeight - scroller.clientHeight;
      document.querySelectorAll("*").forEach((el) => {
        const s = getComputedStyle(el);
        const h = el.scrollHeight - el.clientHeight;
        if ((s.overflowY === "auto" || s.overflowY === "scroll") && h > bestH) { scroller = el; bestH = h; }
      });
    } catch {
      /* getComputedStyle can throw on detached nodes; ignore */
    }

    const saved = scroller.scrollTop;
    try {
      const step = Math.max(400, (scroller.clientHeight || 600) * 0.9);
      for (let y = step; y < scroller.scrollHeight + step; y += step) {
        scroller.scrollTop = y;                                   // real scroll -> IntersectionObserver fires
        scroller.dispatchEvent(new Event("scroll", { bubbles: true }));
        window.dispatchEvent(new Event("scroll"));
        await new Promise((r) => setTimeout(r, 250));             // let the lazy section render
        if (profileHydrated()) break;                            // stop once BOTH id + manager are in
        if (y >= scroller.scrollHeight - scroller.clientHeight) break;
      }
    } finally {
      scroller.scrollTop = saved;                                // return to where the user was
      style.remove();
      probing = false;
    }
    publish(true); // re-detect: id + manager may now be present -> resolve + authorise -> docs load
  }


  // Reads the worker's identity off a Workday worker page. Prefers the Employee ID (shown on the Job
  // Details tab, e.g. "Employee ID 21021") because it resolves to exactly one worker; otherwise falls
  // The target worker's Manager (from the profile's "Manager" field). This is the real authorisation
  // signal: the signed-in user may view a worker only if they are that worker's manager (their report).
  // Workday shows Employee IDs for superiors too, so Employee-ID visibility alone is NOT authorisation.
  function scrapeManagerName() {
    const bodyText = (document.body && document.body.innerText) || "";
    // "Manager" label, then the manager's full name on the same or next line. \bManager\b won't match
    // "Management Level". Capture up to 4 capitalised name tokens (allow a trailing digit, e.g. "Rizzo1").
    const m = bodyText.match(/\bManager\b\s*[:#\-]?\s*\n?\s*([A-Z][A-Za-z.'\u2019-]+(?:\s+[A-Z][A-Za-z0-9.'\u2019-]+){1,3})/);
    return m ? m[1].replace(/\s+/g, " ").trim() : "";
  }

  // back to the worker's name from the profile/sidebar header. Returns a `needsResolve` marker (no
  // businessObjectId yet) — the popup calls the BFF /api/worker/resolve to turn it into the WID.
  function detectWorkerProfile() {
    // 1) Employee ID from the Job Details page — the reliable, unambiguous signal.
    const bodyText = (document.body && document.body.innerText) || "";
    const idMatch = bodyText.match(/\bEmployee ID\b\s*[:#\-]?\s*([0-9]{3,})/i);
    const employeeId = idMatch ? idMatch[1] : "";

    // 2) Worker name from the profile/sidebar header (the data-automation-id varies across Workday
    //    builds, so try the common ones in order).
    let name = "";
    const nameSelectors = [
      '[data-automation-id="pageHeaderTitleText"]',
      '[data-automation-id="pageHeaderTitle"]',
      '[data-automation-id="workerProfileName"]',
      '[data-automation-id="navigationLandmarkTitle"]',
    ];
    for (const sel of nameSelectors) {
      const el = document.querySelector(sel);
      const t = el && el.textContent ? el.textContent.trim() : "";
      if (t) { name = t; break; }
    }
    // 2b) Fallback: Workday sets the tab title to "<Name> - View Worker" (WCP preview and some builds
    //     expose different automation-ids, so the title is the reliable signal). Take the leading
    //     segment before the " - " / " | " separator; the org-chart/team guard below filters bad names.
    if (!name) {
      const m = (document.title || "").trim().match(/^(.+?)\s+[-\u2013|]\s+\S/);
      if (m) name = m[1].trim();
    }
    name = name.replace(/\s*\([^)]*\)\s*/g, " ").replace(/\s+/g, " ").trim();

    // Guard: some Workday pages that are NOT a single worker profile (Org Chart / People View, team
    // views, directory or task pages) still expose a header title. Never treat those as a worker — no
    // record, and definitely no "not authorised" for a made-up name like "Org Chart".
    const path = location.pathname + location.hash;
    if (/\/rel-task\//i.test(path) || /(org chart|people view|view team|team member|directory)/i.test(name)) {
      return null;
    }

    // Authorisation needs BOTH the Employee ID (identifies the worker) and the Manager (the access
    // signal). Both lazy-render below the fold, so on first load scroll the whole profile to hydrate
    // them and hold a "probing" state until the scroll finishes — never decide on a half-rendered page.
    const managerName = scrapeManagerName();
    if (name && !profileHydrated() && empIdProbeBudget > 0 && Date.now() - lastEmpIdProbeAt > 1000 && !probing) {
      empIdProbeBudget--;
      lastEmpIdProbeAt = Date.now();
      probeForEmployeeId();
    }
    // While the auto-scroll is in flight, tell the panel to hold a loading state regardless of what's
    // rendered so far (the Employee ID may appear before the Manager does).
    if (probing && name) {
      return { rawType: "employee", notAuthorized: true, probing: true, displayName: name || `Employee ${employeeId}`, managerName };
    }
    if (employeeId) {
      return { rawType: "employee", needsResolve: true, resolveQuery: employeeId, displayName: name || `Employee ${employeeId}`, managerName };
    }
    if (name) {
      return { rawType: "employee", notAuthorized: true, displayName: name, managerName };
    }
    return null;
  }

  // Outlook on the web: modern OWA puts the message id in the path (.../mail/.../id/<id>);
  // older OWA used an ItemID hash/query param. Try both.
  function detectOutlook(loc) {
    const pathMatch = loc.pathname.match(/\/id\/([^/?#]+)/i);
    if (pathMatch) {
      return { rawType: "email", businessObjectId: decodeURIComponent(pathMatch[1]), displayName: document.title.trim() };
    }
    const params = new URLSearchParams(loc.hash.replace(/^#/, "") || loc.search);
    const itemId = params.get("ItemID") || params.get("itemid");
    if (itemId) {
      return { rawType: "email", businessObjectId: itemId, displayName: document.title.trim() };
    }
    return null;
  }

  // Some LOB apps expose very long record ids (Outlook message ids can be 150+ chars). HFS/CIC stores
  // each record's documents under a folder named after the businessObjectId, which has a length limit,
  // so an over-long id is rejected and the upload/list fails. We deterministically shorten over-long
  // ids to a short, STABLE hash (same input -> same output across reloads/machines) so the same email
  // always maps to the same record and previously-uploaded docs are still found.
  const MAX_ID_LEN = 64;
  function hashId(str) {
    function fnv(s, seed) {
      let h = seed >>> 0;
      for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = (h * 0x01000193) >>> 0; }
      return h >>> 0;
    }
    const a = fnv(str, 0x811c9dc5).toString(36);
    const b = fnv(str.split("").reverse().join(""), 0x811c9dc5).toString(36);
    return a + b;
  }

  function detect() {
    const loc = window.location;
    const host = loc.hostname;
    let raw = null;

    if (/\.force\.com$|\.salesforce\.com$|\.lightning\.force\.com$/.test(host)) raw = detectSalesforce(loc);
    else if (/\.service-now\.com$/.test(host)) raw = detectServiceNow(loc);
    else if (/\.workday\.com$|\.myworkday\.com$/.test(host)) raw = detectWorkday(loc);
    else if (/^outlook\.(office(365)?|live)\.com$|^outlook\.cloud\.microsoft$/.test(host)) raw = detectOutlook(loc);

    if (!raw) return null;

    const businessObjectType = normalizeType(raw.rawType);
    if (!businessObjectType) return null;

    // Workday worker profiles resolve to their WID via the BFF, so they have no id yet — pass the
    // scraped name/Employee ID through as a `needsResolve` context for the popup to resolve.
    if (raw.needsResolve && raw.resolveQuery) {
      return {
        businessObjectType,
        needsResolve: true,
        resolveQuery: raw.resolveQuery,
        displayName: raw.displayName || raw.resolveQuery,
        managerName: raw.managerName || "",
        source: host,
        url: loc.href,
      };
    }

    // Workday worker with no visible Employee ID (a peer or your own manager) -> not authorised to view
    // or capture their documents. Carry the scraped NAME so the panel can show a clear message.
    if (raw.notAuthorized) {
      return {
        businessObjectType,
        notAuthorized: true,
        // While the off-screen auto-scroll is still running, a lazy Employee ID may yet appear, so the
        // panel must hold a loading state (not the 403) until this clears.
        probing: probing || undefined,
        displayName: raw.displayName || "this employee",
        managerName: raw.managerName || "",
        source: host,
        url: loc.href,
      };
    }

    if (!raw.businessObjectId) return null;

    // Guard against ids too long for the HFS folder-name limit (e.g. Outlook message ids).
    let businessObjectId = raw.businessObjectId;
    if (businessObjectId.length > MAX_ID_LEN) {
      businessObjectId = `${businessObjectType}-${hashId(raw.businessObjectId)}`;
    }

    return {
      businessObjectType,
      businessObjectId,
      displayName: raw.displayName || `${businessObjectType} ${businessObjectId}`,
      source: host,
      url: loc.href,
    };
  }

  // ---- state + publishing ------------------------------------------------------------------

  let lastKey = null;

  function contextKey(ctx) {
    if (!ctx) return "none";
    if (ctx.notAuthorized) return `noauth:${ctx.businessObjectType}:${ctx.displayName}`;
    if (ctx.needsResolve) return `resolve:${ctx.businessObjectType}:${ctx.resolveQuery}`;
    return `${ctx.businessObjectType}:${ctx.businessObjectId}`;
  }

  function publish(force = false) {
    const ctx = detect();
    const key = contextKey(ctx);
    try { console.log("[UCEB-DETECT]", location.hostname, "=>", key, JSON.stringify(ctx)); } catch { /* ignore */ }
    if (!force && key === lastKey) return;
    lastKey = key;
    try {
      ext.runtime.sendMessage({ type: "UCEB_CONTEXT", context: ctx });
    } catch {
      /* the service worker may be asleep; the popup can also pull on demand */
    }
  }

  // Answer on-demand requests from the popup / side panel.
  ext.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type === "UCEB_GET_CONTEXT") {
      sendResponse({ context: detect() });
      return true;
    }
    // Panel Refresh / navigation: reset the probe budget and kick an immediate auto-scroll so a
    // lazy-rendered Employee ID gets hydrated again (no-op if it's already in the DOM).
    if (msg?.type === "UCEB_REPROBE") {
      // Don't re-arm or kick a second scroll while one is already in-flight — the running pass will
      // hydrate the Employee ID (or confirm it's absent) on its own. Only re-arm once it's idle.
      if (!probing) {
        empIdProbeBudget = EMP_ID_PROBE_BUDGET;
        lastEmpIdProbeAt = Date.now();
        probeForEmployeeId();
      }
      sendResponse({ ok: true });
      return true;
    }
    return undefined;
  });

  // ---- SPA navigation detection ------------------------------------------------------------
  // These apps are single-page: the record changes without a full page load. Re-detect whenever
  // the URL changes (history API, back/forward, hash) and, as a fallback, on large DOM changes.

  let debounceTimer = null;
  function schedulePublish() {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => publish(false), 350);
  }

  // A real SPA navigation (incl. browser Back/Forward) bumps navSeq so the Employee-ID probe re-runs
  // for the new page view; MutationObserver churn does NOT bump it (no repeated auto-scrolling).
  const onNavigate = () => { navSeq++; empIdProbeBudget = EMP_ID_PROBE_BUDGET; schedulePublish(); startServiceNowWatch(lastKey); };
  const wrap = (fnName) => {
    const original = history[fnName];
    history[fnName] = function (...args) {
      const result = original.apply(this, args);
      onNavigate();
      return result;
    };
  };
  wrap("pushState");
  wrap("replaceState");
  window.addEventListener("popstate", onNavigate);
  window.addEventListener("hashchange", onNavigate);

  // The history wrappers above run in the content script's isolated world, so the PAGE's own
  // pushState/replaceState calls never reach them. Watch the URL itself to catch SPA navigation.
  let lastHref = location.href;
  setInterval(() => {
    if (location.href !== lastHref) {
      lastHref = location.href;
      onNavigate();
    }
  }, 700);

  // Fallback: some frameworks swap record content without touching the URL.
  const observer = new MutationObserver(() => schedulePublish());
  observer.observe(document.documentElement, { childList: true, subtree: true });

  // ServiceNow renders the record form INSIDE a child iframe that loads AFTER this script; its internal
  // mutations are invisible to the top-document observer, so neither the initial publish nor the observer
  // sees the `sys_original.<table>.number` input. Re-detect when any child iframe finishes loading, plus a
  // short polling fallback, until a record is found. Re-armed on SPA navigation.
  let snPoll = null;
  function watchChildFrames() {
    for (const f of document.querySelectorAll("iframe")) {
      if (f.__ucebWatched) continue;
      f.__ucebWatched = true;
      try { f.addEventListener("load", () => schedulePublish()); } catch { /* ignore */ }
    }
  }
  // After a navigation the nested form can still show the OLD record for a moment, so keep polling until
  // the detected record differs from the one before the navigation (or the budget runs out).
  function startServiceNowWatch(prevKey = null) {
    if (!/\.service-now\.com$/.test(location.hostname)) return;
    if (snPoll) clearInterval(snPoll);
    let tries = 0;
    snPoll = setInterval(() => {
      tries++;
      watchChildFrames();
      publish(false);
      const changed = lastKey && lastKey !== "none" && lastKey !== prevKey;
      if (changed || tries >= 60) { clearInterval(snPoll); snPoll = null; }
    }, 600);
  }

  // Initial publish once the page settles, then watch for the late-loading ServiceNow form.
  publish(true);
  startServiceNowWatch();
})();
