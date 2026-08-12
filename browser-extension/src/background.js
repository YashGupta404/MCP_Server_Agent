// Service worker (Chromium) / background script (Firefox). Kept minimal: the popup drives the
// chat directly. This exists so the manifest has a background context and for future use
// (e.g. token refresh alarms, context-menu entry).

const ext = globalThis.browser ?? globalThis.chrome;

const SESSION_KEY = "uceb_bff_session";

ext.runtime.onInstalled.addListener(() => {
  console.info("[background] UCEB Agent Chatbot installed.");
});

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
