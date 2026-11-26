# Day 49 — Design kickoff: KE Context + Agent Builder for config-less solution mapping

_Continues day-48. The goal is to replace the current heuristic import-field matcher with an Agent Builder mapping workflow. KE Context Service remains the document-understanding service; the existing AI context detector provides LOB page/record context; UCEB supplies the live ECM schema. We are proceeding one step at a time and will preserve the existing context agent behavior._

## Target PoC architecture (agreed 2026-11-17)

1. **LOB context:** the Chrome extension captures the active page and the existing `context_extractor_poc` Agent Builder agent identifies the business-object type/id and, where possible, relevant LOB fields/labels/values.
2. **Live ECM schema:** BFF calls MCP `list_document_types(fetchMetadata: true)` for the active system to obtain the real document types and their metadata attributes/keywords (names/labels, IDs, data types). No bundled/hardcoded catalog in the target design.
3. **Document understanding:** KE Context Service processes customer sample docs (verified to work with a raster PNG) and returns document classification, extracted entities, and summary. The tested response did not contain a raw OCR transcript (`textMetadata` was null); use the returned outputs unless raw-text access is separately verified.
4. **Agent mapping:** Agent Builder receives the LOB context/fields, KE results, and live ECM catalog. It proposes mappings for:
   - LOB business object -> ECM document type;
   - LOB record/owner fields -> ECM record-link/owner attributes;
   - import field mappings and value sources (LOB record, KE/document, filename, static, user);
   - display columns and query/default-list mapping where sufficient evidence exists.
5. **Final agent:** consolidates sub-agent proposals into a solution-config-shaped result with rationale/confidence. BFF validates every ECM type/attribute ID against the live UCEB catalog before showing/applying it. Ambiguous mappings require review; do not silently save guesses.

## Existing work / current state

- KE Context API token + presign -> PUT -> `/content/process` -> poll has been tested successfully. It classified text and raster-image samples and returned OCR-derived entities/summaries.
- Existing mapping PoC used a live `list_document_types(fetchMetadata: true)` call, but the BFF `KeMappingGenerator` still applied deterministic name/label heuristics for import fields. It is a prototype, not the desired final Agent Builder mapping architecture.
- In-plugin "Auto-map (PoC)" card calls BFF `POST /api/autoconfig/analyze` and currently displays mappings-only output.
- Existing context agent is `context_extractor_poc`, agent ID `741a9eac-ed49-46e1-84ef-0e93e9c2239d`, invoked by BFF through `ContextAgent:VersionId=latest` at `https://api.agents.ai.dev.app.hyland.com`.
- The context detector captures active-page URL/title/text/cleaned DOM in the extension (`ContextDetectorService.captureActivePage`) and sends it to BFF `/api/context/extract`; Agent Builder has no direct browser/DOM access.
- Agent Builder supports Task agents and Conversational/tool orchestration per prior day-43 research. Need inspect the actual Studio configuration before deciding whether to extend `context_extractor_poc` or create a separate mapping sub-agent; preserve current context detection behavior either way.

## Today's sequence — one step at a time

### Step 1 (next user action): inspect and preserve the existing context agent

Open Agent Builder Studio in the Dev environment, open `context_extractor_poc`, and inspect its **Instructions** and **Outcome/Output schema**. Before changing anything, copy/export the current instructions and schema or capture them for backup. We need to see the current prompt/output before deciding whether to extend it for LOB field inventory or leave it unchanged and pass its result to a new mapping agent.

Do not change or publish the agent yet. After Step 1, record the current outcome shape and decide the minimal additive change (if any). Then proceed to the next single step.

## Later steps (not yet done)

