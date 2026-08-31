// In-page launcher (Jira Rovo-style).
//   * Injects a small floating circular button (FAB) into the page, isolated in a Shadow DOM.
//   * Clicking the FAB opens the extension's full, persistent SIDE PANEL (same panel the toolbar
//     icon opens) — not a small in-page popup.
//
// This coexists with detector.js (context detection); it only adds UI and messaging.

(() => {
  const ext = globalThis.browser ?? globalThis.chrome;

  // Guard against double-injection (SPA re-inject / multiple runs).
  if (window.__ucebLauncherInjected) return;
  window.__ucebLauncherInjected = true;

  const HOST_ID = "uceb-launcher-host";
  let hostEl = null;
  let shadow = null;
  let fabEl = null;
  let launcherVisible = false;

  const logoUrl = (() => {
    try {
      return ext.runtime.getURL("src/assets/hyland-logo.png");
    } catch {
      return "";
    }
  })();

  function build() {
    if (hostEl) return;

    hostEl = document.createElement("div");
    hostEl.id = HOST_ID;
    // Keep the host itself out of the page's layout/flow.
    hostEl.style.all = "initial";
    shadow = hostEl.attachShadow({ mode: "open" });

    const style = document.createElement("style");
    style.textContent = `
      :host { all: initial; }
      .wrap {
        position: fixed;
        right: 24px;
        bottom: 24px;
        z-index: 2147483647;
        font-family: "Segoe UI", system-ui, -apple-system, Roboto, sans-serif;
      }
      .fab {
        position: fixed;
        right: 24px;
        bottom: 24px;
        width: 56px;
        height: 56px;
        border-radius: 50%;
        border: none;
        cursor: pointer;
        background: #1f5fd6;
        box-shadow: 0 6px 18px rgba(0, 0, 0, 0.28), 0 2px 6px rgba(0, 0, 0, 0.22);
        display: none;                 /* hidden until the toolbar icon toggles it on */
        align-items: center;
        justify-content: center;
        padding: 0;
        transition: transform 0.15s ease, box-shadow 0.15s ease;
        z-index: 2147483647;
      }
      .fab:hover { transform: translateY(-2px) scale(1.04); box-shadow: 0 10px 24px rgba(0,0,0,0.32); }
      .fab:active { transform: scale(0.96); }
      .fab.is-visible { display: flex; }
      .fab img { width: 30px; height: 30px; object-fit: contain; display: block; border-radius: 6px; background: #fff; padding: 2px; }
    `;

    fabEl = document.createElement("button");
    fabEl.className = "fab";
    fabEl.type = "button";
    fabEl.title = "Hyland Integrations Agent";
    fabEl.setAttribute("aria-label", "Open Hyland Integrations Agent");
    fabEl.innerHTML = `${logoUrl ? `<img src="${logoUrl}" alt="Hyland" />` : ""}`;
    fabEl.addEventListener("click", openSidePanel);

    shadow.append(style, fabEl);
    (document.body || document.documentElement).appendChild(hostEl);
  }

  // Ask the background service worker to open the extension's persistent side panel. Sending this
  // from the FAB's click handler keeps the user-gesture active so sidePanel.open() is allowed.
  function openSidePanel() {
    try {
      ext.runtime.sendMessage({ type: "UCEB_OPEN_SIDE_PANEL" });
    } catch {
      /* background not reachable */
    }
  }

  function showLauncher() {
    build();
    launcherVisible = true;
    fabEl.classList.add("is-visible");
  }

  function hideLauncher() {
    launcherVisible = false;
    if (fabEl) fabEl.classList.remove("is-visible");
  }

  function toggleLauncher() {
    if (launcherVisible) hideLauncher();
    else showLauncher();
  }

  // Toolbar icon click (from background) shows/toggles the floating button on/off.
  ext.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type === "UCEB_TOGGLE_LAUNCHER") {
      toggleLauncher();
      sendResponse?.({ ok: true, visible: launcherVisible });
      return; // sync response
    }
    return undefined;
  });
})();
