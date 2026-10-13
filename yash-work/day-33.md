# Day 33 — 2026-10-13

_Focus: brought up the full demo stack for the revamped **Angular** plugin (Salesforce +
`OnBase9714_Integrations_Agent`), continuing the UI/UX revamp from day 32._

---

## 1. Demo stack started (all green)

| # | Component | State |
|---|---|---|
| 1 | **MCP :5200** | `switch-lob.ps1 -Lob salesforce-staging` → logged in, `[Lob] Token app key = 'hfs' -> api`, warm-up complete (token ~15 min) |
| 2 | **Active system** | `set_active_system_configuration` → **OnBase9714_Integrations_Agent** |
| 3 | **BFF :5010** | `dotnet run --project bff\UcebBff.csproj` (Development) → `https://localhost:5010` |
| 4 | **devtunnel (chatbot)** | `devtunnel host giant-ant-2f6br43` → `https://4kw1kpcm-5200.asse.devtunnels.ms` (port 5200) |
| 5 | **devtunnel (IDP files)** | `joyful-horse-g88zxrw` → `https://qpzmsssb-5010.euw.devtunnels.ms` (port 5010) |
| 6 | **IDP token** | `idp-refresh.ps1` → refreshed silently (expires_in 900s) |
| 7 | **Angular UI** | `npm run build` → `browser-extension/ng/` rebuilt (main.js 11:53) |

**Verified end-to-end before demoing:** `query_documents(account, queryId=115, filterFieldId=223,
filterValue=001gK00001KbuonQAB, maxResults=500)` → **51 documents** with full columns
(Document Name / Document Date / Document Type).

### Gotcha found + fixed during startup
`Idp:PublicBaseUrl` was already set to `https://qpzmsssb-5010.euw.devtunnels.ms`, but **that tunnel wasn't
hosting** (only the :5200 MCP tunnel was). The euw tunnel `joyful-horse-g88zxrw` had **0 ports**, and
`devtunnel host <id> -p 5010` fails with *"Batch update of ports is not supported"* → must
**create the port first**, then host:
```
devtunnel port create joyful-horse-g88zxrw -p 5010 --protocol https
devtunnel host joyful-horse-g88zxrw
```
It then came up on the exact saved URL (`qpzmsssb-5010.euw.devtunnels.ms`), so **no user-secret change was
needed**. (Without this, IDP auto-classify is disabled because IDP's cloud can't fetch the staged file.)

Other repeat gotchas: fresh PowerShell terminals need
`Set-ExecutionPolicy -Scope Process Bypass -Force` before `npm`/`.ps1`; the persistent terminal
intermittently corrupts (cmdlets "not recognized") → re-run in a fresh terminal.

## 2. Ready to demo
Open a Salesforce Account record → extension side panel (Angular build) → sign in → system picker →
Documents (list / Query search / row-click CIC viewer / upload) and **Talk with agent** (chat via
devtunnel → MCP).

---

## 3. Mid-demo failure: "document type + doc list not appearing" → OnBase/CFS broker stalls

**Symptom:** panel showed *"No content linked to this record yet."* and an empty doc-type dropdown, after
both had worked 20 minutes earlier with zero code changes.