2. Design a stable JSON contract for LOB context + ECM catalog + KE outputs and Agent Builder mapping output.
3. Choose: extend existing context agent with explicit field inventory, or retain it and create separate mapping sub-agent(s); preserve its current context detection use.
4. Configure/test the mapping agent using known ServiceNow and Salesforce/OnBase examples; require evidence/confidence and a review path.
5. Update BFF orchestration to call context detection, fetch live ECM catalog, run KE Context, invoke mapping agent(s), validate output, and return mappings-only.
6. Test in-plugin and Bruno against at least one LOB/doctype; compare proposals with current hand-authored solution configs.
7. Only after mapping review works, consider an Apply action that persists the generated config.

## Important constraints

- KE classifies documents/extracts document content; it does not intrinsically know LOB fields or write solution configs.
- Agent Builder should propose field mappings from supplied evidence; it must not invent ECM attributes. Validate identifiers against the live UCEB catalog.
- LOB record fields may need an authorized LOB API/schema or observable page DOM; the current detector does not guarantee a complete field schema.
- `inputSource` meanings and solution-config contracts must be validated against existing UCEB/config behavior before applying generated mappings.
- Keep secrets out of prompts, logs, source control, and screenshots.

## New information supplied (2026-11-17): existing context agent contract

User shared the internal evaluation page for `context_extractor_poc`. It defines the agent's purpose as
positive detection of business-object context on arbitrary LOB pages and authoring a reusable extraction
recipe. Expected `businessContext` fields: `isBusinessObject`, `businessObjectType`, `businessObjectId`,
`sourceSystem`, `confidence`, `evidence`, `basis`, `extractionRecipe`. The recipe includes page match rules,
extractors for business-object type/id, and sample values. Inputs are `pageUrl`, `pageTitle`, `pageContent`,
and cleaned `pageHtml`.

Architecture implication: keep this agent focused on business-object detection + recipe. It does **not** return
a full inventory of all LOB record fields, so the mapping workflow must additionally provide page DOM/context
to a mapping agent (or explicitly extend the context agent's output with field inventory after backing up and
testing its current contract). Prefer a separate mapping agent initially to avoid breaking the existing
context-detection use case. Existing recipe can be used to identify/replay the primary record type/id, but
not treated as the complete set of importable LOB fields.

Step 1 remains: in Agent Builder Studio, inspect and back up the actual current Instructions and Outcome
schema for `context_extractor_poc` before changing anything. The shared evaluation page describes the target
contract/sample, but is not a copy of the exact current Studio configuration.

## Step 1 DONE — actual Studio outcome schema of `context_extractor_poc` (backed up)

One outcome `businessContext` (Object, Required, not multiple). Outcome instructions: populate every field;
empty string when unknown; never invent an ID; `isBusinessObject` false for lists, dashboards, search results,
settings, non-record pages. Fields (all Required):

| Field | Format | Notes |
|---|---|---|
| `isBusinessObject` | boolean | true only for ONE specific identifiable record |
| `businessObjectType` | string | concise singular noun; "" if not a business object |
| `businessObjectId` | string | exactly as shown; never invent/reformat |
| `sourceSystem` | string | inferred from domain/chrome/content |
| `confidence` | number | 0–1 |
| `evidence` | string | brief citation |
| `basis` | string | "- " bullet lines of signals/rules |
| `extractionRecipe` | string | single JSON string (fixed recipe schema) |

Observations:
- **No LOB field inventory** in the outcome → confirms the context agent cannot feed import mapping by itself.
- `extractionRecipe` is a **string** holding JSON (the BFF/engine must `JSON.parse` it).
- Possible prompt drift: Studio outcome says lists/dashboards → `false`, while the v3 eval page says a list
  with an in-focus item should still be detected. Not changing it now; note for the evals owner.
- Decision: **leave `context_extractor_poc` unchanged**; build a NEW mapping agent that consumes its output.

## Step 2 (next): create the new mapping agent shell + inputs

