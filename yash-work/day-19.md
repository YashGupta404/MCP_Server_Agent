# Day 19

_Focus: got the **Workday + OnBase** plugin demo working end-to-end. Fixed **upload/capture** (two separate
bugs behind the same "Invalid data" 400), fixed the **chatbot document listing**, then chased the
**"preview not available"** problem to its real cause — the OnBase **staging** config ships with **no
renditions and no viewer URL** (unlike CIC). Also fixed the extension's viewer-fallback bug and drafted the
mentor ask for the staging Studio URL._

---

## 0. Demo stack brought up

| Component | Where | Notes |
|---|---|---|
| **MCP** | `:5200` | arizzo (staging), token app key `wdx` → `bow`, active system `onbase_hcm_stg` |
| **BFF** | `:5010` | relays panel → MCP and chatbot → Agent Builder → tunnel → MCP |
| **Dev tunnel** | `giant-ant-2f6br43` → `:5200` | `https://4kw1kpcm-5200.asse.devtunnels.ms` (chatbot path only) |
| **UCEB** | deployed staging | `https://api.uceb.app-intel.staging.app.hyland.com` — does the CFS token exchange **internally** |

Key facts:
- The **panel** path (list/upload/view) needs only BFF + MCP. The **chatbot** path additionally needs the
  tunnel + Agent Builder.
- **Every MCP restart resets the active `systemFriendlyName` to the appsettings default `cic`** — must
  `set_active_system_configuration onbase_hcm_stg` again (the plugin re-asserts it on connect; backend tests
  must set it explicitly).

---

## 1. Upload/capture bug #1 — the WID was never attached

**Symptom (panel):** Upload → `400 "Invalid data provided in the request"` from `/bow/core/documents`.

**Root cause:** the capture was sending **all 13** default document-type attributes with **null** values.
The record's WID never got injected. The injection logic (both MCP
`GetCaptureDefaultAttributesAsync` and the BFF `/api/capture`) only set a value on an attribute whose
id/name **ends with `businessObjectId`** — but this OnBase schema names it **`Employee Object ID` (id 161)**
and **`Employee ID` (id 162)**. Nothing matched → all values null → 400.

**Empirical proof** (backend capture smoke, valid PNG, WID `4bc212416f234ba1b4749e4bebe4c2eb`, docType 118
"Offer Letter"):