**Root cause (read from the MCP + BFF logs, not guessed): the deployed staging UCEB → CFS broker → OnBase VM
was intermittently stalling.** The token was fine (`Using cached user token`), the active system was right.
UCEB returned, per call (each after a 20–26 s wait):
```
503  DocumentTypeService: Request timeout. Job <id> is still processing. Retry after 3 seconds.
503  ContentQueryService:  Request timeout. Job <id> is still processing. Retry after 3 seconds.
408  DocumentTypeService: Operation timed out after 20 seconds. Processed 1 groups before timeout.
     To avoid timeouts, consider using the 'doctypeGroupId' query parameter ... instead of all groups at once.
```
Proof it was intermittent (not broken config/code): a **direct** `query_documents(queryId=115, filter 223 =
recordId)` returned **51 docs**, while the BFF's *identical* call (log: `query_documents called for
type=account id=(null) queryId=115`) 503'd — same tool, same args, different outcome seconds apart.
`/api/context` therefore logged `-> 0 document(s)` after ~26–32 s.

**MCP restart** (user's call) + fresh sign-in restored the doc LIST (51 docs again); doc-types stayed slow.

### Fix applied (BFF, our side) — auto-retry transient broker stalls
`bff/Mcp/McpJsonRpc.cs`: `CallToolWithStatusAsync` now wraps the single call (extracted as
`CallToolOnceAsync`) in a **3-attempt retry with a 3 s delay**, triggered by `IsTransientBrokerStall(text)`
(matches *"still processing" / "Retry after" / "Request timeout" / "Operation timed out"*). The backend
literally instructs callers to retry, so this makes the panel resilient instead of showing an empty list on
the first stall. Benefits **every** BFF→MCP call (context, doctypes, queries, upload) since they all route
through this method. BFF restarted; no MCP rebuild needed.

### Still open (post-demo)
`list_document_types` asks UCEB for **all** content-type groups, which now exceeds UCEB's 20 s budget on this
OnBase repo (hence the 408). Proper fix = pass **`doctypeGroupId`** on the document-types call so it fetches
one group instead of all (needs an MCP change + rebuild). Only affects the upload doc-type dropdown.

### Fix 2 — doc-type dropdown no longer enumerates the whole repo (no MCP rebuild needed)
Rather than fetching every content-type group (the thing that 408s), the dropdown now reads the
**solution config**, which already lists exactly the content types that are *configured/uploadable* for the
record's business object — one fast config read instead of a broker-wide enumeration.
- `bff/Program.cs` `/api/doctypes`: accepts `?businessObjectType=`; when present it calls the existing
  `McpJsonRpc.GetConfiguredCicContentTypesAsync(boType)` (reads `businessObjectConfig.additionalConfig[]`
  → `ecmContentTypeName`) and returns those sorted, tagged `source: "solution-config"`.
  Falls back to `list_document_types` only when no boType is given or nothing is configured for it.
- `agent-ng` `DocumentService.listDocTypes(businessObjectType?)` + `documents.view.ts loadDocTypes()` now
  pass `context.businessObjectType`.
- Bonus: the dropdown now shows only types that can actually be filed on this record (matches native HFS),
  instead of every type in the repository.
BFF restarted + Angular rebuilt (12:32).

### Why it "suddenly" broke (answer to "it was working perfectly")
Nothing changed on our side — it's **load/latency on the shared staging OnBase/CFS broker**. UCEB gives each
broker job a fixed ~20 s budget; when the broker is busy the job isn't done in time and UCEB returns
503 "still processing"/408 "timed out" instead of data. That's why the *same* call succeeded directly and
failed from the panel seconds apart. Both fixes above make the plugin tolerate that: **retry** the transient
stalls, and **stop making the most expensive call at all** for the dropdown.

---

## 4. Figma design pass (chat + chrome)
Matched the remaining deltas against the Figma mock: avatar now has the **green check badge**; close, refresh
and collapse are **real stroke SVG icons** (were text glyphs `×` `↻` `^`) and the chevron actually
collapses/expands the record row; the chat composer is a **pill** with a proper `+` icon and a **circular
purple send button**; chat area made taller so the empty-state illustration sits vertically centred; header
type tightened (app name 14.5/700 -0.01em, tag 9.5px 0.1em) and the global font stack now prefers **Inter**
(what the Figma uses) with Segoe UI fallback + grayscale smoothing.

**Second pass (icons + type fidelity):**
- **Real Inter webfont** — added the Google Fonts `<link>` (Inter 400/500/600/700) to `src/index.html`.
  MV3 extension pages only restrict `script-src`/`object-src`, so a remote stylesheet/font loads fine;
  Segoe UI remains the fallback if it can't be fetched. (Previously the stack *asked* for Inter but it
  isn't installed on Windows, so everything silently rendered in Segoe UI — hence "font not same".)
- **Cloud + document icons redrawn as inline SVGs** matching the mock's thin outline style
  (cloud = 21px teal `#27B5A8` 1.6 stroke; file = 16px indigo `#4B40B5` outline with folded corner).
  They were previously CSS-background SVGs with a filled/opacity look.
- **Send arrow** is now a filled **paper-plane**, not a line arrow.
- System card spacing/type tuned to the mock: padding `15px 16px 16px`, name 15px/600 -0.01em with
  ellipsis, record row gap 7px / margin-top 11px, "Account" 13px/600, ID monospace 11.5px.

## 5. DEV LOOP — stop re-signing-in after every UI change
Problem: every rebuild meant reload extension → sign in → pick ECM → navigate back. Cause: the BFF session
lives in `chrome.storage.session`, which **Chrome wipes on every extension reload**, and the ECM pick was
never remembered.

Added a **`devKeepSession`** flag (`public/configs.json`, typed in `app-config.model.ts`):
- `AgentAuthService.sessionStore` → uses `chrome.storage.local` when the flag is on (survives reloads),
  otherwise the normal `storage.session` (auto sign-out on browser close).
- `SystemConfigService` → `setActive()` persists the chosen friendlyName (`uceb_picked_system`);
  new `rememberedSystem()` / `restoreRemembered()` re-apply it on the MCP (which resets to its default
  on restart).
- `SignInView.ngOnInit` → if a session exists **and** a system is remembered, it re-applies it and routes
  **straight to `/documents`**, skipping the picker.

