# Day 42 — ServiceNow demo stabilization (outage + UI fixes) → IDP "automate" process rework (HXSF-881)

_Continues day-41. First half = the unrecorded Nov 9–11 work: a platform-wide staging search outage, a
demo-only optimistic insert (added then REVERTED when search recovered), and a batch of panel UI fixes.
Second half (ongoing) = RESUMING a paused task: move IDP off the direct REST APIs onto Hyland's "automate"
process (HXSF-881). The design was worked out on 2026-10-30 and paused on a content-upload blocker. This log
grows as that work proceeds._

Demo stack (unchanged): local UCEB :5000 + `switch-lob.ps1 -Lob servicenow` (MCP :5200, silent saved session)
+ BFF :5010 + devtunnels `giant-ant-2f6br43` (agent→MCP) and `joyful-horse-g88zxrw` (IDP cloud→BFF) +
`idp-refresh.ps1`. MCP session now persists (day-41) so restarts sign in without a browser.

---

## A. Staging CIC/CIC search outage (2026-11-09) — NOT our code

- Symptom: upload shows success, but the new doc never appears in the panel list OR the Studio content
  browser; only older docs show. `/api/query` also 503s intermittently → random upload/list failures.
- Exact error (local UCEB log): `HttpRequestExecutor 'Unable to execute GetPlatformSystemConfigurations on
  resource https://appintel-staging-test.content.staging.app.hyland.com/api/query using POST: Status code -
  ServiceUnavailable'` → `PlatformSystemConfigService 'Friendly name 'CIC' did not match any accessible
  system configuration.'` → upload complete returns 400 `{"title":"File type is not supported."}` (a
  MISLABEL — the config load failed, not the file type; png is allowed).
- The failing call is the **content platform (CIC) `/api/query`** at `*.content.staging.app.hyland.com`,
  which local UCEB (:5000) CALLS to (a) resolve the CIC/solution config on every call and (b) list
  documents. One 503 breaks both. UCEB is healthy; it only relays the 503.
- `CIC` vs `cic` is a RED HERRING: resolution is case-insensitive; `query_documents?systemFriendlyName=CIC`
  returned 200/7 docs many times. "Did not match" only fires AFTER the 503 (can't enumerate configs when the
  service returns nothing).
- PROOF it's an index problem, not our upload: added a diagnostic `get_document` MCP tool
  (`UcebApiClient.GetDocumentByIdAsync` → GET `{contentHost}/api/documents/{id}`, direct to the content
  platform, bypassing search). Compared an indexed doc (`invoice-acme`, sys_created 2026-11-08 12:09 UTC) vs
  a doc uploaded during the outage (`6c11b042`, 2026-11-09 06:50 UTC): IDENTICAL — same
  `sys_parentPath /hfsn-documents/CHG0040019`, same `sys_parentId`, same `sys_primaryType change-request-type`,
  same `sys_mixinTypes [SysFilish, hfsn-lob-mixin]`, `hfsn_recordNumber=CHG0040019`; both download by id. Only
  difference = `sys_created`. So uploads store & file correctly; the SEARCH INDEX just stopped ingesting after
  Nov 8 12:09 UTC. The panel and the Studio content browser both read that index → both blind to new docs.
- ROOT CAUSE confirmed by other teams in Teams (Aura Team): `503 {"message":"OpenSearch is unavailable
  (circuit breaker open)"}`. CIC `/api/query` is OpenSearch-backed. Platform-wide (hit appintel-staging-test,
  healthcare-automation-dev, and others). Escalated to Cloud Content Back End; they reindexed.
- RECOVERY (2026-11-09 ~15:51 IST): a fresh upload appeared in search within seconds (count 7→8). Later a full
  reindex backfilled the outage docs (count → 17/18). Search healthy again.
- Hardening KEPT (harmless, helps future 503 bursts): MCP `UcebTools.AttachContentAsync`
  `WithTransientRetryAsync` (retries initiate+complete fresh-session + attach on transient config failures,
  6 attempts) and `UcebApiClient.SendWithTransientRetryAsync` (5 attempts, wraps ListDocuments/QueryDocuments/
  GetSolutionConfigurations on 503 / 22404 / 21404 / "Unknown type"). `get_document` tool kept (read-only).

## B. Optimistic insert — ADDED for the demo, then REVERTED (user instruction)

- While search was down, added a demo-only "optimistic insert": after a successful upload the panel showed the
  file in the list immediately from the upload's returned documentId (bypassing search; viewing works by id).
  BFF `/api/upload` returned `uploadedDocuments:[{name,documentId}]`; `document.service.ts` + `documents.view.ts`
  kept `optimisticDocs` merged across reloads until search returned the same docId.
- User's standing instruction: revert once search is fixed (HXPDOC notes also say not to document it). On
  2026-11-09, after confirming the index recovered (idxcheck marker indexed in seconds), REVERTED all of it:
  removed `optimisticDocs/optimisticRecordId/mergeOptimistic` + upload collection + merge calls (panel),
  `uploadedDocuments` (service + BFF). Back to the clean search-driven list.

## C. Post-upload list refresh race — FIXED (2026-11-10)

- After the revert the list didn't update after an upload. Cause: `refreshAfterUpload` called `reload()`, which
  uses a `loadSeq` token; the ServiceNow page's constant DOM churn fires competing reloads that supersede the
  post-upload one, so its update was dropped. It also only applied the fetch when the count GREW.
- Fix: `refreshAfterUpload` now fetches `listForContext` directly and ALWAYS applies the fresh result (zone.run
  + detectChanges), independent of `loadSeq`, polling 1.5/2.5/4/6/8s until the list holds the expected count.
  New document appears on its own within a few seconds.

