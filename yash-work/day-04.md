# Day 4

_Focus: move the whole local stack from **staging → dev** (the agent lives in dev), create the
**plugin → agent** OAuth client in **dev IAM**, and figure out **which scopes/permissions** that
client needs._

---

## 1. Why we moved to dev (mentor directive)

The agent runs in the **dev** environment
(`appintel-dev-test.agent-studio.ai.dev.app.hyland.com`). Rule from mentor:

> If the agent is in dev, the client must also be in dev, and **UCEB dev** must be used everywhere.
> The MCP runs locally -> points to local UCEB -> which was pointing to **staging**. That's the
> problem. Point local UCEB to **dev**, then the **dev IAM** access token works.

So everything (UCEB API, MCP server, plugin) must authenticate against **dev IAM**, not staging.

### Dev URLs (confirmed from `appsettings.Development.json`)

| Purpose | Dev value |
|---|---|
| IAM authority | `https://auth.dev.app.hyland.com/idp` |
| authorize | `https://auth.dev.app.hyland.com/idp/connect/authorize` |
| token | `https://auth.dev.app.hyland.com/idp/connect/token` |
| endsession | `https://auth.dev.app.hyland.com/idp/connect/endsession` |
| Nucleus API | `https://api.platform.dev.app.hyland.com` |
| Content | `content.dev.app.hyland.com` |
| Audience | `uceb` |

**Good news:** the UCEB API's `appsettings.Development.json` **already points to dev**. Day 2 only
hit staging because we ran it with `Security__...=staging` **env overrides**. So "point local UCEB
to dev" = run it in `Development` **without** those overrides. No file edit needed there.

---

## 2. The two dev clients

Two separate IAM clients are needed in **dev**, for two different "doors":

| Client | For (door) | Type | Secret | Redirect URI |
|---|---|---|---|---|
| **MCP dev client** | MCP -> UCEB | web service app (confidential) | **Yes** | `https://uceb-mcp-local.dev.hyland.com:5005/callback` |
| **Plugin dev client** | You -> Agent | web service app + **PKCE**, public | **No** | `https://hmeanojcjlkalipmknanlcdimhhfjneb.chromiumapp.org/` |

- **MCP dev client:** ✅ created (this authenticates the MCP server to UCEB).
- **Plugin dev client:** ⏳ to be created — this is today's blocker (which scopes/permissions?).

Why a separate plugin client (recap): the MCP client is **confidential** (has a secret — unsafe in a
browser), its redirect URI is the loopback, and its token is scoped for **UCEB**, not the agent API.
The plugin needs its own **public/PKCE** client to authenticate **the user to the agent**.

---

## 3. HOW to find the scopes/permissions for the plugin -> agent client

The plugin token must be accepted by the **Agent Orchestrator API**. That means the client needs the
right **scopes**, and the user needs the right **roles**. These are two different things:

- **Scopes on the client** = what the app is allowed to *request*.
- **Roles on your user (via groups)** = whether you're actually *allowed* to do it.

### Reliable ways to discover the exact scopes

1. **Capture Studio's own login `scope=` (most reliable).**
   Studio is itself a client that calls the same agent. Whatever scopes it requests are exactly what
   the agent needs.
   - Open an **Incognito** window, F12 -> **Network** -> tick **Preserve log**.
   - Go to `https://appintel-dev-test.agent-studio.ai.dev.app.hyland.com`.
   - When it redirects to `auth.dev.app.hyland.com/idp/connect/authorize?...`, copy the **`scope=`**
     parameter value. Mirror that on the plugin client (keep `offline_access` for refresh).

2. **Decode an existing agent token's claims.**
   In a logged-in Studio tab, a request's `Authorization: Bearer <jwt>` token has `scope` and `aud`
   claims. Decode the JWT **locally** (e.g. jwt.ms) and read `scope` / `aud`.
   _Do NOT paste the token into chat/tools — it's a live credential._

3. **Look at the agent/Studio client registration in the dev admin portal.**
   Its **allowed scopes / API scopes** list shows what the agent platform expects.

