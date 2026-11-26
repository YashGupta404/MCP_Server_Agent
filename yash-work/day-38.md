# Day 38 — Prod Workday demo: viewer, detection, access-model & CFS search fixes

_Goal: bring up the **production** Workday (`onbase_cfs_prod`) stack for a demo and fix the issues that
surfaced live — document viewing, worker detection UX, the "access restricted" logic, and the broken
"saved query" search. Several root causes turned out to be **backend/config** (escalated to mentors
Sudipto & Sagnik); the rest were fixed client-side. All UI changes are in `agent-ng/` (Angular) +
`browser-extension/`; one BFF change in `bff/`._

---

## 0. Stack brought up (prod Workday)

```
Extension side panel → BFF (:5010) → MCP (:5200, prod Workday) → UCEB (api.uceb.app-intel.app.hyland.com) → OnBase via CFS
```
- MCP: `.\switch-lob.ps1 -Lob workday-prod` (arizzo interactive login, ~15 min token), active system
  `onbase_cfs_prod` (`friendlyName`, NOT `systemFriendlyName` — the `set_active_system_configuration`
  arg is `friendlyName`).
- Chatbot tunnel `giant-ant-2f6br43` → :5200; IDP tunnel `joyful-horse-g88zxrw` → :5010
  (`qpzmsssb-5010.euw.devtunnels.ms`, matches `Idp:PublicBaseUrl`).
- Caveat that bit us repeatedly: **async terminals get reclaimed** → MCP/BFF/tunnels silently drop.
  Prefix `.ps1` runs with `Set-ExecutionPolicy -Scope Process Bypass -Force`.
- Test worker: **Anthony Rizzo1** (signed-in user), WID `4bc212416f234ba1b4749e4bebe4c2eb`, Employee ID `21021`.

---

## 1. Show the Employee ID, not the raw WID  ✅ fixed (client)

The context header showed the opaque 32-hex WID. Now shows **name + Employee ID** for Workday workers.
- `record-context.model.ts`: added `employeeId?`.
- `documents.view.ts`: `applyResolvedWorker` stores `match.employeeId`; new `recordLabel()` →
  `"<name> · Employee ID <id>"` for Workday, `"ID <id>"` otherwise.
- `documents.view.html`: record line uses `recordLabel()`. i18n `documents.record.{id,employeeId}`.

---

## 2. In-panel document viewer — 2-layer backend gap, now unblocked  ✅ fixed (client + mentor)

**Symptom:** opening a doc on prod showed *"not authorised"*, then after fixing the host, a 400.

**Root cause chain:**
1. `open_document_in_viewer` returned the **staging** host `cic-viewer.staging.app.hyland.com` with a
   **prod** envKey (`appintel-production-prod`) → staging viewer can't auth prod content → "not authorised".
   The host comes from the system config's `viewerCfg_baseUrl` (stored in deployed UCEB), not our code.
2. Prod host is **`cic-viewer.app.hyland.com`** (confirmed: DNS 32.194.247.172, HTTP 200; arizzo logs in
   fine — no auth error). Other guesses (`production.`/`prod.`) don't resolve.
