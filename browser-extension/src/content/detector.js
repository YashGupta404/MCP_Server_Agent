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
    // Workday / Outlook fall back to their raw type name.
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

  // Workday: object type + id are usually opaque; best effort from the URL fragment.
  function detectWorkday(loc) {
    const m = loc.hash.match(/\/([A-Za-z_]+)\/([A-Za-z0-9$-]+)(?:[/?]|$)/);
    if (m) {
      return { rawType: m[1], businessObjectId: m[2], displayName: document.title.trim() };
    }
    return null;
  }

  // Outlook: use the selected message's internet id where exposed; otherwise skip (no record).
  function detectOutlook(loc) {
    const params = new URLSearchParams(loc.hash.replace(/^#/, ""));
    const itemId = params.get("ItemID") || params.get("itemid");
    if (itemId) {
      return { rawType: "email", businessObjectId: itemId, displayName: document.title.trim() };
    }
    return null;
  }

  function detect() {
    const loc = window.location;
    const host = loc.hostname;
    let raw = null;

    if (/\.force\.com$|\.salesforce\.com$|\.lightning\.force\.com$/.test(host)) raw = detectSalesforce(loc);
    else if (/\.service-now\.com$/.test(host)) raw = detectServiceNow(loc);
    else if (/\.workday\.com$|\.myworkday\.com$/.test(host)) raw = detectWorkday(loc);
    else if (/^outlook\.office(365)?\.com$/.test(host)) raw = detectOutlook(loc);

    if (!raw || !raw.businessObjectId) return null;

    const businessObjectType = normalizeType(raw.rawType);
    if (!businessObjectType) return null;

    return {
      businessObjectType,
      businessObjectId: raw.businessObjectId,
      displayName: raw.displayName || `${businessObjectType} ${raw.businessObjectId}`,
      source: host,
      url: loc.href,
    };
  }

  // ---- state + publishing ------------------------------------------------------------------

  let lastKey = null;

  function contextKey(ctx) {
    return ctx ? `${ctx.businessObjectType}:${ctx.businessObjectId}` : "none";
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
