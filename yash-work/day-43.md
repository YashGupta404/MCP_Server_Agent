# Day 43 — IDP→Automate verdict (HXSF-881 paused) → NEW: AI context-detection agent PoC (Hyland Agent Builder)

_Continues day-42. Two big things: (1) finished researching the IDP→Automate migration and reached a
definitive verdict with the IDP architects, then PAUSED it on the mentor's decision; (2) started an entirely
new task — a config-less AI agent that detects the business object (+ a reusable extraction recipe) on ANY
website, built on **Hyland Agent Builder**. Covers 2026-11-11 → 2026-11-13._

Demo stack unchanged (local UCEB :5000 + MCP :5200 via `switch-lob.ps1` + BFF :5010 + devtunnels). The new
context-detection PoC needs ONLY the BFF :5010 + the extension's normal sign-in — NOT MCP/IDP/tunnels.

---

## A. IDP → Automate (HXSF-881): researched, verdict reached, then PAUSED

Goal was to move IDP classify/extract off the direct REST API onto Hyland Automate (BPMN). Read the Confluence
docs (via the authenticated browser — direct fetch is SSO-blocked) and asked the IDP team + Rovo.

- **How IDP+Automate actually works:** IDP only runs INSIDE an Automate process. You build the IDP project
  (classes + fields) first, then **Build Process → "Document Classification and Field Extraction"** template
  auto-generates the whole BPMN (vars `contents` + `batchState` + a wired `idpConnector`). Built a throwaway
  `idp_servicenow` config + "Document AI Process" (key `Process_1767797409502`) to learn the flow.
- **`batchState` output shape:** after classify `{classId, className, classificationConfidence,
  classificationReviewStatus, extractionReviewStatus, markAsRejected, tables}`; after extract each field has
  `{name, value, boundingBox, ocrConfidence, extractionConfidence, extractionReviewStatus}`.
