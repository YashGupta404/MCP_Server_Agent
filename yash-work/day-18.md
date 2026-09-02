# Day 18

_Focus: made OnBase **document listing** and **upload** actually work end-to-end from the MCP. Built a new
`query_documents` tool on the mentor's **#1 global-queryConfig endpoint** (the old list path is a 501 stub
for OnBase), then fixed OnBase **upload** so it sends the content type's real import keyword instead of
`hfs_Name`, and finally proved a full **upload → list-that-exact-doc** round-trip against OnBase9714._

---

## 1. Why the old list path didn't work for OnBase

There are two document-list endpoints in UCEB:

1. **#1 — global queryConfig:** `POST api/core/queries/{queryId}/execute` (`DocumentQueryController`,
   policy `CanQueryDocuments`). Works for **both** CIC and OnBase/CFS — only the `systemFriendlyName`
   selector changes which backend resolves it.
2. **#2 — record-scoped:** `GET api/core/business-objects/{id}/documents`. For OnBase this hits a CFS
   **501 not-implemented stub**, and the mentor noted it gets **wiped in staging** when the Salesforce
   config UI is saved.

Mentor's guidance: *"use #1 for document listings for now."* So the MCP needed a tool that calls #1.

---

## 2. New MCP tool — `query_documents`

**`Clients/UcebApiClient.cs` — `QueryDocumentsAsync(...)`:**
- Loads the solution config once, resolves the `queryId` from
  `data.configurations.businessObjectConfig.queryConfig[busObject == businessObjectType].queries[]`
  (prefers the one flagged `default`) when the caller doesn't pass one.
