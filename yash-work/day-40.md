# Day 40 — ServiceNow (hfsn) CIC build in Studio Modeller: base schema + mixin + content types

_Continues day-39 (ServiceNow LOB `appKey = hfsn`). Goal today: build the CIC content model in Studio
Modeller that the ServiceNow solution config will reference, mirroring the Salesforce `hfs-configurations`
project. Learning part-by-part while building; Bruno (create/get/edit/post the solution config) still on
hold until the CIC model exists._

---

## Decisions locked

- **Separate project:** created a NEW Studio Modeller project **`hfsn-configurations`** (distinct from
  `hfs-configurations`) for all ServiceNow content types/schemas. Rationale: `hfsn` is a separate appKey
  with its own content folder (`hfsn-documents`) + solution config, so its content model lives in its own
  project/CIC application. (Resolves the day-39 open question "new project vs reuse".)
- **Scoping key = ServiceNow `number`** (mentor): `boProp = "number"` (e.g. `CHG0040019`), verified in the
  DOM for change_request / incident / sc_req_item on day-39 via `sys_original.<table>.number`.

---

## 1. `hfsn-base-schema` (mirror of `hfs-base-schema`)

`hfs-base-schema` (prefix `hfs`) holds exactly 2 scoping fields — `hfs_sObject_Id`, `hfs_sObject_owner` —
and `hfs-lob-mixin` injects them into every content type. ServiceNow mirror:

- Schema **`hfsn-base-schema`**, prefix **`hfsn`**, desc "Base schema for ServiceNow (hfsn) that holds the
  LOB business context (record scoping)."
- Fields (type String, not required, single-valued):
  - **`hfsn_recordNumber`** ← stores the ServiceNow `number` → `boContext.ecmProp`
  - **`hfsn_recordOwner`** ← stores the owner → `boUser.ecmProp`

Maps to solution-config `contextParams`:
```json
"boContext": { "boProp": "number",            "ecmProp": "hfsn_recordNumber", "ecmPropId": "<assigned at publish>" },
"boUser":    { "boProp": "<sys_created_by | opened_by — TBD>", "ecmProp": "hfsn_recordOwner", "ecmPropId": "<assigned at publish>" }
```

OPEN: confirm the `boUser.boProp` ServiceNow owner field (`sys_created_by` vs `opened_by`) and verify it's
DOM-readable (`sys_original.<table>.<field>`).

---

## 2. `hfsn-lob-mixin` — DONE

