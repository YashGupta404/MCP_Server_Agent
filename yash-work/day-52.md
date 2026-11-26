# Day 52 — 2026-11-23

## Goal: run the zero-setup capture PoC (built for Salesforce, day 50–51) on ServiceNow

User is creating, in OnBase `OnBase9714_Integrations_Agent`: doctype group **ServiceNow2** with doctypes
**ChangeRequests** and **Incidents**. Got the "Document Type Settings" dialog.

### Step 1 (user, OnBase Configuration) — what to set
1. Document Type Settings (per doctype):
   - Document Type Group = ServiceNow2 (set).
   - Default File Format = e.g. PDF / Image File Format.
   - **Default Disk Group = the same disk group as 205 Invoices** (needed to store the file).
   - Everything else default -> Save. Repeat for Incidents.
2. Keywords (Document Types -> doctype -> Keywords): a **record-id keyword** (Alphanumeric, e.g. `hfsn_sys_id`;
   the ServiceNow record id from the URL — the agent maps it as record linkage, inputSource 1), optional owner
   (`hfsn_opened_by`), plus business fields the agent can fill from the document:
   - ChangeRequests: Change Number, Short Description, Priority, State
   - Incidents: Incident Number, Caller, Category, Priority
   Use Alphanumeric unless truly date/number (Currency/numeric keywords reject text -> broker 500, day 51).
3. User Groups / Rights: give the group that sees Invoices (205) access to ServiceNow2 + both doctypes, else UCEB
   won't return them in the catalog.

### Keywords — FINAL (user: AI won't understand "SN Number"; use descriptive, record-type names)
Descriptive names let the mapper agent match them (its prompt looks for "number"/"record number" for record
linkage) and are unique in OnBase (no prefix needed). All Alphanumeric.

ChangeRequests:
1. **Change Request Number** (40) — RECORD ID, required (e.g. CHG0030001)
2. **Change Request Opened By** (100)
3. **Change Request Short Description** (250)
4. **Change Request State** (40)
5. **Change Request Priority** (40)
6. **Change Request Assignment Group** (100)
7. **Change Request Planned Start Date** (40) — Alphanumeric
8. **Change Request Category** (100)

Incidents:
1. **Incident Number** (40) — RECORD ID, required (e.g. INC0010001)
2. **Incident Caller** (100)
3. **Incident Short Description** (250)
4. **Incident State** (40)
5. **Incident Priority** (40)
6. **Incident Category** (100)
7. **Incident Assignment Group** (100)
(Superseded: the "SN ..." list and the `hfsn_*` list below.)

### Keyword Type Settings (OnBase dialog) — same for all 8 Change Request keywords
- Dialog defaulted to **Numeric (Up to 9 Digits)** -> must be **Alphanumeric** (CHG0030001 has letters; a numeric
  keyword rejects text, like the Currency/"USD" broker 500 on day 51).
- Maximum Length = per list above (40 / 100 / 250 / 40 / 40 / 100 / 40 / 100).
- Storage: Single Table. Character Case: **Mixed Case Values** (Uppercase would change "Network upgrade").
- Data Sets / Drop Down Lists: off (a data set rejects values the AI reads that aren't in the list).
- Usage Restrictions: None (many documents share one change number — not unique).
- Display Options, AutoFill Keyword Set (None), Advanced, Classification: all off / unchanged
  (Info_Only / Not For Retrieval would block searching by the keyword).
- Save; then add all 8 to the ChangeRequests doctype (Document Types -> ChangeRequests -> Keywords).

### Scope cut (user): test with 2 keywords only for now — ChangeRequests
1. **Change Request Number** — Alphanumeric, max length 40 (record id)
2. **Change Request Priority** — Alphanumeric, max length 40 (values like "2 - High" -> not numeric)
Both: Single Table, Mixed Case, no data set, Usage None, Display/AutoFill/Advanced/Classification off.
Incidents parked for now. Next: add both to ChangeRequests -> I verify via UCEB document-types?doctypeGroupId.

### VERIFIED via UCEB (MCP salesforce-staging, active system OnBase9714)
- `GET /api/core/document-type-groups` -> **125 ServiceNow2** (plus 117, 118, 119, 111, 116, 102).
- `GET /api/core/document-types?fetchMetadata=true&doctypeGroupId=125` ->
  **228 ChangeRequests**: **321 Change Request Number [String]**, **322 Change Request Priority [String]**.
- Alphanumeric -> String confirmed; rights OK (visible to the PoC's sign-in). Incidents not created yet (parked).
- Next: restart MCP on `-Lob servicenow` (app key hfsn), back up + reset the ServiceNow config on OnBase9714,
  then the panel test on a ServiceNow change request page.

### Can the PoC run on ServiceNow? Yes (code is LOB-generic) — 2 blockers found
- Expected flow: context agent -> change request + number; Data Curation -> text; mapper picks **228
  ChangeRequests** (only doctype with "Change Request Number/Priority"); BFF writes the entry + uploads.
- Blocker 1: MCP user-secrets have NO ServiceNow (hfsn) client. Key names present: Auth:ClientId/Secret/Scopes
  (currently the Salesforce-staging client, copied by switch-lob), Lob:salesforce*/workday* secrets. The
  `servicenow` profile uses Auth:* directly (ClientFromSecrets) -> would sign in as Salesforce (hfs).
  USER STEP: `dotnet user-secrets set "Auth:ClientId"/"Auth:ClientSecret"` with the Hyland for ServiceNow client
  (typed by the user, never in chat).
- Blocker 2: `servicenow` profile points at LOCAL UCEB http://localhost:5000 (not running). Options:
  A) start local UCEB; B) add a `servicenow-staging` profile -> staging UCEB (only if staging has hfsn onboarded;
  verify after sign-in).
