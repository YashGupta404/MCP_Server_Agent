# Day 8

_Focus: build a **self‑contained manual upload section** in the context panel, then chase down a
**four‑layer upload bug** until a file actually attaches, fix the **chatbot list/view 400**, confirm
the **config tools**, and write a full **chatbot test script**._

---

## 1. Self‑contained manual upload section in the panel

Added a dedicated **"Upload a document to this record"** section to the context panel — completely
separate from the chat composer's "+" attach (which still feeds the chat/LLM only). It behaves like a
mini form, mirroring the deterministic "Documents" list:

- **Drag‑and‑drop + Attach** → its own hidden `#uploadFileInput` → `pendingUploadFiles` (kept separate
  from the chat's `pendingFiles`).
- **Document type** = a **dropdown** (`#uploadDocType`, was originally free text). Populated by
  `populateDocTypes()`: seeds a fallback list then merges the live list from BFF `GET /api/doctypes`
  (→ `list_document_types` → tolerant `ParseDocumentTypes`).
- **Record id** (`#uploadRecordId`) = **auto‑filled** from the detected context
  (`currentContext.businessObjectId`) in `loadContextPanel()`.
- **Upload** button → `uploadDocuments()` → BFF `POST /api/upload`.

**Data flow (no LLM):** bytes go **BFF → MCP `/staging/upload`** (returns a `stagingId`) → BFF calls
`upload_staged_file` by `stagingId`. The file bytes never pass through the agent/prompt.

Also this session: replaced the header "H" mark + extension icon with the **Hyland logo**
(`src/assets/hyland-logo.png`, `manifest.json` icons).

---

## 2. The upload bug — four layers, peeled one at a time

The upload "worked" (said success) but nothing appeared in the list. Fixing that surfaced the next
problem, and so on — four distinct bugs stacked on top of each other.

### Layer 1 — false success (BFF ignored the tool result)

**Symptom:** clicking Upload showed "Uploaded: <file>" **instantly**, but the file never appeared.

**Root cause (`bff/Program.cs`):** `/api/upload` counted an upload as successful whenever the MCP
`tools/call` **didn't throw**. But `upload_staged_file` can *return* a failure (either `isError=true`,
or `isError=false` with an error message in its text) while the JSON‑RPC call itself succeeds.

**Fix:** added `McpJsonRpc.CallToolWithStatusAsync` returning `(string Text, bool IsError)` (reads
`result.isError`); `CallToolAsync` now wraps it. `/api/upload` counts success only when **not
`isError`** and the text passes a failure check, and pushes the tool's **real error text** into
`errors[]` (surfaced in `#uploadStatus`, red). 502 when nothing uploaded.

### Layer 2 — wrong tool argument names

With real errors now visible, the tool threw:
`missing value for required parameter 'businessObjectId'`.

**Root cause:** the BFF sent `boId` / `boType`; the MCP tool `UploadStagedFileAsync` actually takes
`businessObjectId` / `businessObjectType` (the old note listing `boId`/`boType` was wrong).

**Fix (`bff/Program.cs`):** send `businessObjectId` + `businessObjectType` in the `upload_staged_file`
arguments.

### Layer 3 — `.jpeg` extension rejected by the content platform

Next real error:
`/api/core/documents/upload/{uploadId}/complete → 400 {"title":"File type is not supported."}`.

**Root cause:** the file was a valid JPEG (`FF D8 FF E0`), and UCEB's own validator allows `jpeg` —
but the **downstream content platform validates by the filename EXTENSION string** and accepts `jpg`
but **not** `jpeg` (both are `image/jpeg`). Proof: the existing `invoice-1 2.jpg` uploaded fine; the
identical‑format `.jpeg` did not.

**Fix (`bff/Program.cs`):** `NormalizeUploadExtension()` maps `.jpeg → .jpg` (and `.tif → .tiff`)
before staging; the normalized name is used for both the staged file name and the document name. Bytes
are unchanged.

### Layer 4 — false **failure** (heuristic matched a GUID)

The upload then **actually succeeded** (`documentId: a40ec82d‑c77f‑4b67‑9897‑1401688085e3`), but the
BFF returned 502 anyway.

**Root cause:** the failure heuristic scanned for bare HTTP codes like `"401"`, which matched the
substring **`1401`** inside the documentId GUID.

**Fix (`bff/Program.cs`):** removed bare numeric codes (and over‑broad `error`/`invalid`) from the
failure phrases; kept whole‑phrase markers (`failed`, `not supported`, `badrequest`, `denied`, …); and
added a **positive success signal** — text containing `documentId` or `attached it to` (with no failure
phrase) = success.

**Result:** uploads now genuinely attach to the record and report accurate success/failure. (Note: one
of the "failed" attempts during debugging had *actually* created `documentId a40ec82d` — the upload had
worked; only the verdict was wrong.)

---

## 3. Chatbot **list / view** returned 400 — MCP null‑bool crash

