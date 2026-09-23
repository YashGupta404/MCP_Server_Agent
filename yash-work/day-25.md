# Day 25 — 2026-09-17

_Focus: **research only** (no code changed). Mentor + meeting feedback: the plugin's search and document-naming
model is wrong. Researched the real UCEB code + the native Hyland-for-Salesforce (HFS) UI to understand how
**listing, search/queries, keyword validation, and capture** actually work, so it can be implemented correctly._

---

## 0. The correction (mentor + meeting)
- The plugin's **client-side search** (`popup.js` `filterDocs` over already-loaded rows) is **NOT** how search
  works in HFS / HFW. Real search = **server-side query on keyword/metadata fields**.
- The **filename approach is wrong**: we should **NOT** store the upload filename in a keyword (the `229` +
  "Document" relabel hack). The **documentName** is a **generated pattern** from metadata/keywords.
- Mentor screenshots = the **native HFS UI** the plugin must mirror (list columns, Queries/Add-Query, keyword
  inputs with validation, capture metadata). Also earlier HFW "Matching documents" search dialog.

---

## 1. documentName is a GENERATED pattern, not the upload filename
- **CIC/Hx** (`DocumentArchiveService.cs:424`, `MultipartFormCaptureService.cs:197/460`,
  `ConfigRepoService.cs:901/991/1058/1131`):
  ```
  sys_title = {fileName}-{documentType}-{yyyy_MM_dd}
  ```
- **OnBase/CFS**: OnBase generates its **own** name `"<documentType> - <date>"` (e.g. `PatientReport - 4/15/2026`),
  returned by the CFS adapter as `document.Name`.
- In the native HFS **Capture** modal, the user **types `hfs_Name`** (a required field) — that **is** the
  document name. Name = **metadata**, not the raw filename.
- => Our "store filename in keyword 229 + relabel it Document" is the wrong model. Rely on the platform
  documentName / the entered name field.