3. Even on the right host, the MCP returned the **CIC-native** form `/#/documents/{onbaseHandle}` →
   `/v1/repository/document/14949/properties` → **400 `{"message":"Invalid documentId format"}`** (the
   handle `14949` isn't a GUID). Server-side preview rendition = `{"error":"no_preview"}`; direct
   download = the viewer-URL envelope. So all three default paths were dead.

**Sudipto's unlock** — the correct CFS form (opens fine as arizzo@aurahyland):
```
https://cic-viewer.app.hyland.com/#/cfs/62ff4bf7-c587-40df-a721-98e7b37b5c8a/14949/1?envKey=appintel-production-prod
```
Key: **contentId is just the OnBase handle** (`14949`), no GUID — only the URL **form** (`/#/cfs/{integrationId}/{handle}/{logicalPartId}`) was wrong.

**Fix (client):**
- `bff/Configuration/BffOptions.cs`: new `ViewerOptions.CfsSystems` (`Host`, `IntegrationId`, `EnvKey`,
  `LogicalPartId`), keyed by active `systemFriendlyName`.
- `bff/appsettings.json` → `Viewer:CfsSystems`:
  - `onbase_cfs_prod` → host `cic-viewer.app.hyland.com`, integrationId `62ff4bf7-c587-40df-a721-98e7b37b5c8a`, envKey `appintel-production-prod`.
  - `onbase_hcm_stg` → staging host, integrationId `532c245c-...`.
- `bff/Program.cs` `/api/viewer`: for a configured CFS system, builds `{Host}/#/cfs/{IntegrationId}/{docId}/{part}?envKey={EnvKey}` instead of the MCP's `/#/documents/` URL.
- `browser-extension/` whitelisted `cic-viewer.app.hyland.com` (+ prod auth hosts) in `manifest.json`
  (host_permissions + viewer-storage content script), `rules.json` (DNR sub_frame header strip), and
  `background.js` (SameSite cookie promotion) so it renders **in-panel**.

**Still a mentor item:** the prod `onbase_cfs_prod` config's `viewerCfg_baseUrl` points at *staging* and
the MCP emits the `/#/documents/` form for CFS — ideally fixed server-side so other clients work too.

---

## 3. Worker detection UX — 403 flash, Summary-tab misses, debug line  ✅ fixed (client)

Three issues navigating between workers:
- **403 "Access restricted" flashed** then docs loaded: the grace re-check was only ~4.2s but prod
  Workday lazy-renders the Employee ID slower → committed the 403, then a late re-detect flipped it.
  → extended `graceRecheckNotAuthorized` to ~12s + show a "Checking access…" state (no flash).
- **Empty state + a dev debug line** (`tabs=… ask=null bg=null`) on the Summary tab: the Employee ID
  isn't in the Summary DOM (it's under Job Details), so detection returned null.
  → **Switched detection to resolve by NAME** (`detector.js detectWorkerProfile`): any tab shows the
  name, so `needsResolve` uses the name (upgrades to the exact Employee ID if it hydrates). Removed the
  debug line; added a **"Detecting record…" spinner** and extended `resolveContext` grace to ~3s.

Files: `browser-extension/src/content/detector.js`, `agent-ng/.../documents.view.{ts,html,scss}`, `en.json`.

---

## 4. "Who can I see?" — access authorization is NOT enforced (big finding)

Manager question: should the plugin base document access on the **plugin** user or the **Workday** user?

**What I proved:**
- With arizzo's token, `list_documents` returns docs for **everyone** — Helen Meyer (14, a direct
  report) AND Oliver Reynolds (7, the CIO above). So **UCEB/OnBase does not scope by hierarchy**.
- The old "Employee-ID-visible-in-DOM = authorized" check was just a **client-side proxy** for Workday's
  own UI security, and it breaks on the Summary tab. Not real enforcement.
- Solution config **has** the slots for it — `businessObjectRoles`, `securityGroupConfigs`,
  `securityPolicyName(s)` — but on **prod `onbase_cfs_prod` they're all empty/null** → no filtering.
  (The captured `workday-solution-config.json` *does* have `Manager`/`HR Partner` roles +
  `org-sync-*` security policies — likely staging.)
- Where the model lives/enforces (all in deployed UCEB, **Workday-specific** assembly
  `Hyland.Experience.UCEB.Workday.Api.Library`): DTO `WorkdayHxConfigRequest.cs`, parser
  `WorkdayHxConfigRepoService.cs`, `WorkdayHxConfigurationService.GetSecurityPolicyNames`, applied by
  `Controllers/WorkdayUserRolesController.cs` (org-sync → OnBase security policies). **Our
  plugin/BFF/MCP read/enforce none of it.**

**Conclusion (agreed direction):** the plugin should NOT be the security boundary and should NOT mirror
Workday's hierarchy. Access belongs in the solution config's security sections, enforced server-side by
UCEB/OnBase for the signed-in (IAM) user. Role-based config is ECM-level but currently Workday-specific.

**Manager's ask → FULL hierarchy (final):** show docs for the signed-in Workday user's whole subordinate
tree (direct + indirect); 403 for peers/superiors.
- The Workday API client can't walk the hierarchy: worker detail has **no manager**; `?expand=managementChain`
  ignored; `/supervisoryOrganizations` → **403**; `/workers/{id}/directReports` & `/organizations` → **404**.
  So a client/BFF tree-walk is impossible with this client's scopes.
