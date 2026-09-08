// Service worker (Chromium) / background script (Firefox).
// Responsibilities:
//   * Auto sign-out on browser close (session lives in in-memory storage.session).
//   * Cache the business-object context each content script detects, keyed by tab id, so the popup
//     / side panel can instantly show the record the user is currently viewing.

const ext = globalThis.browser ?? globalThis.chrome;

const SESSION_KEY = "uceb_bff_session";

// tabId -> { businessObjectType, businessObjectId, displayName, source, url } | null
const contextByTab = new Map();

ext.runtime.onInstalled.addListener(() => {
  console.info("[background] UCEB Agent Chatbot installed.");
});

// ---- CIC viewer cookie promotion (in-panel iframe support) ----
// The CIC viewer + IAM set their session cookies as SameSite=Lax, which the browser never sends
// inside a cross-site iframe (our side panel). We re-write those cookies to SameSite=None; Secure
// so the embedded viewer's API calls carry the session and render in the panel instead of erroring.
const VIEWER_COOKIE_HOSTS = [
  "cic-viewer.staging.app.hyland.com",
  "cic-viewer.dev.app.hyland.com",
  "bravo.cic-viewer.sandbox.app.hyland.com",
  "auth.iam.staging.experience.hyland.com",
  "auth.iam.dev.experience.hyland.com",
  "auth.staging.app.hyland.com",
  "auth.dev.app.hyland.com",
];

function viewerHostMatches(domain) {
  const d = (domain || "").replace(/^\./, "");
  return VIEWER_COOKIE_HOSTS.some((h) => d === h || d.endsWith("." + h));
}

// Re-set a Lax/Strict cookie as SameSite=None; Secure, preserving its scope. No-op when the cookie
// is already unrestricted (this is what stops our own set from looping through onChanged).
async function promoteViewerCookie(cookie) {
  if (!cookie || cookie.sameSite === "no_restriction") return;
  if (!viewerHostMatches(cookie.domain)) return;

  const host = (cookie.domain || "").replace(/^\./, "");
  const details = {
    url: `https://${host}${cookie.path || "/"}`,
    name: cookie.name,
    value: cookie.value,
    path: cookie.path,
    httpOnly: cookie.httpOnly,
    secure: true,                 // required for SameSite=None
    sameSite: "no_restriction",
    storeId: cookie.storeId,
  };
  if (!cookie.hostOnly) details.domain = cookie.domain;                 // keep domain-scoped cookies
  if (!cookie.session && cookie.expirationDate) details.expirationDate = cookie.expirationDate;

  try {
    await ext.cookies.set(details);
  } catch (err) {
    console.warn("[cookie-promote] failed for", cookie.name, err);
  }
}

ext.cookies?.onChanged?.addListener(({ removed, cookie, cause }) => {
  if (removed || cause === "overwrite") return;   // "overwrite" is the paired delete before a set
  promoteViewerCookie(cookie);
});

// Promote any already-present cookies on startup/install.
(async function sweepViewerCookies() {
  try {
    for (const host of VIEWER_COOKIE_HOSTS) {
      const cookies = await ext.cookies.getAll({ domain: host });
      for (const c of cookies) await promoteViewerCookie(c);
    }
  } catch (err) {
    console.warn("[cookie-promote] sweep failed:", err);
  }
})();

// The toolbar icon only reveals the in-page overlay button (it does NOT open the side panel). The
// side panel opens only when the user clicks the overlay button (UCEB_OPEN_SIDE_PANEL below).
try {
  ext.sidePanel?.setPanelBehavior?.({ openPanelOnActionClick: false });
} catch {
  /* sidePanel API not available (e.g. Firefox) — ignore */
}

// Toolbar icon click -> show/toggle the floating overlay button on the active tab. Inject the
// content script on demand if the tab wasn't refreshed after the extension was (re)loaded.
ext.action?.onClicked?.addListener(async (tab) => {
  if (!tab?.id) return;
  const send = () => ext.tabs.sendMessage(tab.id, { type: "UCEB_TOGGLE_LAUNCHER" });
  try {
    await send();
  } catch {
    try {
      await ext.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["src/content/launcher.js"],
      });
      await send();
    } catch (err) {
      console.warn("[background] launcher injection failed:", err);
    }
  }
});

// ---- context cache ----
ext.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  // The in-page overlay (FAB) asks to open the persistent side panel. This must run synchronously
  // within the message handler to keep the originating user gesture active for sidePanel.open().
  if (msg?.type === "UCEB_OPEN_SIDE_PANEL") {
    const tabId = sender?.tab?.id;
    const windowId = sender?.tab?.windowId;
    try {
      if (typeof tabId === "number") {
        ext.sidePanel?.open?.({ tabId });
      } else if (typeof windowId === "number") {
        ext.sidePanel?.open?.({ windowId });
      }
    } catch (err) {
      console.warn("[background] sidePanel.open failed:", err);
    }
    sendResponse?.({ ok: true });
    return; // sync
  }

  if (msg?.type === "UCEB_CONTEXT") {
    const tabId = sender?.tab?.id;
    if (typeof tabId === "number") {
      contextByTab.set(tabId, msg.context ?? null);
      updateBadge(tabId, msg.context);
    }
    return undefined;
  }

  // The popup asks for the ACTIVE tab's context. Prefer the cache; fall back to asking the tab.
  if (msg?.type === "UCEB_GET_ACTIVE_CONTEXT") {
    getActiveContext().then((context) => sendResponse({ context }));
    return true; // async response
  }

  return undefined;
});

async function getActiveContext() {
  const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return null;

  if (contextByTab.has(tab.id)) return contextByTab.get(tab.id);

  // Not cached yet (content script may have loaded before the worker woke). Ask the tab directly.
  try {
    const res = await ext.tabs.sendMessage(tab.id, { type: "UCEB_GET_CONTEXT" });
    const context = res?.context ?? null;
    contextByTab.set(tab.id, context);
    return context;
  } catch {
    // No content script on this page (unsupported site) — no context.
    return null;
  }
}

function updateBadge(tabId, context) {
  try {
    const text = context ? "●" : "";
    ext.action?.setBadgeText?.({ tabId, text });
    ext.action?.setBadgeBackgroundColor?.({ tabId, color: "#2f7d4f" });
  } catch {
    /* badge is optional */
  }
}

ext.tabs.onRemoved.addListener((tabId) => contextByTab.delete(tabId));

// Auto sign-out on browser close: the session lives in storage.session (in-memory), which the
// browser clears automatically when it shuts down. On the next launch we defensively remove any
// leftover session id so a browser restart always requires a fresh sign-in.
ext.runtime.onStartup.addListener(async () => {
  try {
    await ext.storage.session?.remove?.(SESSION_KEY);
  } catch {
    /* storage.session may be unavailable on some builds — ignore */
  }
  await ext.storage.local.remove(SESSION_KEY);
});