| Variant | Attributes sent | Result |
|---|---|---|
| A | `Employee Object ID` (161) = WID only | ✅ filed (documentId 14300) |
| B | `Employee ID` (162) = WID only | ❌ 500 "unexpected error at the broker" (WID isn't the employee number) |
| C | 161 + 162 = WID | ❌ 500 broker error |
| D | full 13-attr schema, only 161 = WID, rest null | ✅ filed (documentId 14301) |

So: only **`Employee Object ID` (161)** must carry the WID, and sending the other attributes null is fine
(no null-dropping needed).

**Fix — `Clients/UcebApiClient.cs` `GetCaptureDefaultAttributesAsync`:** two-pass injection.
- Pass 1: match a schema attribute whose id/name (normalized: spaces stripped, lower-cased) **ends with**
  the supplied name (`businessobjectid`).
- Pass 2 (fallback, only if Pass 1 matched nothing **and** the supplied name ends with `objectid`): set the
  attribute whose id/name ends with `objectid` → maps `businessObjectId` → **`Employee Object ID`**.

The BFF re-serializes the array the MCP returns, so fixing the MCP alone fixes **both** the panel and the
chatbot.

---

## 2. Upload/capture bug #2 — doc-type sent as a NAME, not an id

After bug #1 was fixed, the panel **still** 400'd. The MCP log showed the real reason:

```
[UcebTools] capture_document called: documentTypeId=Offer Letter   ← the NAME, not "118"
```

The browser extension (`popup.js` → `captureDocument(ctx, docType, files)`) passes the dropdown's **friendly
name**. So `get_capture_default_attributes("Offer Letter")` threw at the UCEB call
(`EnsureSuccessAsync`, `UcebApiClient.cs:1523`), the BFF fell back to `attributes=[]`, and
`capture_document` posted `documentType.id="Offer Letter"` with no attributes → 400.

**Fix — `Tools/UcebTools.cs`:** resolve the friendly name → numeric id in **both** `capture_document` and
`get_capture_default_attributes`, using the existing `ResolveWorkdayDocumentTypeIdAsync` helper (returns the
value as-is if it's already an id; case-insensitive name match otherwise). Covers panel **and** chatbot.

**Verified** (exact panel flow, using the name "Offer Letter"):
- `get_capture_default_attributes('Offer Letter')` → resolved to 118 **and** `Employee Object ID` = WID ✅
- `capture_document('Offer Letter')` → filed, documentId 14302 ✅

---

## 3. Chatbot listing — "no document queries are configured for employee"

The chatbot uses a **different tool** than the panel: `query_documents` (not `list_documents`). The MCP log:

```
[UcebTools] query_documents called for type=employee id=4bc... queryId=(null)   → IsError=False
```

`query_documents` resolves queries via `GetRecordListQueriesAsync(businessObjectType)`. The
`onbase_hcm_stg` solution config has **no query configured for `employee`**, so it returned
_"No document queries are configured for businessObjectType 'employee'."_ Meanwhile `list_documents` (the
Workday `/bow` `ListDocumentsAsync` path) **works** and returns ~88 docs.

**Fix — `Tools/UcebTools.cs` `QueryDocumentsAsync`:** when `recordQueries.Count == 0`, **fall back** to
`_ucebApiClient.ListDocumentsAsync(businessObjectId, businessObjectType)` and format the same way.

**Verified** both directly and **through the tunnel**: `query_documents(employee)` → `Found 90 document(s)`.

> Gotcha discovered: after this fix the chatbot still echoed the old error once — the **cloud agent was
> replaying its earlier tool result from conversation memory**. A **fresh chat** re-queries and works. The
> tunnel-direct call proved the MCP itself was correct.

---

## 4. "Preview not available" — the real cause (not our code)

The panel showed "preview not available"; the console also showed the viewer fallback 400'ing with
`missing_docId`.

**Tested every rendition type** (`preview`, `captureSuccessPreview`, `thumbnail`, `originalRendition`,
`captureSuccess`) via `GET :5200/documents/{id}/preview?renditionType=…` for **both** a freshly-captured doc
(14304) and a **pre-existing** one (14293 "Application"):

```
ALL → HTTP 404 {"error":"no_preview"}
```

So this OnBase **staging** environment provisions **no preview-image renditions** for any document. When
there's no rendition the platform returns the fixed `preview-unavailable.png` placeholder, which the MCP
correctly maps to 404 `no_preview`.

**Confirmed in the `onbase_hcm_stg` solution config** (via `get_solution_configurations`):

```jsonc
"renditionIds":  { "data": null }   // → no renditions      → inline preview 404s
"viewerBaseUrl": { "data": null }   // → no viewer URL      → falls back to the hard-coded DEV Studio URL
"imageScalingConfigs": [ thumbnail 100x100, captureSuccessPreview 500x500 ]  // scaling defined, but no renditionIds to serve
```

**Why "view worked previously":** the earlier demo was on **CIC (dev)** — CIC auto-generates renditions
**and** its solution config carried a Studio `viewerBaseUrl`, so both inline preview and "open in viewer"
worked. `onbase_hcm_stg` (staging) has **neither** configured. This is a **backend config gap**, not a
regression.

### Viewer URL resolution recap
`GetViewerUrlAsync` is **config-first**: it reads the active system's
`data.configurations.operationConfig.viewer.baseUrl`, and only falls back to the appsettings
`Uceb:WorkdayViewerBaseUrl` (a **dev** `studio.dev.app.hyland.com` URL) when that's empty. For staging docs
that dev URL is the wrong environment. The `/documents/{id}/content` endpoint does return a viewerUrl, but
it's the dev one:

```
https://key-a6cbaddb-…studio.dev.app.hyland.com/wdx-configurations-b1dfc8ae/ui/default-ajs4f/#/default/documents/14304
```

---

## 5. Extension viewer-fallback fix

When inline preview is unavailable the panel is meant to offer "Open in viewer". But
`showPreviewFallback()` in `browser-extension/src/popup.js` **re-called** `openInViewer(currentPreviewDocId)`
even when `currentPreviewDocId` was null → BFF `/api/viewer` `400 missing_docId`.

**Fix:** reuse the viewer URL `openDocumentPreview` already resolved (`currentViewerUrl`), and only call the
resolver when we don't have a URL **and** we have a docId. This is a **popup.js change → reload the
extension only** (no MCP rebuild/login).

---

## 6. Files changed (all uncommitted)

**MCP (`Hyland.Experience.UCEB.Api` fork, branch `feature/uceb-mcp-server-poc`):**
- `Clients/UcebApiClient.cs` — two-pass WID injection in `GetCaptureDefaultAttributesAsync`.
- `Tools/UcebTools.cs` — doc-type **name → id** resolution in `capture_document` +
  `get_capture_default_attributes`; `query_documents` **fallback** to `list_documents`.

**Extension (`MCP_Server_Agent` fork, branch `main`):**
- `browser-extension/src/popup.js` — `showPreviewFallback` reuses `currentViewerUrl` (fixes
  `missing_docId`).

---

## 7. Verified working at end of day

- ✅ **Panel upload/capture** into OnBase (`Offer Letter`, docIds 14300–14304) — real files
  (PDF/DOCX/PNG; `.txt` is rejected by OnBase: allowed = png, jpg, jpeg, bmp, tiff, pdf, docx, doc).
- ✅ **Panel + chatbot listing** — 90 docs for the employee (chatbot via `query_documents` fallback, tested
  through the tunnel).
- ✅ Doc-type **name → id** resolution and **WID injection** confirmed end-to-end.
- ⚠️ **Inline preview / view** — blocked on backend config (`onbase_hcm_stg` has no `renditionIds` and no
  `viewerBaseUrl`). CIC works for view; OnBase-staging view needs the staging Studio URL + renditions.

---

## 8. Open item — staging Studio viewer URL (mentor ask)

Need the **staging** equivalent of the dev Workday viewer template (ending in `/documents/{doc_id}`):
```
https://key-a6cbaddb-…studio.dev.app.hyland.com/wdx-configurations-b1dfc8ae/ui/default-ajs4f/#/default/documents/{doc_id}
```
Once received, set it on the config via `set_viewer_url` (config-first → used immediately, no rebuild). Also
asked whether OnBase-staging **renditions** should be provisioned (for inline preview) and whether Studio or
an OnBase QuickAccessViewer is the intended viewer.

---

## 9. Gotchas / notes for next time

- **MCP rebuild + restart** is required after any MCP code change, and each restart pops the **arizzo staging
  login** browser (120s) and **resets the active system to `cic`**.
- The VS Code persistent PowerShell terminal got **corrupted** mid-session (stuck `^U`, cmdlets
  "not recognized"). Fresh `run_in_terminal` terminals work but default to **Restricted** execution policy →
  prefix scripts with `Set-ExecutionPolicy -Scope Process Bypass -Force;`.
- Backend smoke helpers in `%TEMP%`: `capture-variants2.ps1`, `capture-variant-d.ps1`, `verify.ps1`,
  `verify2.ps1`, `verify3.ps1`, `preview-test.ps1`, `content-test.ps1`, `solcfg.ps1`. Stage endpoint:
  `POST :5200/staging/upload {fileName,mime,dataBase64}` header `X-Api-Key`.

---

# Day 19 (continued — 2026-09-04)

## 10. "Open in viewer" for Workday/OnBase — QAV + the Microsoft-login finding

**Inline preview stays unavailable** (confirmed day-19 §4: `onbase_hcm_stg` has no `renditionIds`), so the
value is the **"Open in viewer"** fallback.

- **Fixed the fallback bug** (`browser-extension/src/popup.js` `showPreviewFallback`): it re-called
  `openInViewer(currentPreviewDocId)` even when the id was null → BFF `/api/viewer` `400 missing_docId`. Now
  it **reuses the already-resolved `currentViewerUrl`** and only calls the resolver when needed.
- **Viewer URL was pointing at dev.** `GetViewerUrlAsync` is config-first (`operationConfig.viewer.baseUrl`);
  `onbase_hcm_stg`'s is null, so it fell back to the hard-coded **dev** Studio URL. Mentor gave the OnBase
  **QuickAccessViewer (QAV)** URL; set `Uceb:WorkdayViewerBaseUrl` =
  `https://hylandforworkdayextend.hyland.com/QAV-VER-LATEST/viewer/document/{doc_id}`. Verified the content
  endpoint now returns the QAV link.
- **Why clicking it opened a Microsoft login (investigated, not guessed).** Opened the QAV URL in a browser
  and read the redirect: QAV does **SP-initiated SAML SSO to Azure AD tenant
  `bc385427-8e74-4f77-bec3-664ca9dc22e8`** (the Workday customer IdP). arizzo's tenant
  (`aurahyland.onmicrosoft.com`) is `5150c46e-…` — **different** — so arizzo can't sign in there. It's QAV's
  own IdP config; our extension opens the URL correctly (plain new tab). Not fixable in our code.

## 11. Document **name + type** in the list (panel + chatbot)

**Symptom:** OnBase cards showed a wrong title + a repetitive attribute dump; no clean type.

**Root cause:** `bff/Program.cs` `ParseDocumentList` resolved the card name **only from `hfs_Name`** — a
Salesforce/CIC column. Workday/OnBase uses `Name`/`Document Name` + `Document Type`/`Type`, so it fell back
to the first arbitrary attribute.

**Fix (MCP_Server_Agent — no MCP rebuild):**
- `bff/Program.cs` `ParseDocumentList`: name = first of `hfs_Name`/`Document Name`/`Name`/`File Name`/`Title`
  else docId; extract **type** from `Document Type`/`Type`; drop those duplicate columns; return
  `{docId, name, type, attributes}`.
- `browser-extension/src/popup.js` `renderDocuments`: icon uses the extension or a `DOC` badge; sub-line
  leads with the **document type**, then de-duplicated attribute values.

## 12. Why the exact **filename** can't show on `onbase_hcm_stg` (mentor's 9714 vs here)

Manager asked: on **OnBase 9714 (Salesforce)** the name worked — why not here?

- On 9714 the displayed name was a **keyword** (`Entity Name`) that we **set at upload** AND added as a
  **display column** on the `queries/execute` query.
- Verified on `onbase_hcm_stg`: I **can** set the `File Name` keyword at capture (earlier 500 was a bad
  value; a clean value captured fine — docs 14306/14307), **but the `/bow` record list never returns it**.
- **Structural reason:** Workday content has **no `queries/execute` endpoint** — listing uses the
  record-scoped `/bow/.../documents` path whose columns are **OnBase's fixed projection** (`documentName`
  auto = `"<DocType> - <Date>"`, `documentType`, a fixed `simpleDocumentAttributes` set). The solution
  config's `queryConfig`/`displayColumns` are never consulted, so `set_query_display_columns` (the 9714
  lever) doesn't apply. **Backend/OnBase change needed** to project a name keyword. Mentor message drafted.