New **Task** agent `solution_config_mapper_poc` (same Dev Agent Builder environment, same model family).
Inputs (all String, JSON text where noted):
- `businessContext` — JSON of the context agent's `businessContext` outcome.
- `lobPageContent` — visible page text (same capture as context detection; source of LOB field labels/values).
- `ecmCatalog` — JSON of live UCEB `list_document_types(fetchMetadata:true)` (doctypes + attributes).
- `documentInsights` — JSON array of KE Context results per sample doc (classification, entities, summary).
Instructions and outcomes come in Steps 3–4.

## Step 2 detail — full Studio form for `solution_config_mapper_poc` (given to user)

Studio requires ≥1 input AND ≥1 outcome before Save, so Steps 2–4 are done in one pass:
- Details: name/display `solution_config_mapper_poc`, model Claude Haiku 4.5, description (≤200 chars),
  mode **Task**. Tools: none. Guardrails: none (PoC). Streaming: off.
- Inputs: the 4 String inputs above, each with a description.
- Instructions: mapping prompt (inputs referenced via the `/` picker) — doctype from KE classification vs live
  catalog names; record linkage from businessContext + page labels; import mappings with the inputSource
  codes 1–6; ECM ids ONLY from `ecmCatalog`; unresolved -> listed, not guessed.
- Outcome: one Object `solutionMapping` (Required). Nested lists are emitted as **JSON strings** (same pattern
  as `extractionRecipe`), because Studio object fields only offer scalar formats.
  Fields: `ecmDocumentTypeName`, `ecmDocumentTypeId`, `doctypeMapping`, `recordLinkageMapping`,
  `fieldImportMappings`, `displayColumns`, `queryMapping`, `unmapped`, `confidence` (number), `rationale`.

## Step 2 DONE — `solution_config_mapper_poc` created and saved in Studio

4 String inputs, instructions prompt (inputs referenced via `/`), outcome `solutionMapping` (Object, Required,
10 Required fields as above). Streaming off.

## Step 3 (next): Studio test run with real data

