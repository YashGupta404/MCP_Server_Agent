# Day 55 — 2026-11-26

## New PoC: HIX (Hyland Integrations Extension) — integration profiles + AI-driven capture

### Context gathered
- Re-studied the whole project (UCEB API, old Copilot/stdio MCP PoC, current MCP server, BFF, agent-ng, browser
  extension, day logs 1–54, docs) as the base for the new, larger PoC.
- Mentor diagrams:
  - **Flow**: Browser (URL, DOM) -> agent "extract context + LOB properties" -> LOB properties JSON;
    Document -> KE document extraction -> markdown; UCEB -> document types + keywords; UCEB import config
    processor -> import metadata mapping (optional). All feed agent "keyword extraction" -> keyword/value map ->
    HIX upload processor.
  - **Integration profile**: the LOB site URL identifies a profile; a profile links 1..n UCEB system configs
    (one default), each with its own solution config, ideally grouped under a "UCEB LOB tenant".
- Mentor: UCEB has no LOB-tenant concept yet, so HIX stores one solution config per system config. To demo
  Salesforce + ServiceNow, create two system configs and use one per integration profile.
- UCEB `inputSource` values (lookup.uceb.hfs.v1.json): 1 object property, 2 literal, 3 LOB user name,
  4 file name, 5 file type, 6 manual entry. There is no "extracted from the document" source.

### Mockup studied (ChatGPT site source: `Downloads/Hyland-Integration-Extension`)
- Buildless JS prototype, everything simulated (no IAM/UCEB/AI), profiles in localStorage.
- Admin (gear): integration profile list (one per LOB tenant), 3-step wizard (content systems + default,
  business objects incl. custom, review & save with 5 sections: tenant / systems / objects / import / view,
  each with a confidence; < 85% must be marked reviewed; edits invalidate the review).
- Per system x business object: document types, a population source per keyword (LOB / document / manual),
  one view query per document type (all filtered by the business object id, results combined).
- End user: profile matched by hostname (+ optional path/query), system picker limited to the profile,
  Documents tab (combined queries, search, pagination, preview/download/delete), Upload tab (multi-file
  tiles, background extraction, lowest keyword confidence per tile, keyword panel, human review with the
  document next to the form and source highlights, upload one/all). No chat tab.

### Decisions
- Build everything in the mockup except the hix-sandbox / hix-prod environment dropdown.
- Keep our Satori UI and the chat tab. Removed the AI context / Auto-map PoC cards from the system picker.
- Use the **hfs app key** (no HIX app key yet). Profiles stored locally in `bff/data/profiles.json`.
- On profile save, write the real solution config into each selected system config (backup first).
- Multiple single-purpose agents (human in the loop); I write prompts + IO schemas, I create them in Studio.
- Mentor rules (15:23):
  1. Demo both Salesforce and ServiceNow.
  2. Context extraction and everything else via AI only — solution configuration and end user.
  3. Review uses the current image + PDF rendering only (no HxTS).
  4. No integration profile -> the integration must not work for the end user.
- Work step by step, one step at a time.

### Started (kept, not wired or compiled yet)
- `bff/Profiles/`: `ProfileModels.cs`, `ProfileStore.cs` (JSON store), `ProfileCatalog.cs` (tenant from URL,
  profile match, MCP active-system scope, per-system catalog cache), `ProfileSuggester.cs` (rule-based
  suggestions — to be replaced by agents per mentor rule 2), `ProfileApplier.cs` (profile -> solution config).
- `EcmKeyword.Required` parsed from `isRequired`.

### Step 1 (proposed, waiting for confirmation)
Agent lineup — admin: A1 tenant identifier, A2 document type matcher, A3 keyword source assigner,
A4 query selector; end user: E1 record context + LOB properties, E2 document classifier, E3 keyword extractor.
Open questions for the mentor: tenant id / record detection by AI vs code, highlights on images without HxTS,
who creates the two system configs, model choice (Haiku 4.5).
