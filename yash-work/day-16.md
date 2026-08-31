# Day 16

_Focus: **new phase of the Hyland Integrations Agent.** Before writing any code, I studied the whole
UCEB stack end-to-end — the Chrome plugin, the BFF, the MCP server (the agent's tools), and the UCEB
API — then broke down the five tasks my manager handed me, worked out **exactly what each one means in
this codebase**, and wrote down **how I'll actually do it.** Then — following my mentor's steer on where
to start — I implemented the first slice: **letting the agent connect to OnBase by switching the
`systemFriendlyName` at runtime** (§5)._

---

## 1. The system, top to bottom (so the tasks make sense)

The whole thing is a five-tier chain. A message typed in the plugin travels all the way down to a
content repository and back:

```
Chrome side panel (browser-extension)
        |  HTTPS, X-BFF-Session
        v
BFF  :5010   (bff/, .NET 8 minimal API) — holds the OAuth secret + the user's IAM tokens
        |  per-user OAuth bearer  /  X-Api-Key for the MCP HTTP endpoints
        v
Agent Builder (cloud, api.agents.ai.dev)  — the LLM agent + its registered MCP server
        |  dev tunnel (public https)  ->  X-API-Key
        v
MCP server :5200  (Hyland.Experience.UCEB.McpServer) — the agent's TOOLBOX
        |  interactive user token (Auth Code + PKCE)
        v
UCEB API :5000  (Hyland.Experience.UCEB.Api) — the content-services broker
        |
        v
ECM repository (CIC / HxP content platform today; OnBase is the new target)
```

Key facts I re-confirmed while reading the code:

- **The MCP server is where "the agent" gets its abilities.** It's an ASP.NET app exposing ~27 MCP
  tools (`Tools/UcebTools.cs`) — login, list/upload/capture/download documents, and the **config
  tools** (`get_solution_configurations`, `add_business_object_config`, `set_viewer_url`,
  `list_business_object_types`, `get_content_platform`, …). Whatever the agent can "do" is exactly the
  set of tools here.
- **One MCP process = one LOB.** The signed-in token carries an `hxp_authorization.appkey`
  (`Configuration/LobRoutingContext.cs`). `wdx` → Workday → routes record calls to `bow/*`; anything
  else → `api/*` (Salesforce/CIC). So Salesforce and Workday can't both run in one MCP at once —
  switch the confidential client + restart (`bff/switch-lob.ps1`).
- **UCEB is a broker, not a store.** It never creates Salesforce/Workday records; it files and lists
  **documents** against a `businessObjectId` in whatever ECM repo it's pointed at.

---

## 2. The single most important thing I learned today: **two different "configurations"**

My manager's first two tasks say "System Configurations" and "Solution Configurations." Those are **not
the same thing** — they're two separate config surfaces in the UCEB API, and confusing them would sink
the whole phase. I dug both out of the source:

### (a) Platform **System** Configuration — _"which ECM backend, and how to connect to it"_

- Controller: `Api.Library/Controllers/PlatformSystemConfigurationController.cs`
- Routes (`Api.Library/Models/OpenApi/RoutePaths.cs`):
  - `GET  api/config/system-config`                    — list all system configs
  - `GET  api/config/system-config/{systemType}`       — list configs for one system type
  - `POST api/config/system-config/{systemType}`       — **create** a system config
  - `GET  api/config/system-config/{systemType}/{id}`  — full config by id (write-level perm)
- `systemType` is the enum **`ECMPlatformType`** (`Core/Utility/ECMPlatformTypeEnum.cs`):
  **`CIC, OnBase, Alfresco, Nuxeo, Perceptive`**.
- Model: `Core/Models/Configs/PlatformSystemConfigModel.cs` (polymorphic on `systemType`):
  - **CIC** → `CicPlatformSystemConfig` — a **placeholder / empty** payload. CIC "doesn't currently
    require a concrete system payload" (it's the default content platform), so a CIC system config is
    basically `{ friendlyName, active, default, configurations:{ systemType:"CIC" } }`.
  - **OnBase** → `OnBasePlatformSystemConfig : NativePlatformSystemConfig` — carries real connection
    settings:
    - `cfs` (`CfsSystemConfig`: `baseUrl`, `systemIntegrationId`) — the hybrid CFS path.
    - `onBase` (`OnBaseSystemConfig`):
      - `authParameters` → `idpIssuerUrl`, `idpTokenUrl`, `clientId`, `clientSecret`
      - `apiServerConfig` → `onbaseUrl`, `onbaseGraphUrl`
      - `isTechnicalUserBasedLicense`
- Permissions: `CanReadSystemConfiguration` / `CanWriteSystemConfiguration`, plus per-platform
  (e.g. `uceb.onbase_configuration.read` / `.write`).

**So a System Configuration = the connection profile for a backend repository.** CIC is trivial;
OnBase needs the IdP + API-server + CFS details above.

### (b) UCEB **Solution** Configuration — _"how a LOB's business objects map to content"_

- Controller: `Api.Library/Controllers/UCEBConfigurationController.cs`
- Routes:
  - `GET/POST api/config/solution-configurations/uceb`  (Salesforce/CIC)
  - `…/uceb/{businessObjectType}`, `…/uceb/v2`, `…/lob`
  - Workday equivalent lives under **`bow/config/solution-configurations`** (see day-11).
- Contents (the blob the MCP already reads/writes): the **business-object mappings**
  (`busObject → ecmContentTypeName`, `displayColumn`), the **import mapping** (which record/document
  attributes flow into which content-type fields on capture), and the **view configuration**
  (`operationConfig.viewer.baseUrl`, columns shown in the doc list, etc.).
- The MCP already touches this: `get_solution_configurations`, `add_business_object_config` (append a
  mapping), `set_viewer_url` (write `operationConfig.viewer.baseUrl`).

**So a Solution Configuration = the per-LOB mapping between the LOB's records and UCEB content.**

> One-line mental model: **System config = _where_ the documents live (CIC vs OnBase). Solution config
> = _how_ a LOB (Salesforce/Workday) maps its records onto documents (import mapping + view).**

---

## 2.5 Clarification from manager — this is an **admin-driven, LOB-generic onboarding flow**

**Important correction to how I first framed tasks 1–2.** The config tools are **not** meant to be
hard-wired to "Salesforce" and "Workday" — those are just the two LOBs we happen to have running.
The real requirement is a **self-service onboarding flow, done through the chatbot itself**, that works
for **any** LOB:

```
Admin opens the chatbot
   -> "Set up the SYSTEM config"     (pick the ECM repo: CIC / OnBase / …)     [Task 1]
   -> "Set up the SOLUTION config"   (map this LOB's objects, import mapping, view)  [Task 2]
   -> from then on, end users of that LOB can UPLOAD and LIST documents        [Tasks 4/5]
```

So Salesforce/Workday in tasks 1–2 are **examples, not the whole list**. The tools must be:

- **Parameterized, not hard-coded** — the LOB/business-object/content-type/system all come in as
  arguments (or from the signed-in token), never baked in. Nothing should assume `employee`,
  `hcmisbeemployee`, `dev-test-account`, `CIC`, etc. as constants; those become inputs/defaults only.
- **Admin-gated** — creating/updating config is an **admin** action (needs
  `CanWriteSystemConfiguration` / `CanWriteUCEBConfiguration`); regular users just upload/list once the
  admin has finished onboarding their LOB.
- **Ordered** — system config **before** solution config **before** upload/list. An un-onboarded LOB
  has no mappings, so upload/list correctly returns nothing until the admin sets it up.

This is really a **"provision a new integration from the chat"** capability: the agent becomes the
admin console for standing up a brand-new LOB against a chosen ECM repo — not a Salesforce/Workday-only
helper.

---

## 3. What my manager actually wants — task by task

### Task 1 — "Have the agent create/add new System Configurations for **both CIC and OnBase**"

**What it means:** teach the agent (= add MCP tools) so an **admin can register any ECM backend from
the chatbot**. CIC and OnBase are the first two we must support, but the tool takes `systemType` as an
argument so `Alfresco / Nuxeo / Perceptive` work later with no code change:
- **CIC** system config — the near-empty placeholder payload.
- **OnBase** system config — with the CFS + OnBase IdP/API-server settings from §2(a).
- (generic) any other `ECMPlatformType` — same tool, different `systemType` + payload.

**How I'll do it:**
- Add MCP tools in `UcebTools.cs`:
  - `list_system_configurations` → `GET api/config/system-config`
  - `create_system_configuration(systemType, friendlyName, …)` → `POST api/config/system-config/{systemType}`
- Add matching methods in the MCP `UcebApiClient.cs` that build the `PlatformSystemConfigModel` JSON
  (CIC = minimal; OnBase = full connection block). OnBase secrets (clientSecret) must come from
  user-secrets / prompt — never hard-coded.
- Requires `CanWriteSystemConfiguration` on the signed-in token — check with `get_token_claims` first,
  escalate to mentor if the dev-prod user lacks it (same pattern as the earlier config 403s).

### Task 2 — "Create/add new Solution Configurations for **both Salesforce and Workday** (import mapping, view configuration)"

**What it means:** the agent should let an admin stand up a **solution config** for **any LOB** — not
just append one business object, but set the **import mapping** (attribute → content-type-field) and the
**view configuration** (viewer URL + list columns). Salesforce/Workday are the proof cases; the tools
take the LOB/business-object/content-type as inputs so a new LOB is onboarded the same way.

**How I'll do it:**
- Salesforce/CIC writes to `api/config/solution-configurations/uceb`; Workday writes to
  `bow/config/solution-configurations` — the existing `LobRoutingContext.IsWorkday` +
  `SolutionConfigPath` already route this correctly.
- Extend beyond today's append-only `add_business_object_config`:
  - `set_import_mapping(busObject, mappings[])` — write the attribute→field map into the blob.
  - `set_view_configuration(busObject, displayColumns[], viewerUrl)` — the doc-list columns + viewer.
  - Reuse the existing GET-blob → edit-node → POST-whole-blob pattern already in `SetViewerUrlAsync`.
- Both LOBs already have working solution configs in dev (Salesforce SF types; Workday `employee`
  group `hcmisbeemployee`), so I'll model the new tools on those shapes (`get_solution_configurations`
  output).

