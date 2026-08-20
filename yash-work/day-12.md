# Day 12

_Focus: recovered the whole local stack (dev tunnel was down → chatbot 504 on every prompt),
then chased the **document viewer** problem for Workday. Tried the manager's new CIC viewer
(hit a 401 in the extension iframe), reverted it, and finally made the viewer **LOB-aware** so
Salesforce keeps its HxViewer URL and Workday uses its own HxViewer URL. Also documented exactly
how the **upload/capture** path is wired._

---

## 1. Chatbot 504 "did not respond within 180s" — root cause: dev tunnel down

**Symptom:** every prompt in the extension chat (even "get health status") hung for 180s, then the
BFF returned **504**.

**Root cause:** it was **not** a code bug. The **dev tunnel was down**.

- The chain is: extension → BFF (`:5010`) → cloud Agent Builder (`api.agents.ai.dev.app.hyland.com`)
  → **dev tunnel** (`https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`) → local MCP (`:5200`) → UCEB API (`:5000`).
- If the tunnel isn't hosting, the cloud agent can't reach the local MCP, so **any** tool call hangs.
  The BFF aborts at its 180s timeout → 504.
- Diagnosis: `devtunnel show giant-ant-2f6br43` → "Login token expired" and no `devtunnel.exe` process.

**Fix (runbook Step 3):**

```
devtunnel user login -g -d          # GitHub device code, approve at github.com/login/device
devtunnel host giant-ant-2f6br43    # host the PERSISTENT NAMED tunnel (keep running)
```

- Fixed tunnel URL (registered in Studio, **do not change**): `https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`, header `X-API-Key`.
- Verified with a POST MCP `initialize` → HTTP 200 SSE. Chatbot then returned real health + build info.

> Never run bare `devtunnel host -p 5200` (creates a new random tunnel) or delete `giant-ant-2f6br43`.

---

## 2. VS Code window reload killed the whole stack

Reloading the VS Code window **kills all integrated terminals** → UCEB API `:5000`, MCP `:5200`,
BFF `:5010`, and the dev tunnel all die. After any reload you must restart the whole chain in runbook
order (see `HOW-TO-RUN.md`):

1. UCEB API `:5000` (Development, staging/dev Security overrides, `--no-launch-profile`)
2. MCP `:5200` (`dotnet run -- --http`) → complete the browser IAM sign-in as **arizzo@aurahyland.onmicrosoft.com**
   within 120s → wait for `[Lob] ... wdx -> bow` + `[Warmup] Pre-authentication complete`
3. dev tunnel (`devtunnel host giant-ant-2f6br43`)
4. BFF `:5010` (restarting it wipes in-memory sessions → re-sign-in in the extension)

> Correction to an earlier assumption: a window reload **does** kill the servers. Restart everything.

---

## 3. Context-aware chat — already working

Confirmed the chatbot already injects page context. `popup.js` (just before `sendMessageToAgent`)
appends a hidden hint when a record is detected on the page:

```
[Context — the user is viewing this record in the browser: businessObjectType=…, businessObjectId=….
 Use these when listing or uploading documents for "this record".]
```

So "list documents for this record" / "upload to this record" already resolve the WID/type from the
active Workday tab. No change needed.

---

## 4. Viewer 404 for Workday docs — root cause

`open_document_in_viewer` → `UcebApiClient.GetViewerUrlAsync` was returning the **Salesforce** HxViewer
URL for **every** LOB (it preferred the single `Uceb:ViewerBaseUrl`). Workday (`wdx → bow`) documents
don't exist in the Salesforce content-browser experience → the viewer returned
`CONTENT_BROWSER.DOCUMENT.LOAD_ERROR.DEFAULT` (a 404). The document itself was fine (it showed up in
`list_documents`).

---

## 5. CIC viewer experiment (manager's URL) → 401 in the iframe → reverted

Per the manager we briefly pointed the viewer at the new **CIC viewer**:

```
https://bravo.cic-viewer.sandbox.app.hyland.com/#/documents/{doc_id}?envKey=appintel-dev-test
```

- `open_document_in_viewer` resolved correctly (placeholder substituted, `envKey` preserved).
- **But** in the extension the CIC viewer app loaded, then `GET …/user` returned **401 Unauthorized**
  → the viewer couldn't resolve the signed-in user → the document wouldn't render.
- Two candidate causes (not fully separated): (a) Chrome blocks the viewer's session cookie in the
  third-party `chrome-extension://` iframe (partitioned / SameSite); (b) an environment mismatch — the
  whole stack + login are on **dev** (`auth.dev.app.hyland.com`) but the viewer host is on the
  **sandbox** cluster (`bravo.cic-viewer.sandbox…`).
- Decisive test (if we revisit CIC): open the exact URL in a normal top-level tab. Renders there but
  not in the iframe → third-party-cookie issue (open in a new tab). 401s in a tab too → env/session
  mismatch (needs the correct host/login from the manager).

We abandoned the CIC viewer in favor of the Workday HxViewer URL below.

---

## 6. A build error we introduced then fixed (CS0162)

A hardcoded `return "<CIC url>";` had been placed at the **top** of `GetViewerUrlAsync`, which made all
the logic below it unreachable → **`CS0162: Unreachable code detected`**, and because this project
treats warnings as errors, the build failed. Removed that one line (it was also redundant — the same
URL was already in `appsettings.json`, and it hardcoded a single doc id, ignoring the `documentId`
parameter). Build went green.

---

## 7. Final fix: LOB-aware viewer URL (Salesforce vs Workday)

Requirement (user): **keep the Salesforce HxViewer URL for Salesforce, and switch to the Workday
HxViewer URL when the login is Workday.** The Workday viewer URL came from a Workday teammate and was
verified to render (top-level tab, signed in as arizzo).