Test inputs prepared in `ke-poc/agent-test/` (paste each file's contents into the matching test box):
- `ecmCatalog.txt` — compacted LIVE Salesforce/OnBase catalog (14 doctypes, from `bff/dt-out.txt` =
  `list_document_types(fetchMetadata:true)`). Contains three invoice-like doctypes — `Invoices` (205),
  `Installation Invoice` (214), `Invoice` (119) — a good ambiguity test.
- `documentInsights.txt` — real KE Context result for `samples/invoice-sample.txt` (classification, summary,
  entities).
- `businessContext.txt` / `lobPageContent.txt` — hand-made fixtures modelled on the Salesforce Account
  `Health Systems` (`001gK00001KbuonQAB`); in the real flow these come from the context agent + page capture.
Expected sanity checks: doctype = `Invoices` (205); record link -> `223 hfs_sObject_Id`, owner -> `224`;
no invented ECM ids; invoice fields mostly inputSource 6 with KE sample values; Account Name vs Vendor Name
should NOT be force-matched (different parties) — ideally reported in `unmapped`/rationale.

## Step 3 RESULT — agent v1 test (Studio)

- ✅ Doctype `Invoices` (205) — correctly beat `Installation Invoice` (214) and `Invoice` (119).
- ✅ Record link `223 hfs_sObject_Id`, owner `224 hfs_sObject_owner` (lobField "Account Owner").
- ✅ No invented ECM ids. 229/226/227/234 -> inputSource 6 with correct KE sample values.
- ✅ Sensible `unmapped` (Account Number, phone/website/address) and confidence 0.72 with honest rationale.
- ❌ 228 Vendor Name -> inputSource 1 from Account Name ("Health Systems") although the doc vendor is
  "Acme Industrial Supply Co." (its own reason contradicts it). Note: hand-written `solution-config.json` also
  maps 228 <- Account `Name` (inputSource 1) — the agent reproduced that quirk; on this evidence it's wrong.
- Minor: used our internal key `businessObjectId` as the LOB field name for boContext/query.

## Step 4 (next): prompt v2 — two HARD RULES added
- inputSource 1 only when LOB value and document value agree; otherwise 6 + conflict in `unmapped`.
- Name LOB fields as the application calls them (e.g. "Id"), never "businessObjectId".
Re-test with the same four inputs; expect 228 -> 6 ("Acme Industrial Supply Co.") + conflict note.

## Step 4 RESULT — agent v2 test: PASS

- 228 Vendor Name -> inputSource 6, sample "Acme Industrial Supply Co.", Account Name conflict listed in
  `unmapped`. LOB field names now "Account Id" / "Account Owner". Doctype 205, link 223/224 unchanged.
- Agent emits `inputSource` as a number (6), not "6" -> normalized in BFF validation (no prompt change).

## Step 5 DONE — BFF + plugin wired to the mapping agent (code)

- `bff/Configuration/BffOptions.cs`: `MappingAgentOptions` (ApiBaseUrl/AgentId/VersionId).
- `bff/appsettings.json`: `MappingAgent` section (AgentId to fill in; VersionId `latest`).
- `bff/Models/BffModels.cs`: `AgentMapRequest(Attachments, BusinessContext, PageContent)`.
- `bff/Ke/AgentMappingValidator.cs`: parses the JSON-in-string outcome fields; checks the doctype id and every
  attribute id (record linkage, imports, display columns, query) against the LIVE catalog; normalizes
  `inputSource` to a "1"–"6" string; returns `{mapping, issues}`.
- `bff/Program.cs`: `POST /api/autoconfig/agent-map` — session + token refresh -> live
  `list_document_types(fetchMetadata)` -> KE Context per sample (classes = live doctype names) -> invoke
  `solution_config_mapper_poc` (`invoke-task`, inputs as JSON text) -> `ExtractNamedOutcome(body,
  "solutionMapping")` -> validate -> returns `{ mappings, validation:{valid, issues} }`.
  Old heuristic `/api/autoconfig/analyze` kept untouched for comparison.
- `agent-ng` ecm-picker: Auto-map now captures the active LOB page, reuses the last `businessContext` from
  "Detect context" (or calls `/api/context/extract` itself), then POSTs `/api/autoconfig/agent-map` with
  attachments + businessContext + pageContent.
- Builds: BFF clean; agent-ng EXIT 0 -> `browser-extension/ng`.

## Step 6 (next): end-to-end test in the plugin
Needs the mapping agent's id in `MappingAgent:AgentId`, BFF restart, extension reload, re-sign-in; then on the
Salesforce Account page with the Salesforce/OnBase system active: Auto-map -> pick invoice sample -> Generate.

- `solution_config_mapper_poc` agentId = `028b4d65-e2e5-4f75-bb50-69c8fc80c006` -> set in
  `bff/appsettings.json` `MappingAgent:AgentId`.
- Stack was fully down; started MCP `switch-lob.ps1 -Lob salesforce-staging` (:5200, silent sign-in from saved
  session, deployed staging UCEB, app key `hfs` -> route `api`) and BFF (:5010). Local UCEB :5000 not needed.
- MCP restarts on default system `cic`; set it to `OnBase9714_Integrations_Agent` (the system the extension
  had stored as `uceb_picked_system`) via `mcp-admin.ps1 -Tool set_active_system_configuration -ArgsJson
  '{"friendlyName":"..."}'` (param is `friendlyName`, not `systemFriendlyName`). Live catalog = 14 OnBase
  doctypes incl. `Invoices` (205), `Installation Invoice` (214), `Invoice` (119).

### Q&A: where the mapper's `lobPageContent` comes from
`ContextDetectorService.captureActivePage()` injects one script into the LOB tab and returns `pageUrl`,
`pageTitle`, `pageContent` (= `document.body.innerText` + same-origin iframes, cap 20k) and `pageHtml` (cleaned
DOM). Context agent gets all four; the mapper gets only `pageContent` (BFF trims to 12k). Limits: only rendered
/visible fields, display labels not API names, labels/values not strictly paired. Robust version = LOB API/schema.

## Step 6 RESULT — END-TO-END PASS in the plugin (2026-11-17)

Real Salesforce Account page (Health Systems) + `invoice-scan.png` (raster, pixels-only -> real OCR) via the
Auto-map card -> `/api/autoconfig/agent-map`:
- Doctype `Invoices` (205) chosen over `Installation Invoice` (214) / `Invoice` (119).
- Record link `Id` -> `223 hfs_sObject_Id`; owner `Account Owner` -> `224 hfs_sObject_owner`; query filters 223.
- Imports 228/229/227/226/234 -> inputSource "6" with KE values (Acme Industrial Supply Co., INV-2026-004821,
  USD 2322.00, 2026-11-10, USD). `unmapped`: Account Type/Phone/Website/Industry. Confidence 0.72.
- **Validation: `valid: true`, `issues: []`** — every doctype/attribute id exists in the live catalog.
- Minor: Invoice Total sample value includes currency ("USD 2322.00"); only a sample value, not a mapping issue.

Architecture proven: KE Context (OCR + classification + entities) -> live UCEB catalog -> Agent Builder
`solution_config_mapper_poc` makes all mapping decisions -> BFF validates. No hardcoded catalog, no heuristics.

## Next options
- Step 7: convert the validated agent mappings into the real solution-config shape (contextParams +
  additionalConfig with ecmMetadataFieldLabels / displayColumns / metadataFieldImportMappings / defaultListQuery)
  and diff it against the hand-written `solution-config.json` Invoices entry.
- Generalization test: ServiceNow change request on a CIC system (different LOB + ECM, named keywords).
- Later: review UI + Apply (`set_solution_configuration`) once mappings are reviewed.

## Step 7 — prompt v3 (LOB-agnostic, detailed) + inputs visible in output

- Wrote `ke-poc/agent-test/mapper-instructions-v3.txt`: role, glossary, input shapes, 8 golden rules,
  10-step procedure (sanity checks, doctype tie-breaks, record/owner linkage, inputSource decision table a–g
  incl. same-entity LOB fields and doc/LOB conflicts, KE value role-matching, columns, query, unmapped,
  confidence deductions, inputs restatement, rationale), LOB patterns (Salesforce, ServiceNow, Workday, Jira,
  Guidewire, SAP, unknown apps), ECM guidance (OnBase numeric ids, CIC named ids), edge cases, 2 worked examples.
  Each input referenced exactly once (section 2) so the `/` picker swap is simple.
- Inputs in the output, two ways:
  - Studio outcome: 3 new String fields `receivedBusinessContext` (verbatim), `receivedDocumentInsights`
    (verbatim), `receivedEcmCatalogSummary` (count + id/name/attributeCount per doctype). Full catalog echo
    avoided: costs tokens and an LLM copy can be truncated/altered.
  - BFF: response now also has `inputsSent { businessContext, ecmCatalog, documentInsights }` = exact bytes sent
    (no lobPageContent). Validator passes the `received*` fields through when present.
- BFF rebuilt (clean) and restarted; MCP left running (active system still OnBase9714_Integrations_Agent).

## Step 8 — query mapping done properly (research + v4)

User flagged: display columns / query had no reasons, and the "query" was only the default list — the ECM has
other search queries too.

### Research (live system + UCEB source)
- **Queries are predefined in the ECM.** MCP `list_queries` on OnBase9714_Integrations_Agent -> 9 queries:
  111 CL Loan Number, 110 Commercial Lending Search, 115 Docs by sObjectId, 102 Get Accounts, 109 Invoices,
  118 Patient Query New All, 120 Patient Report New, 121 Patient Visit And Report New, 119 Patient Visit New.
- `get_query_metadata` gives each query's `inputs` (id = attribute id, dataType, required,
  `supportedOperators`) and `resultColumns`. 109 Invoices: input 228 Vendor Name (EqualsCI/StartsWithCI/
  ContainsCI), columns 228/229/227/226/DocumentTypeName/DocumentName/223/DocumentID. 115 Docs by sObjectId:
  input 223, columns DocumentName/DocumentDate/DocumentTypeName/223.
