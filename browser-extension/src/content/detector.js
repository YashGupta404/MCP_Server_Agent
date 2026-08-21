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

  // ServiceNow: .../<table>.do?sys_id=<guid>  or  /now/nav/ui/.../<table>/<sys_id>
  function detectServiceNow(loc) {
    const params = new URLSearchParams(loc.search);
    const sysId = params.get("sys_id");
    const doMatch = loc.pathname.match(/\/([a-z_]+)\.do$/i);
    if (sysId && doMatch) {
      return { rawType: doMatch[1], businessObjectId: sysId, displayName: document.title.trim() };
    }
    const navMatch = loc.href.match(/\/([a-z_]+)\/([0-9a-f]{32})(?:[/?#]|$)/i);
    if (navMatch) {
      return { rawType: navMatch[1], businessObjectId: navMatch[2], displayName: document.title.trim() };
    }
    return null;
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

  // Reads the worker's identity off a Workday worker page. Prefers the Employee ID (shown on the Job
  // Details tab, e.g. "Employee ID 21021") because it resolves to exactly one worker; otherwise falls
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
    name = name.replace(/\s*\([^)]*\)\s*/g, " ").replace(/\s+/g, " ").trim();

    // Prefer the precise Employee ID; fall back to the name.
    const query = employeeId || name;
    if (!query) return null;
    return { rawType: "employee", needsResolve: true, resolveQuery: query, displayName: name || `Employee ${query}` };
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
    if (ctx.needsResolve) return `resolve:${ctx.businessObjectType}:${ctx.resolveQuery}`;
    return `${ctx.businessObjectType}:${ctx.businessObjectId}`;
  }

  function publish(force = false) {
    const ctx = detect();
    const key = contextKey(ctx);
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

  const wrap = (fnName) => {
    const original = history[fnName];
    history[fnName] = function (...args) {
      const result = original.apply(this, args);
      schedulePublish();
      return result;
    };
  };
  wrap("pushState");
  wrap("replaceState");
  window.addEventListener("popstate", schedulePublish);
  window.addEventListener("hashchange", schedulePublish);

  // Fallback: some frameworks swap record content without touching the URL.
  const observer = new MutationObserver(() => schedulePublish());
  observer.observe(document.documentElement, { childList: true, subtree: true });

  // Initial publish once the page settles.
  publish(true);
})();
