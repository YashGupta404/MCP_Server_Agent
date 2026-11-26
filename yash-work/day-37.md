# Day 37 — ServiceNow LOB: research to build the integration from scratch

_Goal (manager): build the SAME extension solution for **ServiceNow** as we have for Salesforce and
Workday. The catch: ServiceNow is a **brand-new LOB** — unlike SF/Workday it has **no UCEB system
config, no solution config, and no UCEB/LOB endpoint settings** yet. So the work is to **connect
ServiceNow to UCEB first**, then configure it. This day = extensive research + a concrete plan +
a learning path. No production code changed today._

---

## 1. How a LOB actually connects to UCEB (the model we must replicate)

The stack (same for every LOB):
```
Extension side panel → BFF (:5010) → [chat: Agent Builder → dev tunnel] → MCP (:5200) → UCEB API → ECM (CIC / OnBase-via-CFS)
```

Two facts that define what a "LOB" even is:
1. **UCEB is a content broker, not a record store.** It never creates the ServiceNow record. It files
   documents under a folder named after the `businessObjectId`. HFS layout =
   **`{appKey}-documents/{businessObjectId}/<docs>`**, auto-created on first upload
   (`DocumentArchiveService.EnsureFolderPathExists`).
2. **`{appKey}` = the LOB, baked into the signed-in token.** `IAuthService.GetAppKey(token)` reads it
   from the `hxp_authorization` claim. Observed app keys: **Salesforce = `hfs` → route `api` (CIC)**;
   **Workday = `wdx` → route `bow`**. The MCP's `LobRoutingContext` derives the record-doc route from
   that app key (`salesforce→api`, `workday→bow`). So the "LOB" is really **the IAM Application the
   confidential login client is registered under**, which stamps the app key.

### The 4 pieces that added Salesforce (the template for ANY new LOB)
1. **Page detection** — `detector.js` reads `{type, id}` from the URL (SF: `/lightning/r/<Obj>/<18id>/view`).
2. **manifest.json** — host_permissions + content_scripts.matches for the LOB's hosts.
3. **UCEB business-object config** — `add_business_object_config(busObject, ecmContentTypeName,
   displayColumn)` maps each record type → an ECM content type (+ import mappings + queries). Without it,
   uploads 23400 and lists return nothing.
4. **Auth/routing** — the MCP signs in via a confidential Auth-Code+PKCE client registered under the
   LOB's **IAM Application**, whose token's app key routes the content folder.

---

## 2. What ALREADY exists for ServiceNow (free, from prior work)

- **Extension detection is DONE.** `browser-extension/src/content/detector.js` `detectServiceNow(loc)`:
  `.../<table>.do?sys_id=<guid>` and `/now/nav/ui/.../<table>/<sys_id>`; `SITE_TYPE_MAP` already maps
  `incident`, `change_request`, `sc_req_item`. The Angular `context-detector.service.ts` also URL-parses
  ServiceNow (`/<table>.do?sys_id=`). ServiceNow `sys_id` is a 32-char GUID (short → no hashing needed).
- **manifest.json** already has `https://*.service-now.com/*` in host_permissions + content_scripts.
- So the CLIENT side is essentially ready. **The gap is entirely BACKEND/config: UCEB has no ServiceNow
  LOB/appKey, no system config, no solution config.**

---

## 3. The core decision (Phase 0 — ask the mentor FIRST)

**Does a "Hyland for ServiceNow" IAM Application / appKey exist in the platform?** This decides everything:

- **Option A — reuse the existing `hfs` (Salesforce/CIC) LOB (LIGHTEST, likely the fastest demo path).**
  UCEB only knows `appKey + businessObjectType + businessObjectId` — it does NOT care that a record came
  from "ServiceNow". So ServiceNow tables (`incident`, `change_request`, …) can be added as **new
  business object types under the SAME `hfs` appKey**, filing into `hfs-documents/{sys_id}` in the same
  CIC (or OnBase-via-CFS) ECM Salesforce already uses. **No new IAM client, no UCEB code, no new route.**
  Work = solution-config business-object mappings + (optional) a dedicated system config. This is the
  SF recipe pieces #3 only (detection + manifest already done).

- **Option B — dedicated ServiceNow LOB (new IAM Application → new appKey → possibly a new UCEB route).**
  Needed only if (a) Hyland ships a real "Hyland for ServiceNow" product/appKey the manager wants used,
  (b) ServiceNow content must be isolated in its own `{appKey}-documents` silo, or (c) ServiceNow auth
  must be its own identity. This mirrors the **Workday** effort: create a confidential Auth-Code+PKCE
  client under the ServiceNow Application, add the appKey→route mapping in `LobRoutingContext`
  (`WorkdayAppKeys` equivalent), and wire `switch-lob.ps1` with a `servicenow` entry. Heavier.

> Recommendation to propose: start with **Option A** to get a working end-to-end demo fast (it needs
> zero UCEB code), and only move to Option B if the mentor says ServiceNow must be a first-class LOB.

---

## 4. What "connect ServiceNow to UCEB" concretely requires (the build plan)