- Solution config `queryConfig[busObject].queries[]` = `{name,id,type,default,filterClauses[{ecmFieldName,
  ecmFieldId,inputSource,value,editable,operator}],displayColumns,displayColumnConfig}`.
- UCEB `DocumentQueryService.ExecuteQuery`: query must exist in queryConfig for the busObject; every input sent
  must be a configured filterClause (else 25403); operator comes from the request (config operator =
  placeholder/default). `ExecuteDefaultListQueries`: auto-filters each default query by the boContext field =
  record id; a query that can't express that filter is DROPPED. => default list query must have the boContext
  attribute as an input -> on this system that's **115 Docs by sObjectId** (input 223). Our earlier
  `queryMapping` (filter 223) was synthetic, not tied to a real query.
- **inputSource authoritative meanings** = per-LOB lookup files
  `UCEB.Infra.Hx/Configuration/Resources/lookup.uceb.<lob>.v1.json` (hfs, hfsn, hfgw-bc/cc/pc, sap-hana, sufa):
  1 "<LOB> Object Property", 2 "Literal", 3 "LOB UserName", 4 "File Name", 5 "File Type", 6 "Manual Entry"
  (default 6). Same codes are validated on filterClauses. NOTE: existing configs use "3" on editable user-typed
  search fields (ServiceNow day-41, Salesforce 109) — per the lookup that means LOB user name; user-typed
  should be "6". Query operators are NOT in the lookup file; they come live per input (`supportedOperators`).
  CIC adapter can't do *CaseInsensitive / StartsWith (day-41).

