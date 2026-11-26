# Day 44 — DOC notes for HXPDOC-62745 + KE PoC kickoff (config-less solution-config generation)

_Continues day-43. Two threads: (1) wrote the Documentation Notes for the "Hyland Integrations Agent –
Next phase" DOC ticket (HXPDOC-62745) that the TW put On Hold; (2) pushed the config-less initiative forward
by scoping a **Knowledge Enrichment (KE)** PoC that auto-generates the solution-config mapping, and set up KE
access in Staging. Covers 2026-11-13 → 2026-11-16._

Demo stack unchanged. The KE PoC is a NEW standalone .NET console app (not the BFF/MCP), talking to CIC KE
APIs in Staging.

---

## A. Documentation Notes — HXPDOC-62745 (DOC - Hyland Integrations Agent - Next phase)

TW **Soumya Guha** moved the DOC ticket to **On Hold** because its *Documentation Notes* field was empty and
commented asking for the user-facing / MRG details. Ticket was auto-generated from dev epic **CONTEN-512**
(Done). Wrote the notes following the mentor's recommended examples (**HXPDOC-28599** = short, directive
writer-actions style; **HXPDOC-47558** = fuller feature-list style).

- **Structure written:** Summary + Impact → WHAT CHANGED (delivered, with story links) → NOT IN THIS PHASE /
  IN PROGRESS → 14 feature bullets → Prerequisites → Documentation actions for the writer → Supporting
  material → Release notes → Notes for the writer.
- **Delivered-in-phase stories cited:** System Configs CIC+OnBase (HXSF-815), OnBase ECM (HXSF-817), CIC Viewer
  + iframe (HXSF-819, HXSF-786), document search (HXSF-816), Workday support (HXSF-787), chat shell + capture
  (HXSF-759, HXSF-760), MCP productionization — HTTP/SSE, API-key, dev tunnel, Agent Builder
  (HXSF-755/756/757/758).
- **Flagged as NOT shipped (hold docs):** Figma UI/UX redesign (HXSF-818), auth refinements (HXSF-761),
  IDP→Automate rework (HXSF-881), MCP cloud deploy (HXSF-762).
- **Resolved/caveats:** iframe-vs-tab decision = **embedded iframe is the shipped default** (HXSF-786/819);
  told writer the **"optimistic" upload list is temporary** (do not document); **IDP is interim** (HXSF-881).
- Switched wording to **LOB (Line of Business)** terminology; added **ServiceNow** as a supported LOB
  alongside Salesforce + Workday.
- **Files produced (yash-work/):** `documentation-notes-HXPDOC-62745.md` (md), `...-PLAIN.txt` (Jira-paste,
  no markdown), `...-LINKED.md` (every CONTEN-/HXSF- key as a browse link).
- **Jira how-to learned:** the *Documentation Notes* field is under the collapsed **"More fields"** on the
  issue; it is a rich-text field (markdown shows literally; pasting into a code block caused the "weird" look
  — fix: remove code block, paste plain with Ctrl+Shift+V). Jira **auto-links bare issue keys** on paste.
  Don't touch *Documentation Changes* (writer's field) or *Release Notes Required* (`N`). Plan: paste PLAIN
  text, reply + @mention Soumya, optionally mirror into CONTEN-512.