- **⚠️ VERDICT — Automate CANNOT do dynamic per-request schema.** Our doc types/metadata come from the
  solution-config (customer-defined, vary per LOB). Agent/IDP facts (Rovo on IDPAG RFC-0008/0012/0074/0015 +
  IDP team lead Dutta Arnab):
  - Automate's IDP connector is **bound to the ONE Document AI config deployed with the app**; runtime you
    pass only `contents`+`batchState`, never a schema. No API to create/edit a config from code (Studio UI +
    Release + Deploy only, restarts pods). Config is FROZEN at "Release"; change = "Application Upgrade".
  - The IDP **REST capability endpoints ARE stateless** and accept inline class/field defs per job
    (`POST /api/classification` + `/api/extraction`, zero-shot) — i.e. dynamic schema works ONLY via direct
    REST, which is **what our interim path already does**. BUT the IDP lead says REST is **not for production**:
    (1) not built for native integration (missing controls), (2) **no lifecycle data cleanup → legal/compliance
    cannot claim "customer data not stored in Hyland"**, (3) outside SDLC (may regress). RFC-0055 calls direct
    cross-boundary calls "non-compliant".
  - The SUPPORTED modeling→runtime path is **message-based deployment integration** (RFC-0063/0064/**0068**,
    pub/sub): on app deploy/upgrade IDP validates + snapshots the config. So "dynamic" in the supported world =
    re-deploy/upgrade the app when config changes, NOT per-request. Partly in-progress (HXIDP-7587/8029).
- **Limits:** 300 classes/project, 100 fields/class, 1 IDP config/app, batch 500 docs, 100 pages/min/account.
- **Mentor's decision (then PAUSE):** all doc types already exist in OnBase/CIC (Studio Modeller
  `hfsn_configurations`) and fit in 300. Plan when resumed = pre-create IDP class+fields for all 3 LOBs
  (Salesforce/OnBase, Workday/OnBase, ServiceNow/CIC) up front, and classify by selecting the suitable doc
  types per LOB (plugin knows the LOB). Then mentor moved me to the new task below → **HXSF-881 ON HOLD**.
  Full detail in repo memory `idp-automate-hxsf881.md`.

---

## B. NEW task: config-less AI agent — "detect business context on any website"

Mentor's ask: a PoC agent (with two sub-agents) that replaces manual solution-config with an AI/config-less
approach. Sub-agent 1 = CONTEXT extraction (business object + type + id for ANY site, by DOM/URL/any means,
with judgment about what qualifies). Sub-agent 2 = METADATA extraction (fields + values). NO IDP, NO Automate.

- **Platform decision (Rovo research): Hyland Agent Builder** (CIC, GA since 2025-07-16) — API-first, callable
  from our BFF, returns structured JSON, no IDP/Automate. NOT Agent Mesh (Automate-coupled). Multi-agent =
  "agent-as-tool" (a Tool/Conversational agent calling Task agents). Authoring UI = **Agent Builder Studio**
  at `appintel-dev-test.agent-studio.ai.dev.app.hyland.com`.
- **Hard constraint:** agents have NO browser/DOM/web-fetch tool. So the **Chrome extension must supply the
  page content**; the agent only reasons over it. (A server-side fetch would just hit the SaaS login wall —
  no user session. So client-supplied content is the only workable path.)

---

## C. Built sub-agent 1 — `context_extractor_poc` (Task agent)

- In Agent Builder Studio (dev env). `agentId = 741a9eac-ed49-46e1-84ef-0e93e9c2239d`.
- **Inputs** (String): `pageUrl`, `pageTitle`, `pageContent`, and later `pageHtml` (cleaned DOM).
- **Outcome `businessContext`** (Object, Required) fields: `isBusinessObject`(bool), `businessObjectType`,
  `businessObjectId`, `businessObject`(display name), `sourceSystem`, `confidence`(num), `evidence`, and later
  `basis` (multi-line list of signals/rules). Plus outcome **`extractionRecipe`** (String, the rule JSON — see F).
- **Instructions evolved a lot** (prompt lives in the UI, not a file):
  - v1: detailed business-context prompt (worked — ServiceNow home → `isBusinessObject:false` with evidence).
  - v2: rewrote as a GENERIC structural/semantic ruleset (positive/negative indicators, type/id extraction,
    confidence, guardrails; LOB names only as non-exhaustive illustrative examples, NOT the detection rule).
  - v3 (mentor): made it PERMISSIVE/positive — detect ANY keywordy entity+id (incl content/media like YouTube
    videos); dropped the "prefer false" bias; added `basis` (the list of rules it used). Added a **URL
    IDENTIFIER rule** (`/shorts/{id}`, `/watch?v={id}`, `/browse/{key}`…) and a **SPA/STALE-URL rule** (in
    list+detail apps the URL can be stale; prefer the displayed item; for email build a composite id =
    subject+sender+date).
- **UI facts learned:** agent modes = Task/Conversational/RAG/GraphRAG (orchestrator "Tool agent" ==
  Conversational). Outcome types = String/Number/Integer/Boolean/**Object** (+ "Allow multiple" → array).
  Object has an "Add Field" sub-editor. Inputs referenced in Instructions via the "/" picker. Builder/JSON
  toggle at bottom. **Agents created via the API can't be edited in Studio** (and vice-versa quirks).
  BFF `ContextAgent:VersionId` set to **`latest`** so new saved prompt versions auto-apply (no BFF restart).

---

## D. Extension + BFF wiring (test it on ANY site with just sign-in)

Reused the EXISTING Agent Builder plumbing (the BFF already invokes the "Talk with agent" Conversational agent
`a4374edc…` via the user's OAuth token at host `https://api.agents.ai.dev.app.hyland.com`).

- **BFF** (`bff/`): new `ContextAgent` config section (appsettings) + `ContextAgentOptions` (BffOptions.cs) +
  new endpoint **`POST /api/context/extract`** (Program.cs): takes `{pageUrl,pageTitle,pageContent,pageHtml}`,
  refreshes the OAuth token, POSTs `/v1/agents/{id}/versions/latest/invoke-task` with `{inputs:{…}}`, returns
  `{context, raw}`. Helper `ExtractTaskOutcome` parses the structured outcome. `ContextExtractRequest` added to
  BffModels.cs. **Gotcha:** Agent Builder requires `X-Session-ID` to be a valid UUID → use `Guid.NewGuid()`
  (the BFF session id is not a UUID → 400). pageContent capped 12k, pageHtml capped 120k chars.
- **Extension** (`agent-ng/`): `context-detector.service.ts` → new `captureActivePage()` = ONE fast
  `chrome.scripting.executeScript` injection that walks SAME-ORIGIN iframes (ServiceNow's record lives in the
  `gsft_main` iframe) and returns url + title + `innerText` + a **cleaned DOM** (`pageHtml`: full element tree
  + id/class/name/aria-*/data-*/role/href, strips script/style/svg/base64/handlers/comments). NOTE: an earlier
  `allFrames:true` version STALLED on heavy pages (2–3 min hang) — reverted to the single-injection walk.
  Added a PoC "AI context" card (Detect button + `<pre>` result) to BOTH `ecm-picker.view` (reachable right
  after sign-in, no MCP needed) and `documents.view`. Panel un-escapes `\n` (`.replace(/\\n/g,'\n')`) so the
  multi-line `basis` renders line-by-line.
