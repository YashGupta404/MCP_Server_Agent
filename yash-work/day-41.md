# Day 41 — ServiceNow (hfsn) demo polish: metadata, IDP, auto-refresh, MCP session persistence, and a staging CIC outage

_Continues day-40 (hfsn CIC model + solution config live; upload + list working end-to-end). Today = make
the side panel demo-ready (upload metadata, list auto-refresh, IDP for ServiceNow, record-switch detection)
and persist the MCP login so restarts don't need a browser. Ended the day diagnosing a staging CIC search
outage that is NOT our code._

Stack for the demo: local UCEB :5000 (`dotnet run` in UCEB.Api) + `switch-lob.ps1 -Lob servicenow` (MCP :5200,
silent login) + BFF :5010 + devtunnels `giant-ant-2f6br43` (agent→MCP, Talk with agent) and
`joyful-horse-g88zxrw` (IDP cloud→BFF :5010). IDP token via `idp-refresh.ps1` (silent) or `idp-feature-test.ps1`
(browser, needs :5005 free → stop MCP first).

---

## 1. List documents was empty (defaultListQuery) — FIXED

- Upload worked but the list was empty. Cause: UCEB `GET /api/core/business-objects/{id}/documents` →
  RelatedDocumentsService → `DocumentQueryService.ExecuteDefaultListQueries` runs ONLY the query ids pinned in
  `additionalConfig[].defaultListQuery`. It was `null` → returned empty WITHOUT querying CIC (the legacy
  `DocumentService.GetDocumentList` is NOT on this path).
- Fix: set `additionalConfig[change_request].defaultListQuery = { name/id: "change-request-type",
  type: "ContentType", default: true }` in `servicenow-solution-config.json`, saved via MCP
  `set_solution_configuration`. List returns docs. Every new SN table (incident/sc_req_item) needs its own.
- PS 5.1 gotcha: `Get-Content -Raw` string → `ConvertTo-Json` serializes it as an OBJECT (PSPath props) and
  `set_solution_configuration` 500s ("could not convert StartObject to String"). Use
  `[IO.File]::ReadAllText(...)` for the `dataJson` arg.

## 2. Document name showed the record number — FIXED

- Uploaded docs listed as `CHG0040019` instead of the file name. Cause: MCP
  `UcebApiClient.ResolveImportKeywordFieldAsync` fell back to the FIRST `metadataFieldImportMappings` entry when
  no `inputSource == "1"` existed → stamped the record id into `hfsn_documentName` and SKIPPED
  `PopulateAttributesAsync` (which stamps the file name).
- Fix: the fallback now skips `inputSource` 4/5/6 (file name / file type / user-entered). New uploads get the
  file name; UCEB still writes the record number into `hfsn_recordNumber` via `boContext.ecmProp`. Also BFF
  `ParseDocumentList` now treats any `*documentName` attribute as the display name. Docs uploaded before the fix
  keep the wrong name.

## 3. Query tab / columns empty (filterClauses) — FIXED

- Panel list + Query tab errored `25403 "One or more query inputs are not configured for this query"` for any
  filter. Cause: `queryConfig[...].queries[].filterClauses` was `[]`; UCEB
  `DocumentQueryService.ExecuteQuery` rejects any input whose field id isn't a configured filter clause.
- Fix: added `filterClauses` (inputSource "3", editable) for all six `hfsn_*` fields.
- CIC adapter LIMITS: rejects `*CaseInsensitive` ("not yet supported by the CIC adapter") AND
  `StartsWith/EndsWith` ("prefix/suffix matching unavailable on OpenSearch keyword fields"). Use
  **`EqualsCaseSensitive`**. BFF `/api/context` default-list path now retries with `EqualsCaseSensitive` when a
  `*CaseInsensitive` attempt returns "not yet supported".
- The panel showed only "Document Name / Document Type" earlier because the default-list query failed → BFF fell
  back to `list_documents` → `computeColumns()` default. `computeColumns()` now always leads with
  "Document Name" when deriving columns from attributes.