**Symptom:** asking the chatbot to `list all documents` or `view <doc>` returned
`400 … Not successful when sending request to .../invoke`, while chat **upload** and **health** worked.

**Root cause (MCP logs):** `list_documents` threw
`System.Text.Json.JsonException: Cannot get the value of a token type 'Null' as a boolean`. The LLM
emits `{"onlyMine": null}`, and the tool parameter was a **non‑nullable** `bool onlyMine = false` — a
present‑but‑`null` key can't deserialize into `bool` (the default is only used when the key is
**absent**). The framework marshaller throws **before** the method body → tool error → agent runtime
returns 400. **View** failed for the same reason (the agent lists documents first to resolve the id).

**Fix (`Hyland.Experience.UCEB.McpServer/Tools/UcebTools.cs`):** changed `list_documents.onlyMine` and
`list_document_types.fetchMetadata` to nullable `bool?` with `?? false`. Rebuilt (0 errors) + restarted
MCP (warm‑up sign‑in re‑done). The deterministic **panel** list/view were never affected (BFF
`/api/context` omits `onlyMine`, so no null).

**Lesson:** every MCP tool `bool`/number/enum parameter the LLM might send as `null` **must be
nullable** — LLMs frequently pass explicit `null` for optional params.

---

## 4. Config tools — do they work, and can they be changed?

Reviewed every tool's signature. **Read** config tools (`list_business_object_types`,
`get_solution_configurations`, `get_content_platform`, `get_platform_capabilities`,
`list_document_types`, `get_document_type_metadata`, `list_document_type_groups`,
`evaluate_feature_flag`) are all safe — they're parameterless or take only strings (which tolerate
`null`); `list_document_types` was fixed alongside `list_documents`.

**Write** config tools (both real writes to dev config):
- `add_business_object_config(busObject, ecmContentTypeName, displayColumn)` — maps a business object
  type so it can be uploaded to / listed. **Upsert** (re‑maps if it already exists). Requires
  `CanWriteUCEBConfiguration`.
- `set_viewer_url(viewerBaseUrl)` — sets the viewer URL template (must contain `{doc_id}`). Rarely
  needed — the dev viewer URL is baked into MCP `appsettings`.

---

## 5. Why `opportunity-content-type` upload failed (23400)

Testing the panel with doc type `opportunity-content-type` on record `701gK00001Ka30GQAR` failed. The
MCP logs showed staging OK, `/complete` **201** (bytes uploaded), then the **attach**
(`POST business-objects/{id}/documents`) → **400 code 23400**
`EcmContentTypeName does not match any configured document type`.

**Two reasons it can't work here:**
1. **Mapping mismatch.** UCEB requires the uploaded `ecmContentTypeName` to **equal** the content type
   configured for that record's `businessObjectType`. `opportunity-content-type` isn't the mapped type
   → error 23400.
2. **Required fields.** Even if it were mapped, `opportunity-content-type`'s metadata has three
   **required** fields the upload tool can't set: `hfs_OpportunityAliasReqMultivalued` (required,
   multi‑value), `hfs_OpportunityDate` and `hfs_OpportunityDatetime` (required DateTimes) → it would be
   rejected with "…is required".

**Takeaway (dev):** an upload only succeeds when the chosen document type (a) is the one mapped to that
record's business object type **and** (b) has no required fields the tool can't fill. Right now
**`dev-test-account`** (has optional `hfs_Name`, no required fields, mapped to `account`) is the only
type that satisfies both. This is a **data/config restriction, not a code bug** — the panel now
surfaces the exact UCEB reason.

---

## 6. Chatbot test script

Wrote `tests.txt` (workspace root) — a top‑to‑bottom `Prompt:` / `Expect:` script covering: health /
build / auth, list documents (incl. the fixed `onlyMine` case), view/open, chat‑driven upload, all
config **read** tools, config **write** tools (with an end‑to‑end `add_business_object_config` verify),
and negative/edge checks that should fail cleanly. Includes prerequisites (4 processes, sign‑in), the
test record, the supported file‑type list, and a troubleshooting section.

---

## Outcome

- Manual upload section (drag‑drop + doc‑type dropdown + auto record id) is live and **actually
  attaches** files to a record.
- The upload path reports **accurate** success/failure with the real UCEB message.
- Chatbot **list** and **view** work again (MCP null‑bool fix).
- Config read + write tools verified; documented which document types are actually uploadable and why.
- `tests.txt` lets anyone smoke‑test the whole chatbot surface.

**Files touched:** `bff/Program.cs` (status‑aware MCP call, correct tool args, `.jpeg→.jpg`
normalization, phrase‑based success/failure heuristic, `/api/upload`, `/api/doctypes`);
`Hyland.Experience.UCEB.McpServer/Tools/UcebTools.cs` (`onlyMine` + `fetchMetadata` → `bool?`);
`browser-extension/src/{popup.html,popup.js,popup.css,agent.js,manifest.json}` (upload section, doc‑type
dropdown, Hyland logo); `tests.txt` (new).
