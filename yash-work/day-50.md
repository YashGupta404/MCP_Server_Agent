# Day 50 — Design: zero-setup capture (open LOB page -> upload -> AI picks doctype + fills metadata -> captured)

_Continues day-49 (mapper agent `solution_config_mapper_poc` end-to-end PASS, v4 prompt with queries). New goal
(2026-11-18): the customer only opens the LOB page, opens the extension and uploads a document. The AI detects
the LOB + business object, picks the best doctype from the live list, fills the metadata with confidence (using
the **Data Curation API** text) and captures — with NO manual solution-config work. Example: ServiceNow change
request -> OnBase doctypes created specifically for ServiceNow, on system OnBase9714_Integrations_Agent (default
config)._

## Feasibility verdict (evidence from earlier days)

**"No solution config at all" is NOT possible through UCEB** — the server enforces config on attach:
- day-07: upload rejected when the content type isn't mapped to that busObject ("prescription not configured for
  email") -> needs `additionalConfig` busObject->ecmContentTypeName.
- day-18: attach 400 `23401` "import fields are not configured in metadataFieldImportMappings" -> every
  attribute sent must be in the import mappings.
- day-40: `21404` "Default Configuration not found" -> a config must exist.

**"Zero MANUAL config" IS possible — just-in-time (JIT) config:** the AI decides doctype + fields, the BFF upserts
the minimal entry into the active system's (default) config, then uploads with the filled values. The customer
configures nothing; the admin step is automated. OnBase9714 with its default config works — the AI appends to it
the first time a doctype is used.

Existing building blocks:
- `solution_config_mapper_poc` (day-49) already returns doctype, record linkage, import mappings WITH sample
  values (= filled metadata), validated against the live catalog by `AgentMappingValidator`.
- MCP `add_business_object_config` is an UPSERT (day-07).
- MCP `upload_staged_file(additionalAttributesJson=...)` takes explicit attrs and skips the MCP-side config lookup.

## Agents — reuse, don't rebuild

- Agent 1 `context_extractor_poc` (unchanged): page -> businessContext (LOB, busObject, record id).
- Agent 2 `solution_config_mapper_poc` (reuse): already has the inputs the user listed — businessContext,
  lobPageContent, ecmCatalog (+ ecmQueries) and documentInsights. Change only what the document input contains:
  add the **Data Curation full text** next to (or instead of) the KE Context results, so field values come from
  the real text rather than generic NER entities.
- Optional later: a light `capture_filler` agent for repeat uploads of an already-configured doctype (only doctype
  + values; much smaller prompt than the 22.7k-char v4 -> faster per upload).

## Flow

First upload of a doctype for this LOB:
1. Detect context (Agent 1).
2. Data Curation: presign -> PUT -> poll `GET /status/{job_id}` until `Done` -> fetch `get_url` -> text.
3. Live catalog + queries (cache per active system; `list_document_types(fetchMetadata)` took ~36s).
4. Mapper -> doctype + record link + fields with values + confidence; BFF validates.
5. Panel shows the proposal (editable, low-confidence highlighted) -> user selects Upload.
6. BFF JIT-upserts the config entry -> `upload_staged_file` with the filled values -> captured.

Later uploads of the same doctype: config exists -> skip step 6's write; only doctype + values needed.

## Prerequisites / open items

1. **Data Curation contract** — known: `/presign` -> `job_id`, PUT upload, `GET /status/{job_id}` (`Done`/`Error`,
   results at `get_url`), option `json_schema` FULL|MDAST|PIPELINE. Unknown: full request/response schema ->
   export the Staging Data Curation Swagger JSON.
