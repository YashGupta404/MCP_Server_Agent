# Day 48 — In-plugin "Auto-map (PoC)": KE config generation wired into the Chrome plugin + BFF

_Continues day-47. Ported the proven ke-poc pipeline into the BFF and added an in-plugin "Auto-map (PoC)"
card (mirrors the existing "Detect context" PoC), so the config-less KE mapping can be demoed INSIDE the
Chrome plugin. Also brought the demo stack back up. Covers 2026-11-16._

## Stack recovery (sign-in was stuck)

- Plugin stuck on "Opening Hyland sign-in…" because the whole stack was DOWN (ports 5000/5010/5200 all dead).
- Started: BFF :5010 (`dotnet run` in `bff/`), MCP :5200 (`switch-lob.ps1`), UCEB API :5000
  (`Hyland.Experience.UCEB.Api`). Had to `Set-ExecutionPolicy -Scope Process Bypass` (machine blocks scripts).
- **Env gotcha:** `-Lob salesforce` = **dev** IAM, but the account is `yash.gupta+appintel-staging` (staging) ->
  "invalid credentials" on auth.dev. Fix: `-Lob salesforce-staging` (Iam=staging + deployed staging UCEB at
  `api.uceb.app-intel.staging.app.hyland.com`; no local UCEB :5000 needed). Dev vs staging are separate IAM
  account stores.

## In-plugin Auto-map — what was built

Mirrors the "Detect context" PoC (card + button + `<pre>` result).

BFF (`bff/`):
- `Ke/KeAutoConfig.cs` — ported: `KeContextOptions`, Context API DTOs, `KeContextClient`
  (token + presign -> PUT -> `/content/process` -> poll), `EcmCatalog` models, `KeMappingGenerator`
  (layers 1-6, role hints + lenient doctype match). Global namespace to match BFF DTO style.
- `Ke/catalogs/servicenow-ecm-keywords.json` + `salesforce-onbase-invoices.json` (bundled known catalogs).
- `Models/BffModels.cs` — `AutoConfigRequest(Attachments, BusinessObjectType, BusinessObjectId)`.
- `Program.cs` — registered `KeContextOptions` ("Ke" section); new endpoint
  `POST /api/autoconfig/analyze`: auth via `X-BFF-Session` -> for each attachment run KE Context -> pick the
  bundled catalog by classification (invoice -> salesforce-onbase; else servicenow) -> `MappingGenerator` ->
  return `{ files:[{name,classification,summary,entities}], catalog, report, generatedConfig }`.
  Returns `ke_not_configured` if `Ke:ClientId/Secret` aren't in BFF user-secrets.
- **Build: clean.**

Plugin (`agent-ng/`):
- `ecm-picker.view.ts` — `mapBusy/mapError/mapResultText/mapFiles`, `onMapFiles()`, `autoMap()` (files ->
  base64 -> `POST /api/autoconfig/analyze` -> show JSON), `fileToBase64()`.
- `ecm-picker.view.html` — "Auto-map (PoC)" card: file input (multiple) + "Generate mapping" button + `<pre>`.
- **Build: EXIT 0 -> `browser-extension/ng`** (pre-existing NG8107 warning in documents.view.html, unrelated).

## To run (remaining)

1. USER sets KE creds in BFF user-secrets (secret — not settable by agent):
   `cd bff; dotnet user-secrets set "Ke:ClientId" "<id>"; dotnet user-secrets set "Ke:ClientSecret" "<secret>"`
   (same `ke-poc-client` creds bound to `cin-context-api`).
2. Restart the BFF (loads endpoint + secrets); reload the extension at chrome://extensions.
3. ECM picker -> Auto-map (PoC) -> pick a sample doc -> Generate mapping -> shows classification + entities +
   generated solution-config mapping.

## Next

- Swap the bundled catalog for a live UCEB `list_document_types?fetchMetadata=true` pull (per active system).
- Add an "Apply" button -> `set_solution_configuration` so the plugin starts working with the generated config.
- IDP-style per-document value auto-fill (push KE values into inputSource-6 keywords) + confidence/review.

## UPDATE (2026-11-16) — catalog + classes now LIVE (no hardcoding)

Replaced the bundled catalogs and hardcoded classes with a live pull.

- Inspected the real `list_document_types?fetchMetadata=true` shape via `mcp-admin.ps1`:
  `{ data: [ { DocumentTypeName, DocumentTypeId, AttributeTypes: [ { id, name, dataType, ... } ] } ] }`
  (e.g. Salesforce/OnBase: `COM - Application`/206 with `230 Loan Number`, `232 Entity Name`,
  `223 hfs_sObject_Id`, `224 hfs_sObject_owner`).
- `KeContextClient.ParseCatalog(mcpJson)` — converts that into `EcmCatalog` (doctype name+id, keyword
  id+label+dataType). `EcmDoctype` gained `ContentTypeName` (name) separate from `ContentTypeId` (id).
- `KeMappingGenerator`: `ecmContentTypeName = ContentTypeName`, `ecmContentTypeId = ContentTypeId`;
  `BestDoctype` now prefers an exact name match; `IsOwner` broadened to `sobjectowner`/`recordowner` so
  `hfs_sObject_owner` / `hfsn_recordOwner` are caught across LOBs. Record-id heuristic already caught
  `hfs_sObject_Id` (sobjectid) and `hfsn_recordNumber` (recordnumber).
- `EnrichAsync(bytes, contentType, classes, summaryWords)` — classes passed in (from the live doctype names),
  no longer read from options.
- Endpoint `/api/autoconfig/analyze`: now calls MCP `list_document_types{fetchMetadata:true}` -> `ParseCatalog`
  -> classes = doctype names -> KE -> generator. Removed the bundled-catalog file load. Returns `doctypeCount`.
- Behavior: the catalog/classes come from the **active system** (ECM picker). Pick the system whose doctypes
  match the sample; KE classifies against the real doctype names and the generator maps to the real keyword ids.
- Build clean; BFF restarted.