## 4. Upload metadata not reflected in the list — FIXED

- Values typed in the upload form (e.g. `hfsn_priority = 2`) were dropped. Cause: `documents.view.ts`
  `metaForFile()` only returned the form values for the file CURRENTLY selected in the file list
  (`selectedUploadName`). Adding a file doesn't select it → values silently lost. (UCEB
  `CreateDocumentCreationRequest` passes ALL additional attributes through, so the server side was fine.)
- Fix: when no file is picked in the list, `metaForFile()` falls back to `collectUploadMeta()` (the form values),
  so they apply to every uploaded file. Works for ALL metadata fields, not just priority.

## 5. Metadata form leaked between files — FIXED

- After IDP, selecting the 2nd file (and setting it to change-request-type) showed the 1st file's values. Causes
  in `loadUploadMetaFields` / `onDocTypeChange` / `selectUploadFile`: (a) carried the previous file's
  `docTypeFieldValues` into a file with no snapshot, (b) treated an empty snapshot `{}` as "no snapshot", (c) a
  late IDP extract applied to whichever file was selected.
- Fix: track `metaValuesFor` (which file the current values belong to) and never carry across files; treat `{}`
  as a real saved snapshot; ignore a stale IDP extract whose file/type is no longer selected.

## 6. Post-upload list auto-refresh — ADDED

- `documents.view.ts` `refreshAfterUpload(expected)`: after the first reload, polls quietly (2/3/5/8s, no
  skeleton) until the list count reaches the expected total, to ride out CIC search-index lag. Replaces the old
  single "reload + one 2.5s retry".

## 7. ServiceNow table: doc types + record-switch detection

- **`hfsn-base-type` hidden from the upload dropdown:** the panel loaded doc types before the record was
  detected (unscoped → every repo type incl. `hfsn-base-type`, which only carries the mixin). Fix: panel reloads
  doc types once `businessObjectType` is known (`docTypesBoType`); BFF `/api/doctypes` fallback also drops any
  `*-base-type`.
- **Panel didn't switch when you opened another change request:** `detector.js` wrapped `history.pushState` in
  the content script's ISOLATED world, so the page's own pushState never fired it. Fix: poll `location.href`
  every 700ms → `onNavigate`; `startServiceNowWatch(prevKey)` keeps polling until the detected record key differs
  from the pre-navigation key (the nested classic form shows the OLD record for a moment). RELOAD the extension +
  refresh the SN tab after content-script edits. (Debug logs `[UCEB-SN]`/`[UCEB-DETECT]` still in detector.js →
  remove before commit.)
- **Empty-state copy + debug line:** removed the `tabs=…|rt=…|url=…` `ctxDebug` line from the empty state;
  `documents.noRecord` now mentions ServiceNow ("Open a Salesforce, ServiceNow or Workday record…").

## 8. IDP works for ServiceNow — samples added

- IDP is LOB-agnostic: on a change_request it classifies against the record's CONFIGURED types (just
  `change-request-type`), described to IDP by its field labels, and extracts `hfsn_shortDescription /
  hfsn_changeState / hfsn_priority` to pre-fill the upload form. Confirmed live: `change-request-form.png` →
  change-request-type @96% with all three fields filled.
- Samples: `bff/make-samples-servicenow.ps1` → `samples-servicenow/change-request-form.png` (should classify as
  change-request-type) + `grocery-receipt.png` (knowingly-unrelated → should come back Undefined / review).
- Two prerequisites for IDP: a valid IDP token (`idp-refresh.ps1` silent, or `idp-feature-test.ps1` with MCP
  stopped) AND the BFF devtunnel `joyful-horse-g88zxrw` up (`Idp:PublicBaseUrl =
  https://qpzmsssb-5010.euw.devtunnels.ms`) so IDP's cloud can fetch the staged file.
