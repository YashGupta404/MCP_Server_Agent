# Day 45 — KE PoC scaffold (Step 1: token acquisition)

_Continues day-44. Started building the config-less KE PoC as a standalone .NET console app (`ke-poc/`).
Step 1 (token) is coded, builds clean; Steps 2+ are stubbed pending the KE API endpoint docs. Covers 2026-11-16._

## Built today

- New project **`ke-poc/`** (`.NET 10`, `ke-poc.csproj`, `UserSecretsId` 8052d9b6-…). Added config packages:
  Configuration, Binder, EnvironmentVariables, Json, UserSecrets (11.0.0).
  - Removed a duplicate `KePoc.csproj` I had generated — kept the pre-existing `ke-poc.csproj` so the
    user-secrets store already init'd against it is preserved.
- **`KeOptions.cs`** — binds the `Ke` config section: `ClientId`, `ClientSecret`,
  `TokenEndpoint` (default Staging IAM), `Scope` (`environment_authorization`), plus `DataCurationBaseUrl` /
  `ContextBaseUrl` placeholders. `Missing()` validates required secrets.
- **`TokenService.cs`** — `client_credentials` token acquisition against the Staging IAM token endpoint,
  with in-memory caching (reuse until ~60s before expiry).
- **`KeClient.cs`** — skeleton with `SubmitDataCurationAsync` (Step 2) and `SubmitContextEnrichmentAsync`
  (Step 3) throwing `NotImplementedException` until the real KE endpoints/contracts are confirmed from docs.
- **`Program.cs`** — loads config (appsettings → user-secrets → env), validates secrets, requests a token,
  prints masked client id + success (never prints the token value). Returns non-zero on missing secrets / failure.
- **`README.md`** — how to set the two secrets and `dotnet run`.
- **Build:** `dotnet build ke-poc.csproj` succeeds, no analyzer errors.

## How to run (Step 1 verification — user action)

```powershell
cd ke-poc
dotnet user-secrets set "Ke:ClientId" "<client_id>"
dotnet user-secrets set "Ke:ClientSecret" "<client_secret>"
dotnet run
```

Expect `OK - access_token acquired ...`. Secrets stay in the user-secrets store (not in repo / frontend).

## Next (day 46)

1. Run `dotnet run`, confirm `access_token` (close out Step 1).
2. **Get KE endpoints from docs** (blocker): Data Curation submit + async job/results, Context API
   enrichment actions + input flow + results retrieval. Fill `DataCurationBaseUrl` / `ContextBaseUrl`.
3. Implement `SubmitDataCurationAsync` (Step 2): send one sample ServiceNow doc, poll, get extracted text.
4. Implement `SubmitContextEnrichmentAsync` (Step 3): classify → doctype; NER → candidate fields.
5. Pull UCEB keyword catalog (`list_document_types?fetchMetadata=true`) for the active system; match
   extracted fields → ECM keywords; derive `inputSource`; resolve record-id keyword from context busObject id.
6. Emit a solution-config fragment; diff vs `servicenow-solution-config.json`.