- **Test flow:** start BFF :5010 → `chrome://extensions` reload → open panel → sign in → **Detect context**.
  Needs only the normal sign-in; MCP/IDP/tunnels NOT required. All additive — nothing existing was changed.
- **Results (working across LOBs):** ServiceNow Problem `PRB0007601` → type Problem; Jira Story `HXSF-893`;
  ChatGPT conversation; YouTube Short (after the URL-identifier rule); Outlook email (opaque but per-email
  message-id from the URL; composite subject+sender+date when the URL is stale). Home/dashboard →
  `isBusinessObject:false` correctly.

---

## E. Extraction recipe + rule engine (DESIGNED, approved, NOT yet built)

Goal: run the AI **once per page-type/LOB** to emit a machine-readable **extraction recipe**, then a **code
rule engine** replays it on every future page with **zero AI** (cost saving).

- Added input **`pageHtml`** (cleaned DOM structure, so the agent can author xpath/css) and outcome
  **`extractionRecipe`** (String holding JSON).
- **FIXED canonical recipe schema** (same shape for every LOB; agent just fills values or null):
  ```
  { sourceSystem, match:{urlHostPattern, urlPathPattern},
    fields:{ businessObjectType:{primary:<EXTRACTOR>, fallbacks:[…]},
             businessObjectId:{primary:<EXTRACTOR>, fallbacks:[…]} },
    sampleValues:{businessObjectType, businessObjectId} }
  EXTRACTOR = { source:"url|dom|constant|null",
    url:{queryParam,pathSegmentIndex,regex,regexGroup},
    dom:{cssSelector,xpath,labelText,attribute,regex,regexGroup},
    constant, validate, value }
  ```
  Prompt rules: ordered fallbacks; STABLE selectors by role/label (never bake the current value, e.g. no
  `img[alt='Story']`); `constant` only for truly fixed values (sourceSystem); prefer `url.regex` over
  `pathSegmentIndex`; add `validate` regex; selectors only from `pageHtml`; put extracted values into
  `value`/`sampleValues` for self-verification.
- **Validation observations (Jira):** `/browse/HXSF-893` detail page and the `/jira/software/…?selectedIssue=`
  BOARD page produce DIFFERENT recipes, each scoped by `match.urlPathPattern`. => **recipes are per PAGE-TYPE,
  not just per LOB**; the engine keeps a LIBRARY keyed by `match` and picks by URL. (The agent still sometimes
  puts a value-specific selector as `primary` for type — harmless because the generic fallback covers it.)
- **Rule-engine design (TO BUILD):** runs in the extension. Detect → capture url+DOM → find the stored recipe
  whose `match` fits → run primary→fallbacks per field (URL regex/param or `document.evaluate`/`querySelector`)
  → `validate` → return `{type,id}` with NO AI. Store recipes in `chrome.storage.local` keyed by
  sourceSystem+match. AI fallback ONLY on a miss (no matching recipe, or validation fails) → then call
  `/api/context/extract`, show result, and store the new recipe. **User said: do NOT build yet — hold.**

---

## Key facts / files
- Agent: `context_extractor_poc`, `agentId 741a9eac-ed49-46e1-84ef-0e93e9c2239d`, BFF uses version `latest`.
  Host `https://api.agents.ai.dev.app.hyland.com`; Studio `appintel-dev-test.agent-studio.ai.dev.app.hyland.com`.
- BFF files: `bff/Program.cs` (`/api/context/extract`, `ExtractTaskOutcome`), `bff/appsettings.json`
  (`ContextAgent`), `bff/Configuration/BffOptions.cs` (`ContextAgentOptions`), `bff/Models/BffModels.cs`
  (`ContextExtractRequest`).
- Extension files: `agent-ng/src/app/services/context-detector.service.ts` (`captureActivePage`),
  `…/views/ecm-picker/ecm-picker.view.{ts,html}` and `…/views/documents/documents.view.{ts,html}`
  (`detectAiContext` + PoC card). Build: `cd agent-ng; npm run build` → outputs to `browser-extension/ng`.
- Repo memory: `agent-builder-poc.md` (full technical detail + the prompts + ids), `idp-automate-hxsf881.md`.

## Pending / next
- **Build the rule engine** (§E) when resumed — the zero-AI recipe replay + storage + AI-fallback loop.
- **Sub-agent 2 (metadata extractor)** + the orchestrator (Conversational Tool agent) — not started.
- Validate a recipe on a 2nd record of the SAME page-type (e.g. a Jira Bug) to confirm type generalizes.
- Tighten the prompt so the TYPE `primary` stops being value-specific (currently harmless via fallback).
- The PoC card uses inline styles (clearly a PoC) — Satori-ize if it graduates past PoC.
