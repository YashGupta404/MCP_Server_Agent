# Day 31 — 2026-10-06

_Focus: shipped the **IDP auto-classify + metadata auto-fill** feature end-to-end in the plugin.
Proved extraction (metadata VALUES) after cracking the recognition→extraction pipeline, wired both into
the BFF + extension, and stood up the full Salesforce demo stack._

---

## 1. IDP feasibility — fully proven (Option A, public URL, staging)
Tested against `https://api.idp.staging.app.hyland.com` with a local authorization_code+PKCE login
(`bff/idp-feature-test.ps1`, token cached to `%TEMP%/idp_token.txt`, ~15 min life). Public sample =
GitHub raw `sample-invoice.png`.
- **Classification (doc TYPE) — PROVEN.** `POST /api/classification` (NOT `/job`), inline
  `documentClassDefinitions` among 5 candidate types (Invoice/Receipt/PO/Contract/Clinical Fax) →
  `className:"Invoice"`, `confidence:0.9075`, `AUTOWIN`, `ReviewNotRequired`. Zero-shot, General Purpose
  profile `078abc02` v3.0, **no trained project**. `treatEachFileAsDocument:true`.
- **Zero-shot field READING — PROVEN.** `POST /api/classification/suggestions/fields` → field
  definitions + locations (Invoice No/Date/Total/ACME Corp). But returns field NAMES/locations, not
  clean values for predefined keywords → not enough for metadata auto-fill on its own.

## 2. Auth architecture decision (two clients)
- We have a **separate IDP client** (`yash_hia_idp`, Web Server/authorization_code) and the **UCEB client**.
- Options: **(A)** IDP as a service client (client_credentials, no token exchange, two independent tokens) —
  recommended for prod; **(B)** token exchange / OBO (one user login flowed into IDP — needs IAM config,
  not confirmed enabled); **(C)** two interactive logins (works today, demo-grade).
- **Decision: Option C for the demo** (BFF reads the cached IDP token). Option B kept as a future option.

## 3. Shipped feature v1 — auto doc-TYPE classification in the plugin
BFF (`bff/Program.cs`): `IdpOptions` + appsettings `Idp` section; `IdpClient.ReadToken` (reads cached
token, checks JWT exp) + `ClassifyAsync`; endpoints `GET /api/idp/file/{id}` (PUBLIC — serves staged bytes
as the IDP sourceUrl), `GET /api/idp/status`, `POST /api/idp/classify` (stages file in-memory → sourceUrl =
`{Idp:PublicBaseUrl}/api/idp/file/{id}` → classify against the active sysconfig's doc types → return
docType+confidence). className == doc type 1:1.
Extension: `agent.js classifyWithIdp`; `popup.js` **✨ Auto-classify (IDP)** button + per-file
type/confidence **badge**; `popup.html`/`popup.css`. Doc-type dropdown auto-selects the detected type.
**Verified live:** `invoice.png → bills-content-type · 86%`.

## 4. Demo infra + the port-5005 gotcha
- IDP's cloud can't reach localhost, so the BFF serves staged files PUBLICLY via a **devtunnel**
  (`devtunnel host -p 5010 -a --protocol https`) → set `Idp:PublicBaseUrl` in BFF user-secrets → restart BFF.
  (devtunnel's anti-phishing page only hits browsers, not IDP's server fetch — confirmed with a 404 probe.)
- **GOTCHA:** the MCP's LoginCallbackServer holds `uceb-mcp-local.dev.hyland.com:5005`, which is ALSO the
  IDP login script's only registered redirect. Running the IDP login while the MCP is up → MCP intercepts
  the callback → "Login rejected (state mismatch)". **Fix/order: do the IDP login while the MCP is stopped
  (5005 free), THEN start the MCP** (which re-binds 5005 for its own login). Both tokens ~15 min.

## 5. Metadata VALUES — recognition→extraction pipeline CRACKED 🎉
Extraction (`/api/extraction`) kept returning `pages 0x0` + empty `classId` + Failed. Root-caused and
solved (`bff/idp-chain.ps1`). **Three keys:**
1. **Recognition FIRST.** `POST /api/recognition/file {correlationId, fileReference, sourceUrl, actions:"Ocr"}`
   → 202 (empty body). `actions` is a **single enum STRING** "Ocr" (NOT an array; int `1` also works).
   Poll result by **correlationId+fileReference** (not jobId):
   `GET /api/recognition/file/metadata?correlationId=..&fileReference=..` → `{pages[{imageHeight,imageWidth..}], status:"Succeeded"}`.