### Task 3 — "Integrate the plugin with **CIC Viewer** (open a doc → CIC Viewer). Investigate iframe embedding within the solution."

**What it means:** clicking a document should open it in the **CIC Viewer** experience, ideally
**embedded inside the side panel** (iframe), not a new tab.

**What I already know (Days 14–15, and re-confirmed):** a raw cross-site iframe of the Studio/CIC
viewer **cannot** reliably render a login-gated document from a `chrome-extension://` panel:
- the viewer is third-party to the extension → Chrome won't send the Hyland session cookie → viewer
  APIs come back **anonymous → 401**, then the viewer redirects to IAM login, which sends
  `frame-ancestors 'none'` → the frame is refused. This is a **browser privacy rule + Hyland CSP**,
  not our bug.

**How I'll do it (the approach that actually works):**
- **Primary:** keep the **in-panel render of rendition/PDF bytes** we already built (PDF.js on a
  `<canvas>`, images in `<img>`) — that's same-origin `blob:` so there's no cookie/frame problem. For
  CIC specifically, wire `open_document_in_viewer` / the doc-row click to the CIC **file-preview**
  bytes endpoint (`api/core/documents/{id}/file-preview?renditionType=preview`) instead of embedding
  the SPA.
- **"Open in CIC Viewer" affordance:** when the user explicitly asks the agent/plugin to "open in the
  CIC Viewer," open the CIC viewer URL in a **first-party extension window** (`windows.create`) where
  the Hyland session is first-party and auth works — that's the honest answer to "can it be embedded":
  **embedded read-only preview = yes (bytes); embedded full interactive viewer SPA = no (unless Hyland
  allowlists our origin in the viewer CSP `frame-ancestors` and sets viewer cookies
  `SameSite=None; Partitioned`).**