4. **Reference the staging-test env** (mentor's tip): the equivalent client there already works —
   copy its scope + role setup into dev-test.

### Roles / groups (the "permissions" half)

- Add **your user** to the **group(s)** that grant agent access.
- Ensure those groups carry the roles to **invoke the agent** / **read UCEB**.
- Mirror how it's configured in **staging-test**.
- Note: **dev may have no config data** initially, so tools may return empty until data is created
  in **dev-test**.

---

## 3b. Scopes/permissions — ANSWER from the docs (2026-08-10)

Source: **AgentBuilderPlatform/UserGuide/Authentication** ("Authorization" page).

The Agent Builder Platform supports **three** auth methods — and **none is browser PKCE**:

| Method | Who | How |
|---|---|---|
| **Login Authorization** | interactive users | **only via Agent Builder Studio** (session/cookie) |
| **Client Credentials – External Application** | external apps | Service User + client secret, `grant_type=client_credentials`, scopes `hxp environment_authorization` |
| **Client Credentials – Restricted Scope** | internal Hyland only | Terraform-provisioned |

**Scopes for the Agent Orchestration API:** `hxp environment_authorization`.
**Client settings (external app):** add scopes `environment_authorization` + `hxp`; Application =
**`cin-agent-builder`**; select your Environment.
**Invoke-only permission:** put the user/service-user in group **`Agent Studio Solution Consumers`**.
IAM permissions cover **read / create / edit / invoke / delete** agents.
IAM Admin Portal: `https://admin.<ENV>.app.hyland.com/`.

### Key implication (raise with mentor)
The **documented** programmatic path is **Client Credentials** (a **Service User + secret**, one shared
machine identity) — essentially a service account, **not** a per-user browser login. There is **no
documented Authorization Code + PKCE public-client** method for the agent API, and interactive
per-user login only works **through Studio** (cookie/session — confirmed: `/bff` calls carry a
`Cookie` + `X-Csrf`, **no Authorization/bearer header**; the token is kept server-side in the BFF).

So a pure browser extension has two realistic paths:
1. **Add a tiny backend (BFF)** that holds the client secret and calls the agent via
   client_credentials; the extension talks to that backend. (Documented/supported.)
2. **Keep PKCE** and test whether the invoke API accepts a **user** bearer token carrying
   `hxp environment_authorization`. (Not documented — must verify.)

`config.js` scopes set to `openid profile offline_access hxp environment_authorization` pending this
decision.

---

## 4. Endpoint finding (base host)

DevTools capture (2026-08-07): the Studio SPA lists agents via
`https://appintel-dev-test.agent-studio.ai.dev.app.hyland.com/bff/agents?offset=0&limit=100`.

- `/bff/` = the **Backend-For-Frontend** for the Studio web app — usually **cookie/session** auth,
  not Bearer. That's the SPA's private backend.
- Our extension is a **Bearer** (PKCE) client, so it should target the **documented public `/v1`
  API** (same host, likely): `POST /v1/agents/{id}/versions/latest/invoke`.
- ⏳ Still confirming the real **chat POST** path (capture the invoke POST when sending a message in
  Studio chat) to be 100% sure of `/v1` vs a `/bff` chat route.

---

## 5. Changes made in code

- `browser-extension/src/config.js`: auth endpoints switched **staging -> dev IAM**; clientId
  placeholder renamed to `REPLACE_WITH_DEV_PUBLIC_CLIENT_ID`; redirect URI documented.
- Extension redirect URI confirmed via `chrome.identity.getRedirectURL()`:
  `https://hmeanojcjlkalipmknanlcdimhhfjneb.chromiumapp.org/` (Chrome ext id
  `hmeanojcjlkalipmknanlcdimhhfjneb`).

Pending code changes (on go-ahead):
- MCP server `appsettings.json` `Auth` endpoints **staging -> dev**.
- Update runbook so the UCEB API launch no longer forces staging overrides.

---

## 6. Progress

- [x] Dev URLs confirmed; UCEB Development config already dev.
- [x] MCP -> UCEB **dev** client created.
- [x] Extension config repointed to dev IAM.
- [x] Confirmed Studio SPA uses `/bff` (extension will use documented `/v1` Bearer API).
- [x] Capture Studio login `scope=` -> set plugin client scopes.
- [x] Create the **plugin -> agent** public/PKCE dev client + assign user to groups/roles.
- [x] Put plugin client id into `config.js`.
- [x] Repoint MCP `appsettings.json` to dev + set MCP dev client id/secret in user-secrets.
- [x] Test the plugin end-to-end.

---

## 7. OUTCOME (2026-08-10): full chain working end-to-end ✅

Sending **"check the health status"** in the extension returned real data from the UCEB backend
(`Healthy`, version `1.0.0+1adfd914…`). The complete path:

```
Extension (Chrome) → BFF (:5010) → Agent Builder API (api.agents.ai.dev) → dev tunnel → MCP (:5200) → UCEB API (:5000)
```

### 7.1 The architecture we shipped: a BFF with **per-user** PKCE

The docs (§3b) say the only documented programmatic path is **client_credentials** (one shared
service identity), and that per-user PKCE for the agent API is "undocumented". We built a small
**.NET BFF** that keeps the client secret server-side but still does a **per-user Authorization
Code + PKCE** login, and **empirically the per-user bearer token is accepted** by the invoke API.

- The extension runs the PKCE login (its `chromiumapp.org` redirect URI, which IAM accepts) and
  POSTs `{code, codeVerifier, redirectUri}` to **`/auth/exchange`**. The BFF adds the **client
  secret**, exchanges for the user's tokens, keeps them server-side, and returns an opaque
  **session id**.
- The extension then POSTs **`/api/chat`** with header **`X-BFF-Session`**; the BFF attaches the
  user's bearer token and calls the agent invoke endpoint.
- The secret and IAM tokens **never** reach the browser. Restarting the BFF wipes its in-memory
  sessions → you must sign in again in the extension.

**Plugin dev client** (per-user, in **dev IAM**): confidential "Web Server Application"
`yash-plugin-to-ai-client` (clientId `wsc-5906caef-…`); secret set via `dotnet user-secrets`
(`Auth:ClientSecret`). Token claims confirmed: `aud=[hxp, hxp.authorization]`,
`scope=[openid, profile, hxp, environment_authorization, offline_access]`.

### 7.2 Finding the real Agent Orchestration API host

The base host is **not** in the OpenAPI `servers:` block. It is only on the docs page
**"Testing the API" → Environment Setup**: `BASE_URL = https://api.agents.ai.<ENV>.app.hyland.com`
(ENV = `dev`/`staging`; omit for prod). So for us:

```
https://api.agents.ai.dev.app.hyland.com
POST /v1/agents/{agentId}/versions/{latest|versionId}/invoke
Body: { "messages": [ { "role": "user", "content": "…" } ] }
Authorization: Bearer <user token>
```

Sanity checks: `/health` → `200 {"message":"Healthy"}`; `/v1/models` & `/v1/agents` → `401` without
a bearer (all fast). **Dead-ends that wasted time** (do not retry): `appintel-dev-test.agent-*`
subdomains served a **dummy catch-all cert / SPA**; `api.ai.dev.app.hyland.com` is an **AWS API
Gateway** wanting SigV4/IAM (not bearer); `agent-studio…/bff` is **cookie/session** only.

### 7.3 The env split that actually works

- **Extension → Agent** hop = **dev** (agent lives in dev; plugin client + user token are dev).
- **MCP → UCEB** hop = **staging** (UCEB API run with `Security__…=staging` overrides, MCP
  auto-login is staging). This hop is **independent** of the agent's environment because the
  **Agent → MCP** hop authenticates with an **API key only** (not IAM). So MCP and UCEB just have
  to agree with **each other** (both staging = the tested runbook). MCP `appsettings.json` `Auth`
  was briefly flipped to dev, then **reverted to staging** to match the running UCEB.

### 7.4 Two timeout bugs that caused the "it just hangs" symptom

The agent kept hanging/`504`-ing on the first message. Root cause was **two separate ~100s caps**
colliding with the MCP server's interactive UCEB login:

1. **`UcebApiClient` had the default 100s `HttpClient.Timeout`.** The interactive login runs
   *inside* that client's pipeline (via `AuthTokenHandler`), so the 100s cap **cancelled the login
   at 100s**, before its own 120s window — the token never cached, so **every** message re-prompted
   a login and never succeeded. Fix: `httpClient.Timeout = Timeout.InfiniteTimeSpan` on the
   `UcebApiClient` registration (the login keeps its own 120s guard; UCEB calls are fast).
2. **The Agent Builder platform aborts any MCP tool call not answered within ~100s.** Even after
   fix #1, a human browser sign-in *during* a tool call can't beat that. Fix: a **login warm-up at
   MCP startup** (`RegisterLoginWarmup` on `ApplicationStarted`, HTTP mode) triggers the interactive
   login **before** any tool call, on a background thread. The user signs in **once** at startup;
   the cached token (singleton `InteractiveLoginService`, ~900s lifetime) serves every tool call
   instantly.

Also raised the **BFF invoke timeout 90s → 180s** and set its invoke `HttpClient` to
`Timeout.InfiniteTimeSpan` so the 180s cancellation token is the sole guard.

> ⚠️ Token lifetime is ~15 min. After it expires the next tool call does a fast **silent refresh**;
> if that ever fails and needs a fresh interactive login mid-call, it could hit the ~100s platform
> cap again — restart the MCP terminal to re-run the startup warm-up.

### 7.5 Startup order that worked

1. **UCEB API** on `:5000` (Development + `Security__…=staging` overrides).
2. **MCP server** on `:5200` (`-- --http`; signs into UCEB at startup via the warm-up).
3. **Dev tunnel** `devtunnel host -p 5200 --allow-anonymous` (URL is **ephemeral**).
4. **Studio** → set MCP **Server URL = `https://<tunnel>/mcp`**, Auth = API Key, header
   `X-API-Key`.
5. **BFF** on `:5010`.
6. Extension → sign in → chat.

### 7.6 Cleanup done

Removed the BFF `diag` route + `ProbeRoutes`/`DescribeToken` helpers + verbose failure logging +
the `Config:` startup log; removed `console.info` diagnostics from `auth.js`; deleted
`bff/probe-api*.ps1`.

---

## 8. Deep-dive: config APIs, "records", business objects, HFS folders (2026-08-10)

_This section answers the recurring confusion: **can UCEB (and therefore the MCP) create a new
"record" / business object / HFS folder, and if not, why does the MCP look like it can?**_

### 8.1 The four things people call a "record" (they are NOT the same)

| Term | What it really is | Where it lives | Created by UCEB? |
|---|---|---|---|
| **businessObjectType** (`busObject`) | a *type name* (e.g. `Account`, `campaign`) — the Salesforce object API name | Salesforce schema + UCEB **config** | **No** (you only *map* it in config) |
| **businessObjectId** (a "record") | one *instance* of that type — a real Salesforce record (e.g. an Account row) | **Salesforce** | **No** — UCEB never creates Salesforce records |
| **document** (HFS/CIC document) | a file stored in the HFS/CIC content store, filed under a `businessObjectId` | **HFS/CIC** content repo | **Yes** — this is the *only* "new record" the MCP creates (via upload+attach) |
| **HFS folder** | a container node in the HFS content/config repo | **HFS/CIC** content repo | **Yes, but internally only** (provisioning) — no public endpoint / no MCP tool |

The mentor's statement — "business-object type / record / folders inside HFS can be created using
UCEB" — is true **only at the provisioning/internal layer** (see §8.5). It is **not** exposed as an
on-demand REST endpoint, so the MCP cannot do it as a tool.

### 8.2 The UCEB config APIs (what they actually are)

All config lives under `api/config/...`. The important ones (from `RoutePaths.cs`):

| Route | Verb | Purpose |
|---|---|---|
| `api/config/solution-configurations/uceb` | **GET** | read the **whole** UCEB solution config for the LOB (business-object configs, viewer URL, capture rules, columns…) |
| `api/config/solution-configurations/uceb` | **POST** | **overwrite** the whole UCEB solution config (`UCEBConfigurationToValidate` = the GET body's `data` node). Needs `CanWriteUCEBConfiguration`. |
| `api/config/solution-configurations/uceb/{businessObjectType}` | GET | read just one business-object's config |
| `api/config/solution-configurations/lob` | POST | save LOB-level config (`CanWriteLOBConfiguration`) |
| `api/config/feature-flags/{flagKey}/evaluate` | GET | evaluate a feature flag for the caller's context |
| `api/core/content-platform` | GET | which ECM platform is wired (e.g. `CIC`/HFS, `OnBase`) |
| `api/core/platform-capabilities` | GET | what that platform supports |
| `api/core/document-types` (`?fetchMetadata=`) | GET | the document/content types = valid `ecmContentTypeName` values |
| `api/core/document-types/{id}/metadata` | GET | a type's attribute fields (e.g. `hfs_Name`) |
| `api/core/document-type-groups` | GET | the type groups |

> **Key point:** the config API is a **read-the-whole-blob / write-the-whole-blob** model. There is
> **no** "create one business object" verb. `add_business_object_config` (below) works by GET → append
> one entry → POST the entire blob back.

### 8.3 How the MCP tools map to those APIs (config side)

| MCP tool | Calls | What it does |
|---|---|---|
| `get_solution_configurations` | GET `.../uceb` | returns the full config JSON |
| `list_business_object_types` | GET `.../uceb` | pulls every `busObject` value out of the config |
| `add_business_object_config(busObject, ecmContentTypeName, displayColumn)` | GET then **POST** `.../uceb` | **append** a config entry mapping a `busObject` → a content type (enables upload) + a display column (enables listing). No-op if it already exists. Needs `CanWriteUCEBConfiguration`. |
| `get_content_platform` / `get_platform_capabilities` | GET `.../content-platform`, `.../platform-capabilities` | environment info |
| `list_document_types` / `get_document_type_metadata` / `list_document_type_groups` | GET `.../document-types…` | discover valid `ecmContentTypeName` + its fields |
| `evaluate_feature_flag` | GET `.../feature-flags/{key}/evaluate` | flag check |

**This is where the confusion comes from:** `add_business_object_config` *sounds* like "create a
business object", but it only edits **configuration** — it declares "documents for `busObject` X get
filed as content type Y and shown with column Z." It does **not** create a Salesforce record and it
does **not** create anything in HFS by itself.

### 8.4 How a NEW document ("record") is actually created — the only create path in the MCP

The upload tools (`upload_document`, `upload_document_from_content`, `upload_pasted_image`) all funnel
through the same **3-step** flow (`UcebTools.AttachContentAsync` → `UcebApiClient`):

1. **Initiate** — `POST api/core/documents/upload` → returns an `uploadId`.
2. **Complete** — `POST api/core/documents/upload/{uploadId}/complete?PartNumber=0&ChunkCount=1&FileName=…&MimeType=…`
   with the file bytes as one `application/octet-stream` chunk.
3. **Attach** — `POST api/core/business-objects/{businessObjectId}/documents` with JSON
   `{ ecmContentTypeName, fileName, uploadId, businessContext:{ businessObjectType, boContextUser }, businessObjectAdditionalAttribute:[{ name:"hfs_Name", … }] }`
   → returns the new **documentId**.

Under the hood (`MultipartFormCaptureService`) step 3 builds an HFS document node:
`sys_primaryType = <ecmContentTypeName>`, `sysfile_blob = { uploadId }`,
`sys_title = "<file>-<type>-<date>"`, and stamps the business-object id onto the doc as the
`uceb_boContextId` attribute (that attribute is how `list_documents` finds all docs for a record).

So: **the "new record in HFS" = a new document filed under an *existing* `businessObjectId`.** The
`businessObjectId` itself must already exist in Salesforce — UCEB does not create it.

**Pre-reqs for a dev upload to succeed** (dev-test may start empty, per §3):
- a valid `ecmContentTypeName` → `list_document_types`;
- the `businessObjectType` mapped in config → `list_business_object_types`, or add it with
  `add_business_object_config`;
- a real `businessObjectId` (a Salesforce record id) to attach to;
- file extension in the allowed set: `bmp, jpg, jpeg, png, pdf, tiff, docx, pptx, xlsx, txt, json, mp4, mp3`.

### 8.5 HFS folders — created by UCEB, but only internally (why there's no tool)

`Infra.Hx/.../Folder/FolderService.cs` **does** create HFS folders — it `POST`s an `HxFolder`
(`PrimaryType=folder`) with an ACL to the Hx content API, and `EnsureFolderPathExists` walks a path
creating each missing segment. **But**:

- It runs during **provisioning / repo setup** (config-repo + content-repo folder structure, ACLs per
  role), **not** on a per-request basis.
- There is **no controller / REST route** that exposes "create folder" (see the full `RoutePaths` list
  — only config, upload/attach, query, download, delete, revision). No route ⇒ **no MCP tool**.

So the mentor is right that *UCEB's codebase* can create HFS folders/records structurally; but the
**public API surface** the MCP wraps has no such endpoint, which is why the MCP correctly has no
"create business object / create folder" tool.

### 8.6 So — can the MCP "create a new record"? Straight answer

- **Create a new Salesforce business object (record) of a type** → **No.** Not UCEB's job; belongs to
  Salesforce. No endpoint, no tool.
- **Create/declare a business object *type* mapping in config** → **Yes**, via
  `add_business_object_config` (config only — enables upload/list for that `busObject`).
- **Create a new HFS document ("record") under an existing record** → **Yes**, via the `upload_*`
  tools (the 3-step flow). This is the real "new record in HFS documents of dev."
- **Create an HFS folder on demand** → **No public API / no tool** (only internal provisioning).

If we ever need on-demand "create business object" or "create folder", we'd have to (a) add a new
UCEB controller/endpoint that exposes `FolderService` / a Salesforce-create, then (b) add an MCP tool
that wraps it. Neither exists today.

### 8.7 Dev vs staging for uploads (recap tie-in)

Uploads land in whatever environment the **UCEB API** points to. To upload into **dev HFS**: run the
UCEB API in `Development` **without** any `Security__…=staging` overrides (so it uses
`content.dev` / `api.platform.dev` / `auth.dev`), and make sure the MCP server signs in with the
**dev** MCP confidential client (user-secrets `Auth:ClientId`/`Auth:ClientSecret`) so its token is a
**dev** token the dev API will accept. Both sides of the MCP→UCEB hop must be the **same** env.

### 8.8 So is a `businessObjectId` a folder or a record in HFS? **Neither — it's an attribute**

> ⚠️ **CORRECTION (see Day‑5):** This section is **wrong for our CIC/Hx stack.** It traced the
> **Workday** capture path (`MultipartFormCaptureService` / `WorkdayBusinessObjectTypeUtils`). The real
> CIC path is `DocumentArchiveService`, where the folder **is** `{appKey}-documents/{businessObjectId}`
> and the per‑id folder is **auto‑created on upload**. Read [day-05.md](day-05.md) for the corrected model.

This is the most common mix-up. Traced through `MultipartFormCaptureService.CreateOrUpdateDocMetadata`
and `DocumentQueryAdapterCIC`, here is what physically exists in HFS/CIC:

- **Folder = per business-object TYPE, not per id.** On upload the doc is created via
  `POST {hx}/api/documents/path/{folderName}` where `folderName` comes from the **`businessObjectType`**
  (`WorkdayBusinessObjectTypeUtils.GetContentFolderName(businessObjectType)`). So there's roughly **one
  content folder per type** (e.g. one for `Account`), created/reused by HFS — **not** one folder per record.
- **Document = the actual HFS node ("record").** Each uploaded file becomes a content node
  (`sys_primaryType = <ecmContentTypeName>`, `sysfile_blob = { uploadId }`, `sys_title`). This node is
  the thing the API returns as `documentId`.
- **`businessObjectId` = a metadata ATTRIBUTE stamped on that document**, not a node/folder. Capture
  writes it into the configured boContext field (default `uceb_boContextId`). It is **not** created in
  HFS at all — it's a **Salesforce record id** that already exists in Salesforce; UCEB just copies the
  value onto the document as a tag.
- **"Documents for a record" = a SEARCH, not a folder listing.** `GET /api/core/business-objects/{boId}/documents`
  runs an HXQL query over the content type filtered by that boContext attribute
  (`DocumentQueryAdapterCIC`: "CIC has no native query concept; a content type is treated as a query").
  So docs are grouped **logically by attribute value**, not by living in a folder named after the id.

**Answer in one line:** the `businessObjectId` is neither a folder nor a record inside HFS — it's an
**attribute/label** that links HFS **documents** (the records) to an **existing Salesforce record**. The
only real HFS folders are per **type**, and they're created by HFS/provisioning, not per upload.
