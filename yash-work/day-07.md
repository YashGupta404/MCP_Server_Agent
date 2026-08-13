# Day 7

_Focus: make the agent tolerant of loose prompts, fix Outlook record‑id length + email upload
config, then **pivot the content‑in‑context demo from Outlook to a real Salesforce instance** —
create a record, capture its documents into Hyland CIC via the plugin, and auto‑fetch those
documents when the record is opened._

---

## 1. Agent stopped understanding loose prompts / HTTP 500 on some questions

**Symptom:** prompts like `list documents of TEST-ACCT-001` worked, but
`what is the account type / businessObjectType of TEST-ACCT-001` returned
`Error: Agent call failed (500): HTTP 500`. The agent seemed to need very exact wording.

**Root cause:** the BFF just passes through whatever the Agent Builder platform returns. Those two
questions are **unanswerable by the toolset** — there is no tool that derives a record's *type* from
its *id* (the type is an INPUT, not a stored value). The agent tried anyway, called a tool with a
bad/guessed argument → the tool threw → the platform returned **HTTP 500**. This is an **agent‑side
(Studio) config** issue, not a plugin/BFF bug.

**Fix:** rewrote the agent **system prompt in Agent Builder Studio** — it now (a) lists each tool and
when to use it, (b) resolves "this record" from the plugin's injected context note, (c) asks for a
missing `businessObjectType` instead of guessing, and (d) **answers in words** ("I can't look up a
record's type from its id…") instead of calling a tool for impossible questions. Verified: the two
questions now return a graceful explanation, no 500. `list documents` still works.

---

## 2. Long record ids (Outlook) broke uploads → deterministic short id

**Symptom:** Outlook email uploads failed because the message id in the URL (`/id/<id>` or `ItemID`)
is 150+ chars. HFS/CIC stores a record's documents under a **folder named after the
`businessObjectId`**, which has a length limit → the folder can't be created → upload/list fails.

**Fix (`browser-extension/src/content/detector.js`):** `detect()` now caps any id longer than
`MAX_ID_LEN` (64) and replaces it with `` `${type}-${hashId(rawId)}` `` (e.g. `email-3k9x2ab7q1z4m`).
`hashId` = two FNV‑1a passes (forward + reversed) → ~13 base36 chars, **deterministic** so the same
record always maps to the same id and previously‑uploaded docs are still found. Applies generally
(protects Workday too); short ids (Salesforce 18‑char, ServiceNow 32‑char) are unaffected.

---

## 3. Email upload blocked by its content‑type mapping → made config an upsert

**Symptom:** uploading to an email record failed with
`hfs_Name does not exist` + `hfs_Multivalue_Field is required`.

**Root cause:** `email` was mapped to `multivalue-content-type`, which **rejects** `hfs_Name` and
**requires** `hfs_Multivalue_Field` — a required multi‑value field the `upload_staged_file` tool has
no way to set. UCEB also **validates that the upload's content type is the one configured for that
business object**, so passing a clean type (`prescription`) for an email record was rejected
("prescription not configured for email"). The real fix is to **re‑map `email`** — but
`AddBusinessObjectConfigAsync` was a **no‑op** when the business object already existed.

**Fix (`Hyland.Experience.UCEB.McpServer/Clients/UcebApiClient.cs`):** turned
`AddBusinessObjectConfigAsync` into an **UPSERT** — if an entry for the busObject exists it removes it
and appends the new mapping (returns "Updated" vs "Added"). Build succeeded, 0 errors. Requires MCP
rebuild + restart. To re‑map email:
`add_business_object_config(busObject=email, ecmContentTypeName=prescription, displayColumn=hfs_Name)`.

---

## 4. PIVOT: content‑in‑context demo on a real Salesforce instance

Dropping Outlook for the demo. Using a Salesforce org. Key insight: the **Account** object already
maps cleanly in UCEB (`account → prescription`), and Salesforce record ids are 18 chars (no hashing),
so **Account records work end‑to‑end with no config changes**.

### How the plugin reads a Salesforce record
`detector.js` `detectSalesforce()` matches the Lightning URL
`.../lightning/r/<Object>/<RecordId>/view` → `businessObjectType = <Object>` (normalized lower‑case
via `SITE_TYPE_MAP`; `account`, `contact`, `opportunity`, `case`, `lead`, `campaign` supported),
`businessObjectId = <18‑char RecordId>`. It publishes this to the background worker; the side panel
then calls `list_documents(type, id)` to show the record's Hyland docs.

### Runbook — create → capture → auto‑fetch
1. **Create the record:** App Launcher → **Accounts** → **New** → set **Account Name** (e.g.
   `Acme Content Demo`) → **Save**.
2. **Get the record id:** read it from the URL —
   `https://<mydomain>.lightning.force.com/lightning/r/Account/**001XXXXXXXXXXXXXXX**/view`.
   The bold `001…` segment is the 18‑char `businessObjectId`.
3. **Store a document via the plugin:** open the side panel on that Account (it auto‑detects
   `type=account`, `id=001…`) → **Attach** a file → send
   `Upload the attached file to this record using document type prescription`. The agent stages the
   bytes and calls `upload_staged_file` → the doc is filed under
   `{appKey}-documents/001…/` in HFS.
4. **Auto‑fetch (content‑in‑context):** re‑open (or refresh) that Account → the plugin detects the
   record and the panel auto‑lists the documents stored under that record id → click a row to open in
   the Hyland viewer.

### Prereqs
- Stack up: UCEB :5000 → MCP :5200 (`--http`) → `devtunnel host -p 5200` → update Studio MCP Server
  URL → BFF :5010. Sign in from the extension.

---

## 6. Human‑readable document **names** (upload + list) — mentor feedback

**Problem:** documents were being uploaded and listed by their opaque `docId`. Users can't remember
`docId`s, so they can't tell what a document is. Mentor's guidance: **give the document a name on
upload, and show that name when listing** — via the content type's **display columns**.

**How naming actually works in UCEB**
- A document's readable name is stored in the **`hfs_Name`** attribute of its content (document) type.
- `list_documents` only returns the columns that are configured as the business object's
  **display columns** (`ecmMetadataFieldLabels` / `displayColumns` in the solution config). So a name
  shows up in the list **only if** (a) `hfs_Name` was set on upload **and** (b) `hfs_Name` is the
  business object's display column.
- **Not every content type has `hfs_Name`.** I probed each type's metadata
  (`get_document_type_metadata`) and found:

  | Content type | Name field | Required fields | Good for named uploads? |
  |---|---|---|---|
  | **`dev-test-account`** | **`hfs_Name`** (optional) | none | ✅ ideal |
  | `case-content-type` | `hfs_DocName` (required) | 6 required | ❌ too many required fields |
  | `bills-content-type` | `hfs_billing-doc-name` | 1 required | ~ different column |
  | `prescription` | none | none | ❌ no name field (upload was rejected when named) |
  | `multivalue-content-type` | `hfs_normal-field` | `hfs_Multivalue_Field` required | ❌ |

  → the Salesforce **Account** demo now files uploads under **`dev-test-account`** (has an optional
  `hfs_Name`, no required fields), instead of `prescription`.

**Changes made**
1. **MCP — always name uploads, safely** (`Tools/UcebTools.cs`):
   - `upload_staged_file` (plugin path) and `upload_document` now **default the document name to the
     original file name** when the caller doesn't pass one (before, the staged path passed it through
     as `null`, so docs had no name).
   - `AttachContentAsync` now **guards** the `hfs_Name` stamp: it calls a new helper
     `ContentTypeHasAttributeAsync(ecmContentTypeName, "hfs_Name")` (reads
     `get_document_type_metadata`) and only stamps the name **when the type actually has `hfs_Name`**.
     This makes "always pass a name" safe for every type — types without `hfs_Name` (e.g.
     `prescription`, `multivalue-content-type`) silently ignore it instead of rejecting the upload.