- Deliverable of the "investigate iframe" part = a short written finding stating exactly the above,
  with the two Hyland-side changes that would be required to make true embedding possible.

### Task 4 — "Support **OnBase** as the ECM repo"

**What it means:** today the plugin lists/uploads documents from **CIC (HxP)**. The manager wants the
same flow to work when the backing repository is **OnBase**.

**What I found:** OnBase is a **fully-built adapter** in the UCEB API — `Infra.OnBase/`
(`OnBasePlatformAdapter`, `OnBaseSessionManager`, `ObDocumentManagementRESTClient`, Redis session
pooling, technical-user vs per-user license strategies). UCEB picks the repo via the **system config /
`systemFriendlyName` selector**, which the MCP currently **hard-codes to `"CIC"`**
(`UcebApiClient.WithSystemConfig` reads `options.Value.SystemFriendlyName`).

**How I'll do it:**
1. Create an **OnBase system configuration** (Task 1) so UCEB knows how to reach OnBase.
2. Make the MCP **system-aware**: replace the hard-coded `SystemFriendlyName="CIC"` with a
   selectable value (config/option or per-call param) so record/list/upload/download calls carry the
   **OnBase** `systemFriendlyName`. Add it to `LobRoutingContext` (it already centralizes LOB routing)
   or a new `SystemRoutingContext`.
