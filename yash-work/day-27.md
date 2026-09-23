# Day 27 — 2026-09-23

_Focus: finished the **chatbot search** for Salesforce, then brought up **Workday-staging** (catch-22 finally
resolved) and figured out the **query/search story for Workday** (queries are Salesforce-only)._

---

## 1. Chatbot search — finished + fixed (Salesforce)
- Implemented the conversational search: BFF `/api/chat` injects a **search catalog** (`McpJsonRpc.BuildSearchCatalogAsync(boType)`)
  listing ALL `queryConfig[boType]` queries + their filter field(s)/operator, so the agent offers the queries,
  asks for a value, runs `query_documents`, and reports results. Flow works great (agent shows a Query/Search-By
  table, then asks for the value).
- **Option B chosen**: include ALL configured queries (matches the panel's Search dropdown), not just editable ones.
- **Bug fixed — maxResults cap**: search hint originally said `maxResults=500`, but query 109 (Invoices) etc. have
  Max Items = 50 → `query_documents` 400 "ContentQueryService: Invalid data". Proven: 500 → 400, **25 → works**
  (found ACME SUPPLIES, INV-1007). Changed search steering to `maxResults=25`. (Only query 115 has Max Items 500.)

## 2. Workday-staging — CATCH-22 RESOLVED
- Mentor/IAM **restored arizzo's `environment_authorization` grant**. Re-added `environment_authorization` to the
  workday-staging Scopes in `switch-lob.ps1`. Result: login succeeds WITH env auth → token `appkey=wdx` → route `bow`,
  and **`/bow/common/build-info` → 200** (was 403). Workday-staging + OnBase now reachable. (Do NOT revert the scope.)
- Active system set to **`onbase_hcm_stg`** (`2412c2db-…`).
- **Default document list WORKS natively**: `list_documents(employee WID 4bc212416f234ba1b4749e4bebe4c2eb)` (Anthony
  Rizzo) via `/bow/core/business-objects/{WID}/documents` returns the full doc set with rich attributes (Document
  Name/Date/Type + Employee ID/Name). **No custom query needed** (unlike Salesforce, which needed query 115).

## 3. Workday search — queries don't exist for Workday (mentor-confirmed)
- The query capability is `api/core`-only. Proven both ways:
  - `GET /api/core/queries` (onbase_hcm_stg) → **403** "token valid but denied" (wdx token denied on the `api` app).
  - `GET /bow/core/queries` → **404** (no queries route on `bow`). Tried routing queries via `CoreBasePath` in the
    MCP → 404 → reverted (kept hardcoded `/api/core/queries`; identical to Salesforce since its CoreBasePath=api/core).
- POST `business-objects/{id}/documents` = **capture/upload** (`CanCaptureDocuments`), NOT search. The GET listing has
  no server-side filter param.
- **Mentor's answer: queries/search only exist for Salesforce, not Workday.** So drop server-side query search for WD.

## 4. Decision — Workday search UX (to implement next)
- When connected to **Workday** (employee / onbase_hcm_stg): **HIDE the search-query dropdown** in the panel; keep the
  **"Filter results" box** (client-side filter over the full loaded list) as the search.
- **Chatbot**: no query search either — it answers/filters from the listed document **metadata** (as it already does),
  not the query mechanism.
- Salesforce keeps the full server-side query search (dropdown + chatbot catalog).

## 5. Workday panel — page-employee + self-chatbot + permission gating
- **Stage 1 (panel = page employee):** removed the self-override; the panel now shows the documents of the worker
  whose page you're on (a subordinate you can see), via the `needsResolve` → `resolveWorker` flow.
- **Stage 2 (chatbot = self):** `loadContextPanel` resolves the signed-in worker (`selfWorker` = arizzo/Anthony)
  separately; the chat submit handler uses `selfWorker` for Workday so "show me my documents" always returns
  Anthony's list, even while the panel is showing a subordinate's page.
- **Stage 3 (permission gating) — "try and react", gate = Employee ID visibility:**
  - Rule from mentor + testing: Workday only shows the **Employee ID** on the profiles of workers you're authorised
    to view/capture (your subordinates). Peers / your own manager show a **name but no Employee ID**.
  - `detector.js detectWorkerProfile()`: Employee ID present → `needsResolve` (resolve WID + show docs). Name only,
    no Employee ID → `{ notAuthorized: true, displayName: name }`. `detect()` + `contextKey()` pass the flag through.
  - `popup.js loadContextPanel`: new `notAuthorized` branch hides the doc list, filter box and upload card and shows
    **"You are not authorised to view or capture <Name>'s documents."** `selfWorker` still resolves first, so the
    chatbot answers "my documents" as Anthony even on a peer's page. Added `id="uploadSection"` to the upload
    `<section>` for reliable show/hide.
  - **Fix for Oliver Reynolds (manager) showing "No Record":** the `data-automation-id` name selectors didn't match
    this Workday build → name empty → `detect()` returned null. Added a `document.title` fallback that parses
    `"<Name> - View Worker"` (reliable across builds). Now a peer/manager page resolves to the not-authorised message.

## Stack state (end of day)
MCP `:5200` (workday-staging, appkey=wdx, active=onbase_hcm_stg), BFF `:5010`, devtunnel `giant-ant-2f6br43`.
Note: MCP in-memory token only → every restart needs a fresh interactive login (arizzo).