2. **Config — make the name visible in the list** (runtime, not code): re‑mapped the `account`
   business object with the now‑upsert `add_business_object_config`:
   `add_business_object_config(busObject=account, ecmContentTypeName=dev-test-account,
   displayColumn=hfs_Name)` → uploads file under `dev-test-account`, and `list_documents` returns
   `hfs_Name` as a column.
3. **BFF — pick the name, not a random column** (`bff/Program.cs`, `ParseDocumentList`): now prefers
   the **`hfs_Name`** attribute as the card title (case‑insensitive), falls back to the first
   non‑empty attribute, then the `docId`; and **removes** the name attribute from the set so the
   card's sub‑line doesn't just repeat the title.
4. **Plugin — already ready** (`popup.js`, `renderDocuments`): shows `doc.name || doc.docId`, so once
   the BFF returns a real name it renders automatically; the sub‑line shows remaining attrs or
   `docId …`.

**Net effect:** attach a file → it's uploaded to `dev-test-account` with `hfs_Name = <file name>` →
the panel (and the agent's `list_documents`) shows the file's **name** instead of the `docId`. Opening
a row still uses the `docId` under the hood.

**Verify:** on the Test Account record, Attach a file → send
`Upload the attached file to this record using document type dev-test-account` → refresh the panel →
the document row shows the file name.
- Extension manifest/content‑scripts must match the org's host
  (`*.lightning.force.com` / `*.my.salesforce.com`). If detection shows "No record" on Salesforce,
  add the exact host to `manifest.json` matches + `detect()` host regex, then reload the extension.
- `account → prescription` is already configured (no re‑map needed). To use a different sObject
  (contact/opportunity/case/lead), first ensure it's configured with a clean content type via
  `add_business_object_config` (now an upsert).

---

## 5. Panel action buttons + collapsible panel (UX)

**What the panel buttons do** (they just pre‑fill the chat and submit to the agent):
- **Attach** — opens the file picker / accepts a drag‑drop; the file is staged and uploaded to the
  current record on the next send.
- **History** — sends "Show the version history of the documents on this record." → agent lists each
  document's revisions/versions.
- **Extract** — sends "Extract the key data from the documents on this record and summarize it." →
  agent reads the record's documents and returns a summary of their key data.

**Collapsible context panel:** the chat area felt cramped because the "Hyland Enterprise Content"
panel takes the top ~62%. Added a **chevron toggle** (`#contextToggle`) in the panel bar next to the
gear. Clicking it adds `hec--collapsed` to `#contextPanel`, which hides everything except the bar
(`.hec--collapsed > :not(.hec__bar){display:none}`) so the chat expands to full height; the chevron
rotates to indicate expand. Files: `popup.html` (button), `popup.css` (collapse rules + chevron),
`popup.js` (`contextToggle` element + click handler toggling the class / aria / title).