## 13. NEW: "my documents" = the **signed-in user** only (Workday)

Manager: in Workday, the panel **and** chatbot must default to the **signed-in user (arizzo)**, not whichever
employee profile page is open (navigating to a subordinate via View Team + "show my documents" wrongly
showed the subordinate). Decisions: auto-resolve self from the login identity; panel always shows arizzo;
chatbot default = self, override a subordinate **by employee id only**.

**Key discovery:** the access token has **no name/email** (only an opaque `sub` GUID) — but the IAM
**userinfo** endpoint does. Verified end-to-end:
- **MCP** `get_my_identity` (new): `Auth/InteractiveLoginService.cs` `GetUserInfoAsync` + `IdToken`;
  `Tools/UcebTools.cs` `get_my_identity`. userinfo → `{ name: "Anthony Rizzo", preferred_username:
  "arizzo@aurahyland.onmicrosoft.com" }`.
- **Workday Staffing** search `"Anthony Rizzo"` → **exactly 1** match → WID `4bc2…` (no ambiguity;
  `"arizzo"` → 0, `"21021"` → 1).
- **BFF** `/api/me` (new, `bff/Program.cs`, cache `selfWorkerIdentity`): `get_my_identity` → parse userinfo
  name → Staffing `/workers?search=name` → unique WID.