- Why IDP needs its OWN IAM sign-in separate from the MCP: IDP uses a different client (`Idp:ClientId`) and
  scopes (`hxp.integrations hxpr hxps`); the ServiceNow MCP token doesn't carry them. The devtunnel only lets
  IDP reach the BFF (IDP→us); the sign-in lets us call IDP (us→IDP). Normally silent-refreshed; only prompts when
  the IDP refresh token expires (a few days → `invalid_grant`).

## 9. MCP session now PERSISTS across restarts — ADDED

- Problem: every MCP restart opened a browser login. In-memory silent refresh already existed but was lost on
  restart.
- Fix (UCEB `McpServer`): `Auth:PersistSession = true` → `InteractiveLoginService` saves the refresh token
  DPAPI-encrypted (ASP.NET Data Protection, per Windows user) at
  `%LOCALAPPDATA%\UcebMcp\session-<sha256(clientId)[..16]>.bin`; on startup it loads + silently refreshes. One
  file per client id (switching LOBs never reuses another client's token). Deleted by the logout tool or on a
  failed refresh. Confirmed: restart → `[InteractiveLogin] Found a saved session — signing in silently`, no
  browser. `switch-lob.ps1` literal-replaces appsettings so the setting survives a LOB switch.

## 10. Upload/list hardened against staging CIC 503s — ADDED

- MCP `UcebTools.AttachContentAsync`: `WithTransientRetryAsync` retries initiate+complete (fresh session) and
  attach on transient config failures ("File type is not supported" / 22404 / 21404 / ServiceUnavailable);
  6 attempts, backoff 1.5–8s.
- MCP `UcebApiClient.SendWithTransientRetryAsync` (5 attempts) wraps `ListDocuments / QueryDocuments /
  GetSolutionConfigurations`: retries on 503 or a body containing 22404/21404/"Unknown type".

## 11. NEW diagnostic tool: `get_document`

- Added MCP tool `get_document` + `UcebApiClient.GetDocumentByIdAsync` → GET
  `https://{env}.content.staging.app.hyland.com/api/documents/{id}` (DIRECT to the content platform, bypassing
  search). Returns a doc's real stored fields: `sys_parentPath`, `sys_parentId`, `sys_primaryType`,
  `sys_mixinTypes`, `hfsn_recordNumber`, blob, etc. Read-only; kept because it's the only way to see ground truth
  when search is stale. NOTE: UCEB's own `/api/core/documents/{id}` is DELETE-only (GET → 405); metadata-by-id
  lives on the content platform host.

---

## BLOCKER (end of day 41, 2026-11-09) — staging CIC search/index outage. NOT our code.

- Symptom: upload shows success, but the new doc never appears in the panel list OR the Studio content browser.
  Only ~7 older docs show. `/api/query` also 503s intermittently → random upload/list failures.
- Exact UCEB error: `HttpRequestExecutor 'Unable to execute GetPlatformSystemConfigurations on resource
  https://appintel-staging-test.content.staging.app.hyland.com/api/query using POST: Status code -
  ServiceUnavailable'` → `PlatformSystemConfigService 'Friendly name 'CIC' did not match any accessible system
  configuration.'` → upload `complete` returns 400 `{"title":"File type is not supported."}` (a MISLABEL: the
  config load failed, not the file type; `png` is allowed).
- The failing call is the **content platform (CIC) `/api/query`** at `*.content.staging.app.hyland.com`, which
  UCEB (`localhost:5000`) CALLS. UCEB is healthy and only relays the 503. UCEB uses `/api/query` to (a) resolve
  the CIC/solution config on every call and (b) list documents — so one 503 breaks both.
- `CIC` vs `cic` is a RED HERRING: resolution is case-insensitive (`Resolved friendlyName 'CIC' →
  systemConfigId 0000…`), and `query_documents?systemFriendlyName=CIC` returned 200/7 docs many times. The
  "did not match" only fires after the 503 (can't enumerate configs when the service returns nothing).
- PROOF it's an index problem, not our upload: compared an indexed doc (`invoice-acme`, `sys_created`
  2026-11-08 12:09 UTC) vs today's (`6c11b042`, 2026-11-09 06:50 UTC) via `get_document`. IDENTICAL structure —
  same `sys_parentPath /hfsn-documents/CHG0040019`, same `sys_parentId a3b5b1e5-…577ad`, same
  `sys_primaryType change-request-type`, same `sys_mixinTypes [SysFilish, hfsn-lob-mixin]`,
  `hfsn_recordNumber=CHG0040019`. Both download by id. The ONLY difference is `sys_created`. So uploads are
  stored & filed correctly; the search index just stopped INGESTING after Nov 8 12:09 UTC. Both the panel and
  the Studio content browser read that same index → both blind to new docs.
- Can't self-serve: the Admin Portal (admin.staging.app.hyland.com) has only Users/Identity/External
  Systems/Metrics — no reindex control. Escalated in Teams (Aura Team) with env `appintel-staging-test`
  (id `06a2be36-c97a-4edc-864a-6741ba1643ab`), repo `default`, the Nov 8 12:09 UTC cutoff, and the `/api/query`
  503. Waiting on Sagnik/Abhishek to reindex/restart the indexer.

## PENDING (not yet built) — demo-safe "optimistic insert"  → DONE 2026-11-09

- After a successful upload, the file shows in the list IMMEDIATELY (name + entered metadata columns),
  independent of the stale search. Implementation:
  - BFF `/api/upload` now parses `documentId` from the upload tool result and returns
    `uploadedDocuments: [{ name, documentId }]`.
  - `document.service.ts upload()` returns `uploadedDocuments`.
  - `documents.view.ts`: on upload success builds optimistic `UcebDocument` rows (docId + file name +
    entered `hfsn_*` metadata) → `optimisticDocs` (scoped to the record via `optimisticRecordId`);
    `mergeOptimistic(list)` prepends them to every list load and drops each once search returns the same
    docId; wired into `reload()` and `refreshAfterUpload()`. Viewing works by id (download bypasses search),
    so the row is fully functional, not a placeholder. Rows are session-local (gone on record switch / panel
    reopen until search recovers).
- Build note: instead of the stash trick, unblocked the agent-ng WIP compile by adding forward-declarations
  for the half-done Workday feature: `RecordContext.{employeeId,managerName,probing}` (optional) in
  `record-context.model.ts`, and `ContextDetectorService.lastTabUrl` + no-op `reprobe()` in
  `context-detector.service.ts`. Harmless for ServiceNow; the Workday feature will implement them properly.

## Files touched today
- `servicenow-solution-config.json` (defaultListQuery, filterClauses).
- UCEB repo (`Hyland.Experience.UCEB.Api/src`): `McpServer/Clients/UcebApiClient.cs` (name-field fallback,
  SendWithTransientRetryAsync, GetDocumentByIdAsync), `McpServer/Tools/UcebTools.cs` (WithTransientRetryAsync,
  get_document tool), `McpServer/Auth/InteractiveLoginService.cs` + `Configuration/UcebAuthOptions.cs` +
  `appsettings.json` (PersistSession), earlier Infra.Hx diagnostic reverted.
- `agent-ng/src/app/views/documents/documents.view.ts` + `.html`, `public/i18n/en.json` (metadata, refresh,
  doc-type rescope, columns, empty-state copy). NOTE: agent-ng WIP still doesn't compile (half-done Workday
  RecordContext fields) → build via the stash trick (stash WIP, re-apply fixes to HEAD, `npm run build`, restore).
- `bff/Program.cs` (EqualsCaseSensitive retry, base-type filter), `bff/Mcp/McpJsonRpc.cs` (documentName name),
  `bff/make-samples-servicenow.ps1` (NEW), `bff/switch-lob.ps1` (servicenow LOB from day 40).