- Builds the **required, non-empty** `inputs` array (the endpoint rejects empty inputs). The JSON key is
  literally `operator` (a C# keyword), so the body is built with a `Dictionary` to control the property
  name exactly.
- `POST WithSystemConfig("/api/core/queries/{queryId}/execute")`, deserialized into the existing
  `DocumentListResponse` shape (`data.columns` + `data.documents` + `total`).

**`Tools/UcebTools.cs` — `query_documents` tool:** mirrors `list_documents` formatting (`docId` +
`columnId → name` attributes). Params: `businessObjectType` (required), optional `businessObjectId`,
`queryId`, `filterFieldId` / `filterValue`, `filterOperator` (default `EqualsCaseInsensitive`),
`maxResults`.

**Key facts learned:**
- `systemFriendlyName` **applies** to this endpoint; the path is identical for CIC vs OnBase.
- It is **not** LOB-aware — always `api/core` (Workday's `bow` has no `queries/execute`, so Workday keeps
  using the record-scoped path).

---

## 3. OnBase9714's account queries (from the live config)

The `account` business object maps to two OnBase content types, each with its own keyword query:

| Content type | id | Query | queryId | Filter field |
|---|---|---|---|---|
| **COM - Application** | 206 | Commercial Lending Search (default) | `110` | `232` = **Entity Name** |
| **Invoices** | 205 | Invoices | `109` | `228` = **Vendor Name** |

Both keyword fields are import-mapped from the account **Name** (`inputSource 1`). The queries use
`EqualsCaseInsensitive` (exact match), so `*`/`%` return an empty (but valid, HTTP 200) result.

**List verified working for OnBase** — proof the chain is alive vs. the old 501:
- value `*` → 400 *"ContentQueryService: Invalid data"* (reached OnBase's ContentQueryService)
- value `%` on query 110 and 109 → **HTTP 200, valid empty result**
- operator `Contains` → 400 *"operator 'Contains' is not supported"* (only `EqualsCaseInsensitive`
  configured)

Full chain: **`query_documents` → UCEB `queries/execute` → CFS token exchange → OnBase ContentQueryService
→ 200.**

---

## 4. OnBase upload — the `hfs_Name` blocker and the fix

First upload attempt (`upload_staged_file`, the CIC 3-step attach) got as far as:

```
InitiateUpload   → 202
CompleteUpload   → 201   (bytes staged in CIC)
Attach           → 400 code 23401
   "The following import fields are not configured in metadataFieldImportMappings: hfs_Name"
```

**Root cause:** the CIC attach path reads the content type's metadata
(`GET api/core/document-types/{type}/metadata`) to decide which attributes to stamp — but that call
**500s at the OnBase broker**, so it falls back to stamping `hfs_Name`. OnBase content types only accept
their **configured numeric import fields** (`230`, `232` for COM - Application), not `hfs_Name` → 400.

**Fix (`Tools/UcebTools.cs`):**
- Added an optional `additionalAttributesJson` parameter to `upload_staged_file` (threaded through
  `AttachContentAsync`; the other three upload tools default it to `null`, so they're unchanged).
- New helper `ApplyExplicitAttributes` parses `[{name|id, value, type?}]` straight into the attach
  request's `BusinessObjectAdditionalAttribute` list and **skips** the metadata-driven population (so no
  broker metadata call and no `hfs_Name`).

For OnBase you now pass e.g. `additionalAttributesJson = [{"name":"232","value":"Acme Corp"}]` to set the
**Entity Name** keyword that the list query filters on.

---

## 5. Full upload → list-that-doc round-trip (verified)

Against local UCEB `:5000` + MCP `:5200`, signed in staging (`hfs`), active system **OnBase9714**:

**Upload** a doc with Entity Name `232` = a unique marker:
```
POST .../business-objects/YASH-ENTITY-20260831-204127/documents?systemFriendlyName=OnBase9714 → 200
upload_staged_file completed. IsError = False        ← document landed in OnBase
```

**List that exact doc** via query `110` (Entity Name = marker) → `total = 1`:
```
docId: 266   (Document Type = COM - Application, Entity Name = YASH-ENTITY-20260831-204127, Loan Number = )
```

The query returned **only the one document just uploaded** — a clean, end-to-end OnBase upload+list demo.

---

## 6. Answer: do list and upload work now?

**Yes — for both backends:**

| | CIC / Salesforce | OnBase9714 |
|---|---|---|
| **List** | ✅ (`list_documents`) | ✅ new `query_documents` (`queries/execute`) |
| **Upload** | ✅ (`upload_staged_file`) | ✅ `upload_staged_file` + `additionalAttributesJson` |

---

## 7. Commits (fork `YashGupta404/Hyland.Experience.UCEB.Api`, branch `feature/uceb-mcp-server-poc`)

- `8b796c7` — Add `query_documents` MCP tool using the `queries/execute` endpoint (works for OnBase/CFS).
- `ca402a0` — Support explicit import-field attributes on `upload_staged_file` for OnBase/CFS (send Entity
  Name / keyword instead of `hfs_Name`).

---

## 8. Notes / gotchas

- The OnBase upload call is **slow (~12–23 s)**: CIC upload steps + broker round-trips. Read the **MCP
  server log** for the real result — buffered console output from a wrapper script can truncate before it
  flushes.
- These OnBase #1 queries are **keyword searches** (filter on the query's own field), not record-scoped by
  the boContext id — #2 was the record-scoped path.
- Still to escalate separately: the OnBase broker `500` on the per-type **metadata** endpoint (cosmetic
  for our upload now that we bypass it, but it blocks schema-driven attribute population).

---

## 9. Plugin end-to-end for OnBase — panel **and** chatbot

Wired the whole browser plugin to OnBase, keeping the code **generic** (no OnBase-specific hardcoding in
the BFF/extension):

- **BFF (`bff/Program.cs`) made generic:** `/api/context` calls `query_documents(type, id)` and falls back
  to `list_documents` only when no queries are configured; `/api/upload` calls plain `upload_staged_file`
  (the MCP tool auto-resolves the keyword). Removed the temporary `OnBase` options block from
  `bff/appsettings.json`. New endpoints **`GET /api/system-configs`** (list ECM systems) and
  **`POST /api/system-config`** (set the active one).
- **Extension onboarding:** after sign-in the panel shows a **system picker** (`#systemConfigView`) so the
  user chooses the ECM system (CIC / OnBase9714 / …) before documents load. Stored in `storage.session`
  (`auth.js`), fetched/applied via `agent.js` (`fetchSystemConfigs` / `setSystemConfig`).

**Three real bugs found via the actual plugin:**
1. **Empty doc-type dropdown** — BFF `ParseDocumentTypes` tried the numeric `DocumentTypeId` first and
   rejected names with spaces; fixed to prefer `DocumentTypeName` and allow spaces.
2. **Wrong keyword per content type** — first cut hardcoded one field for all types; replaced with
   config-driven `ResolveImportKeywordFieldAsync` (reads the type's `metadataFieldImportMappings` entry
   with `inputSource == 1`) plus a **multi-query merge** in `query_documents` (loops every configured
   query for the busObject and de-dupes) so either content type lists.
3. **The big one — panel always empty though docs existed:** OnBase's `ContentQueryService` **rejects
   `maxResults=100` with HTTP 400** "Invalid data". The BFF omitted `maxResults`, so `query_documents`
   used its default of 100 → every list 400'd → shown as empty. Fixed the tool default to **25**.

**Performance:** the OnBase solution-config `GET` is slow (~10–20 s) and `query_documents` reads it on
every call, which pushed one agent invoke to ~43 s (near the platform's ~100 s tool-call cap). Added a
**10-minute solution-config cache** in `LobRoutingContext` (first call warms it; the rest are instant;
any config write invalidates it).

---

## 10. "Name + file type" in the document list (like CIC)

CIC lists show a readable **name + file-type icon** (from `hfs_Name`). OnBase docs were listed only by the
keyword (the account id), because the OnBase **Invoices** type has **no name keyword** (only 226 Invoice
Date, 227 Invoice Total, 228 Vendor Name = the scoping keyword, 229 Invoice #), its metadata endpoint 500s,
and the `DocumentTypeName` system column comes back **empty** for the keyword query (109).

Fix — **store the filename on upload + show it as a column** (both config-driven):

- **New MCP tool `set_query_display_columns(businessObjectType, queryId, columnsJson)`** —
  `UcebApiClient.SetQueryDisplayColumnsAsync` does a GET → find `queryConfig[busObject].queries[id]` → set
  its `displayColumns` + `displayColumnConfig` → POST (same write pattern as `set_viewer_url`), then
  invalidates the config cache. Adding a **new** tool doesn't drift the existing agent tools.
- **Filename stamping on upload** — new option `Uceb:NameKeywordFieldByContentType` (`{ "Invoices": "229" }`).
  `AttachContentAsync`'s OnBase auto-keyword block now stamps **both** the scoping keyword (228 = record id)
  **and** the name keyword (229 = the filename). Config-driven, so it works for **both** the panel (BFF
  passes the original name) and the chatbot.
- **Relabeled query 109** so `Invoice #` (229) displays as **"Document"** and appears first → the panel
  picks it as the doc name and derives the file-type icon from the extension.

**Verified:** uploaded `Demo-Invoice.txt` → log `auto-stamping import keyword 228=… + name 229=Demo-Invoice.txt`
→ `query_documents` returned `docId 271 (Document=DEMO-INVOICE.TXT, …)`. (OnBase **uppercases** stored
values; older docs uploaded before the change show a blank Document column.)

---

## 11. Viewer generalization — config-first (one setting per system, from the system itself)

The chatbot's "Open in Hyland Viewer" link 404'd for OnBase (`CONTENT_BROWSER.DOCUMENT.LOAD_ERROR.DEFAULT`).
Root cause: `GetViewerUrlAsync` **preferred the appsettings Salesforce Studio URL for every system**, so an
OnBase doc opened in the wrong (Salesforce) content browser.

Each system config already carries its **own** viewer at `operationConfig.viewer.baseUrl`:
- CIC/Salesforce → Studio content browser
- OnBase9714 → **QuickAccessViewer** `https://oifsdev.hyland.com/QuickAccessViewer/viewer/document/{doc_id}`

**Generalization #1 (implemented):** flipped `GetViewerUrlAsync` to **config-first** — it reads the *active*
system's own `viewer.baseUrl` and only falls back to the appsettings URL when the config has none. Now every
system opens in its own viewer automatically, with no per-system hardcoding. Verified:
`open_document_in_viewer(270)` → `https://oifsdev.hyland.com/QuickAccessViewer/viewer/document/270`.

**Why it looked broken at first:** off-VPN the QAV host gave `ERR_QUIC_PROTOCOL_ERROR` (blank page) — a
**network-reachability** issue: `oifsdev.hyland.com` is an internal OnBase dev host. **On the Hyland VPN it
renders the invoice** (with the OnBase Keywords panel). So the URL was always correct; only the host was
unreachable off-network.

**Universal alternative (Generalization #2, already in place):** the **in-panel byte preview**
(`/documents/{id}/content` → `api/core/download/{id}` for OnBase/CFS) returns real bytes — proven: doc 271
downloaded as a **23 KB `application/pdf`**, the same byte path the panel's PDF.js viewer uses for CIC and
Workday. That renders OnBase docs inside the panel with no external viewer and no VPN.

---

## 12. Result & commits

Everything works end-to-end for OnBase from the plugin — **list**, **upload** (with a readable name + type),
and **view** (QAV over VPN, or in-panel bytes anywhere) — through both the **panel** and the **chatbot**.

- MCP server (fork `YashGupta404/Hyland.Experience.UCEB.Api`, branch `feature/uceb-mcp-server-poc`):
  `4a88c91` — config-driven list/upload, `set_query_display_columns` tool, name-keyword stamping,
  solution-config cache, and config-first viewer resolution.
- BFF + extension (fork `YashGupta404/MCP_Server_Agent`, branch `main`): generic OnBase path, system-config
  endpoints + onboarding picker, doc-type/`maxResults` fixes.
