# Day 17

_Focus: the **Integrations Agent — OnBase** milestone. Built the tools that let the agent **switch the
active ECM system** (CIC ↔ OnBase) from the chat, connected to OnBase end-to-end, moved Salesforce to
**staging**, wired the **CFS token exchange** so the local UCEB can reach OnBase, and then traced exactly
why OnBase **list** and **upload** still fail (one is a UCEB code stub, the other is a broker/OnBase-side
failure)._

---

## 1. The two config layers (recap from Day 16, needed here)

- **System Configuration** = which ECM backend + how to connect. Route `api/config/system-config/{systemType}`,
  `systemType` ∈ `ECMPlatformType` = **CIC, OnBase, Alfresco, Nuxeo, Perceptive**. CIC = empty placeholder;
  OnBase = CFS + OnBase connection block.
- **Solution Configuration** = per-LOB object mappings / import mapping / view. Route
  `api/config/solution-configurations/uceb` (Salesforce/CIC) / `bow/...` (Workday).
- UCEB picks the backend via the **`systemFriendlyName`** selector, which the MCP had **hard-coded to
  `"CIC"`**. That was the thing to make switchable.

---

## 2. New MCP tools — switch the active ECM system from the chat

The mentor's plan: *"list the system configs, there's an OnBase 9714 VM config, set the systemFriendlyName
to it — that connects to OnBase."* The agent couldn't do this: there was **no tool to list system configs**
and **no way to change `systemFriendlyName` at runtime** (it was a `readonly` field from appsettings).

**Implemented (MCP server, builds clean):**
- `Configuration/LobRoutingContext.cs` — made the selector **runtime-mutable**: a `volatile`
  `_systemFriendlyName` (seeded from options), a `SystemFriendlyName` getter, and `SetSystemFriendlyName()`.
  Being on the singleton, one change is shared across all per-request `UcebApiClient` instances.
- `Clients/UcebApiClient.cs` — dropped the captured field; `WithSystemConfig()` now reads
  `_lob.SystemFriendlyName` **live**. Added `ActiveSystemFriendlyName` / `SetActiveSystemFriendlyName()`
  pass-throughs and `GetPlatformSystemConfigurationsAsync()` → `GET /api/config/system-config`.
- `Tools/UcebTools.cs` — **three new tools**:
  - `list_system_configurations` — lists the registry (friendlyName + systemType per entry).
  - `get_active_system_configuration` — shows the current selector.
  - `set_active_system_configuration(friendlyName)` — sets it; persists for the MCP process life.

`WithSystemConfig` is applied to **every** document endpoint (list / download / upload / attach / capture /
document-types / content-platform / feature-flags), so flipping the selector points all of them at the
chosen system.

**Verified on dev** — `list_system_configurations` returned 5 configs incl. OnBase ones (`CFS for OnBase
RDV_010078`, …) + `CIC`. `set_active_system_configuration("CFS for OnBase RDV_010078")` +
`list_document_types` → UCEB routed to the **OnBase/CFS adapter** (`DocumentTypeServiceCFS` in the stack),
proving the switch works. It then failed at the **CFS token exchange** (`invalid_request`) because the
**local** dev UCEB had no `TokenExchange` credentials.

Added the three tools to the agent in Agent Builder (`uceb_mcp_server`, the `4kw1kpcm` tunnel) and started
the dev tunnel (`devtunnel host giant-ant-2f6br43`).

---

## 3. Moved Salesforce to **staging** (so OnBase9714 lives there)