- **Extension**: `agent.js` `fetchMe()`; `popup.js` `loadContextPanel` **overrides `currentContext` to self
  (arizzo)** when the context is Workday. Because the chatbot injects `currentContext.businessObjectId` as
  its context hint, this **one override fixes the panel (list + upload) AND the chatbot default**.

**Remaining (Phase 4):** chatbot **override** "list documents of employee id X" needs the cloud agent to
resolve an employee id → WID — requires a **new MCP worker-resolve tool** (with Workday Staffing creds); the
agent can't reach the BFF's `/api/worker/resolve`.

## 14. Files changed today (uncommitted → being committed)

**UCEB API (`feature/uceb-mcp-server-poc`):** `Auth/InteractiveLoginService.cs` (userinfo),
`Tools/UcebTools.cs` (`get_my_identity`), `McpServer/appsettings.json` (QAV `WorkdayViewerBaseUrl`).
_(Note: `Api/appsettings.Development.json`'s emptied `TokenExchange:ClientId` was a debug leftover and was
reverted, not committed.)_

**MCP_Server_Agent (`main`):** `bff/Program.cs` (`ParseDocumentList` name/type, `/api/me`),
`browser-extension/src/popup.js` (viewer fallback, `renderDocuments`, self-override),
`browser-extension/src/agent.js` (`fetchMe`), `yash-work/day-19.md`.