- A first cut did a **direct-report** check (scrape the viewed worker's Manager, compare to `/api/me`) —
  but that's only one level, which the manager didn't want. Removed.
- **The old vanilla UI already solved this** by piggybacking on **Workday's own security**: Workday renders
  a worker's **Employee ID** (Job Details) only for workers the signed-in Workday user is authorised to see
  — i.e. their **entire subordinate tree**. So `detector.js`: Employee ID present → `needsResolve` → resolve
  + show (authorised, full tree); name only (Employee ID hidden) → `notAuthorized` → 403. No tree-walking.
  (My earlier same-day switch to resolve-by-name had bypassed this and shown everyone — now reverted.)
- **Reverted to the Employee-ID signal**, keeping today's UX hardening so the old flash/empty problems don't
  return: the probe (`probeForEmployeeId`, now 6 passes) hydrates the below-the-fold Job Details section; the
  panel's ~12s grace re-check + "Checking access…" state flips to authorised the instant the Employee ID
  appears, so a real subordinate never flashes a 403. Removed the `managerName` scrape + `isAuthorizedForWorker`.
- Depends on this tenant's Workday security actually hiding Employee IDs for non-subordinates (the old UI
  relied on it). The proper server-side equivalent is still the solution-config org-sync
  (`businessObjectRoles` + `securityPolicyNames`, empty on prod) → OnBase enforcement.

---

## 5. "Saved query" search — 500 on CFS, rerouted to the list endpoint  ✅ fixed (client)

**Symptom:** the Query panel showed "100 result(s)" but **empty cells**, and only after clicking Run.

**Root cause:** `query_documents` → UCEB **`POST /api/core/queries/{id}/execute?systemFriendlyName=onbase_cfs_prod`**
→ **500 InternalServerError, empty body** (fails even with no filter). The MCP catches it and falls back
to the default `list_documents` (employee docs), so the Contract columns from the query metadata sat
over employee fallback rows → every cell blank. (To surface the raw 500, call `query_documents` WITHOUT
a `businessObjectId` — the catch re-throws when the id is blank.)

**Sudipto's unlock:** the documents endpoint works per business object —
```
GET https://api.uceb.app-intel.app.hyland.com/bow/core/business-objects/{contractObjectId}/documents?systemFriendlyName=onbase_cfs_prod&businessObjectType=contract  → 200, 100 docs
```
Verified via our MCP: `list_documents businessObjectType=contract businessObjectId=ecc843944b6944618c437cff588d4a41` returns 100 contract docs with **Contract columns populated** (match the metadata).

**Fix (client):**
- `bff/Models/BffModels.cs` `ContextRequest`: added `Direct`.
- `bff/Program.cs` `/api/context`: when `direct=true`, **skip** the defaultListQuery (which runs the
  500ing native query) and go straight to `list_documents`.
- `agent-ng/.../document.service.ts` `listForContext`: added `direct?`.
- `agent-ng/.../documents.view.ts` `runQuery()`: for `*_CFS` queries, derive `businessObjectType` from
  the `"{X} Object ID"` input and list that object's documents directly (`direct: true`). Columns from
  the query metadata (now aligned). The `"{X} Number"` filter is unused (the list endpoint keys off the
  Object ID; number-filtering needs the broken `/execute`).

**Route nuance (recorded):** documents use `/bow/core/...` (Workday route); queries use `/api/core/queries/.../execute`. Only `queries/execute` 500s on CFS.

---

## 6. Mentor / backend follow-ups (open)

1. **Viewer:** fix `onbase_cfs_prod` config `viewerCfg_baseUrl` → prod host, and/or MCP emit the CFS
   `/#/cfs/{integrationId}/{handle}/1` URL for CFS systems.
2. **Query execute 500:** why does `/api/core/queries/{id}/execute` 500 for CFS on prod (metadata works)?
   Check deployed UCEB/CFS logs (empty body = unhandled server exception).
3. **Access control:** `businessObjectRoles`/`securityGroupConfigs`/`securityPolicyNames` empty on prod →
   is open access intended, or should they be populated so OnBase scopes to the signed-in user? Does the
   plugin user's identity even reach OnBase (CFS token exchange is internal to deployed UCEB)?
4. **Subordinate access:** decision — implement the direct-manager stopgap, or pursue expanded Workday
   API scopes for a true subordinate-tree check.

---

## 7. Net

- Fixed client-side today: Employee-ID display, CFS in-panel viewer URL, 403-flash, name-based detection
  + loading spinner, debug-line removal, CFS saved-query search.
- Proven backend gaps (mentor-owned): viewer config/URL form, `queries/execute` 500, empty security
  config / no per-user enforcement, Workday hierarchy API scopes.
- Reload the extension + re-sign-in after the BFF restart to pick everything up.