Dev loop is now: `npx ng build --watch` (running) → edit → **reload just the panel** (right-click → Inspect →
Ctrl+R) → change is live, still signed in, straight to Documents.

**TO REVERT BEFORE SHIPPING:** set `"devKeepSession": false` in `configs.json` — the normal
sign-in → system-picker → documents flow returns (nothing else to undo).

Also fixed while here: `background.js` `onStartup` was deleting the session from **storage.local** (the
deliberate auto-sign-out), which defeated the flag on every browser restart — it now reads
`ng/configs.json` and skips the wipe when `devKeepSession` is true. And `BffApiService` now treats a
**401 as "session expired"**: it clears the stored session so the app returns to sign-in instead of
erroring on every call (this happens whenever the BFF is restarted, since its session store is in-memory).

---

## 6. OnBase backend degraded → then went DOWN (environment, not us)
Progression observed over ~1 hour on the same unchanged stack:
1. `503 … Job is still processing. Retry after 3 seconds` (broker slow)
2. `408 … Operation timed out after 20 seconds. Processed 1 groups` (broker slower)
3. `404 ExecuteQuery: A service is currently unavailable.` ← **service actually down**

**Scoped the outage precisely:** `get_build_info` → **200 OK**, so UCEB itself, the network and the token
are all healthy; only the **OnBase/CFS content services** (ExecuteQuery → ContentQueryService → CFS broker →
OnBase VM) are unavailable. A *direct* `query_documents` reproduces the identical 404, proving it is not the
panel/BFF.

The first 404 slipped past the new retry (19 s = a single attempt) because
`"A service is currently unavailable"` didn't match the retry phrases — added
`"currently unavailable"` to `IsTransientBrokerStall`, so the panel will now ride out this class of
outage too and recover on its own once the service returns. BFF restarted.

**Nothing further to do on our side** — the plugin, BFF and MCP are proven healthy; this needs the
OnBase/CFS environment to come back (same family as the earlier MCA-app-pool restarts the mentor has done).

### CORRECTED DIAGNOSIS (the 404 was NOT an outage)
Swept every query on the same config/token and the picture changed completely:

| Query | Result size | Outcome |
|---|---|---|
| 109 Invoices (Vendor Name = ACME SUPPLIES) | 1 doc | ✅ fast |
| 110 Commercial Lending Search | empty | ✅ fast |
| 115 Docs by sObjectId **with a dummy value** | no match | ✅ fast ("No documents found") |
| **115 Docs by sObjectId with the real record** | **51 docs** | ❌ 503/408/404 |
| 102 / 111 | — | "No query configuration found" = simply not registered in `queryConfig[account]` in our copied config (not a fault) |

So **UCEB, CFS, OnBase, the token and our config are all fine**, and query 115 is *not* broken — its
*metadata* reads fine and it executes fine when the result set is small. The failure correlates with
**result size**: pulling this record's **51 documents** (4 columns each) out of OnBase exceeds the broker's
~20 s budget, so UCEB returns 503 "still processing" → 408 "timed out" → occasionally 404 "a service is
currently unavailable" (UCEB's generic catch-all, which is what made it look like an outage).
Light queries on the same system return instantly, every time.

### Fixes that address THIS (both in the BFF, no MCP rebuild)
1. **Retry** transient broker responses (`still processing` / `Retry after` / `Request timeout` /
   `Operation timed out` / `currently unavailable`) — 3 attempts, 3 s apart, in `CallToolWithStatusAsync`
   (so every BFF→MCP call benefits). The job often completes server-side and a retry picks it up.
2. **Fallback in `/api/context`**: if the configured `defaultListQuery` returns **nothing**, fall through to
   the merged record listing (`list_documents` → generic `query_documents`) instead of reporting an empty
   panel. Previously the fallback only ran when *no* dlq was configured, so one slow/failing query blanked
   the list.

**Real fix for the root cause (OnBase side, for the mentor):** the "Docs by sObjectId" custom query is too
slow for 51+ documents. Options: raise the broker/UCEB timeout, or tune the OnBase Custom Query (indexing /
fewer display columns), or have the panel page the results instead of pulling all 500 at once.

---

## 7. Upload metadata restore attempt, then parked

- Ported the old popup's upload metadata behavior into the Angular side panel so the selected file could
  show its classified doc type and extracted metadata fields.
- Tried to fix the bug where switching the doc type away and back should restore the previous metadata
  values instead of leaving the inputs empty.
- Added snapshot/restore helpers in the Angular documents view and rebuilt the UI, but the visible behavior
  still did not change, so the issue was left parked for now.
- User explicitly asked to leave it after the last check, so no further changes were made to that path.
- Verified the Angular build still succeeds after the upload-state changes.