The dev OnBase configs didn't include the mentor's "9714 VM". Switched the MCP↔UCEB half to **staging**
(the agent hop stays dev — it's API-key, env-agnostic):

- MCP `appsettings.json`: Auth endpoints → `auth.staging.app.hyland.com/idp`; `Uceb.NucleusApiBaseUrl` →
  `api.platform.staging…`; `Uceb.ContentBaseUrl` → `content.staging…`; dropped the Workday-only `wdx` scope.
- UCEB API run with `Security__…=staging` overrides.
- New **staging confidential client** `wsc-f3ec0fd9-47a3-4f03-a67f-28b70100141b` (id + secret in MCP
  user-secrets).
- Two-account rule confirmed: **plugin sign-in = DEV account** (Agent Builder is dev), **MCP warm-up =
  STAGING account** (this token reaches staging UCEB). The MCP warm-up user gates document access, not the
  plugin user.

On staging, `list_system_configurations` returned **13** configs — including **`OnBase9714`**
(_"OnBase residing in VM RDV-009714"_, id `46fe25d4-…`). **That's the mentor's config** — it was in
**staging**, not dev. (Staging's default system is `OnBaseGuidewire`, not CIC.)

Documented in `bff/switch-lob.ps1` `.NOTES`: the script swaps client id/secret/scopes but **not** the Auth
endpoint URLs (those live in appsettings and are shared) — dev-test & dev-prod share dev IAM; staging is a
different IAM, so flip the endpoints back to `auth.dev` before running Workday again.

---

## 4. The CFS token-exchange insight (mentor) — and proving it

Mentor: _"you won't need to do the CFS token exchange — UCEB does it internally; just pass the
systemFriendlyName."_ Verified in code (`Infra.CFS/Auth/Services/TokenExchangeService.cs`): UCEB itself
POSTs an OAuth **subscription-token-exchange** to `{Security:AuthorityUrl}/connect/token`, swapping the
user token for a CFS token, using its **own** `TokenExchange:ClientId/Secret` config. So:

- The **deployed** staging UCEB (`https://api.uceb.app-intel.staging.app.hyland.com`) **has** those creds →
  does the exchange → OnBase works.
- Our **local** UCEB had **no** `TokenExchange` section → exchange fails `invalid_request` before it even
  reaches the broker.

**Proved it:** pointed MCP `Uceb:BaseUrl` at the deployed UCEB → `set_active_system_configuration("OnBase9714")`
→ `list_document_types` returned **11 real OnBase document types** (COM - Application, Appraisal, Credit
Report, Note, Pay Stubs, Photos, Proof of Insurance, Installation Invoice, SCH - Schedule A, Invoices,
Invoice). **First successful OnBase call.** `get_solution_configurations` on OnBase9714 also worked
(returned an OnBase QuickAccessViewer URL).

`subscription-token-exchange-v3` is a **grant type**, not a scope, and is **platform-restricted** — the
Admin Portal only exposes scopes, so you can't self-grant it. The mentor provides the exchange client.

---

## 5. Getting OnBase working on the **local** UCEB (the whole point)

Mentor sent the CFS exchange client via Kiteworks:
`TokenExchange: { GrantType: subscription-token-exchange-v3, ClientId: uceb-iam-token-exchange-staging,
ClientSecret: <secret> }`.

Wired it **securely** into the local UCEB API:
- `GrantType` + `ClientId` → `appsettings.Development.json` (`TokenExchange` section; not secret).
- `ClientSecret` → **user-secrets** (`appsettings` left `""`). The API csproj had **no** `UserSecretsId`,
  so added `<UserSecretsId>uceb-api-local-9f2c1a7e</UserSecretsId>` (a GUID, safe to keep). Config merges by
  key: GrantType+ClientId from appsettings, ClientSecret from user-secrets (auto-loaded in Development).
- The exchange posts to `Security:AuthorityUrl` = `auth.staging` (via the Security override), matching the
  `…-staging` client.

**Verified:** `Uceb:BaseUrl` back to `http://localhost:5000`, UCEB API + MCP restarted, staging warm-up
(`hfs`) → `OnBase9714` → `list_document_types` returned the same **11 OnBase doc types on the local UCEB**.
No more `invalid_request`.

> The local stack now serves **all three**: Salesforce (hfs) + Workday (client switch) + OnBase. The
> deployed UCEB is no longer required for OnBase. **Do not commit the secret** — it's only in user-secrets.

Also confirmed the signed-in user (via `get_token_claims`): local staging IAM account
`yash.gupta+appintel-staging@hyland.com`, sub `0256992c-61c6-4541-942d-72ca4e496f81`, appkey `hfs`, roles
`uceb.hfs_admin`/`uceb.hfs_user`, env `06a2be36-…`. The mentor created this user **in OnBase** + added it to
the right OnBase group/permissions and restarted the **MCA app pool** (which is what cleared an earlier
"at the broker" 500 on the deployed test).

---

## 6. OnBase status — what works and what doesn't (with root causes)

| Operation | Status | Owner of the blocker |
|---|---|---|
| Connect / list system configs / switch | ✅ works | — |
| `list_document_types` | ✅ works | — |
| `get_solution_configurations` | ✅ works | — |
| `list_documents` (a record's docs) | ❌ **501** | **UCEB code** (unimplemented stub) |
| `upload` (attach a document) | ❌ **500** | **Broker / OnBase side** |

### 6a. `list_documents` → 501 "The method or operation is not implemented"

Root cause is a **UCEB product-code gap**, not the flag/config:
- The `document-query-onbase` feature flag **is on** (read from `appsettings.Development.json`
  `FeatureFlags`), so the call resolves to the **real** CFS adapter (a disabled capability would 404, not
  501).
- `Infra.ECM/Services/RelatedDocumentsService.GetRelatedDocuments` loops over the configured document types
  and calls `documentService.GetDocumentTypeFieldDetails(...)` **before** the (working) `GetDocumentList`.
- In `Infra.CFS/Content/AdapterServices/DocumentServiceCfs.cs`, `GetDocumentTypeFieldDetails` (both overloads),
  `GetDocumentById`, and `GetDocument` are `throw new NotImplementedException()` **stubs**.
  `NotImplementedException` → HTTP 501.
- Reading document *types* works because that's a different, implemented service.
- **Fix:** implement (or stub to return empty) `GetDocumentTypeFieldDetails` in the CFS adapter so the flow
  falls through to the working `GetDocumentList` — but that's **product code** in
  `Hyland.Experience.UCEB.Infra.CFS`; likely still under development. Left it for the mentor/product team.

### 6b. `upload` → 500 "DocumentArchiveService: Error Retrieving Content: An unexpected error occurred at the broker"

The archive path is **fully implemented** (unlike list). `Infra.CFS/…/DocumentArchiveServiceCFS.ArchiveDocument`
ran every step: token exchange → CFS config → connectionId → validate context → **promote the staged file to a
CIC document** → **get a presigned CIC download URL** → submit a **CFS content-create job** (`POST
/connections/{id}/contents`, accepted) → poll → **job FAILED on the broker**. The `"Error Retrieving Content"` /
`"at the broker"` text is **not in UCEB source** — it's the broker's own error, propagated as `FailureReason`.

Which broker step failed: the CFS broker's **content-ingest** phase — where it **fetches the promoted
document from the CIC presigned URL and archives it into OnBase**. Ruled out (evidence-based): not a stub
(archive is complete), not the fake `account/1` id (that would 400 at the store step), not a missing config
mapping (that 400s inside UCEB before the broker). It's a **genuine broker/OnBase-side failure**.

**What's needed (broker/environment, mentor/OnBase team):** broker egress to the CIC presigned-URL host,
OnBase archive target (App Server / disk group) healthy and write-configured, and the `OnBase9714` CFS
connection provisioned for **archive**, not just query. Confirm via the CFS job-status error + OnBase App
Server logs for that correlation/job id.

---

## 7. Where things stand

- **Salesforce (staging) + OnBase read (doc types)** work end-to-end on the **local** stack; the CFS token
  exchange is configured locally now.
- **OnBase `list_documents`** is blocked by an unimplemented CFS adapter method in UCEB (a code gap to raise
  with the product team).
- **OnBase `upload`** is blocked on the **broker/OnBase side** (content-ingest fails at the broker) — an
  environment/broker issue for the mentor/OnBase team, not a UCEB code defect.
- Config state to remember: MCP `Uceb:BaseUrl = http://localhost:5000`; MCP on **staging** (client
  `wsc-f3ec0fd9`, `hfs`); UCEB API run with `Security__…=staging`; `TokenExchange` secret only in
  user-secrets (never commit it); API csproj now has a `UserSecretsId`.

**Next:** hand the two OnBase blockers to the mentor (list = UCEB CFS stub; upload = broker ingest), and
optionally apply the minimal `GetDocumentTypeFieldDetails` stub-to-empty patch to see if listing falls
through to the working query.