## D. Query results overflow the panel + Talk-with-agent didn't show the doc list — FIXED (2026-11-11)

- Table overflow: the documents/query table (6 columns) pushed the whole narrow side panel wide. Fix: wrapped
  `<table class="docs__table">` in `<div class="docs__tableWrap">` with `overflow-x:auto` + table
  `min-width:max-content`, so wide results scroll WITHIN the table area, not the panel.
- Chat doc list: "Show my documents" replied "Here are N documents…" with no rows. Root cause = a RENDER bug:
  the agent DOES return `[[doc:<docId>|<name>]]` markers and `extractChatDocs` parses them into `turn.docs`,
  but the chat message template never rendered `t.docs`. Fix: added a `docs__msgDocs` block of clickable
  buttons per doc (`openChatDoc(d)` → opens in the in-panel viewer) + SCSS. Now the chat lists each document
  as an "Open" row.

## E. Build note

- agent-ng WIP previously didn't compile (half-done Workday RecordContext fields) → forced the stash-build
  dance. Fixed by adding forward-declarations so it compiles directly: `RecordContext.{employeeId,managerName,
  probing}` (optional) in `record-context.model.ts`; `ContextDetectorService.lastTabUrl` + no-op `reprobe()` in
  `context-detector.service.ts`. Harmless for ServiceNow; Workday will implement them properly. Build =
  `cd agent-ng; npm run build` (→ browser-extension/ng); reload the extension after.

---

## F. IDP "automate" process rework (HXSF-881) — NEW TASK (in progress)

GOAL: move IDP classification/extraction OFF the direct REST APIs (interim) and onto Hyland's **"automate"**
process (Activiti/BPMN workflow engine, `org.activiti.cloud` runtime). This is RESUMED work — the full design
was worked out on **2026-10-30** (prior chat session `02153cdd`) and then PAUSED. Not net-new.

CURRENT (interim) path = DIRECT IDP REST API, NOT automate:
- BFF `bff/Idp/` (`IdpEndpoints.cs`, `IdpClient.cs`): `/api/idp/classify` stages the file to a public devtunnel
  URL, then calls IDP cloud REST directly (`api.idp.staging.app.hyland.com` — classification job → poll →
  recognition/OCR → extraction job → poll), with its own IDP OAuth token (`Idp:ClientId`, scopes
  `hxp.integrations hxpr hxps`). IDP fetches the file from the public `sourceUrl`.

AGREED DESIGN (Option B, lean — from 2026-10-30):
- The BPMN/Automate process's ONLY job = return **classification (doc type) + metadata** per document. It does
  NO capture and never touches OnBase/CIC storage. Human review stays in the plugin's pre-filled grid; the
  existing **Upload** button does the real capture (unchanged `/api/capture` / `upload_staged_file` path).
- BPMN is tiny: Start (accepts `contents[]`, returns `batchState`) → IDP Classification node → IDP Extraction
  node → End (returns `batchState`). Built in Studio Modeller.

AUTOMATE TRIGGER MECHANICS (from the knowledge dump, turn 288):
- Auth = OAuth 2.0 **client-credentials**: `POST https://auth.iam.experience.hyland.com/idp/connect/token`,
  `grant_type=urn:hyland:params:oauth:grant-type:api-credentials`, Basic `base64(appId:apiKeySecret)`.
- Files via **content references, NOT multipart**: upload binary to HXP Content Services → get `sys_id` → pass
  as the `contents` process variable `[{ sys_id }]`.
- Start a process instance: `POST https://{STUDIO_ACCOUNTKEY}.studio.experience.hyland.com/{appName-env8}/rb/v1/
  process-instances` body `{payloadType:"StartProcessPayload", processDefinitionKey, name, variables:{contents}}`.
- Poll `query/v1/.../{id}` until COMPLETED, then `GET .../{id}/variables` → `batchState` (the classify+extract
  result). IDP caps **500 docs / 1000 pages / 500MB per run** → BFF must CHUNK large drops into ≤500-doc batches.

WHY IT WAS PAUSED (the blocker, turn 355):
- To pass `contents:[{sys_id}]` we must first upload the file to HXP Content Services to get a `sys_id`. The
  tried endpoint `POST api.platform.staging.app.hyland.com/api/upload` **rejected the Bearer token** (wants AWS
  SigV4 → `403 IncompleteSignatureException`). The real Bearer-auth content-upload base was unknown → paused.
- Workaround that unblocked the interim demo = Option A: give IDP a **public HTTPS GET URL** as `sourceUrl`
  (GitHub raw / our devtunnel), no HXPR upload needed. That's what the current REST path uses.

TO RESUME — open items:
- [ ] Is the Automate BPMN process (classify→extract→batchState) actually BUILT + deployed in Studio Modeller?
      (Need the `processDefinitionKey` + the `{appName-env8}` + `{STUDIO_ACCOUNTKEY}` for the rb/v1 URL.)
- [ ] Resolve the content-upload endpoint for the `sys_id` — OR reuse what we already have: our MCP/UCEB upload
      already stores a file and returns a documentId (sys_id). Could stage to a temp folder (e.g.
      `/automate/tmp_testing`) to get a sys_id for IDP, classify, then capture properly. Evaluate vs. the
      public-URL approach.
- [ ] Client-credentials automate app (appId + API key secret) + scopes for `rb/v1/process-instances`.
- [ ] Build BFF: new path behind the SAME panel contract (`/api/idp/classify` →
      `{results:[{name,docType,confidence,fields,uploadable}]}`) so the UI doesn't change — get token, upload
      files → sys_ids, chunk ≤500, start process per batch, poll, GET variables, map `className→docType` +
      `fields→attributes`, merge. Keep the interim REST path as fallback until proven.

(Findings + build steps appended below as the work proceeds.)

