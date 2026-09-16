# Day 23 — 2026-09-15

_Focus: manager asked me to gather **feature feedback from the two mentors** (Workday + Salesforce) for the
Integrations Agent plugin. This day records the **Workday mentor's** feedback and my assessment of which
items are implementable **in the plugin**._

---

## 0. Context

Manager: "Ask your Workday mentor and your Salesforce mentor what more features to include in the plugin."
This note captures the **Workday mentor's** feedback + a feasibility pass (what's implementable in the
extension / BFF / MCP, and how much work each is).

---

## 1. Workday mentor feedback (verbatim)

1. Show document list with other metadata as well apart from file name, like — doc type and other
   configured fields.
2. A search filter is preferred to search documents of an employee based on metadata values.
3. Ability to add other metadata values apart from document type during document upload.
4. Ability to fetch documents using **employee id** also instead of employee object id — as a user when I
   open the Integrations Agent for a worker profile, I should see all the documents whose either **Workday
   ID or Worker ID** matches document's **employee object ID or employee ID**.
5. Most configs are now not relevant, so we can think of **updating the config schema** and show relevant
   configs here. (viewer base url, dynamic columns, etc. are no longer relevant.)

---

## 2. Feasibility assessment (can it go in the plugin?)

### #1 — Show doc type + other configured metadata in the list ✅ YES (easy, plugin)
- The list data already carries the fields. `list_documents`/`query_documents` return each doc's
  `simpleDocumentAttributes` (Document Type, Document Name, Document Date, …) / column values.
- Today `renderDocuments` (popup.js) shows name + a type/sub-line (day-19). Extend it to render the
  **configured display columns** (from the query's `displayColumns` in the solution config) or a chosen set
  of attributes.
- **Where:** mostly extension (`renderDocuments`); optionally surface the query's `displayColumns`.
- **Effort:** low.

### #2 — Search / filter documents by metadata values ✅ YES (plugin)
- **Client-side (quick):** a search box that filters the already-loaded list by any attribute value —
  instant, no backend change.
- **Server-side (thorough):** `query_documents` already supports `filterFieldId` + `filterValue`, so we can
  search **all** of an employee's docs by a metadata field, not just the loaded page.
- **Where:** extension (search box) + BFF/MCP already support the filter args.
- **Effort:** low (client-side) / medium (server-side field-scoped search).

### #3 — Add other metadata values during upload ✅ YES (plugin + existing MCP)
- The capture path already accepts explicit attributes (`additionalAttributesJson` →
  `businessObjectAdditionalAttribute`). `get_capture_default_attributes` returns the doc type's fields.
- Add a **dynamic attribute form**: when a document type is picked, fetch its fields and render inputs; pass
  the filled values into capture.
- **Where:** extension (dynamic form) + BFF/MCP capture (already supports explicit attributes).
- **Effort:** medium (UI + validation).

### #4 — Fetch documents by employee id too (WID OR Employee ID) ✅ YES (medium, plugin + MCP)
- Today the list is scoped to the **WID** (Employee Object ID) as `businessObjectId`. The docs also carry an
  **"Employee ID"** attribute (id 162) alongside **"Employee Object ID"** (id 161).
- To also match Employee ID: resolve the worker's **Employee ID** (from the profile / Staffing / `/api/me`),
  run a second `query_documents` filtered on the **Employee ID field** = that id, and **merge + dedupe** with
  the WID-based results.
- **Where:** MCP/BFF (extra query + merge) + extension (unchanged display).
- **Effort:** medium (resolve Employee ID + second query + dedupe).

### #5 — Trim the config schema / show only relevant configs ⚠️ PARTIAL / SEPARATE
- This is about the **config-management** capability (which solution-config fields matter), **not** the
  document-viewing plugin. `viewerBaseUrl`, `dynamicColumns`, renditions etc. are less relevant now that the
  CIC viewer + envKey are resolved server-side (day-22).
- The plugin currently has **no config-editing UI** — config is driven via the API / agent tools
  (`get_solution_configurations`, `set_viewer_url`, `set_query_columns`, `add_business_object_config`).
- Doable, but it's a **config-tooling redesign** (define a trimmed schema of what an admin actually needs to
  set), not a document-list feature. Needs its own design + `CanWriteUCEBConfiguration`.
- **Effort:** larger / separate track.

---

## 3. Summary — what goes in the plugin (Workday)

| # | Feature | In plugin? | Effort |
|---|---|---|---|
| 1 | Doc type + configured metadata in list | ✅ yes | low |
| 2 | Search/filter by metadata | ✅ yes | low–med |
| 3 | Add metadata values on upload | ✅ yes | med |
| 4 | Fetch by Employee ID (WID **or** Employee ID) | ✅ yes | med |
| 5 | Trim config schema / relevant configs | ⚠️ separate (config-tooling, not doc plugin) | larger |

**1–4 are all implementable in the plugin.** #5 is a separate config-management track (not a document
feature) and needs its own design.

---

## 4. Implemented this day — #1 (metadata in list) + #2 (search filter)

Both are extension-only (no BFF/MCP change — the data is already returned per doc).

- **#1 — richer list metadata** (`popup.js` `renderDocRows`): each document row now renders the **document
  type + every configured metadata field** as labeled chips (`Type: …`, `Field: value`), instead of the old
  value-only sub-line. Fields shown are whatever columns the active query returns (so surfacing *more* fields
  is a query `displayColumns` / solution-config concern).
- **#2 — search filter** (`popup.html` `#docSearch` + `popup.js` `filterDocs`): a search box above the list
  filters the loaded documents by **name / type / docId / any metadata value** (case-insensitive, live,
  client-side). The tab shows `Documents (shown/total)` while filtering.
- Files: `browser-extension/src/popup.js`, `popup.html`, `popup.css`.
- Reload the extension to test (no server rebuild).

Next candidates: #3 (metadata-on-upload form) and #4 (fetch by Employee ID) — both need MCP/BFF wiring.
