# Day 36 — Salesforce demo bring-up, OnBase/CFS broker outage RCA, IDP fix, and a repo-wide change-detection fix

_Context: Salesforce staging demo on the revamped Angular side panel (active system
`OnBase9714_Integrations_Agent`, record `001gK00001KbuonQAB`)._

## 1. Full stack startup (Salesforce)
Standard bring-up: MCP (`switch-lob.ps1 -Lob salesforce-staging`, interactive login) → BFF (:5010) →
devtunnel `giant-ant-2f6br43` (:5200) → `npm run build` (agent-ng → browser-extension/ng) → `idp-refresh.ps1`.
- The ECM **system choice is the user's** — do not auto-`set_active_system_configuration` during startup.
- MCP token ~15 min; a stale login tab 400s on `auth.staging` → restart MCP for a fresh 120 s login window.

## 2. "No content linked to this record" → CFS **broker Disconnected** (NOT our code)
Panel showed empty; the BFF was masking backend failures as an empty list.
- Probed directly: `list_document_types`, `query_documents` (115/110/109) all returned **503
  "A service is currently unavailable" / "GetAllContentTypeGroupsAsync: A service is currently unavailable"**.
- Isolation proved it was CFS-only: `get_build_info` 200, `list_system_configurations` 200, and the **CIC**
  default system listed 7 doc types fine on the same token/UCEB. Only OnBase/CFS 503'd.
- Two OnBase error classes surfaced: `503 "service unavailable"` = **no broker connected to that connection**;
  `500 "at the broker"` = broker reachable but erroring. OnBase9714 was the 503 (no live broker).
- **CFS Admin Portal confirmed it** — crucially in the RIGHT env `appintel-staging-**test**` (not prod):
  connection **`CF_Broker_Salesforce`**, Integration ID **`4873af36-dd9a-4e90-9b54-962dc4079fb1`** (exactly what
  our `OnBase9714` config binds to, per day-22), status **Disconnected**.
- Red herring ruled out: a `Onbase9714` connection in `appintel-staging-**prod**` (integration
  `0564b2dc-…`, created that day) is a DIFFERENT integration — not ours. Our config = `4873af36` in staging-test.
- **Resolution: the on-prem CFS broker agent for `CF_Broker_Salesforce` was reconnected** (broker/env action,
  same family as the MCA-app-pool restarts). After it went Connected: `list_document_types` → 14 types
  (`error:null`); query 115 for the account → **25 docs**. Our code/config never changed and were correct.
- Matches day-33's conclusion verbatim: "UCEB, CFS, OnBase, the token and our config are all fine."

## 3. IDP auto-classify "unknown type" for every file → IDP :5010 devtunnel was **not hosted**
IDP's cloud classifies by fetching each staged file over `Idp:PublicBaseUrl`
(`https://qpzmsssb-5010.euw.devtunnels.ms`) — a SEPARATE devtunnel pointing at the **BFF :5010** (not the
:5200 MCP tunnel). That tunnel (`joyful-horse-g88zxrw.euw`, port 5010) had **0 host connections** → IDP
couldn't fetch the files → no class → "unknown type" for all.
- **Fix: `devtunnel host joyful-horse-g88zxrw`** → `/auth/status` over the public URL returned 200. Classify works.
- Startup reminder: the IDP BFF tunnel must be hosted too, in addition to the MCP tunnel + `idp-refresh.ps1`.

## 4. Repo-wide "click then load" bug (change detection) — FIXED across all views
**Symptom:** search/classify/upload/etc. finished but the UI stayed on "Searching…/loading" until a DOM event
(e.g. a right-click) forced a repaint.
**Root cause:** the app uses native `fetch` (via `BffApiService`), whose promise continuations resolve
**outside Angular's zone**. State set AFTER an `await` didn't trigger change detection.
**Fix:** added a `tick()` helper (`zone.run(() => cdr.detectChanges())`) and applied it to every
backend-driven method:
- `documents.view.ts`: `reload`, `loadQueries`, `onQueryChange`, `runQuery`, `loadIdpStatus`, `autoClassify`,
  `loadDocTypes`, `onDocTypeChange`, `loadUploadMetaFields`, `selectUploadFile`, `sendChat`, `preview`,
  `runDownload`, `confirmDelete`, `doUpload`.
- `ecm-picker.view.ts`: `choose()` error path (ngOnInit already had it).
- `sign-in.view.ts`: `signIn()` (injected `NgZone` + `ChangeDetectorRef`).
All compile clean; rebuilt to `browser-extension/ng`. **Long-term option:** route calls through Angular
`HttpClient` (zone-aware) or an interceptor that ticks, instead of per-method.

## 5. Satori token audit (answer: effectively yes)
`agent-ng` view SCSS uses **286 `var(--mat-sys-*)` token references** across sign-in / ecm-picker / documents
— no hardcoded hex/rgb in styling, EXCEPT:
- `sign-in.view.scss:71` → `color: #1e8e4e` (the success-message green) — a real token violation to fix
  (no MD3 "success" token; map to a Satori/custom success token or `--mat-sys-tertiary`).
- `documents.view.html:219-236` → the chat empty-state **SVG illustration** uses fixed hex fills — decorative
  illustration art (acceptable, but ideally a Satori illustration per the guardrails).