Created mixin **`hfsn-lob-mixin`** (desc "Any content type that uses this mixin participates in ServiceNow
(hfsn) integration. Uses hfsn-base-schema."), Selected schema = **`hfsn-base-schema`**. Mirror of
`hfs-lob-mixin`. This is the glue: any content type including it inherits the scoping fields.

## 3. `hfs-base-type` structure (confirmed from screenshot) → building `hfsn-base-type`

`hfs-base-type` recipe = **Parent `SysFile`** (gives file/document capability: inherited schemas
`sysfile, sysfiles, system` + inherited mixin `SysFilish`) **+ selected mixin `hfs-lob-mixin`** (scoping
fields). Schemas Selected = EMPTY (hfs-base-schema arrives via the mixin, NOT added directly).

ServiceNow mirror → create content type **`hfsn-base-type`**:
- Parent Content Type = **`SysFile`**
- Selected mixin = **`hfsn-lob-mixin`**; Selected schema = none (don't add hfsn-base-schema directly)
- desc: "Base type that uses hfsn-lob-mixin. Primary types extended from this one participate in
  ServiceNow (hfsn) integration."

**DONE (saved):** `hfsn-base-type` created = perfect mirror (Parent `SysFile`, inherited mixin `SysFilish`,
selected mixin `hfsn-lob-mixin`, inherited schemas `sysfile, sysfiles, system`, no direct schema).

## 4. Next: per-table content types + schemas (in progress)

Each ServiceNow table = a **schema** (its metadata fields, prefix `hfsn`) + a **content type** (parent
`hfsn-base-type` + that schema). Mirrors Salesforce (bill-schema→bills-content-type, case-schema→
case-content-type). Starting with **change_request** (CHG0040019 available to test). Metadata field set
per table = a design decision (TBD with mentor); start lean, can extend.

**DONE:** `change-request-schema` (prefix `hfsn`) with 4 String fields: `hfsn_documentName`,
`hfsn_shortDescription`, `hfsn_changeState`, `hfsn_priority`. Next: `change-request-content-type`
(parent `hfsn-base-type`, selected schema `change-request-schema`).

**DONE:** `change-request-type` content type created — parent `hfsn-base-type`, inherited schemas
`hfsn-base-schema, sysfile, sysfiles, system`, inherited mixins `hfsn-lob-mixin, SysFilish`, Selected
schema `change-request-schema`. (Name capped at 26 chars → used `<table>-type` convention:
`change-request-type`; later `incident-type`, `sc-req-item-type`.) CIC model for change_request COMPLETE.

Decision (user): build ONE busObject (change_request) first, test end-to-end, then clone for the rest.

## 5. Next: publish + solution config (in progress)

- **Validate + Release** the `hfsn-configurations` project so the content types go live in the CIC app
  that UCEB queries (drafts aren't visible until released).
  - Validate warning (non-blocking): "prefix 'hfsn' shared by multiple schemas (change-request-schema,
    hfsn-base-schema)... enforced as an error in a FUTURE release." Decided to IGNORE — `hfs-configurations`
    shares the `hfs` prefix across all its schemas the same way and works; no field-name collision. Keep
    shared `hfsn` prefix (base scoping schema MUST stay `hfsn`). Release not blocked by warnings.- Build `servicenow-solution-config.json`: operationConfig; `boContext{boProp:number,
  ecmProp:hfsn_recordNumber, ecmPropId:<id>}` + boUser; ONE `additionalConfig` entry for change_request →
  `change-request-type` (ecmContentTypeId = name, groupId null, string column ids hfsn_*) with import
  mappings; configVersion. OPEN: whether ecmPropId must be the CIC-assigned numeric id or the field name
  (Salesforce used numeric 223/224 — resolve when wiring).
- Set hfsn client in MCP user-secrets (Auth:ClientId/ClientSecret), then POST the config via Bruno (or the
  MCP set_solution_configuration tool) and test upload/list on CHG0040019.

## 6. Solution config DRAFTED — `servicenow-solution-config.json`

Released project version 1. Filled the blank config with the change_request mapping (CIC-native, modeled on
the Salesforce `case` entry):
- `boContext { boProp:number, ecmProp:hfsn_recordNumber, ecmPropId:hfsn_recordNumber }`,
  `boUser { boProp:opened_by, ecmProp:hfsn_recordOwner, ecmPropId:hfsn_recordOwner }`
- ONE `additionalConfig` entry: `change_request → change-request-type` (groupId null, string column ids
  `hfsn_documentName/shortDescription/changeState/priority`); import mappings (documentName inputSource 4
  = File Name, rest inputSource 6 = user-entered); displayColumns = the 4 fields.
- `queryConfig` entry: `change_request` → ContentType query `change-request-type` (default true, empty
  filterClauses — scoping by number applied automatically by UCEB from boContext).
- ASSUMPTIONS to verify on test: (1) `ecmPropId` = string field name (Salesforce used NUMERIC 223/224 —
  may need the CIC-assigned numeric id); (2) `boUser.boProp = opened_by` (secondary, refine/verify in DOM).

**RESOLVED (1) — validated UCEB src:** numeric `ecmPropId` is **OnBase/CFS-ONLY** (used as the CFS
`SimpleFieldTypeId`/keyword id, and REQUIRED there). CIC-native scopes by the STRING field name: upload
uses `ecmProp` (EcmPropId never referenced in `Infra.Hx`); list uses `ecmPropId ?? ecmProp`. So our
`ecmPropId = "hfsn_recordNumber"` is CORRECT for CIC. GOTCHA: `??` coalesces only on null → never set
`ecmPropId = ""` (would break list filter); use the field name or omit the key. (User's hypothesis = TRUE.)

## 7. Remaining path to test (change_request end-to-end)
1. POST this config to UCEB as hfsn — via **Bruno** (resume on-hold auth: OAuth2 Auth Code + PKCE, then
   GET empty config → POST → GET verify) OR MCP `set_solution_configuration`.
2. hfsn client auth (Bruno holds client id+secret directly; MCP path needs user-secrets Auth:ClientId/Secret).
3. Extension detector change: detect ServiceNow records + read `number` from `sys_original.<table>.number`
   (all_frames) so the panel scopes to CHG0040019.

## 8. Bruno OAuth attempt (hfsn) → BLOCKED "Not authorized"

Resumed Bruno. Used empty `access_token` collection, collection-level Auth = OAuth2 Authorization Code +
PKCE. Callback URL reused the MCP's registered redirect `https://uceb-mcp-local.dev.hyland.com:5005/callback`
(already registered on hfsn client; Bruno intercepts the redirect, no local server needed). Authorize/token
= auth.staging. Scopes = Salesforce set.

Result: **login prompt shown → entered AppIntel staging account + pwd → authenticated OK → IAM error page
"An error occurred... Not authorized" (Request ID ae84bc8011f60e4116e9abc46e070e3f).** So AUTHN succeeds,
AUTHORIZATION fails after login — mirrors the Workday `environment_authorization` consent-failure pattern.
Likely cause: user not granted a scope (prob `environment_authorization`) on the NEW hfsn client, or the
hfsn client not tied to an environment the user can access → IAM registration gap on hfsn client (mentor/
IAM admin fix), NOT a Bruno problem. Diagnostic in progress: scope-isolation (start `openid profile`, add
back one at a time to find the culprit). environment_authorization is REQUIRED (delivers the hfsn appKey).

**CONFIRMED (scope isolation):** `openid profile` → token fetched OK (authn + client + redirect all fine).
Adding `environment_authorization` → "Not authorized" (Request ID 7c25053ac8953b934e07e89a902e0698). So
`environment_authorization` is the blocker: the AppIntel staging user is NOT granted it on the hfsn client.
Same pattern as the Workday client. CANNOT work around by dropping it (unlike Workday) — for CIC/hfsn this
scope stamps the `hfsn` appKey, and the solution config is stored keyed by appKey (uceb_lob-name); without
it the write hits the MissingLobError guard. => HARD BLOCKER, needs IAM fix on the hfsn client:
  (a) grant the user `environment_authorization` on the hfsn client, AND
  (b) ensure the hfsn client's IAM Application is tied to the AppIntel-staging environment the user can access
      (that association is what mints appKey=hfsn).
Escalate to mentor/IAM with: hfsn client id, user account, scope `environment_authorization`, Request IDs
(ae84bc8011f60e4116e9abc46e070e3f, 7c25053ac8953b934e07e89a902e0698). OUR side is done (CIC model built +
released, servicenow-solution-config.json drafted); blocked only on this IAM grant. Same wall would hit the
MCP (same client+scopes), so no local workaround.

**REFINED (saw the hfsn client in admin.staging.app.hyland.com):** client `yash_hia_servicenow_client` id
`wsc-84411124-abaa-40ca-800f-ae5134e1fa78`, redirect `https://uceb-mcp-local.dev.hyland.com:5005/callback`,
Allowed Scopes INCLUDE `environment_authorization` (+ hxp, hxp.nucleus.account, hxps, offline_access, openid,
profile, uceb), Environment = **Appintel-Staging Test Environment**, Application = **Hyland for ServiceNow**.
So the CLIENT config is fine. => The "Not authorized" is a USER-authorization gap: the signed-in AppIntel
staging account is not a member/role-holder in `Appintel-Staging Test Environment` / `Hyland for ServiceNow`.
NEXT: compare the WORKING Salesforce staging client (wsc-f3ec0fd9-...) Environment + Application; then ensure
the user is authorized in the ServiceNow env/app (admin portal env membership / app role assignment, or ask
mentor/env owner).

## 9. UNBLOCKED — user added to ServiceNow user groups → auth works

Fix: the AppIntel staging identity user was NOT in the ServiceNow user groups; user added themselves. After a
fresh login, Bruno **token fetched successfully** with full scopes (openid profile offline_access uceb
environment_authorization hxp.nucleus.account hxp). => the "Not authorized" WAS a user group-membership gap,
not client config.

### Confirmed UCEB solution-config contract (read from MCP UcebApiClient.cs + UcebTools.cs):
- GET & POST same endpoint: `/api/config/solution-configurations/uceb?systemFriendlyName=cic`
  (SolutionConfigPath for CIC = `api/config/solution-configurations/uceb`; `cic` = default CIC-native system).
- POST body = the config **`data` node** `{configurations, configVersion}` = EXACTLY servicenow-solution-config.json.
- set_solution_configuration tool POSTs dataJson as-is to that endpoint (needs config-write permission).

### Bruno GET result (get-config, systemFriendlyName=cic): HTTP 500 but EXPECTED/healthy:
`{ data:null, total:0, error:{code:"21404", message:"Default Configuration not found for 'uceb' for appKey
'hfsn'."}, resolvedContext:{ systemFriendlyName:"CIC", systemConfigId:"00000000-0000-0000-0000-000000000000",
systemType:"CIC", lobName:"hfsn" } }`. => auth OK, token resolves appKey **lobName=hfsn**, `cic` resolves to
the CIC system; 500 is just "no config exists yet" (nothing posted). systemConfigId all-zeros = default CIC.
NEXT: POST servicenow-solution-config.json to the same endpoint, then re-GET to verify.

## 10. POST → HTTP 500 EMPTY body. ROOT CAUSE (researched UCEB src) = hfsn NOT onboarded as a LOB in UCEB

The POST never reaches the controller action — it dies in **FluentValidation auto-validation of the body**:
- Validator `UCEBConfigurationValidator` loads a per-appKey resource file
  `Configuration/Resources/lookup.uceb.<appKey>.v1.json` → for `hfsn` the file **does not exist** →
  `UCEBContentConfigRepoLookupService.GetLookupData` returns Data=null → validator derefs null →
  **NullReferenceException** → staging pipeline has **no UseExceptionHandler** (only in Development) →
  Kestrel returns **HTTP 500 empty body**. (Not body-shape, not permission, not system-config.)
- The GET's 21404 is the SAME missing-LOB issue: the read fallback looks for shipped
  `default.uceb.hfsn.v1.json` (also absent) → "Default Configuration not found".
- Bundled LOBs in deployed UCEB = **hfs, wdx, sufa, sap-hana, hfgw-bc, hfgw-cc, hfgw-pc — NO hfsn.**
- Permissions: POST auth = scope `uceb.configuration.write`; our token HAS it (POST reached validation, not 403).

### Everything on OUR side is correct — confirmed by the trace:
auth works, token appKey=hfsn, `cic` resolves to CIC, our config body binds fine (`configVersion` ignored;
controller hard-sets ConfigType=UCEB, ConfigVersion=1, FriendlyName from query). Blocked ONLY by the missing
UCEB resource files for the new LOB.

### FIX (UCEB-side code/deploy, NOT a Bruno/API/config action):
1. Add `lookup.uceb.hfsn.v1.json` (MANDATORY — unblocks POST validation) + `default.uceb.hfsn.v1.json` to
   UCEB `Infra.Hx/Configuration/Resources/` (copy `hfs`’s, adjust), marked CopyToOutputDirectory in
   Infra.Hx.csproj; possibly add `uceb.hfsn_configuration.*` perms in PermissionPolicies.cs.
2. Redeploy staging UCEB — OR run UCEB LOCALLY with those files + point Bruno/MCP at the local UCEB to test now.
3. After onboarding, POST works (body must keep `endUserGroupConfig` with ≥1 `adminUser`, else handled 21403;
   and fileTypesAllowed/pageSize/fileSizeLimit/ecmRenditionType values must be valid keys in the new lookup).
ESCALATE to mentor: hfsn needs UCEB LOB onboarding (lookup+default resource files). Likely their call/owns deploy.

## 11. LOCAL UCEB onboarding DONE (to test hfsn now, bypassing deployed staging)

Changes made in the UCEB repo (C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api):
- Created `Infra.Hx/Configuration/Resources/lookup.uceb.hfsn.v1.json` (copy of hfs — generic file-type/
  input-source/rendition/page+size whitelists; our config's values all valid) + `default.uceb.hfsn.v1.json`
  (hfs default shape with hfsn scoping: boContext number→hfsn_recordNumber, boUser opened_by→hfsn_recordOwner,
  ServiceNow groups).
- Registered both in `Hyland.Experience.UCEB.Infra.Hx.csproj` (Content Include + None Remove, CopyToOutputDirectory).
- Pointed local UCEB at STAGING via API user-secrets (project uceb-api-local-9f2c1a7e):
  `Security:AuthorityUrl=https://auth.staging.app.hyland.com/idp`,
  `Security:NucleusApiBaseUrl=https://api.platform.staging.app.hyland.com`,
  `Security:ContentBaseUrl=content.staging.app.hyland.com` (so the staging hfsn token is accepted + content
  resolves to staging CIC). appsettings.Development.json default = DEV (untouched; secrets override).
- Built OK (0 errors); both hfsn resource files confirmed in bin output. Started `dotnet run` → health
  `GET http://localhost:5000/kubernetes/health` = 200 Healthy. CIC capabilities all AVAILABLE. (Datadog
  127.0.0.1:8126 errors = harmless, no local trace agent. "Now listening" line suppressed by Serilog
  Microsoft=Warning override.)
- NOTE: this is LOCAL only. For deployed staging to accept hfsn, UCEB team must add the same 2 resource
  files (+ maybe perms) and redeploy. Local UCEB WRITES the config to the same staging CIC content platform,
  so the created config persists in staging.
NEXT (user): repoint Bruno get-config/post-config to http://localhost:5000/... (same path+?systemFriendlyName=cic,
Auth still Inherit — local UCEB validates the staging token), run GET (should 200 now, returns default hfsn
config), then POST (should 200 saved), then GET to verify the change_request config. Watch the local UCEB
terminal for the REAL exception if anything 500s.

## 12. ✅ SOLUTION CONFIG CREATED for hfsn (POST 200, data:true) — day-40 milestone

Sequence against local UCEB (localhost:5000, pointed at staging):
- GET → 200, returned the default hfsn config (onboarding worked; no 21404).
- POST (1st) → 422 `21400 Invalid User group provided - ServiceNow Solution Administrators` (validation PASSED;
  handled group-name error). My placeholder group names were PLURAL + not real.
- Real group names (user belongs to): **"ServiceNow Solution Administrator"** (adminUser) + **"ServiceNow
  Content User"** (contentUser) — SINGULAR. UCEB resolves endUserGroupConfig by NAME (id "") via
  GetHxprMemberGroup; name must match a real platform member group. Fixed names in servicenow-solution-config.json.
- POST (2nd) → **200 OK `{data:true, total:1, error:null}`** — config SAVED to staging CIC (folder
  hfsn-solution-configurations), keyed by appKey hfsn.

Full hfsn pipeline now proven: Studio Modeller CIC model (hfsn-base-schema/mixin/base-type/change-request-type,
released) → hfsn LOB onboarded in UCEB (lookup+default resource files) → hfsn client auth (env group membership)
→ solution config POST 200.

### REMAINING to test in the actual side-panel UI:
1. Extension detector: detect ServiceNow records + read `number` from `sys_original.<table>.number` (all_frames)
   → scope panel to CHG0040019. (code change in agent-ng/browser-extension + MCP ServiceNow detection.)
2. Point the MCP/extension stack at local UCEB (localhost:5000) with the hfsn client, OR onboard hfsn in the
   DEPLOYED staging UCEB (add the 2 resource files + redeploy) so the deployed path works without local UCEB.
3. Then upload a doc against CHG0040019 + list it. Also clone content types for incident/sc_req_item.

## 13. Extension detector + MCP wiring for ServiceNow (day-40)

- **detector.js `detectServiceNow` REWRITTEN** to scope by the record NUMBER (config boProp="number"), not
  sys_id. New `findServiceNowNumberInput(doc)` recurses the top doc + same-origin child iframes to read
  `input[id="sys_original.<table>.number"]` (covers the Next Experience nested "classic" frame WITHOUT needing
  manifest all_frames — same trick as our DOM probe). Returns {rawType: table, businessObjectId: number}. No
  rebuild needed (static content script) — just reload the unpacked extension. (agent-ng parseUrl URL-fallback
  still returns sys_id for ServiceNow — only a last resort; the content script is primary. Could null it later.)
- **switch-lob.ps1: added `servicenow` LOB** — Scopes=Salesforce set (no wdx), Iam=staging,
  UcebBaseUrl=http://localhost:5000 (local UCEB), BasePath=api (CIC). **ClientFromSecrets=$true** → the client
  **id + secret come DIRECTLY from the MCP user-secrets `Auth:ClientId` / `Auth:ClientSecret`** (set yourself;
  NOT -SetSecret, NOT hardcoded). The script only flips endpoints/scopes/Uceb:BaseUrl. MCP SystemFriendlyName=cic.

### RUN PLAN (full panel upload test):
0. DEPLOY the hfsn-configurations app in Studio Admin first (section 14) — content model must be LIVE.
1. Keep local UCEB running (dotnet terminal, :5000).  2. Set the hfsn client in MCP user-secrets (once):
   `dotnet user-secrets set "Auth:ClientId" "wsc-84411124-abaa-40ca-800f-ae5134e1fa78" --project <MCP csproj>`
   + `dotnet user-secrets set "Auth:ClientSecret" "<hfsn secret>" --project <MCP csproj>`. Then
   `.\switch-lob.ps1 -Lob servicenow` (no -SetSecret; reads id+secret from Auth:* secrets) → MCP interactive
   login as hfsn (browser) → appKey hfsn, points at local UCEB.  3. Start BFF + tunnels (usual demo startup).
   4. Reload the unpacked extension in Chrome.  5. Open a change_request record (CHG0040019) → open side panel
   → should detect change_request + number, list docs (empty), then UPLOAD a doc → stored as change-request-type
   scoped by hfsn_recordNumber=CHG0040019. Watch local UCEB terminal for real errors.

## 14. MENTOR (Nilotpal): RELEASE ≠ DEPLOY — must DEPLOY the content model (prereq for upload)

Studio Modeller **Release** only creates the release version; to make the content types LIVE in the environment
you must **Deploy** via **Studio Admin (SA)**: DevOps → Project Releases → select `hfsn-configurations` row
(latest release = version 1) → Kebab ⋮ → **Deploy**. The solution-config POST succeeded WITHOUT this (config
save doesn't verify content-type existence), BUT uploads/lists against `change-request-type` would FAIL until
deployed. Deploy Application wizard steps: 1) Application Name=hfsn-configurations, Project Release=1,
Environment=Appintel-Staging Test Environment (checkboxes off). 2-4) Variables/Secrets/Validation — none for a
config project, Next through. 5) Permissions (required*): Admin users=self, Admin groups=ServiceNow Solution
Administrator, Users=self, User groups=ServiceNow Content User; Manager/External integration optional. → Deploy.
=> DO THIS before the upload test.