# Day 21 — 2026-09-08

_Focus: made **CIC-native documents render in the CIC viewer inside the side-panel iframe**. Built the
standalone CIC viewer URL, stripped the framing headers, and then chased the real blocker — the viewer's
session cookie is **`SameSite=Lax`**, which browsers never send inside a cross-site iframe. Solved it by
**promoting those cookies to `SameSite=None; Secure`** from the extension's background service worker._

---

## 0. Goal

Open documents in the **CIC viewer** (`cic-viewer.<env>.app.hyland.com`) **inside the Chrome side panel**
(iframe), not a separate tab — for CIC-native docs. (OnBase/CFS docs stay blocked by the day-20 domain
issue — `onbase_hcm_stg` IDS is bound to `wdxorbis`, our login is `aurahyland` → 401.)

Clarified misconceptions from a parallel session:
- **CIC is the ECM**, used by BOTH Salesforce and Workday LOBs — there's no separate "CIC user".
- **Use the CIC viewer, not QuickAccessViewer** (QAV auth is broken by design — SAML redirect to a
  Workday-customer Azure AD tenant, day-19).
- The viewer authenticates **independently** of the MCP: MCP=arizzo (API), plugin=dev user (chatbot),
  **viewer=staging user** (renders). No conflict — the viewer renders based on its own signed-in user.

---

## 1. CIC viewer URL + in-panel wiring

CIC-native URL form: `{host}/#/documents/{documentId}?envKey={envKey}`
(CFS/OnBase form is different: `{host}/#/cfs/{integrationId}/{contentId}/1?envKey={envKey}`.)

Changes (all in `browser-extension/`):
- **`src/config.js`** — new `cicViewer: { host, envKey }` (default staging:
  `cic-viewer.staging.app.hyland.com` / `appintel-staging-prod`).
- **`src/popup.js`** — `buildCicViewerUrl(docId)`; for `isCicSystem()` docs, build that URL and load it in
  the existing panel iframe (`openViewer`) instead of the Studio `open_document_in_viewer` URL.
- **`src/popup.html`** — added `storage-access` to the viewer iframe `allow`.

---

## 2. Framing headers — the CIC viewer refused to embed

The viewer SPA ships `X-Frame-Options` / `Content-Security-Policy: frame-ancestors`, so a plain iframe is
blocked. Fixed with **declarativeNetRequest** (same technique as Dawid's PoC):
- **`manifest.json`** — added `declarativeNetRequest` permission, the viewer/IAM `host_permissions`, and a
  static ruleset.
- **`src/rules.json`** (new) — strips `x-frame-options` + `content-security-policy` on `sub_frame` for the
  cic-viewer + IAM auth hosts (staging + dev).

After this the login **and** the document rendered in the panel — but only for **~1 second**, then
**"Error during processing document for the viewer."**

---

## 3. The real blocker — `SameSite=Lax` session cookie

The doc rendered in a **full tab** perfectly, but died ~1s after the first render in the **iframe**.
DevTools → Application → Cookies confirmed the viewer's session cookie:

```
name: app.session   HttpOnly: ✓   SameSite: Lax
```

**Why it fails:** in a tab the viewer is *first-party* → the `Lax` cookie is sent → every API call works.
In the panel iframe (embedded under `chrome-extension://`) the viewer is *third-party* → the browser
**never sends a `SameSite=Lax` cookie** → the first authenticated call after the initial render goes out
unauthenticated → the SPA throws. This is a hard browser rule — **neither DNR nor the Storage Access API
can override `SameSite=Lax`** (a `storage-access` content script was tried first and did nothing).

### Why Dawid's PoC loads in-panel but ours didn't
Cloned his PoC (`%TEMP%\cic-viewer-poc`). It's **the same technique** (identical DNR strip,
`allow="storage-access"`, no magic) — the only difference is it targets **dev/sandbox**
(`bravo.cic-viewer.sandbox.app.hyland.com` + `auth.iam.dev.experience.hyland.com` + `appintel-dev-test`),
whose viewer cookie **isn't `Lax`**. So it was the **environment**, not the code.

---

## 4. Fix — promote the cookie to `SameSite=None; Secure`

The extension can read even HttpOnly cookies via the `chrome.cookies` API, so we **re-write** the
viewer/IAM cookies from `Lax` → `None; Secure`, which the browser *will* send in the iframe.

- **`manifest.json`** — added `cookies` permission (+ dev/sandbox hosts to `host_permissions`).
- **`src/background.js`** — `promoteViewerCookie()`:
  - Runs on a startup **sweep** (existing cookies) and on `chrome.cookies.onChanged` (re-promotes whenever
    the server resets a `Lax` cookie — this is what makes it stick across the session).
  - Re-sets each matching cookie with `sameSite: "no_restriction"`, `secure: true`, preserving name/value/
    path/httpOnly/domain-scope/expiry.
  - No-ops when already `no_restriction` → that's the guard that stops our own `set` from looping through
    `onChanged`.
  - Hosts: `cic-viewer.{staging,dev}`, `bravo.cic-viewer.sandbox`, `auth.iam.{staging,dev}`,
    `auth.{staging,dev}`.

**Result:** after reloading the extension, `app.session` flips to `SameSite=None`, and the CIC document
**renders and stays** inside the side panel. ✅

> The proper long-term fix is the viewer team setting `SameSite=None; Secure` server-side; the cookie
> promotion is a client-side workaround so we can demo in-panel today.

---

## 5. Files changed (this day, `MCP_Server_Agent` / `browser-extension`)

- `src/config.js` — `cicViewer` host/envKey.
- `src/popup.js` — `buildCicViewerUrl`, CIC docs → CIC viewer URL in the panel iframe.
- `src/popup.html` — `storage-access` on the iframe.
- `src/rules.json` (new) — strip `X-Frame-Options` + CSP on the viewer/auth hosts.
- `src/content/viewer-storage.js` (new) — Storage Access attempt (kept as a first-pass; the cookie
  promotion in `background.js` is what actually fixes it).
- `src/background.js` — cookie promotion (`Lax` → `None; Secure`).
- `manifest.json` — `declarativeNetRequest` + `cookies` permissions, viewer/IAM host_permissions, DNR
  ruleset, viewer-storage content script.

---

## 6. Status

- ✅ **CIC-native docs render in the CIC viewer inside the side panel** (staging, `arizzo` docs, viewer
  signed in as the staging user).
- ⛔ **OnBase/CFS docs** — still blocked by the day-20 IDS domain mismatch (`wdxorbis` vs `aurahyland`);
  the CFS viewer URL is understood and ready, pending the OnBase-→-aurahyland reconfiguration.

---

## 7. Gotchas / notes for next time

- **`SameSite=Lax` = no cross-site iframe.** DNR and the Storage Access API can't override it; the
  `chrome.cookies` promotion (Lax→None;Secure) does.
- **In-panel viewer is env-dependent** — it just works where the viewer cookie is already `None` (dev), and
  needs the promotion where it's `Lax` (staging).
- **Servers must be running for the plugin:** BFF `:5010` (Production) + MCP `:5200` (Development, `--http`,
  opens arizzo login). Both were down this session → "Failed to fetch / ERR_CONNECTION_REFUSED" on sign-in.
- Each MCP restart resets the active system to the appsettings default `cic` (which is what you want for
  testing the CIC viewer).