- After sign-in: check OnBase9714 is a system config for hfsn, back up + reset hfsn config, start BFF.
- Test file: `ke-poc/samples/change-request-sample.txt` (CHG0012345, Priority "2 - High").

### ServiceNow setup run (user: "run them yourself") — option A (local UCEB, the proven hfsn path)
- ServiceNow client found in day-40: `yash_hia_servicenow_client` id `wsc-84411124-abaa-40ca-800f-ae5134e1fa78`
  (env Appintel-Staging Test Environment, app Hyland for ServiceNow). Its SECRET is not stored anywhere locally
  (checked every user-secrets store, names only).
- Done: MCP `Auth:ClientId` = hfsn client id (switch back to Salesforce re-copies its id+secret).
- Done: local UCEB started (`Hyland.Experience.UCEB.Api`, profile `dotnet`, http://localhost:5000) — listening.
- WAITING ON USER: `dotnet user-secrets set "Auth:ClientSecret" "<hfsn secret>"` in the MCP project (typed by the
  user, never in chat).
- Then: stop Salesforce MCP -> `switch-lob.ps1 -Lob servicenow` (browser login) -> active system OnBase9714 ->
  verify 228 ChangeRequests visible -> back up + reset hfsn config -> start BFF -> panel test.

### DECISION (user): use the SALESFORCE client — PoC only proves capture into the ServiceNow doctype
- Rationale: goal = "does capture work for a ServiceNow doctype"; the Salesforce (hfs) sign-in already sees
  228 ChangeRequests. Caveat: the JIT entry lands in the hfs (Salesforce) config on OnBase9714, not a hfsn one.
- Undid the hfsn path: `switch-lob.ps1 -Lob salesforce-staging -NoRun` (Auth:ClientId back to wsc-f3ec0fd9…,
  secret re-copied); stopped the local UCEB.
- MCP (running): app key hfs, `uceb.hfs_configuration.write` OK, active system OnBase9714.
- hfs config on OnBase9714: 1 entry `account -> Invoices` (Salesforce demo), 0 queries; nothing for change
  requests -> capture starts with no setup. Left the Invoices entry in place.
- BFF built + started (https://localhost:5010).
- Test: ServiceNow change request page -> panel -> Auto-map with `ke-poc/samples/change-request-sample.txt`.
  Expect doctype 228 ChangeRequests, record field 321 Change Request Number = the page's CHG number,
  322 Change Request Priority from the document ("2 - High").

### ServiceNow run 1 (CHG0040019, change-request-sample.txt) — mapping PASS, confidence 0.45
- Panel: Record `change request CHG0040019`; Document type **ChangeRequests (228)**; record field **321 = CHG0040019**;
  **322 Priority = "2 - High"** (from the document). Upload in progress at the time.
- Why 0.45 (agent rationale, `bff/logs/agent-map/20261123-092131.json`): doctype choice is solid (KE
  classification "ChangeRequests" = exact match). Deductions are about the FULL config, not capture:
  -0.3 no default list query (no ECM query takes 321 as input; capture-only PoC doesn't need it);
  -0.1 no owner keyword (doc has "Opened By"); -0.1 single sample; unmapped Short Description / State /
  Planned dates (only 2 keywords created); "No LOB page text was provided".
- Finding: `lobPageContent` empty on ServiceNow (new UI renders the record in shadow DOM / nested frames;
  `innerText` likely misses it). Record still detected (context agent uses URL + HTML).
- To raise confidence: add the other planned keywords (Opened By, Short Description, State, Planned Start
  Date); optionally tell the mapper "capture-only" so a missing list query isn't penalized; fix page-text capture
  on ServiceNow.

### ServiceNow capture RESULT — PASS
- MCP log: `POST /api/core/business-objects/CHG0040019/documents?systemFriendlyName=OnBase9714_Integrations_Agent`
  -> **200**; `upload_staged_file` IsError=False. Document stored in OnBase as **ChangeRequests (228)** linked to
  CHG0040019.
- JIT entry written (backup `config-backups/jit/OnBase9714_Integrations_Agent-20261123-092156.json`):
  `change request -> ChangeRequests (228)`, group 125, imports 321 src1 (record id), 322 src6 (from document).
  hfs config now: `account -> Invoices`, `change request -> ChangeRequests`.

### Fix 1 — page text empty on ServiceNow (done)
- Cause: `/now/nav/ui/classic/...` = Next Experience shell; the classic form iframe (`gsft_main`) is inside a
  shadow root. `doc.querySelectorAll('iframe')` doesn't cross shadow roots -> the form frame was never read ->
  `lobPageContent` empty.
- Fix (`context-detector.service.ts` `captureActivePage`): collect iframes from the document AND every shadow
  root (bounded walk). Extension rebuilt OK.

### Fix 2 — mapper prompt v5: capture-only confidence (file ready; USER pastes into Studio)
- `ke-poc/agent-test/mapper-instructions-v5.txt` (copy of v4): STEP 8 now scores CAPTURE only — queries, display
  columns, owner linkage and document fields with no ECM attribute must never lower confidence; only the listed
  deductions; rationale must show the arithmetic. (v4 run deducted 0.3 for "no default query" although v4 says 0.1.)
- Expected for this test after v5: 1.0 - 0.1 (one sample) = 0.9.

### ServiceNow run 2 — Priority empty, confidence 0.35 (log `bff/logs/agent-map/20261123-093019.json`)
- Fix 1 WORKED: the agent now sees the ServiceNow form (lists Short description, Change State, Category, Risk,
  Impact, Type, Assigned to from the page).
- Priority empty: agent mapped 322 as inputSource "1" (from LOB field "Priority") with sampleValue "". The panel
  pre-fills only from sampleValue. It also broke its own rule d: page shows "4 - Low", document "2 - High" ->
  should be "6" with the document value + conflict. Run 1 had no page text, so it used the document ("2 - High").
- Confidence 0.35: Studio is still on prompt v4 (rationale has no arithmetic; still deducts for the missing list
  query, owner and unmapped page fields). v5 not pasted yet.
- v5 updated: sampleValue must always hold the value that will be stored ("1" -> page value, "6" -> document
  value); rule d strict (conflict -> "6" + document value + unmapped).

### Run 3 (v5 in Studio): Priority "2 - High" OK, confidence still 0.45 — agent ignored the scoring rule
- Its arithmetic: 1.0 - 0.3 (no default list query) - 0.1 (one sample) - 0.15 ("severe schema mismatch", invented)
  = 0.45. v5 forbids both. Invoices scored 0.92 because doctype 205 already had query 115 (by 223), owner 224 and
  5 fillable keywords; ChangeRequests is new (2 keywords, no query).
- Also seen: ServiceNow page text contains the whole Priority dropdown (all options), so the agent can't tell the
  selected value ("4 - Low").

### DECISION (user): no default list query; confidence = doctype chosen + keywords auto-populated ONLY
- BFF (code, deterministic): `AgentMappingValidator.CaptureConfidence` -> `captureConfidence` in the agent-map
  response: doctype score = share of documents whose KE classification equals the chosen doctype name (0.8 if no
  KE classification; 0 if doctype not in catalog); keyword score = filled keywords / doctype keywords (record
  field filled from the record id, others need a sampleValue); overall = average. Lists missing keywords.
- Panel: confidence now = `captureConfidence.overall`, with a line "doctype X (reason) · keywords n/m filled
  (missing: ...)".
- Prompt v5: STEP 6/6b SKIPPED (queryMapping empty "Skipped: capture-only", searchQueryMappings "[]", no query items
  in unmapped); STEP 8 = (doctype certainty + share of keywords filled)/2; Example A query line removed.
- Builds OK; BFF restarted. Expected for CHG0040019: doctype 1.0 (KE "ChangeRequests"), keywords 2/2 -> 1.0.

### Q: switch to Salesforce and upload an invoice — does the mapper still work? Yes, with one fix
- Same MCP (hfs, OnBase9714), same live catalog -> Invoices (205), record link 223, Currency coercion; the
  capture re-upserts the existing `account -> Invoices` entry. Expected confidence ~0.93 (6/7 keywords; owner
  224 isn't filled at capture).
- BUG found + fixed: the side panel survives tab switches and `autoMap()` reused the cached `aiContext`
  (CHG0040019) -> an invoice uploaded on Salesforce would have been filed under the change request. Now the
  context is reused only when `page.pageUrl` equals the URL it was detected on (`aiContextUrl`), otherwise the
  context agent runs again. Extension rebuilt OK.

### Config always back to default after a capture (user: "after upload it will be default right?")
- Before: each capture left its entry (`account -> Invoices`, `change request -> ChangeRequests` were in the hfs
  config). Nothing reverted it.
- Now `/api/capture/commit`: reads the config, keeps the original `data` node, writes the JIT entry, uploads, and
  in a `finally` ALWAYS writes the original back (success or failure; `CancellationToken.None`). Response has
  `configRestored: true|false`; panel shows "Solution config restored." (or a warning). The backup in
  `config-backups/jit/` is still written first.
- Reset OnBase9714 hfs config to default now (`onbase9714-salesforce-hfs-reset.data.json`): verified
  additionalConfig 0, queryConfig 0.
- BFF + extension builds OK; BFF restarted.

### ChangeRequests keywords (REVISED, superseded) — user: keep `hfsn_` for CIC only; use ServiceNow-specific names
OnBase keyword types are shared across all doctypes, so prefix "SN " to avoid clashing with existing keywords.
All Alphanumeric (ServiceNow field in brackets):
1. **SN Number** (`number`) 40 — RECORD ID, required (e.g. CHG0030001); record linkage
2. **SN Opened by** (`opened_by`) 100
3. **SN Short description** (`short_description`) 250
4. **SN State** (`state`) 40
5. **SN Priority** (`priority`) 40
6. **SN Assignment group** (`assignment_group`) 100
7. **SN Planned start date** (`start_date`) 40 — Alphanumeric
8. **SN Category** (`category`) 100
Incidents: reuse 1, 2, 3, 4, 5, 6, 8 + **SN Caller** (`caller_id`) 100.
No document-name keyword: the upload already sets the OnBase document name.
(Superseded: the earlier `hfsn_*` list below.)

### ChangeRequests keywords (first draft, superseded) — same names as `servicenow-solution-config.json` (hfsn convention)
All Alphanumeric:
- `hfsn_recordNumber` (40) — RECORD ID, required; ServiceNow `number` e.g. CHG0030001 (config boContext boProp
  `number`; not the 32-char sys_id)
- `hfsn_recordOwner` (100) — Opened by
- `hfsn_documentName` (250), `hfsn_shortDescription` (250), `hfsn_changeState` (40), `hfsn_priority` (40)
- Optional: `hfsn_assignmentGroup`, `hfsn_plannedStartDate` (keep Alphanumeric unless sure of format)
Incidents: reuse the same keywords (minus changeState), optionally `hfsn_incidentState`, `hfsn_category`.

### Step 2 (me) — verify through UCEB
`GET /api/core/document-type-groups` -> ServiceNow2 id; then
`GET /api/core/document-types?fetchMetadata=true&doctypeGroupId=<id>` -> both doctypes + keywords.

### Step 3 — ServiceNow run (per-LOB rule)
- MCP: `switch-lob.ps1 -Lob servicenow` (app key `hfsn`); active system OnBase9714.
- Back up the hfsn solution config, reset it to the default shape, record restore path in
  `/memories/repo/solution-config-backups.md`.
- Panel on a ServiceNow change request / incident page -> Generate mapping -> Upload.
