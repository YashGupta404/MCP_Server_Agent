# Day 30 — 2026-10-05

_Focus: created the IAM **external application** for IDP access and prepared to test the IDP REST API.
(Also earlier: ServiceNow integration knowledge/plan + what to learn; started a ServiceNow PDI.)_

---

## 1. IDP access — external application created (STAGING)
Portal: `admin.staging.app.hyland.com` → External Systems → External Applications.
- **App name:** `yash_hia_idp`
- **Application ID (client_id):** `wsc-cfd5492e-0b47-4c70-8091-d87b2ac459f8`
- **Application Profile:** **Web Server Application** (authorization_code flow — NOT Service/client_credentials).
- **Redirect URI / Post-logout:** `https://oauth.usebruno.com/callback` (Bruno's OAuth callback).
- **Allowed Scopes:** email, environment_authorization, hxp, **hxp.integrations**, hxpr, hxps, offline_access, openid, profile, wdx.
- **Environment:** Appintel-Staging Test Environment. **Application:** Intelligent Document Processing.
- **Application Secret:** set (Reset Secret available).
- **User perms:** on the appintel-staging user **Yash Gupta**, attached **`HIDP_Users`** group (carries the idp role).

### Key implication
Web Server App = **authorization_code** (interactive user login), so the token acts AS Yash (who now has the
idp role via HIDP_Users). This is a workaround for the earlier Service-Application wall (couldn't see/assign
the IDP user groups to a service user). NOTE: the Bruno collection's opencollection.yml auth is client_credentials
— mismatch to confirm (Web Server app may be authorization_code only).

## 2. Goal now
Test whether we can call the IDP REST API with this app's token:
- client_id (above) + client_secret (-> dotnet user-secrets / env var).
- Prefer to test from our terminal probe (`bff/idp-probe.ps1`); fall back to Bruno if authorization_code is required.

## 3. Open questions to resolve for the test
- **Token endpoint (token_url)** for Appintel-Staging IDP (auth.staging.app.hyland.com/idp/connect/token? or the
  experience IAM?).
- **IDP API base URL** for staging: likely `https://api.idp.staging.app.hyland.com` (dev was api.idp.dev.app.hyland.com).
- Does this Web Server app allow **client_credentials** (quick, no browser) or only **authorization_code** (needs
  redirect + browser)? -> decides terminal-probe vs Bruno.

## 4. Earlier today — ServiceNow (context)
- Explained ServiceNow (ticket/workflow platform; system of action) + Hyland-for-ServiceNow (surface OnBase docs in
  a ServiceNow record; same filing cabinet as SF/Workday). Extension already has `*.service-now.com` host perms +
  `detectServiceNow` (table + sys_id). Integration = mostly UCEB business-object + solution/system config + a
  ServiceNow LOB/appKey (Phase 0 gate = is ServiceNow provisioned as a LOB in UCEB?).
- Learnings to do: ServiceNow **Fundamentals** (tables/records/sys_id/URLs) on Now Learning; CSA for the cert;
  CAD/REST only if going native. Started a **PDI** (dev instance) at developer.servicenow.com.

## Status
External app for IDP created on staging; about to test the IDP REST endpoints. Feature still not implemented —
this is the credential/connectivity test.

## 5. IDP REST API — TESTED END-TO-END, CLASSIFICATION WORKS 🎉
Built `bff/idp-auth-test.ps1` (local authorization_code login via the MCP's `uceb-mcp-local:5005` HTTPS cert +
PKCE; token = user token with the idp role) and proved the full flow against **staging IDP**
(`https://api.idp.staging.app.hyland.com`):
- Auth OK; `GET /api/classification/metadata/execution-profiles` -> 200. Pre-built **"General Purpose"** LLM profile
  exists (`078abc02-212b-42fa-92fb-f42edd6bb42d` v3.0, isDefault) = single-pass zero-shot classification — **no custom
  project needed**.
- **CLASSIFICATION SUCCEEDED**: `POST /api/classification` with correlationId(UUID) + General Purpose profile +
  **inline `documentClassDefinitions`** (Invoice/Receipt/Contract) + `contentFileReferences{fileReference,sourceUrl}`
  -> 202 `{jobId}`; polled -> `jobStatus: Succeeded`:
  - `className: "Invoice"`, `confidence: 0.9075`, `reviewStatus: ReviewNotRequired`, `selectionReason: AUTOWIN`,
    plus a human-readable `reason`. => confirms zero-shot inline classes + the confidence/review signals the Hybrid gate needs.
- Request body gotchas found: `correlationId` must be a UUID; `configuration.treatEachFileAsDocument` required;
  `pageLimit` must be 1–25; pass the JSON via `--data @file` (PowerShell mangles inline quotes).

### Content upload (file -> fileReference/sourceUrl) — BLOCKED, used Option A
- Rovo's upload `POST api.platform.staging.app.hyland.com/api/upload` REJECTS our Bearer -> 403
  IncompleteSignatureException (expects AWS SigV4) / 413 on binary. `api.content.staging` -> 404. Real Bearer-auth
  HxPR content-upload base UNKNOWN -> asked teammate.
- **Option A (Rovo) used for the test**: committed `bff/sample-invoice.png` to the public GitHub repo and passed its
  raw URL as `sourceUrl` (IDP's backend downloads any public HTTPS URL). That's how the end-to-end test ran.
- Cleaned up: deleted the short-lived cached token (`%TEMP%\idp_token.txt`).

### Net
The IDP **intelligence** is fully proven (zero-shot inline-class classification via the General Purpose profile). Only
the production **file-upload** step (Bearer-auth HxPR endpoint) remains — pending the teammate's answer. All findings in
repo memory `/memories/repo/idp-bulk-classify-feature.md`.

