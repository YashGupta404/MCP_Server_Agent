# KE PoC — config-less solution-config generation

Standalone .NET 8 console app that will drive the Knowledge Enrichment (KE) pipeline:
OCR/extract (Data Curation API) -> classify/NER (Context API) -> match extracted fields to the known
ECM keywords -> emit a solution-config fragment.

## Step 1 — prove the token (current)

1. Store the KE service-client credentials in user-secrets (never in the repo / frontend):

   ```powershell
   dotnet user-secrets set "Ke:ClientId" "<your_client_id>"
   dotnet user-secrets set "Ke:ClientSecret" "<your_client_secret>"
   # Optional overrides (defaults target Staging):
   dotnet user-secrets set "Ke:TokenEndpoint" "https://auth.staging.app.hyland.com/idp/connect/token"
   dotnet user-secrets set "Ke:Scope" "environment_authorization"
   ```

2. Run it:

   ```powershell
   dotnet run
   ```

   Success prints `OK - access_token acquired ...`. The token value is never printed in full.

## Step 2/3 — Context API enrichment (now wired)

The Context API (`cin-context-api`) does classification + NER + summarization in one `/content/process` call,
so the PoC runs against it directly (Data Curation / OCR deferred). Pass a sample document:

```powershell
dotnet run -- "C:\path\to\change-request.pdf"
```

Pipeline: `GET /files/upload/presigned-url` -> `PUT` bytes -> `POST /content/process`
(`textClassification` + `namedEntityRecognitionText` + `textSummarization`) -> poll
`GET /content/process/{id}/results`. Prints classification, summary, and named entities, and writes the full
JSON next to the file (`<name>.context.json`).

Verified Staging base URL: `https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/context-enrichment`
(CIN Service Endpoint Catalog). If Context returns 401, switch the token host to
`https://auth.iam.staging.experience.hyland.com/idp/connect/token` (set `Ke:TokenEndpoint`).

## Next steps

- Step 4: pull the UCEB keyword catalog (`list_document_types?fetchMetadata=true`) and match the extracted
  entities/metadata to the known ECM keywords; derive `inputSource`; resolve the record-id keyword from the
  context busObject id.
- Step 5: emit the solution-config fragment and diff vs `servicenow-solution-config.json`.
- Data Curation API (OCR) deferred — its submit/result schema was unverified; add once the Staging Swagger
  JSON is exported.
