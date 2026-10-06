# Day 29 — 2026-10-01

_Focus: pinned down the **IDP REST API** for the bulk auto-classification feature — endpoint list, auth,
and (crucially) that classification accepts **inline class definitions** (zero-shot). Also ran a staging
Salesforce demo stack._

---

## 1. IDP endpoint detail (from IDP teammate)
Each stage = submit a **job** against an **execution profile**, then poll status:
- Classification: `POST /api/classification`; `/classification/job/status`; `/suggestions/class|fields|entityfields`;
  `/classification/metadata/execution-profiles` (+/{profileId}+/version/{versionId}).
- Recognition (OCR): `POST /api/recognition/file`; `/recognition/file/metadata`; `/page/ocr`; `/page/svg`.
- Extraction: `POST /api/extraction/job`; `/extraction/job/status`; `/field/format-rules`.
- Separation: `POST /api/separation/job`; `/separation/job/status`. Package: `/package/usage`.

## 2. We probed the IDP API ourselves (api.idp.dev.app.hyland.com)
- It's AWS API Gateway, needs a **Bearer JWT**. `POST /api/classification` -> 401; no public swagger.
- Our dev LOB clients CANNOT mint an IDP token: `wsc-*` are interactive-login only (client_credentials ->
  `unauthorized_client`); api-credentials -> `unsupported_grant_type` @ auth.dev, `invalid_client` @ auth.iam.experience.
- => IDP/CIC is on a DIFFERENT IAM (`experience.hyland.com`) than UCEB/Workday/SF (`app.hyland.com`). Need a proper
  IDP **Application** (client_id+secret) with IDP scope, or a ready token.

## 3. Bruno collection (teammate) — THE UNLOCK
"IDP Classification API - 1" (Classification/, Suggestions/, opencollection.yml) copied into workspace root.
- **Auth:** OAuth2 **client_credentials**, clientId/secret in BODY, `accessTokenUrl={{token_url}}`, Bearer header.
- Client **Application** allowed scopes include **`hxp.integrations`** (+ hxp/hxpr/hxps). (Env "Env 1", App "Genericclaims czt0a".)
- **`POST /api/classification` takes INLINE `documentClassDefinitions[]`** = `{id,name,description,confidenceThreshold,
  reviewThreshold,classAssignmentThreshold,ignoreForAuto}`. `executionProfile` is **OPTIONAL**. Input =
  `contentFileReferences[]{fileReference,sourceUrl}`. Response = `{jobId, documents[]{className,confidence,
  classCandidates[],reviewStatus,selectionReason}}` (async, poll `/job/status`).
- **=> multi-sysconfig auto-class IS buildable:** BFF pulls active sysconfig doc types -> builds documentClassDefinitions
  (name=doc type, description=generated) -> POST inline. NO per-sysconfig trained project. Hybrid gating native via *Threshold fields.
- `suggestions/class` = discover a NEW class (classesToExclude) -> jobId. Config-time helper, not the main classifier.

## 4. Tooling updated
- `bff/idp-probe.ps1` — turnkey probe: takes `$env:IDP_TOKEN`, or `IDP_CLIENT_ID`+`IDP_CLIENT_SECRET` (+`IDP_TOKEN_URL`,
  `IDP_SCOPE`) to mint via **client_credentials**; lists execution-profiles + leaks schemas; `-File sample.pdf` tests recognition.
- Repo memory `/memories/repo/idp-bulk-classify-feature.md` fully updated.

## 5. Still blocked only on credentials
Need the IDP **Application `client_id`+`secret` + `token_url`** (the Bruno `{{...}}` vars) and the **content-upload
endpoint** that returns a `fileReference`. Then the probe runs and we validate end-to-end.

## 6. Demo
Ran the staging Salesforce stack (MCP salesforce-staging `hfs`->`api`, active OnBase9714_Integrations_Agent,
BFF :5010, devtunnel) — 9 queries incl 115. Demo-ready.

## Status
IDP feature design fully de-risked (inline classes confirmed). NOT implemented — waiting on IDP app credentials to test.
