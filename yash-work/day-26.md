# Day 26 — 2026-09-22

_Focus: **stack startup for the Salesforce demo**, a plan for **search inside the chatbot**, and a round of
**UI/UX polish** on the extension panel (search popup, document-list table, upload metadata reset, remove the
redundant Attach button)._

---

## 1. Brought the full Salesforce demo stack up (new day, all was down)
Order (each verified):
1. **MCP** (`switch-lob.ps1 -Lob salesforce-staging`) → `--http` on `:5200`. Interactive IAM login
   (token is **in-memory only** — every MCP restart needs a fresh sign-in; app key resolves to `hfs` → route `api`).
2. **Active system** (`set-active.ps1`) → `OnBase9714_Integrations_Agent`; queries include **115 Docs by sObjectId**.
3. **BFF** (`dotnet run UcebBff.csproj`) on `:5010`.
4. **Devtunnel** (`devtunnel host giant-ant-2f6br43`) → `https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`
   (REQUIRED for the chatbot — the cloud agent reaches the local MCP only through this tunnel).
5. Verified: `query_documents(queryId=115, filterFieldId=223)` → **17 docs**, generic columns
   (Document Name / Document Date / Document Type), Invoices **and** COM - Application.

Gotcha re-confirmed: the MCP’s `dotnet run` child process dies if its terminal is reclaimed, and the token cache
does **not** survive a restart → sign in again each time.

## 2. Plan — search inside the chatbot (deferred, not yet built)
Agreed approach = mirror the panel’s query bar, LLM-driven:
- Reuse existing MCP tools (`list_queries` → `get_query_metadata` → `query_documents`) already exposed via the tunnel.
- BFF injects the **query catalog** (each configured query + its searchable inputs/fields/operators) into `/api/chat`
  so the agent knows what it can search by, then steers it to call `query_documents` and report all results.
- UX idea (user): chatbot offers the list of **custom queries** as choices, then asks for the **field value**, then runs
  the search — i.e. a conversational version of the dropdown. Good method; deterministic panel query bar stays the
  source of truth.

## 3. Confirmed: the frontend is plain vanilla HTML/CSS/JS (no framework)
- `package.json` sole dependency = `pdfjs-dist` (PDF.js viewer). No React/Vue/Angular.
- Manifest V3 Chrome extension; native DOM APIs, ES modules (`popup.js`, `agent.js`, `auth.js`, `background.js`,
  `config.js`), content scripts, `lib/pdf.mjs`.

## 4. UI/UX polish (this session)
- **Search query popup**: redesigned to industry-standard, consistent with the plugin styling.
- **Document-list table**: cleaned up rendering (spacing/alignment/hover), consistent look.
- **Upload metadata**: now **clears the metadata form after a successful upload** (previously the entered values
  lingered).
- **Removed the redundant Attach button** (Browse Files already handles selecting/uploading).

(See the extension `popup.html` / `popup.css` / `popup.js` changes for specifics.)