2. **Extraction reuses the SAME correlationId + fileReference**, `treatEachFileAsDocument:false`,
   executionProfile `078abc02` v3.0 + recognitionProfile `fe81797f` v1.0, `classExtractionDefinitions[{documentClassId, fieldDefinitions[{id,name,description}]}]`.
3. **`documents[].classId`** (NOT `documentClassId`!) MUST equal the `classExtractionDefinitions[].documentClassId`.
   Wrong field name was THE bug (→ classId:"" → Failed → 0x0).
**Proof:** invoice → `Invoice Number="INV-1007"`, `Invoice Date="2026-10-05"`, `Vendor Name="ACME Corp"`,
`Total Amount="$1,234.56"`, each `extractionConfidence 0.935`, `ocrConfidence 1.0`, `ReviewNotRequired`.
Profiles discovered via `/api/extraction/metadata/execution-profiles` + `/api/recognition/metadata/execution-profiles`.

## 6. Wired metadata auto-fill into the product
- BFF: `IdpClient.ExtractAsync` (recognition→extraction for a file + a field list) + `IdpOptions.RecognitionProfile*`
  + `POST /api/idp/extract` ({attachment, fields:[{id,name}]} → [{id,name,value,confidence,reviewRequired}]).
- Extension: `agent.js extractWithIdp`; the **Auto-classify** handler now, after setting the detected type,
  awaits `loadUploadMetaFields()` then calls `extractWithIdp` with the rendered metadata inputs and
  **pre-fills each input** (green `.hec__metaAuto` highlight) with the extracted value. BFF builds clean.

## 7. Sample documents (generated + keyword-aligned)
`bff/make-samples.ps1` (System.Drawing) → 4 OCR-friendly PNGs in `bff/samples/`, content aligned to each
active `cic` type's real keywords so extraction has values to fill:
- `legal-case.png` → **case-content-type** (Case Reason/Amount/Date/Status/Doc Name — richest, best showcase)
- `prescription.png` → **prescription** (Dosage/Medication/Hospital Certified)
- `sales-opportunity.png` → **opportunity-content-type** (Opportunity Name/Type/Date)
- `invoice.png` → **bills-content-type** (Billing Month/Amount)

## 8. Demo stack (Salesforce, staging)
`switch-lob.ps1 -Lob salesforce-staging` (MCP, active system `cic`) + BFF (:5010, `Idp:PublicBaseUrl`=devtunnel)
+ devtunnel + cached IDP token. Doc types confirmed to carry keywords (`list_document_types fetchMetadata=true`).

## Status
Feature COMPLETE and building: auto doc-type classification (verified live) + metadata value auto-fill
(pipeline proven, wired). Pending: live in-plugin test of the metadata fill with the aligned samples.
Scripts added in `bff/`: idp-feature-run, idp-extract-*, idp-recog*, idp-chain, idp-recog-brute, make-samples.
Full recognition→extraction contract recorded in `/memories/repo/idp-bulk-classify-feature.md`.

## Open follow-ups
- Option B (token exchange) if IDP must be attributed to the signed-in user.
- Prod: IDP service client (client_credentials) + a real public file host (Studio modeling-service or a
  hosted sourceUrl) to replace the devtunnel; BFF chunking (IDP 500 docs/batch).
- Avoid the 5005 clash in a cleaner way (dedicated IDP redirect port) for smoother multi-login demos.

## 9. Silent IDP token refresh — kills the 5005 re-login pain
IDP tokens last ~15 min and the login browser needs port 5005 (which the MCP holds while running), so every
expiry forced a stop-MCP → login → restart-MCP dance (plus an extra MCP login). Fixed:
- `idp-feature-test.ps1` now also saves the **refresh_token** (scope has `offline_access`) + client id to
  `%TEMP%/idp_refresh.txt` / `idp_clientid.txt`.
- New **`bff/idp-refresh.ps1`** mints a fresh access token via `grant_type=refresh_token` — **no browser,
  no :5005** — so it works even while the MCP runs. Verified: token refreshed silently, `expires_in=900s`.
- One browser login (MCP stopped) seeds the refresh token; after that every expiry is just `idp-refresh.ps1`
  (refresh tokens rotate; the script re-persists the new one each time).