2. **Second external client** bound to `cin-data-curation-api` (Context client doesn't authorize it).
3. **Config-write permission** `CanWriteUCEBConfiguration` — fine for the PoC; production -> service identity.
4. **Experiment:** UCEB one-call capture `POST /{api}/core/documents` (multipart `captureData`; capability
   `MultipartFormCapture` AVAILABLE for CIC + OnBase/CFS). If it doesn't validate against the config, step 6's
   config write could be dropped. Unverified.
5. OnBase side: create the ServiceNow-specific doctypes + keywords (incl. a record-number keyword) on
   OnBase9714 so the catalog has something to match.

## Next

- Get the Data Curation Swagger + create the Data Curation client; add a `DataCurationClient` to the BFF.
- Feed Data Curation text into the mapper's document input; re-test with the ServiceNow change request.
- Add BFF `/api/capture/commit`: JIT config upsert -> `upload_staged_file`; panel "Upload" after review.
- Run the `/core/documents` capture experiment.

## Scope clarified by user (2026-11-18)

- **All LOBs**, not just ServiceNow: Salesforce, Workday and ServiceNow.
- Each LOB's solution config starts as a **bare default** (no custom busObject/doctype entries).
- **Upload only:** for each upload the AI first adds the needed entry to that solution config, then uses it to
  make the capture call. **Document listing is out of scope for now.**
- LOB-specific capture paths to handle:
  - Salesforce / ServiceNow: 3-step attach (`upload_staged_file`) -> config entry = `additionalConfig`
    (busObject -> doctype, contextParams record link, `metadataFieldImportMappings`).
  - Workday: single-POST `capture_document` (`/bow/core/documents`); config shape differs (`objectMappings`,
    `documentTypeGroupMappings`); OnBase via CFS token exchange (see `switch-lob.ps1` notes).

## Ordered steps

1. **USER TODO (pending — reminder kept by Copilot):** create the doctypes + metadata keywords in
   `OnBase9714_Integrations_Agent` for Salesforce, Workday and ServiceNow (each with a record-id keyword).
2. Export the Staging Data Curation Swagger JSON; create the `cin-data-curation-api` external client.
3. BFF `DataCurationClient` (presign -> PUT -> status poll -> fetch text).
4. Feed Data Curation text into `solution_config_mapper_poc` (doctype + fields + values + confidence).
5. BFF `/api/capture/commit`: JIT config upsert per LOB shape -> capture call (attach or Workday capture).
6. Panel: review proposal -> Upload. Test per LOB: ServiceNow, Salesforce, Workday.
7. Experiment: does `POST /core/documents` skip config validation? (could remove step 5's write)

## Decision (2026-11-18): LOB-generic, Workday dropped

User: drop Workday; build ONE LOB-generic flow — the AI builds the **capture slice** of the solution config for
any LOB, writes it into that LOB's default config, then captures. Verdict: approach is right, with fixes:

1. **Existing `add_business_object_config` is unsafe for this** (UcebApiClient.AddBusinessObjectConfigAsync):
   it upserts keyed on **busObject only** -> removes ALL entries for that busObject (adding `Invoices` for
   `account` deletes `COM - Application` for `account`), and writes an EMPTY `metadataFieldImportMappings`
   (an attach with explicit attrs would then fail `23401`). Need a NEW upsert keyed on
   **(busObject, ecmContentTypeName)** that writes the import mappings for the fields we send
   (record-id field `inputSource 1`, AI-filled fields `6`) and labels; check required doctype fields are filled.
2. **"Any LOB" = any LOB UCEB already knows** (token app key -> that LOB's default config; else `21404`, day-40).
   Known: hfs (Salesforce), hfsn (ServiceNow), hfgw-*, sap-hana, sufa. A brand-new site needs the LOB onboarded
   in UCEB (PoC workaround: host its entries in an existing LOB's config).
3. **Capture slice only:** busObject, doctype name/id, labels, import mappings. Skip contextParams/queries/
   displayColumns (listing out of scope).
4. **Config POST replaces the whole config:** read -> modify one entry -> back up the original -> write.
   Needs `CanWriteUCEBConfiguration`.

Generic capture path: `/api` route 3-step attach (`upload_staged_file` with `additionalAttributesJson`).

Revised steps:
1. **USER TODO (pending):** doctypes + keywords in `OnBase9714_Integrations_Agent` for Salesforce and
   ServiceNow (record-id keyword each). Workday dropped.
2. Data Curation Swagger + `cin-data-curation-api` client.
3. BFF `DataCurationClient`.
4. Data Curation text -> `solution_config_mapper_poc` -> doctype + fields + values + confidence.
5. NEW config upsert keyed on (busObject, doctype) with import mappings (+ backup).
6. `upload_staged_file` with the filled values; panel review -> Upload. Test Salesforce + ServiceNow.

## Data Curation API contract — VERIFIED from the official OpenAPI spec (2026-11-18)

Docs: `hyland.github.io/ContentIntelligence-Docs/KnowledgeEnrichment/reference/data-curation`. The spec is
public at `<base>/openapi.json`; saved as `ke-poc/datacuration-openapi.staging.json` (staging == prod, 69,896 B).
Staging base: `https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/data-curation`.
Auth: Bearer token (client credentials). Flow: `POST /presign` -> PUT file -> poll `GET /status/{job_id}` ->
download `get_url`.

- `POST /presign` body = `SubmitOptions` (all optional): `normalization` (obj|true), `pii` (obj like
  `{"mode":"redaction"}` | `false`), `chunking` (obj|bool), `embedding` (obj|bool),
  `json_schema` ("FULL"|"MDAST"|"PIPELINE"|false), `image` (obj), `image_embedding` (alpha).
  Response `PresignResponse`: `job_id`, `put_url`, `get_url`, `options` (required), `message`, and
  **`put_headers`** (headers the client MUST send with the PUT — differs from the Context API).
- `GET /status/{job_id}` -> `StatusResponse`: `jobId`, `status` (Processing/Done/Error/Deleted), `startedAt`,
  `updatedAt`, `fileType`, `fileSize`, `page_count`, `attempts`, `errorClassification`
  (permanent|transient), `errorMessage`. 404 = job not found.
- Other: `GET /models`, `/config/options*` (environment defaults/rules), `/health`.
- Limits: 5 GB per PUT; 600+ file types; text-layer docs extracted as-is, images OCR'd, audio/video transcribed.
- NOT in the spec: the format of the result file at `get_url` for each `json_schema` value -> verify with a
  real run. Planned options for our use: `pii:false` (don't redact names we need for metadata),
  `chunking:false`, `embedding:false`, try `json_schema:"FULL"` first.

## Step 1 DONE — Data Curation client created + verified (2026-11-18)

- External app `yash-ke-datacuration-client` (service user `yash_ke_for_hix_user`, scope
  `environment_authorization`, env Appintel-Staging, Application = Data Curation API).
- Creds in BFF user-secrets (`uceb-bff-9f2c1a7e`): `DataCuration:ClientId` (`sc-6e7...`), `DataCuration:ClientSecret`.
- Token test (secret never printed): `auth.staging.app.hyland.com/idp/connect/token` -> OK, expires_in 900.
  Claims: appkey `cin-data-curation-api`; roles `cin-data-curation.user`, `cin-data-curation.admin`,
  `cin-context-data-curation.user`, `cin-context-data-curation-content-lake.user`, `ece-ke.user`.

## Step 2 (next): first real Data Curation run
presign (pii:false, chunking:false, embedding:false, json_schema:"FULL") -> PUT with `put_headers` -> poll
status -> download `get_url`; inspect the result file to find where the full text lives.

## Step 2 DONE — first real Data Curation run (OCR) PASS

Script `ke-poc/dc-run.ps1 -File <file> [-JsonSchema FULL]` (reads creds from BFF user-secrets, secret never
printed). Ran on `samples/invoice-scan.png` (pixels-only image -> real OCR):
- presign -> `job_id=API_<guid>`, `put_headers={"Content-Type":"application/octet-stream"}`, options echoed
  `{normalization:{quotations:false,dashes:false}, pii:false, chunking:false, embedding:false,
  json_schema:"FULL", image_embedding:false}`.
- PUT 200. Status progression: `Wait For Upload` -> `Media Text Extraction` -> `Media Text Extraction Results`
  -> `Done`.
- Result (`samples/invoice-scan.datacuration.full.json`, 2.4 KB):
  - **`markdown.output`** = the full OCR text as markdown, line by line, each block preceded by
    `<!-- LOC: page, (x1,y1,x2,y2) -->` (page + bounding box). Labels and values stay paired
    ("Vendor: Acme Industrial Supply Co.", "Invoice #: INV-2026-004821", "Invoice Total: USD 2322.00").
  - **`json`** = a JSON *string* with file `metadata` (format, dimensions...) and a `pages` tree
    (page -> section -> column -> p -> l -> r{text}).
- Conclusion: feed **`markdown.output`** to the mapper (much better than KE Context NER: every label:value pair
  is preserved). Optionally strip the `<!-- LOC -->` comments and `![](page_1.png)` to save tokens; keep them if
  we want to cite where a value came from.

## Ordered steps (current)

1. DONE — Data Curation client + token.
2. DONE — first Data Curation run; text = `markdown.output`.
3. BFF `DataCurationClient` (presign -> PUT with put_headers -> poll -> download -> `markdown.output`).
4. Send that text to `solution_config_mapper_poc` (doctype + fields + values + confidence).
5. **User step — create the OnBase doctypes + keywords** in `OnBase9714_Integrations_Agent` (how-to below).
6. NEW config upsert keyed on (busObject, doctype) with import mappings (+ backup).
7. `upload_staged_file` with the filled values; panel review -> Upload. Test ServiceNow + Salesforce.
8. Experiment: does `POST /core/documents` skip config validation?

### How-to for step 5 (OnBase Configuration module, on the OnBase9714 environment)
1. **Keyword types** (Keywords -> Keyword Types -> Create): one per field. For ServiceNow change requests, e.g.
   `SN Record Number` (alphanumeric, ~40 chars — holds the record id like CHG0012345), `SN Short Description`,
   `SN Change State`, `SN Priority`, `SN Requested By`, `SN Planned Start Date` (date).
2. **Document type** (Document -> Document Types -> Create): e.g. `SN - Change Request`; assign it to a document
   type group the integration exposes (existing Salesforce types use group `116`); add the keyword types above.
3. **Rights**: give the user group the integration signs in with access to the new document type (Users ->
   User Groups / Rights -> Document Types) and the keyword types.
4. **Salesforce**: the system already has 14 types incl. `Invoices` (205) with record-id keyword `223
   hfs_sObject_Id`; reuse them, or add new SF types the same way with a record-id keyword.
5. **Verify**: `mcp-admin.ps1 -Tool list_document_types -ArgsJson '{"fetchMetadata":true}'` shows the new type
   with its keywords.

## Step 3 DONE — BFF Data Curation client (2026-11-18)

- `bff/Ke/DataCurationClient.cs`: `DataCurationOptions` ("DataCuration" section; id/secret in user-secrets),
  `DataCurationClient.ExtractAsync(bytes)` = token -> `POST /presign` (pii:false, chunking:false,
  embedding:false, json_schema FULL) -> PUT with `put_headers` -> poll `/status/{job_id}` every 2s (Done/Error/
  Deleted, 300s timeout) -> download `get_url` -> `markdown.output`. `CleanMarkdown` strips `<!-- LOC -->`
  comments, image placeholders and markdown escapes. Returns `{JobId, Status, FileType, PageCount, Markdown, Text}`.
- `bff/appsettings.json`: `DataCuration` section (TokenEndpoint, Scope, BaseUrl, JsonSchema, TimeoutSeconds).
- `bff/Program.cs`: options registered; new test endpoint `POST /api/datacuration/extract` (X-BFF-Session,
  body `{attachments:[{name,mime,dataBase64}]}`) -> `{files:[{name,jobId,status,text,markdown}]}`.
- BFF build: 0 warnings, 0 errors.
- Tested without a BFF session: `ke-poc` links the same file (`<Compile Include="..\bff\Ke\DataCurationClient.cs">`)
  and has `dotnet run -- dc <file>` (reads the BFF user-secrets). `samples/invoice-scan.png` -> status Done,
  clean text: INVOICE / Vendor: Acme Industrial Supply Co. / Invoice #: INV-2026-004821 / Invoice Date /
  Due Date / Bill To / Subtotal / Tax (8%) / Invoice Total: USD 2322.00. (`fileType`/`page_count` came back
  empty in the status response for this PNG — optional fields.)

### Found: `/api/autoconfig/agent-map` is missing from the source
The plugin's Auto-map card (`ecm-picker.view.ts`) still calls `POST /api/autoconfig/agent-map` (day-49 Step 5/6),
but `bff/Program.cs` (last modified 2026-11-18 11:00) no longer contains it, and the last compiled
`UcebBff.dll` (2026-11-17 13:34) doesn't either. `Ke/AgentMappingValidator.cs` and `Ke/EcmQueryCatalog.cs` still
exist but are unreferenced. Not recreated without the user's go-ahead (may have been undone on purpose).

## Step 4 — `/api/autoconfig/agent-map` REBUILT with Data Curation text (2026-11-18)

User: "rebuild it if required". Rebuilt in `bff/Program.cs` from the day-49 notes + surviving helpers:
- `MappingAgentOptions` ("MappingAgent" section, agentId `028b4d65-...`) registered + resolved again.
- `POST /api/autoconfig/agent-map` (body `AgentMapRequest {attachments, businessContext, pageContent}` — the
  shape the plugin's Auto-map card already sends):
  1. session + token refresh (same as `/api/context/extract`);
  2. in parallel: `EcmDoctypeCatalog.FetchAsync` (full catalog, falls back to per-doctype metadata on 408),
     `EcmQueryCatalog.FetchAsync`, and `DataCurationClient.ExtractAsync` per file;
  3. KE Context per file (optional extra signal; classes = live doctype names; failure -> text only);
  4. `documentInsights` = `[{fileName, documentText (Data Curation), classification, summary, entities}]`;
  5. invoke `solution_config_mapper_poc` (`invoke-task`, inputs `businessContext, lobPageContent (<=12k),
     ecmCatalog [{id,name,attributes[{id,name,dataType}]}], documentInsights, ecmQueries`), 180s timeout;
  6. `ExtractNamedOutcome(body, "solutionMapping")` (new helper) -> `AgentMappingValidator.Validate` ->
     `{ mappings, validation:{valid, issues}, inputsSent{businessContext, ecmCatalog, documentInsights, ecmQueries} }`.
- BFF build: 0 warnings, 0 errors. Plugin unchanged (already calls this endpoint with this body).
- Stack started: MCP `switch-lob.ps1 -Lob salesforce-staging` (silent sign-in, app key `hfs`, route `api`),
  active system set to `OnBase9714_Integrations_Agent`; BFF `dotnet run --no-build` on :5010.
- Next: end-to-end test in the plugin (needs the user's sign-in).

## OnBase9714 Salesforce solution config — backed up; reset BLOCKED by permission (2026-11-18)

Found: `OnBase9714_Integrations_Agent` under the Salesforce LOB (`hfs`) holds a FULL hand-built config, not a
default: 12 `additionalConfig` entries (account/opportunity/campaign -> COM - Application 206 + Invoices 205,
plus CIC leftovers prescription / dev-test-account / bills-content-type / case-content-type), 5 `queryConfig`
entries, record link Id->223 / CreatedById->224. A zero-setup test there would prove nothing (account->Invoices
already configured). User: back it up, reset it, restore it later on request.

- **Backup** (`bff/config-backups/`): `onbase9714-salesforce-hfs-20261118-161801.full.json` (GET response,
  61,797 B) and `...-161801.data.json` (exact `data` node via Node, 25,866 B; verified 12 / 5 / 2 groups).
- **Reset body** = UCEB's shipped `default.uceb.hfs.v1.json` shape (operationConfig + contextParams +
  endUserGroupConfig; no additionalConfig/queryConfig), built from the backup so it keeps the OnBase ids
  223/224: `config-backups/onbase9714-salesforce-hfs-reset.data.json`.
- **Reset attempt 1:** MCP "could not be converted to System.String" — PS 5.1 `Get-Content -Raw` string carries
  NoteProperties and serializes as an object. Fix: `[string][IO.File]::ReadAllText(...)`.
- **Reset attempt 2:** UCEB **403 Access denied**. Token claims (MCP user, client `wsc-f3ec0fd9...`, appkey
  `hfs`): roles `uceb.hfs_user`, `uceb.onbase_system_admin`; permissions `uceb.configuration.read`,
  `uceb.hfs_configuration.read`, `uceb.onbase_configuration.write` (system config), document capture/delete/
  preview/query. **No Salesforce solution-config write.** Live config verified unchanged (12/5).
- Implication for the whole plan: the JIT config write (step 6) needs the same permission -> the signed-in user
  must be a Salesforce Solution Administrator (or a service identity with config write).
- Restore/reset commands recorded in repo memory `solution-config-backups.md`.

### RESET APPLIED (2026-11-18)
- User added themselves to **Salesforce Solution Administrators**. An MCP restart (silent refresh) still returned
  the OLD roles; a full `logout` + interactive `login` was needed. New token: roles `uceb.hfs_admin`,
  `uceb.hfs_user`, `uceb.onbase_system_admin`; permissions now include `uceb.configuration.write` and
  `uceb.hfs_configuration.write`.
- MCP restarts on the default system -> re-selected `OnBase9714_Integrations_Agent`, then
  `set_solution_configuration` with the reset body -> "Solution configuration saved".
- Verified live: **additionalConfig = 0, queryConfig = 0**, contextParams 223 / 224, 2 user groups. Saved as
  `bff/soln-onbase9714.after-reset.json`.
- Restore on request from `config-backups/onbase9714-salesforce-hfs-20261118-161801.data.json`.
- Side effect to remember: Salesforce document listing / queries on OnBase9714 won't work until restore (out of
  scope for now). The config-write permission needed by the JIT step is now present for this user.

## JIT capture PROVEN on the empty (default) config — documentId 417 (2026-11-18)

User rules recorded: reset EACH new LOB's config to its default before testing; PoC = capture only.

Test script `bff/jit-capture-test.ps1` (simulates the mapper output, no plugin needed): read config -> upsert ONE
capture-only entry keyed on (busObject, doctype) with Node (lossless) -> `set_solution_configuration` -> stage
the file on the MCP (`/staging/upload`) -> `upload_staged_file` with explicit attributes. Entry used:
`account -> Invoices (205)`, record field `223` (inputSource 1, value "Id"), AI fields 228/229/227/226
(inputSource 6) with values from the Data Curation text.

Findings (each run):
1. Entry without `displayColumns` -> config POST 400 **"The DisplayColumns field is required."** (required even
   for capture-only). Upload then failed 422 **`3422 "No matching business object configuration found for
   account"`** — confirms the config entry is mandatory for capture.
2. With `displayColumns` but `ecmContentTypeGroupId: null` -> config saved, upload 400
   **"ArchiveDocument: An invalid operation occurred."** Source: `Infra.CFS/.../DocumentArchiveServiceCFS.cs:387`
   throws "ContentTypeGroupId must be provided" (wrapped by HandleServiceException). OnBase/CFS needs the
   doctype's group id.
3. With `ecmContentTypeGroupId: "116"` -> config saved, **upload OK: "Uploaded 'invoice-scan.png' ... attached it
   to '001gK00001KbuonQAB'. documentId: 417"** (OnBase). `get_document 417` 404s only because that tool reads the
   CIC content store, not OnBase — expected.
- Values accepted: 226 Invoice Date `11/10/2026`, 227 Invoice Total `2322.00`, 228/229 text.
- Config reset back to default after the test (verified additionalConfig=0, queryConfig=0).

**Minimum capture-slice entry (validated):** `busObject, ecmContentTypeName, ecmContentTypeId,
ecmContentTypeGroupId (required for OnBase/CFS), contextParams [], ecmMetadataFieldLabels, displayColumns
(required), metadataFieldImportMappings` (record field inputSource 1 + AI fields inputSource 6).

Open item: `list_document_types` doesn't return each doctype's group. `list_document_type_groups` gives the 6 groups
(116 Vendor Documents, 117 Commercial Lending Documents, 118 Network Documents, 119 Patient Records New, 111 Schedule,
102 Vendors) but not membership -> need a doctype->group lookup (e.g. list doctypes per group) for the generic flow.

## Next step
Turn the proven recipe into BFF `POST /api/capture/commit`: input = agent mapping (doctype id/name, record field,
fields+values) + file + record -> resolve the doctype's group -> JIT upsert (busObject, doctype) -> stage ->
`upload_staged_file` -> return documentId. Then panel: review -> Upload.

## Step 6 — `POST /api/capture/commit` BUILT (2026-11-18)

**Doctype -> group lookup (MCP change):** UCEB's `GET /api/core/document-types` already accepts `doctypeGroupId`
(`DocumentTypeMetadataController.GetAllDocumentTypes`, `DoctypeGroupIdToValidate`), but the MCP tool never passed
it. Added optional `doctypeGroupId` to `UcebApiClient.GetDocumentTypesAsync` and the `list_document_types` tool
(non-Workday only; Workday keeps its own group param). Verified: group `116` -> `205 Invoices`.

**BFF** (`bff/Ke/CaptureConfigWriter.cs`, new):
- `CaptureConfigWriter.Upsert(getResponse, spec)` — replaces only the (busObject, doctype id/name) entry, keeps all
  others; `BuildEntry` = the validated minimum entry (group id, labels, displayColumns, import mappings: record
  field inputSource 1 "Id" + fields inputSource 6 with the attribute's dataType).
- `DoctypeGroupResolver.ResolveAsync` — `list_document_type_groups`, then `list_document_types(doctypeGroupId)`
  per group; caches doctype -> group per system.

**BFF endpoint** `POST /api/capture/commit` (X-BFF-Session), body `CaptureCommitRequest {attachments,
businessObjectType, businessObjectId, documentTypeId, recordFieldId, fields:[{id, value}]}`:
1. `get_document_type_metadata` -> real doctype name + attributes; fields not on the doctype are skipped
   (returned in `skippedFields`); empty values dropped.
2. Resolve the group id.
3. `get_solution_configurations` -> save it to `bff/config-backups/jit/<system>-<utc>.json` -> upsert ->
   `set_solution_configuration` (must return "saved").
4. Stage the file on the MCP -> `upload_staged_file` with `[{recordFieldId: businessObjectId}, ...fields]`.
5. Returns `{documentId, system, documentType{id,name,groupId}, recordField, fields, skippedFields, configEntry,
   uploadResult}`.
- Builds: BFF 0 errors; MCP 0 errors. Both restarted (MCP silent sign-in still has `uceb.hfs_configuration.write`;
  active system OnBase9714; config currently the default — 0 entries).
- Test request: `bff/bruno/capture-commit.bru` (needs a plugin session id).

## Step 7 — Capture from the Chrome extension (no Bruno) — BUILT (2026-11-18)
User: "not in bruno do it in chrome extension only".
- `ecm-picker.view.ts/.html`: after **Generate mapping**, the Auto-map card shows a review —
  record (type + id from the context agent), document type (+ confidence), record field, and an editable input per
  mapped field (prefilled with the agent's `sampleValue`) — plus an **Upload** button -> `uploadMapped()` ->
  `POST /api/capture/commit` (first selected file). Shows "Captured as document N (doctype, group G)".
  Raw mapping/capture JSON moved under collapsible "Mapping details" / "Capture details".
- `ecm-picker.module.ts`: added `FormsModule` (ngModel on the field inputs).
- `npm run build` OK -> `browser-extension/ng`. BFF + MCP still running (no restart needed).

### Step 8 (next) — test it in Chrome
1. `chrome://extensions` -> Hyland Integrations Agent -> reload.
2. Open the Salesforce Account page (Health Systems, `001gK00001KbuonQAB`), open the side panel, sign in.
3. On the system picker, in **Auto-map (PoC)**: choose `ke-poc/samples/invoice-scan.png` -> **Generate mapping**.
4. Check the review (doctype should be an Invoice type, group 116), fix any field value -> **Upload**.
5. Expect "Captured as document N". Then reset the OnBase9714 Salesforce config to default.

### Step 8 — first Chrome run: Generate mapping failed `upload failed (502) Bad Gateway`
- Source: Data Curation PUT to its presigned storage URL (`DataCurationClient.ExtractAsync`), not UCEB.
- Re-ran `ke-poc/dc-run.ps1 samples/invoice-scan.png` straight after -> PUT 200, Done. So a transient gateway error.
- Fix: the PUT now retries 5xx up to 3 times (2s/4s backoff). BFF rebuilt (0 errors) + restarted.

### Step 8 — second Chrome run: HTTP 500 — root cause from the BFF log
- Data Curation PUT 200, KE Context 200, mapper agent `invoke-task` 200 (~21s). Then the BFF crashed:
  `InvalidOperationException: The node must be of type 'JsonObject'` at `AgentMappingValidator.Validate` line 61
  (`linkage?["boContext"]?["ecmAttributeId"]`). The agent returned `recordLinkageMapping` (or its `boContext`) as
  a non-object (string), and `JsonNode[string]` on a `JsonValue` throws -> unhandled -> HTTP 500.
- Fix: validator `AsObject()` — accepts objects, parses string-encoded JSON, empty -> null, anything else ->
  reported in `validation.issues` (no crash). Applied to recordLinkageMapping (+boContext/boUser), queryMapping,
  doctypeMapping.
- Every raw agent response is now saved to `bff/logs/agent-map/<utc>.json` (git-ignored) so the exact shape can be
  read after each run. BFF rebuilt (0 errors) + restarted.

### Step 8 — third Chrome run: mapping PASS, but "No record was detected on this page"
- Mapping OK: Invoices (205), confidence 0.82, record field 223 hfs_sObject_Id, fields 228/229/227/226/234 with
  values from the scan (Acme Industrial Supply Co., INV-2026-004821, 2322.00, 2026-11-10, USD).
- Root cause: Agent Builder returns a Task outcome wrapped by name — the saved mapper response's output_text
  starts `{"solutionMapping":{...}}`; the context agent's is `{"businessContext":{...}}`.
  `ExtractTaskOutcome` returned that wrapper as-is, so the panel's `ctx.businessObjectId` was undefined -> empty
  record id -> the panel blocked Upload. (The mapper still saw the id because it read inside the wrapper.)
- Fix: `ExtractTaskOutcome` unwraps `businessContext` from the output text. Only caller: `/api/context/extract`.
  BFF rebuilt (0 errors) + restarted.

### Step 8 — fourth Chrome run: Upload 500 "DocumentArchiveService: An unexpected error occurred at the broker"
- Config entry written correctly (account -> Invoices 205, group 116, fields 228/229/227/226/234).
- Difference vs the doc-417 success: the agent also mapped **234 `currency_type` (dataType Currency) = "USD"**.
- Reproduced with `jit-capture-test.ps1` (no panel), one variable at a time:
  | Run | Values | Result |
  |---|---|---|
  | B | doc-417 values + 234=`USD` | **500 broker error** (same as panel) |
  | A | doc-417 values (no 234) | OK, doc 418 |
  | C | panel values 227=`USD 2322.00`, 226=`2026-11-10` (String types), no 234 | OK, doc 421 |
  | D | doc-417 values + 234=`2322.00` | OK, doc 422 |
- Root cause: OnBase **Currency** keywords accept only a number; text ("USD") breaks the archive at the broker.
  String keywords accept any text. (419/420 were extra uploads from re-runs of C.)
- Fix: `CaptureConfigWriter.CoerceValue` — for numeric keyword types (Currency, Integer, Numeric...) send only the
  number ("USD 2,322.00" -> "2322.00"); no number -> skip the field and report it in `skippedFields`
  (e.g. "234 (Currency: 'USD' is not a number)"). String/Date untouched. Panel shows skipped fields.
- Test docs created in OnBase for account 001gK00001KbuonQAB: 418–422.

### Step 8 — fifth Chrome run: 502 "model's response was cut off ... max_tokens limit"
- BFF log: Data Curation + KE OK; the **mapper agent** `invoke-task` returned 502 (the context agent was fine).
- Cause: the mapper's OUTPUT exceeds the agent's `inferenceConfig.maxTokens` (an agent setting in Agent Builder,
  not sent by the BFF). Measured the 3 saved responses: 10.1k–11.6k chars, varying per run. ~4k chars (35%) are
  the 4 debug echo fields `receivedBusinessContext / receivedDocumentInsights / receivedEcmCatalogSummary /
  receivedEcmQueriesSummary` (just restating the inputs; nothing uses them). `unmapped` ~1.9k, `rationale` ~1k.
- The DOM is not involved here: the mapper gets page TEXT only; `pageHtml` goes to the context agent.
- USER STEP (Studio, `solution_config_mapper_poc`): raise **Max tokens** (e.g. 8192) and save. The 4 `received*`
  fields are REQUIRED by the user — keep them. `versions/latest` picks it up. Fallback if no Max tokens setting:
  prompt-level brevity (one-sentence `reason`s, short `unmapped`/`rationale`), keeping every field.

### Mentor feedback: send HTML only (no CSS/scripts) to the context agent
- Capture already removed script/style/link/meta/svg, inline `style=` and `on*` handlers.
- Now also: body only (no `<head>`), `class` attributes removed (CSS class names, the bulk on Lightning pages),
  whitespace between tags collapsed. Kept id/name/aria-*/data-*/role/href for the recipe selectors (no code
  replays class-based selectors). File: `agent-ng/src/app/services/context-detector.service.ts`. Build OK.