- Also built `servicenow-demo-voiceover-prompt.md` — a prompt pack + ready narration script to add a voiceover
  to the ServiceNow demo clip (ChatGPT can't edit video; use it for the script → TTS → editor).

---

## B. Deep-dive learned: how the solution-config mapping actually works

Groundwork for the config-less PoC — nailed down what the "mapping / keyword mapping" the team referenced
really is.

- **"Keyword" = OnBase's name for a document metadata field; CIC calls it an "attribute".** UCEB normalizes
  both as *document-type attributes/metadata*. ECM-side keywords **pre-exist** — an admin defines them in
  OnBase/CIC; the solution-config only **references/curates** them (it cannot invent them).
- **`inputSource` logic** (authoritative: `bff/Mcp/McpJsonRpc.cs:~288`): `1`=record, `2`=static, `3`=username,
  `4`=filename, `5`=filetype → **auto-populated, hidden** from the upload form; `6`=**caller-provided**
  (user/IDP, shown). Code partitions fields into `autoIds` vs `importableIds`.
- **Record-id keyword = the import mapping whose `inputSource == "1"`** (`UcebApiClient.ResolveImportKeywordFieldAsync`).
  Workday makes it explicit: `"Employee Object ID" → hcmisbebo_businessObjectId` + `documentTypeWithBOIDField`.
- **Six mapping layers in one solution-config:** (1) busObject→doctype (`ecmContentTypeName`), (2) record
  linkage (`contextParams.boContext/boUser`), (3) import mapping + `inputSource` (`metadataFieldImportMappings`),
  (4) labels (`ecmMetadataFieldLabels`), (5) display columns (`displayColumns`), (6) queries
  (`queryConfig.filterClauses` + operator e.g. `EqualsCaseInsensitive`). "Doctype mapping" is only layer 1.

---

## C. KE PoC — config-less solution-config generation (scoped today)

Goal: customer does **zero setup** — installs the extension, uploads a few sample docs, and AI auto-builds the
whole solution-config (all 6 layers). Known = doctypes + ECM keywords (we provide / read from UCEB). Unknown =
LOB-side keywords → derive by **value-matching**.

- **Known-side source endpoint found:** `list_document_types` with `fetchMetadata=true`
  (`GET /api/core/document-types?fetchMetadata=true`) = all doctypes + their attributes for the **active
  system**; plus `get_document_type_metadata` (per type) and `list_document_type_groups`. **No single
  cross-repo call** — iterate per active system (CIC, then OnBase) via `set_active_system_configuration`.
  Workday route differs (`/bow/core/document-types`, needs `documentTypeGroupId`).
- **Engine = KE, not KD.** KE **Data Curation API** (`cin-data-curation-api`, OCR/text extraction) + **Context
  API** (`cin-context-api`, classification / NER / summarization). KE is **async** and **does not persist** —
  our pipeline persists into the solution-config. KD not needed for config bootstrap.
- **Which mapping layers KE can produce:**
  - Layer 1 doctype → **KE classification** ✅
  - Layer 2 record linkage → **busObject id from our existing context-detection agent** (day-43), mapped to
    the designated/semantic BO-id keyword (`inputSource 1`) — **NOT OCR** ✅
  - Layer 3 import mappings → filename/static/username/filetype by heuristic; **content keywords via KE NER +
    field→keyword semantic match** (⚠️ accuracy-limited; needs confidence + review)
  - Layers 4–6 → mechanical once 1–3 exist
  - ⚠️ KE will **not** guarantee clean per-business-field extraction / schema / confidence — don't scope that.
- **PoC success metric:** generated config vs hand-written `servicenow-solution-config.json` diff; scope to
  **one ServiceNow doctype** (`change_request` / `change-request-type`).

---

## D. KE access setup in Staging — DONE today

- **KD ≠ KE.** KD roles do **not** authorize KE. Standard KD bundle lacks the KE keys; only KD-powered-by-ECE
  bundles them. So the KD groups were a red herring.
- **KE apps ARE present in Staging:** *Data Curation API* (`cin-data-curation-api`) and *Context Service*
  (`cin-context-api`). Group-level KE roles already assigned: *Context, Data Curation and Content Lake Group*
  + *Context User* + *Context and Data Curation Group* → **CIN Admins / CIN Users**.
- **Created a service user + registered external client `ke-poc-client`**; obtained `client_id` + `client_secret`.
  Storing via **dotnet user-secrets** (`Ke:ClientId`, `Ke:ClientSecret`, `Ke:TokenEndpoint`, `Ke:Scope`).
- **Token (Staging IAM):** `POST https://auth.staging.app.hyland.com/idp/connect/token`, grant
  `client_credentials`, scope `environment_authorization`. (Confirm exact scope / app mapping under
  External Systems → External Applications.)

---

## E. Next / TODO (day 45)

1. **Verify token** — `client_credentials` call returns an `access_token` (Step 1 done).
2. **Scaffold .NET console PoC** (`KePoc`) with user-secrets + a token service.
3. **Step 2 — first KE call:** submit one sample doc to **Data Curation API**, poll the async job, get text.
4. **Step 3 — Context API:** classify → doctype; NER → candidate fields.
5. **Step 4 — match:** pull UCEB keyword catalog (`list_document_types fetchMetadata=true`) for the active
   system; match extracted fields → ECM keywords; derive `inputSource`; resolve record-id keyword from the
   context busObject id.
6. **Step 5 — emit** a solution-config fragment and **diff** vs `servicenow-solution-config.json`.
7. **Blocker to clear:** need the **KE Data Curation + Context API base URLs + submit/results endpoints + async
   status contract** from the KE docs (don't guess them). KE docs hub:
   `hyland.github.io/ContentIntelligence-Docs/KnowledgeEnrichment`.