3. Verify list/download/capture against OnBase in dev; expect auth/session differences (OnBase uses its
   own IdP + session pool, not the HxP bearer path we use for CIC/Workday).
4. Extension side: the panel is repo-agnostic — it just shows what the BFF returns — so most work is
   MCP/UCEB routing, not front-end.

### Task 5 — "Have the agent **search for relevant documents**"

**What it means:** more than listing a single record's documents — actually **query/search** the repo
for documents relevant to a context or a user's phrase.

**What I found:** UCEB has a **DocumentQuery** capability already:
- `GET  api/core/queries` (list saved queries)
- `GET  api/core/queries/{queryId}/metadata`
- `POST api/core/queries/{queryId}/execute`
- plus `RelatedDocuments` (`api/core/business-objects/{boId}/documents`, already used by
  `list_documents`).

**How I'll do it:**
- Add MCP tools `list_queries`, `get_query_metadata`, and `search_documents(queryId, parameters)` →
  `execute` — so the agent can run a saved query and return relevant documents.
- Surface it in the agent's prompt ("to find documents, use search_documents…") and, optionally, add a
  search box / natural-language "find documents about X" path in the panel that the agent fulfils via
  the query tool.

---

## 4. Plan of attack (execution order for the coming days)

1. **System-config tools** (`list_system_configurations`, `create_system_configuration` for CIC +
   OnBase) — Task 1. Foundation for Task 4.
2. **Make the MCP system-aware** (selectable `systemFriendlyName`) — unblocks Task 4.
3. **OnBase end-to-end** (create OnBase system config → list/download/upload against OnBase) — Task 4.
4. **Solution-config tools** (`set_import_mapping`, `set_view_configuration`) for Salesforce + Workday
   — Task 2.
5. **CIC Viewer**: wire doc-open to CIC file-preview bytes in-panel + first-party "Open in CIC Viewer"
   window; write the iframe-feasibility finding — Task 3.
6. **Search tools** (`list_queries`, `search_documents`) — Task 5.

**Cross-cutting risks to watch (from earlier days):**
- **Permissions:** config writes need `CanWriteSystemConfiguration` / `CanWriteUCEBConfiguration`; the
  dev-prod user (arizzo) has hit 403s before. Check `get_token_claims` early; escalate to mentor for
  role grants rather than guessing.
- **Secrets:** OnBase `clientSecret` and any credentials go through user-secrets / the user's own
  terminal — never into `appsettings.json`, git, or the assistant.
- **One-LOB-per-MCP** still holds for Salesforce vs Workday; the OnBase-vs-CIC choice is the **system**
  axis (orthogonal to LOB), selected by `systemFriendlyName`.
- **"Any LOB" onboarding vs one-LOB-per-MCP:** the tools are LOB-generic, but the LOB a config write
  targets is decided by the **signed-in admin's token appkey**. So onboarding a specific LOB means the
  admin signs in with that LOB's client (or we run/switch an MCP for it). The *tooling* is generic; the
  *active identity* still picks the LOB at write time.
- **CIC Viewer true embedding is Hyland-side** (CSP `frame-ancestors` + partitioned cookies); don't
  burn time trying to force a cross-site iframe — deliver bytes-in-panel + first-party window instead.

_No code changed today — this is the study + planning entry. Next day starts at step 1 (system-config
tools)._

---

## 5. Implemented: connect to OnBase by switching `systemFriendlyName` (mentor's plan)

**Mentor's guidance:** don't build a whole OnBase onboarding first — the dev environment already has an
OnBase system config (an "OnBase 9714 VM" entry). Just **list the system configs, find that entry, and
set the `systemFriendlyName` to it** — that alone points UCEB at OnBase.

**Validation — could the agent already do it? No.** I traced the MCP:
- There was **no tool to list system configs** (the ~27 tools had none for `api/config/system-config`).
- `systemFriendlyName` was a **`readonly` field** in `UcebApiClient`, captured once from
  `appsettings.json` (`"CIC"`) and used by `WithSystemConfig()` — which is applied to **every** document
  endpoint (list / download / upload / attach / capture / default-attributes / document-types /
  content-platform / feature-flags). So there was **no way to change it at runtime.**

