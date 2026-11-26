# Day 46 — KE PoC Steps 2/3 wired (Context API enrichment)

_Continues day-45. Rovo returned the **verified Staging Context API contract** (from the catalog's
`swagger-context-api.yaml`) but could NOT verify the Data Curation submit/result schema. Pivot: build the PoC
against the **Context API** (it does classify + NER + summarize + text metadata in one call), defer Data
Curation/OCR. Steps 2/3 coded and build clean. Covers 2026-11-16._

## Verified Staging facts (from Rovo / CIN Service Endpoint Catalog, 2026-09-03)

- Context base URL: `https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/context-enrichment`
- Data Curation base URL: `https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/data-curation`
- Token host for KE (per account guide): `https://auth.iam.staging.experience.hyland.com/idp/connect/token`
  (kept our tested `auth.staging.app.hyland.com` as default; documented the KE host as the 401 fallback).
- External app selections: Data Curation -> `cin-data-curation-api`, Context -> `cin-context-api`.
  Scope `environment_authorization`; service user needs Data Curation User / Context API User roles.
- **Still unverified:** that OUR client successfully executes an actual call (entitlement vs config vs
  success are different). Data Curation submit/result schema unverified -> deferred (don't invent fields).

## Context API contract implemented

- Direct-upload flow: `GET /files/upload/presigned-url?contentType=...` -> `{presignedUrl, objectKey}` ->
  `PUT` raw bytes to the signed URL (NO Hyland bearer forwarded to storage).
- `POST /content/process` with `{ version: "context.api/v2", objectKeys:[{path}], actions:{...} }` ->
  `{ processingId }`. Actions used: `textClassification{classes,instructions}`,
  `namedEntityRecognitionText{}`, `textSummarization{maxWordCount}`.
- Poll `GET /content/process/{id}/results`: 202 = keep polling, 200 = `ContentsProcessingResults`.
- Result paths: `results[i].textClassification.result`, `.textSummary.result`,
  `.namedEntityText.result` (dict entityType -> string[]), `.textMetadata`.

## Code changes (ke-poc/)

- Deleted the stub `KeClient.cs`.
- Added `ContextModels.cs` — DTOs matching the swagger (`PresignedUrlResult`, `ProcessRequest`/`Actions`,
  `ProcessAccepted`, `ContentsProcessingResults`, `S3ContentProcessingResult`, `StringResult`, `NerResult`).
- Added `ContextClient.cs` — `GetUploadUrlAsync` / `UploadAsync` (no-auth PUT) / `ProcessAsync` /
  `PollResultsAsync` (1s->5s backoff, 300s deadline) + `GuessContentType`.
- Updated `KeOptions.cs` — verified Staging base URLs, PoC `ContextClasses` (placeholder until the UCEB
  catalog feeds them), `SummaryMaxWords`.
- Rewrote `Program.cs` — `dotnet run` (no args) = token-only check; `dotnet run -- <file> [contentType]` =
  full pipeline (presign -> PUT -> process -> poll), prints classification/summary/entities, writes
  `<file>.context.json`.
- Build: `dotnet build ke-poc.csproj` succeeds, no analyzer errors.

## How to run (user action)

```powershell
cd ke-poc
dotnet run -- "C:\path\to\change-request.pdf"
```

## Next (day 47)

1. Run against a real ServiceNow change-request sample; confirm classification + entities come back
   (watch for 401 -> switch `Ke:TokenEndpoint` to the KE IAM host; watch for 403 -> role/subscription).
2. Export the Staging **Data Curation** Swagger JSON if OCR-only extraction is needed later.
3. Step 4: call UCEB `list_document_types?fetchMetadata=true` (active system) -> build the known keyword
   catalog -> match `namedEntityText`/`textMetadata` -> ECM keywords -> derive `inputSource`; resolve the
   record-id keyword from the context busObject id.
4. Step 5: emit a `servicenow-solution-config.json`-shaped fragment and diff vs the hand-written one.

## TEST RESULT (2026-11-16) — BLOCKED on KE entitlement

Ran the pipeline end-to-end against `samples/change-request-sample.txt`. Findings:

- External app `yash-ke-poc-client` (service user `yash_ke_for_hix_user`, Application ID `sc-ecc4e800-…45f`):
  allowed scopes already include `environment_authorization`, but the **Application** binding was the issue.
- Initial `invalid_scope` (400) was because the **Application** field was empty. Binding an Application
  cleared it — **token now issues (length 2973)** against `auth.staging.app.hyland.com/idp/connect/token`.
- BUT `cin-context-api` / **Context Service was NOT available in the Application dropdown**, so it was bound to
  **Knowledge Discovery** instead. Result: `GET /files/upload/presigned-url` -> **403 "You do not have
  permissions to access this resource"**. KD token ≠ KE permission.
- **Verdict:** code + endpoints + token flow are correct (clean 403 at the auth boundary, not a code bug).
  The blocker is **entitlement**: `cin-context-api` is visible (tile + roles) but NOT subscribed/bindable for
  direct API use in Appintel-Staging — the classic "visible ≠ subscribed" gap.

### Action to unblock
- Confirm `cin-context-api` subscription on the Subscriptions page.
- Request KE API entitlement so it appears as a selectable **Application** (architect / Company Admin /
  Nucleus Community / KE team). Then re-bind `yash-ke-poc-client` to `cin-context-api` and re-run.
- Nothing to code until then; pipeline is ready.

## RESOLVED + SUCCESS (2026-11-16)

Re-bound `yash-ke-poc-client` **Application -> Context Service (`cin-context-api`)** (it WAS selectable after
all), kept `environment_authorization`. Re-ran the pipeline against `samples/change-request-sample.txt` —
**full end-to-end success.** (Per Rovo: for both APIs use two clients — Context now, a separate
`cin-data-curation-api` client later for OCR. Not needed yet.)

Context API result (`samples/change-request-sample.context.json`, status `SUCCESS`):

- **textClassification.result = "change request"** -> maps straight to the doctype (layer 1). ✅
- **namedEntityText.result:** `ids_identifiers=[CHG0012345]`, `person_names=[Yash Gupta]`,
  `dates_times=[2026-11-20, 2026-11-21]`, `custom_entities=[v12, v14]`.
  - `CHG0012345` is the **record identifier** -> value-matches the busObject id = record linkage (layer 2). ✅
  - The rest are candidate values for content-field import mappings (layer 3).
- **textSummary.result:** clean one-paragraph summary.

Verdict: presign -> PUT -> `/content/process` -> poll results all work; classification + NER give exactly the
signals the mapping pipeline needs. KE Context PoC is GREEN.

## Next (day 47)

1. **Step 4 — match:** call UCEB `list_document_types?fetchMetadata=true` (active system) -> build the known
   ECM keyword catalog -> match `namedEntityText` values + `textClassification` -> ECM keywords/doctype ->
   derive `inputSource`; resolve the record-id keyword by matching `ids_identifiers` to the context busObject id.
2. **Step 5 — emit** a `servicenow-solution-config.json`-shaped fragment and diff vs the hand-written one.
3. Optional later: second client `yash-ke-datacuration-client` -> `cin-data-curation-api` for OCR on
   image/scanned docs (Context handled the text sample fine without it).