**Changes (McpServer):**

- `Configuration/UcebMcpOptions.cs`: added `WorkdayViewerBaseUrl` (Salesforce/CIC stays on `ViewerBaseUrl`).
- `Clients/UcebApiClient.cs`:
  - new field `_workdayViewerBaseUrl`;
  - `GetViewerUrlAsync` now picks by `_lob.IsWorkday`:
    Workday → `WorkdayViewerBaseUrl` (if set), else falls back to `ViewerBaseUrl`, else the solution-config blob.
- `appsettings.json`:

  ```jsonc
  "Uceb": {
    // Salesforce/CIC HxViewer
    "ViewerBaseUrl": "https://key-a6cbaddb-984f-4c10-afec-f1a579396269.studio.dev.app.hyland.com/hfs-configurations-a94844a0/ui/hyland-for-salesforc-u8fcw/#/default/documents/{doc_id}",
    // Workday HxViewer (wdx -> bow)
    "WorkdayViewerBaseUrl": "https://key-a6cbaddb-984f-4c10-afec-f1a579396269.studio.dev.app.hyland.com/wdx-configurations-b1dfc8ae/ui/default-ajs4f/#/default/documents/{doc_id}"
  }
  ```

Build clean (`get_errors` 0/0). MCP restarted as Workday (`wdx → bow`, warm-up complete) so a doc click
now resolves to the Workday HxViewer URL with that doc's id.

> Caveat still open: a `studio.dev.app.hyland.com` viewer may send `X-Frame-Options`, so it can stay
> blank inside the in-panel iframe (`popup.js` ~L569). Fallback = the open-in-new-tab (↗) button, which
> is the same top-level context where it rendered in the screenshot.

---

## 7b. Viewer must open in a top-level tab (iframe is impossible) — exact cause + fix

After the LOB-aware URL was correct, clicking a doc still failed in the side-panel iframe. Console chain:

1. The Workday HxViewer SPA loads in the iframe (fonts load from `wdx-configurations-b1dfc8ae`).
2. Its backend calls — `…/constant-values`, `…/include-variables`, `…/v1/preferences` — all **401**
   (no authenticated Hyland IAM session inside the iframe).
3. Unauthenticated, the viewer redirects the iframe to the IAM login page `https://auth.dev.app.hyland.com/`.
4. That login page sends **`Content-Security-Policy: frame-ancestors 'none'`** → the browser blocks the
   frame → **"auth.dev.app.hyland.com refused to connect."**

So the **in-panel iframe can never show this viewer**: it needs an interactive IAM login, and the IAM
login page refuses to be framed (Hyland-side policy, out of our control). The browser also had no
first-party Hyland session yet (the Workday widget showed a "Login To Hyland" button).

**Fix (`browser-extension/src/popup.js`, doc-row click handler ~L450):** open the resolved viewer URL
in a **top-level browser tab** via `ext.tabs.create({ url })` instead of the iframe overlay
(`openViewer`). A top-level navigation lets the IAM login complete first-party, so the document
renders — the same context where it worked in the screenshot. The old `openViewer` iframe overlay is
left defined but unused (harmless). `get_errors` clean.

---

## 8. How upload / capture is wired (reference)

For the current Workday login (`wdx → bow`), `UcebApiClient.CaptureDocumentAsync` builds:

```
POST http://localhost:5000/bow/core/documents?systemFriendlyName=CIC
```

- **Base URL:** `http://localhost:5000` (local UCEB API) — `Uceb:BaseUrl`.
- **Route segment:** `bow` for Workday (`api` for Salesforce/CIC) — derived from the token's LOB app key.
- **SystemFriendlyName:** `CIC` — `Uceb:SystemFriendlyName`, appended by `WithSystemConfig(...)`.
- **Body:** `multipart/form-data` with two parts — `file` (bytes + mimeType) and `captureData`
  (JSON: `documentTypeId` + `businessObjectAttributes`). Bytes are staged first (`/staging/upload`,
  posted by the plugin via the BFF), then filed in one POST by `capture_document`.
- **Rendition-500 masking:** on HTTP 500 the client re-lists the worker and, if a new doc landed,
  reports success (the post-archive thumbnail/preview rendition step is unprovisioned in this env).
- **Environment:** MCP runs the **Production** ASP.NET environment (so `appsettings.json` is effective,
  not `appsettings.Development.json`); the UCEB API authenticates against **dev** and routes to the
  **Workday (`wdx → bow`)** content LOB.

> Note: for a `bow` capture the code still appends `systemFriendlyName=CIC`. That selector is a
> Salesforce/CIC concept — Workday's config route uses `businessObjectType` instead and ignores it.
> Harmless on the capture POST, but the first thing to check if Workday capture routing ever looks off.

---

## 9. Stack state at end of day

Running (all async terminals): UCEB API `:5000`, MCP `:5200` (Workday `wdx → bow`), dev tunnel
`giant-ant-2f6br43`, BFF `:5010`.

Anthony Rizzo: WID `4bc212416f234ba1b4749e4bebe4c2eb`, employeeId `21021`. Only the real WID works as
the capture/list business-object id. Workday rejects `.txt` (use pdf/png/jpg/docx). `bp-attachments` is
filtered out of the upload dropdown (needs BP context, never uploadable).

---

## 10. Next / open

- Confirm the Workday HxViewer URL renders end-to-end from the extension (in-panel iframe vs ↗ new tab).
- If we ever return to the CIC viewer: run the decisive top-level-tab test to separate the
  third-party-cookie issue from the dev-vs-sandbox env mismatch.
- Decide whether the in-panel iframe should auto-open studio.dev viewer docs in a new tab (avoids the
  `X-Frame-Options` blank-frame case).