**What I added (MCP server; builds 0 warnings / 0 errors):**
- `Configuration/LobRoutingContext.cs` — the selector is now **runtime-mutable**: a `volatile`
  `_systemFriendlyName` (seeded from options in the ctor), a `SystemFriendlyName` getter, and
  `SetSystemFriendlyName()`. Being on the singleton, one change is shared across all the per-request
  `UcebApiClient` instances.
- `Clients/UcebApiClient.cs` — dropped the captured field; `WithSystemConfig()` now reads
  `_lob.SystemFriendlyName` **live**. Added `ActiveSystemFriendlyName` / `SetActiveSystemFriendlyName()`
  pass-throughs and `GetPlatformSystemConfigurationsAsync()` → `GET /api/config/system-config`.
- `Tools/UcebTools.cs` — **three new agent tools**:
  - `list_system_configurations` — lists the registry (each entry's `friendlyName` + `systemType`).
  - `get_active_system_configuration` — shows the current selector.
  - `set_active_system_configuration(friendlyName)` — sets it; persists for the MCP process life.

**The flow now works end-to-end in code:**
```
agent: list_system_configurations              -> finds the "OnBase 9714 VM" entry's friendlyName
agent: set_active_system_configuration("<that name>")
agent: list_documents / upload / download      -> now carry systemFriendlyName=<OnBase> -> hit OnBase
```

**Still to do:** runtime-verify against the real OnBase VM (rebuild + restart MCP, sign in with a token
that has system-config **read** permission, confirm the OnBase entry appears, switch to it, then
list/download a document from OnBase). The **system axis (CIC vs OnBase)** is orthogonal to the **LOB
axis (api vs bow)** — switching systems does not change the LOB routing. I did **not** add system-config
**create** tools yet; that's the later onboarding phase — this change only lists + selects an existing
config, which is exactly what the mentor asked for to connect to OnBase.

### 5.1 Runtime verification (2026-08-28) — the switch works; CFS token exchange is the real blocker

Brought the stack up (UCEB API `:5000`, MCP `:5200 --http`) and drove the new tools directly over MCP
JSON-RPC. Signed in as an **`hfs` (Salesforce/CIC) token → `api` LOB**.

- **`list_system_configurations` → works.** Returned **5** real configs, including four OnBase ones:
  `CFS for OnBase RDV_010078`, `CFS for OnBase RDV_010717`, `S4hanaCFS_rdv-010078`,
  `OnBaseGuidewireCC`, plus `CIC` (the default). _(Note: the mentor said "OnBase 9714 VM" but the actual
  entries are the `RDV_…` names — there's no literal 9714 in this env.)_
- **`set_active_system_configuration("CFS for OnBase RDV_010078")` + `get_active_system_configuration`
  → work.** The selector is set and persists.
- **`list_document_types` then carried `systemFriendlyName=CFS%20for%20OnBase%20RDV_010078` to UCEB, and
  UCEB routed it into the OnBase/CFS adapter** — proven by the UCEB stack trace:
  `Infra.CFS.Content.AdapterServices.DocumentTypeServiceCFS.GetDocumentTypes`. **So the CIC → OnBase
  switch mechanism is fully working.**
- **The only failure is downstream — CFS token exchange:**
  ```
  InvalidOperationException: Failed to exchange token for CFS API access
   ---> Token exchange failed with HTTP status code: BadRequest. Response: {"error":"invalid_request"}
      at ...Infra.CFS.Auth.Services.TokenExchangeService.ExecuteTokenExchange
  ```
  UCEB tried to exchange the signed-in `hfs` user token for a **CFS** access token and the CFS IdP
  rejected it (`invalid_request`). This is per-user → CFS delegation, independent of which OnBase config
  I pick, so it's an **environment / auth-setup question**, not a defect in the switch tools.

**Verdict:** my three tools do exactly what the mentor asked — the agent can now list system configs and
flip the active ECM system to OnBase, and UCEB genuinely talks to the OnBase/CFS adapter as a result.
**Open question for the mentor:** which user/token + client is provisioned for **CFS token exchange** so
the OnBase call actually authenticates? Once that's sorted, `list_document_types` / `list_documents` /
download should return OnBase content with no further code change.
