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