## 2. Search is a SERVER-SIDE metadata query
Verified in `DocumentQueryService.ExecuteQuery`:
- `POST api/core/queries/{queryId}/execute`, body `inputs:[{ id, operator, value }]`.
- Each input → `SingleValuedMetadataQuery { ComparedMetadataProperty.Id, SearchTerm, QueryOperator }`.
- Validated: every `input.id` must be in the query's configured `filterClauses`. Adapter (CFS/Hx) runs it
  against the content platform's native search. Model = `DocumentMetadataSearchQuery { SingleValuedMetadataQueries[],
  DocumentType, CreationDateRange, MaxResults }`.
- Even "list a record's docs" is a query filtered on the **businessObjectId** field
  (`ExecuteDefaultListQueries` / `BuildBusinessObjectFilters` → `searchConfig.BusinessObjectIdMetadataId`).

## 3. The Query model (drives Queries / Add-Query / validation)
- `GET api/core/queries` → **GetAllQueries** → `QueryListResult { queries:[QueryInfo{ id,name,type,maxResults }] }`.
- `GET api/core/queries/{id}/metadata` → **GetQueryMetadata** → `QueryMetadataResult { id,name,type,maxResults,
  inputs:[QueryInput], resultColumns:[QueryResultColumn] }`.
- **`QueryInput`** = a searchable keyword field: `{ id, name, dataType, required, minLength, maxLength, minValue,
  maxValue, supportedOperators[] }` ← this is exactly the validation source ("Your entry is too short" = minLength;
  required `*`; operators = supportedOperators).
- **`QueryResultColumn`** = a display column: `{ id, name, dataType }`.
- `POST .../execute` body = `QueryExecutionRequest { businessObjectType, resultSetLimits, inputs:[{id,operator,value}] }`.

## 4. Capture metadata model (drives the Capture form)
- `RelatedDocumentTypeAttributeTypeDTO` = a content type's field: `{ DataType, IsRequired, MinLength, MaxLength,
  ... }` (+ MultiValue via ModelMapper). Same constraint set as QueryInput → same validation.
- Native Capture modal shows Content Type dropdown + the type's Properties (required `*`, typed, validated), incl
  `hfs_Name` = the document name the user enters.

## 5. List vs Search (two paths, both metadata queries)
- **Record listing** = `RelatedDocumentsController.GetRelatedDocuments` (`business-objects/{id}/documents`) —
  default list, filtered on the record's boId field.
- **Search** = `DocumentQueryController.ExecuteQuery` (`queries/{id}/execute`) — arbitrary keyword inputs.

## 6. The native HFS UI behavior to mirror (from screenshots)
- **List** shows the query's `resultColumns` (e.g. hfs_sObject_Id, hfs_Name, hfs_Date, hfs_Floating_Point…),
  NOT a fixed column set.
- **Queries** dropdown / "Query" side panel: "Add the queries you would like to run" → add MULTIPLE queries
  (one per content type: dev-test-account, case-content-type, bills-content-type, prescription); running >1 MERGES
  columns/rows (16 → 20 items). Each query row has gear (configure) + trash (remove) + "+ Add Query".
- **Configure keywords** (gear) → the query's `inputs` as a form (some fields disabled); fill keyword values to
  search. Client **validation** from the input constraints; invalid query shows a red warning icon.
- **Filter results** box = quick filter on the loaded results (client-side, on top of the server query).
- **Capture** = file list + Content Type + the type's metadata Properties (required/validated); name = `hfs_Name`.

## 7. What's wrong now + how to correctly implement (panel AND chatbot) — NOT STARTED
- WRONG: client-side `filterDocs`; storing filename in keyword 229 + "Document" relabel; fixed column set.
- CORRECT:
  1. **List** = run configured query/queries → render their `resultColumns`; support multiple queries (merge).
  2. **Search** = per-query keyword inputs (from GetQueryMetadata) with validation (required/minLength/maxLength/
     minValue/maxValue) + operator picker (supportedOperators) → `queries/{id}/execute` server-side.
  3. **Add Query** = choose/configure/remove multiple queries.
  4. **Capture** = show the content type's real metadata fields (required/typed/validated); documentName = entered
     `hfs_Name`; drop the filename-in-keyword hack.
  5. Same behavior in the **chatbot** (agent uses the same query tools).

## 8. Implementation outline (for when user says go)
- **MCP:** new tools `list_queries` (GetAllQueries) + `get_query_metadata` (GetQueryMetadata). `query_documents`
  (execute) already exists; `get_document_type_metadata` already returns capture fields.
- **BFF:** endpoints to expose queries + query metadata (+ pass through inputs/operators).
- **Extension:** Queries UI (add/configure/remove), dynamic keyword-input forms with validation + operators,
  dynamic result columns per query (merge for multi-query), capture-metadata form. Large; phase it.

_All findings also in memory `repo/search-and-naming.md`. No code changed today — research only._

## 9. Last details (authoritative source + merge + filter)
- **Queries are CONTENT-PLATFORM-defined, adapter-served.** `DocumentQueryService.GetAllQueries`/`GetQueryMetadata`
  delegate to the adapter (`DocumentQueryAdapterCFS` for OnBase → `_contentQueryService.GetAllQueries` /
  `GetQueryMetadata`; Hx for CIC). The query's `Inputs` (with `SupportedOperators`), `ResultColumns`, and
  validation constraints come from **OnBase/CIC itself**, not just the solution-config blob. → the plugin should
  use `GetAllQueries`/`GetQueryMetadata` (new MCP tools) as the source of truth for the Queries UI + validation.
- **Multi-query merge is SERVER-SIDE.** `ExecuteDefaultListQueries(... IEnumerable<string> queryIds ...)` dedups
  the query ids, runs them, and merges into ONE `UCEBRelatedDocumentMetadata { Columns, Documents }`. That is the
  native "add multiple queries → merged columns/rows (16→20 items)" behavior. So multi-query = pass multiple
  queryIds; the server returns the unioned columns + documents.
- **"Filter results" = client-side quick-filter** over the already-returned/merged server results (the ONE
  place a client-side filter is legitimate — distinct from the server-side keyword search via query inputs).
- Two list/search paths remain: record listing = `ExecuteDefaultListQueries` (business-objects/{id}/documents,
  default queries filtered on the boId field); search = `ExecuteQuery` (queries/{id}/execute with user inputs).

---

## 10. PHASED IMPLEMENTATION PLAN (correct the model)

Goal: list/search/capture behave like native HFS — server-side keyword queries, dynamic columns from each
query's resultColumns, Queries/Add-Query with per-query keyword inputs + validation + operators, capture with
real metadata; drop the client-side `filterDocs` as "search" and the filename-in-keyword hack. Panel + chatbot.

### Phase 1 — MCP: expose the query API (foundational)
- `UcebApiClient.GetAllQueriesAsync()` → GET `{CoreBasePath}/queries` (WithSystemConfig).
- `UcebApiClient.GetQueryMetadataAsync(queryId)` → GET `{CoreBasePath}/queries/{queryId}/metadata`.
- `UcebTools`: `list_queries`, `get_query_metadata` tools. (`query_documents`=execute already exists.)
- Rebuild MCP.

### Phase 2 — BFF: query endpoints
- `GET /api/queries` → list_queries → `[{id,name,type,maxResults}]`.
- `GET /api/query-metadata?queryId=` → get_query_metadata → `{inputs:[{id,name,dataType,required,minLength,
  maxLength,minValue,maxValue,supportedOperators}], resultColumns:[{id,name,dataType}]}`.
- `POST /api/query-execute` `{queries:[{queryId,inputs:[{id,operator,value}]}], businessObjectType, businessObjectId?}`
  → run each (query_documents), MERGE columns+docs, return `{columns, documents}`.
- Restart BFF.

### Phase 3 — Extension: dynamic columns + Queries UI + server-side search
- Render the doc list from the query `resultColumns` (dynamic, not the fixed set).
- Queries panel: list available queries; add/remove; per-query keyword-input form built from `inputs` with
  **client validation** (required/minLength/maxLength/minValue/maxValue) + **operator** select (supportedOperators);
  "Run".
- Execute server-side via `/api/query-execute`; MERGE multi-query results.
- Keep the existing search box as the **"Filter results"** client-side quick filter over merged results.
- Reload extension.

### Phase 4 — Capture with real metadata
- Extend the existing dynamic upload-metadata form (day-24 feature #3) to render the content type's fields from
  doc-type metadata with **required + validation** (RelatedDocumentTypeAttributeTypeDTO: IsRequired/Min/MaxLength/
  dataType). documentName = entered `hfs_Name`.
- Deprecate the filename-in-keyword hack (`NameKeywordFieldByContentType` 229 + "Document" relabel) — rely on the
  platform documentName. (Leave existing OnBase data as-is; stop stamping filename on new uploads.)

### Phase 5 — Chatbot parity + cleanup
- Ensure the agent can list/search via list_queries/get_query_metadata/query_documents (same tools) → chatbot
  matches the panel.
- Remove `filterDocs` as primary search (becomes the quick-filter only). Update day-25 with results.

Implement in order; each phase is independently testable. Rebuild MCP (Phase 1), restart BFF (Phase 2), reload
extension (Phase 3/4). Sign-in needed after MCP restart.

## 11. IMPLEMENTATION LOG (updated as phases land)
- **Phase 1 DONE** (MCP builds 0/0): `UcebApiClient.GetAllQueriesAsync` (GET /api/core/queries) +
  `GetQueryMetadataAsync(queryId)` (GET /api/core/queries/{id}/metadata); MCP tools `list_queries` +
  `get_query_metadata`. (`query_documents`/execute already supports a single-field server-side filter; multi-field
  AND deferred.)
- **Phase 2 DONE** (BFF builds 0/0): `GET /api/queries`, `GET /api/query-metadata?queryId=`, `POST /api/query-execute`
  ({businessObjectType, queryId?, businessObjectId?, filterFieldId?, filterValue?, filterOperator?} -> query_documents
  -> parsed documents). New `QueryExecuteRequest` record.
- Phase 3–5 pending (extension Queries UI + dynamic columns + server-side search; capture validation; chatbot parity).
- **Phase 1–2 VERIFIED** on OnBase9714_Integrations_Agent: `list_queries` -> 4 OnBase queries (111 CL Loan Number,
  110 Commercial Lending Search, 102 Get Accounts, 109 Invoices) served by the adapter (more than the config blob
  had). `get_query_metadata(109)` -> inputs [228 Vendor Name, String, maxLength 250, ops Equals/StartsWith/Contains
  CaseInsensitive] + resultColumns [228 Vendor Name, 229 Invoice #, 227 Invoice Total, 226 Invoice Date,
  DocumentTypeName Document Type]. Confirms the native inputs/validation/operators/columns model works.
- **Phase 3 DONE** (extension, compiles clean — reload only, no rebuild): `agent.js` adds `fetchQueries()`,
  `fetchQueryMetadata(queryId)`, `executeQuery({...})`. `popup.html` adds a `#queryBar` (query `<select>` + Search
  button + `#queryInputs` + `#queryStatus`) inside `#docPane`; `#docSearch` relabelled "Filter results…" (now a
  client-side quick-filter only). `popup.js`: `loadQueries()` populates the dropdown from the active system's
  queries (called on panel load); `renderQueryInputs(queryId)` renders each native input with an operator `<select>`
  (from `supportedOperators`) + a validated text box (required/minLength/maxLength from the metadata); `runQuery()`
  validates and calls `executeQuery` -> `renderDocuments(documents)` (real server-side keyword search). `popup.css`
  adds `.hec__queryBar/.hec__queryRow/.hec__queryInputs/.hec__queryField/.hec__queryFieldRow/.hec__queryOp` +
  `.context__status--error`. Single-field search for now (first filled input); multi-field AND deferred.
- **Phase 4 DONE** (MCP, compiles clean — needs MCP stop+rebuild+restart+relogin to take effect): retired the
  filename-into-keyword hack. Removed the `ResolveNameKeywordField` name-stamp block in `UcebTools` upload path
  (it wrongly stamped the file/document name into keyword 229 = "Invoice #", corrupting that column) and deleted
  `Uceb:NameKeywordFieldByContentType` from MCP `appsettings.json`. Upload now only auto-stamps the import/scoping
  keyword (record id); the real 229 (Invoice #) and other metadata come from the user's capture/upload metadata
  form (feature #3, `extraAttributesJson`), which is schema-driven from the content type. `ResolveNameKeywordField`
  + the option remain as harmless dead code.
- **Phase 5 DONE (code) / partial (cloud)**: chatbot parity at the tool level — `list_queries`, `get_query_metadata`,
  `query_documents` are exposed MCP tools the cloud Agent can call, so the chatbot can drive the same native
  server-side search as the panel. `filterDocs`/`#docSearch` is now a quick-filter only (primary search = the
  Queries bar). REMAINING (cloud, out of code scope): update the Agent Builder instructions so the chatbot prefers
  `list_queries`/`query_documents` for "find/search documents" intents instead of listing then string-matching.