### Code (BFF)
- `bff/Ke/EcmQueryCatalog.cs`: `list_queries` + `get_query_metadata` per query -> compact
  `[{id,name,type,maxResults,inputs[{id,name,dataType,required,supportedOperators}],resultColumns[...]}]`.
- `/api/autoconfig/agent-map`: fetches the query catalog, sends it as 5th agent input **`ecmQueries`**, echoes it
  in `inputsSent.ecmQueries`.
- `bff/Ke/AgentMappingValidator.cs` (rewritten): display columns accept `{ecmAttributeId,reason}` (+ built-in
  DocumentName/DocumentTypeName/DocumentDate/DocumentID); `queryMapping` = default list query must exist live,
  its `recordFilterAttributeId` must be an input of that query AND equal the boContext attribute, operator must be
  in that input's supportedOperators; `searchQueryMappings` = every queryId live, every filter clause field is a
  real input, operator supported, inputSource 1–6 (normalized to string), display columns ⊂ query resultColumns.
  Passes through `receivedEcmQueriesSummary`. Build clean; BFF restarted.

### Prompt v4 (`ke-poc/agent-test/mapper-instructions-v4.txt`, ~22.7k chars, 5 placeholders)
- Glossary: ECM query / default list query / search query. New input E `{{ecmQueries}}`.
- inputSource wording aligned to the lookup (3 = LOB user name, 6 = manual entry).
- STEP 5 columns with per-column reason; STEP 6 default list query chosen from REAL queries that have the
  boContext attribute as input (tie-breaks, exact-match operator only); STEP 6b search queries: consider every
  query, include relevant ones, one filter clause per used input (inputSource 1/2/3/6 + editable + supported
  operator + reason), columns ⊂ resultColumns, purpose + reason; unused queries -> unmapped.
- Confidence: −0.1 when no usable default list query. Worked example A updated (115 default, 109 search).

### Studio changes needed (user)
New input `ecmQueries` (String); new outcome fields `searchQueryMappings`, `receivedEcmQueriesSummary`; updated
instructions for `displayColumns` and `queryMapping`; replace Instructions with v4.