Assuming **Option A** (reuse `hfs`/CIC), mapped to the tools we already have:

### Phase 1 — pick/confirm the ECM (System Config)
- Decide where ServiceNow docs live: reuse an existing CIC system config (e.g. the default `CIC`) or the
  OnBase one, OR create a dedicated one via `create_system_configuration(systemType, configJson)` (tool
  exists; needs config-admin on the user + fresh login). System config = WHICH repository + how to connect
  (CIC vs OnBase/CFS). For a first demo, reusing the default `CIC` system is simplest (no CFS broker
  dependency — recall day-36's OnBase/CFS broker outages).

### Phase 2 — Solution Config (the real work)
Build the solution-config `businessObjectConfig` for the ServiceNow tables, same shape as SF/OnBase:
- **`additionalConfig[]`**: per table (`incident`, `change_request`, `sc_req_item`) →
  `ecmContentTypeName` + `metadataFieldImportMappings[]` (ServiceNow field → ECM keyword; the scoping
  field = `sys_id`, `inputSource=1`) + display column.
- **`queryConfig[]`**: per table list/search queries (`filterClauses[ecmFieldId]`,
  `displayColumns.ecmColumnSets`, `displayColumnConfig`).
- **`contextParams.boContext.ecmPropId`** = the record-scoping field (the keyword that stores `sys_id`).
- Tools available: `add_business_object_config` (upsert one entry), `set_solution_configuration` (whole
  `data` node), `set_query_display_columns`, `set_viewer_url`. Pattern = GET config → modify → POST.

### Phase 3 — BFF/MCP endpoint settings
- `switch-lob.ps1`: if Option A, ServiceNow rides the existing `salesforce`/`salesforce-staging` entry
  (same `hfs` client, `api` route) — maybe just a doc note. If Option B, add a `servicenow` LOB entry
  (ClientId/Scopes/Iam/UcebBaseUrl/BasePath) like `workday`.
- Confirm `Uceb:BaseUrl` + IAM endpoints match the env the ServiceNow ECM lives in.

### Phase 4 — Extension (minimal)
- Detection already works. Verify `detectServiceNow` against a real PDI URL (the `/now/nav/ui/classic/...`
  and Next-Experience `/now/...` URL shapes vary by instance; confirm `sys_id` extraction on a live page).
- The upload/list/viewer paths are LOB-agnostic in the panel, so they should "just work" once the
  business objects are configured.

### Phase 5 — verify end-to-end
- Open a ServiceNow incident → panel detects `{incident, sys_id}` → upload a doc → it files into
  `hfs-documents/{sys_id}` → list returns it → viewer opens it. Same acceptance path as SF.

---

## 5. ServiceNow knowledge to learn (manager asked to upskill)

- **ServiceNow Fundamentals** (Now Learning, free): tables, records, **`sys_id`** (32-char GUID), the
  **URL model** (classic `*.do?sys_id=`, Next Experience `/now/nav/ui/.../record/<table>/<sys_id>`),
  lists vs forms, update sets, scoped apps.
- **CSA** (Certified System Administrator) — the baseline cert for the platform; good if going deep.
- **CAD / REST API** — only needed if we go *native* (a ServiceNow-side scoped app / UI action that
  talks to UCEB), which is NOT required for the browser-extension approach.
- **PDI** (Personal Developer Instance) at `developer.servicenow.com` — a free dev instance to test the
  detector against real incident/change_request URLs and to have records to file documents against.
  (A PDI was already started per day-30.)
- Concept anchor: ServiceNow is a **system of action** (tickets/workflows). "Hyland for ServiceNow" =
  surface the SAME ECM documents (OnBase/CIC) inside a ServiceNow record — the exact pattern we built for
  Salesforce/Workday, just a different host + record-id source.

---

## 6. Open questions to confirm with the mentor (gating items)

1. **Is ServiceNow provisioned as a LOB/appKey in UCEB (a "Hyland for ServiceNow" IAM Application)?**
   → decides Option A vs B. (Biggest unknown; everything hangs on this.)
2. **Which ECM should ServiceNow docs live in** — reuse CIC, reuse OnBase9714, or a new system config?
3. If Option B: who provisions the ServiceNow confidential IAM client + which env (dev/staging)?
4. Which ServiceNow **tables** are in scope first (incident only, or change_request / sc_req_item too)
   and their **content-type + metadata mappings** (what keywords store `sys_id` and the display fields).
5. The target ServiceNow **instance/URL shape** (our PDI vs a Hyland demo instance) to finalize
   `detectServiceNow` parsing.

---

## 7. Status / next action

- Research complete; the client side is already ServiceNow-ready; the work is **backend config** and it
  is **blocked on Phase 0** (the LOB/appKey question for the mentor).
- Proposed path: confirm Option A, reuse `hfs`+CIC, build the ServiceNow solution config
  (business objects + queries), verify end-to-end against a PDI incident — no UCEB code needed.
- Parallel: continue ServiceNow Fundamentals + keep the PDI for live URL/record testing.
