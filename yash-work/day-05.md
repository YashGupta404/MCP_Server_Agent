# Day 5

_Focus: **correct** the Day‑4 conclusion about HFS folders, nail down **exactly** how a folder named
after a `businessObjectId` gets created inside **HFS documents**, and write the concrete recipe for
**creating those folders + uploading docs into them in dev‑test** using the MCP server._

---

## 1. What triggered this (and the correction)

In staging I looked at **HFS documents** and saw many **sub‑folders, each named after a
`businessObjectId`**, with the uploaded documents living **inside** those folders. In dev‑test the
**HFS documents** container is **empty** (no sub‑folders yet). I wanted to reproduce the staging layout
in dev.

> **Day‑4 §8.8 was WRONG** (folder = per business‑object **TYPE**, `businessObjectId` = just an
> attribute). That conclusion came from reading the **Workday** capture path
> (`MultipartFormCaptureService` + `WorkdayBusinessObjectTypeUtils.GetContentFolderName`). Our stack is
> **CIC/Hx**, and the CIC path is a **different service**. The corrected model is below.

---

## 2. The real CIC path — folder **is** per `businessObjectId`, auto‑created on upload

The attach step (`POST /api/core/business-objects/{boId}/documents`, MCP `AttachContentAsync`) is
handled for **CIC/Hx** by **`DocumentArchiveService.ArchiveDocument`**
(`Infra.Hx/Content/Services/Archive/DocumentArchiveService.cs`). The decisive lines:

```csharp
// 4. Setup folder path for document storage
var folderNameResult = _config.GetFolderName(accessToken);      // base/root folder
var folderName       = folderNameResult.Data;                   // e.g. "hfs-documents"
var folderPath       = $"{folderName}/{documentImportParameters.BusinessContext.BoContextId}";
var result           = await _folderService.EnsureFolderPathExists(
                           apiBaseUrl, accessToken, folderPath, Constants.CONTENT_REPO_ID);
// 5. Create the document *inside* that folder
var requestUri = $"{apiBaseUrl}{Constants.DOCUMENTS_BY_PATH_ENDPOINT}/{folderPath}";
```

And the base folder name (`ContentConfigService.GetFolderName`):

```csharp
public Result<string> GetFolderName(string accessToken)
{
    var lob = _authService.GetAppKey(accessToken);              // LOB / app key from the token
    if (string.IsNullOrEmpty(lob)) return /* MissingLobError */;
    return new Result<string> { Data = $"{lob}-documents" };    // -> "{appKey}-documents"
}
```

So the physical layout in HFS/CIC is:

```
{appKey}-documents/                  <-- the "HFS documents" root you see (appKey = LOB, e.g. "hfs")
    {businessObjectId-A}/            <-- one sub-folder PER businessObjectId
        doc1, doc2, ...
    {businessObjectId-B}/
        doc3, ...
```

**Key facts:**

- The folder path = **`{appKey}-documents / {businessObjectId}`**. The per‑id sub‑folder is **real** and
  is named **exactly** after the `businessObjectId` (`BoContextId`). ✅ matches my staging observation.
- `EnsureFolderPathExists` **walks the path and creates each missing segment** (`FolderService`
  `CreateFolder` → `POST HxFolder{PrimaryType=folder, ACL}`). So the `{businessObjectId}` sub‑folder is
  **auto‑created on the first upload** to that id. No separate "create folder" call is needed.
- The document is then created **inside** that folder via
  `POST {hx}/api/documents/path/{appKey}-documents/{businessObjectId}` (run through the
  `JobOrchestrator` for idempotency + sync‑wait), returning the `documentId`.
- `businessObjectId` is therefore **both** a folder name (the container in HFS) **and** a metadata tag on
  the doc — not "just an attribute" as Day‑4 said.

**Why dev‑test looks empty:** the folders are created **lazily on upload**. Staging has folders because
docs were uploaded there; dev‑test has none simply because **nothing has been uploaded yet**. The moment
you upload against a `businessObjectId`, its folder appears.

---

## 3. How to reproduce the staging layout in **dev‑test** (recipe)

There is **no separate "create folder" MCP tool, and you don't need one** — the upload tools create the
folder for you. To get `hfs-documents/{businessObjectId}/<your docs>` in dev, just **upload** with a
chosen `businessObjectId`.

### 3.1 One‑time prerequisites (dev config must allow the upload)

1. **Business‑object type must be configured** in the UCEB solution config.
   - Check: MCP `list_business_object_types`.
   - If empty/missing: MCP `add_business_object_config(busObject, ecmContentTypeName, displayColumn)`
     to add the mapping (busType → content type + display column). This is the **config** entry, *not* a
     record.
2. **A valid document/content type (`ecmContentTypeName`) must exist in dev** and have **field
   configurations** — `DocumentArchiveService` calls `GetDocumentTypeFieldConfigurations` first and
   **no‑ops if that returns empty**.
   - Check: MCP `list_document_types` / `get_document_type_metadata`.
3. **Token must carry an app key (LOB)** so `GetFolderName` resolves to `{appKey}-documents`; otherwise
   you get `MissingLobError`. This is set by the login scopes/config (already the case for our dev login).

### 3.2 The upload itself (this is what creates the folder)

Call one of the MCP upload tools with your chosen id:

- `upload_document` — from a file path.
- `upload_document_from_content` — from base64/inline bytes.
- `upload_pasted_image` — from a pasted image.

Required inputs (all resolve into `DocumentImportParameters` / `BusinessContext.BoContextId`):

- `businessObjectId`  → **becomes the folder name** under `hfs-documents/`.
- `businessObjectType` → must match a configured type (see 3.1.1).
- `ecmContentTypeName` → the content type of the doc (see 3.1.2).
- the file/content + file name.

Internally each call does the 3 steps: **InitiateUpload → CompleteUpload → AttachDocument**; the attach
(`AttachContentAsync`) is what runs `DocumentArchiveService` → `EnsureFolderPathExists` →
`hfs-documents/{businessObjectId}` → create doc inside.

### 3.3 Verify

- MCP `list_documents(businessObjectId, businessObjectType)` →
  `GET /api/core/business-objects/{boId}/documents?businessObjectType=...` should list what you just
  uploaded.
- In HFS you'll now see `hfs-documents/{businessObjectId}/` with the doc(s) inside.

---

## 4. About "no real Salesforce record" in dev‑test

- Dev‑test (like staging‑test) has **no real Salesforce `businessObjectId`s**. That's fine — the
  `businessObjectId` is just a **string** used as the folder name + doc tag. UCEB does **not** validate
  it against Salesforce on this path.
- So pick **arbitrary but consistent** ids (e.g. `TEST-ACCT-001`, or a fake 18‑char SFDC‑looking id).
  Reuse the same id to pile multiple docs into the same folder; use a new id to get a new folder.
- This is exactly what was done in staging‑test — the folders there are named after **fake/test** ids,
  not production Salesforce records.

---

## 5. Corrected summary vs Day‑4

| Question | Day‑4 (wrong) | Day‑5 (correct, CIC/Hx path) |
|---|---|---|
| Folder granularity | per **type** | per **`businessObjectId`** (`{appKey}-documents/{boId}`) |
| Is `businessObjectId` a folder? | No, only an attribute | **Yes** — it's the sub‑folder name (and also tagged on the doc) |
| Who creates the folder? | HFS provisioning, per type | **The upload/attach** auto‑creates it (`EnsureFolderPathExists`) |
| Need a "create folder" tool? | — | **No** — upload creates it lazily |
| Why dev is empty | — | nothing uploaded yet (folders are lazy) |

**One‑liner:** in CIC, upload a doc with a chosen `businessObjectId` and the MCP/UCEB automatically
creates `hfs-documents/{businessObjectId}` and files the doc inside — no separate folder step, and the id
can be any test string in dev.

---

## 6. Source references

- `Infra.Hx/Content/Services/Archive/DocumentArchiveService.cs` — CIC archive path; builds
  `folderPath = {folderName}/{BoContextId}`, calls `EnsureFolderPathExists`, then creates the doc at
  `DOCUMENTS_BY_PATH_ENDPOINT/{folderPath}` via the job orchestrator.
- `Configuration/Services/ContentConfigService.cs` → `GetFolderName` = `"{appKey}-documents"`
  (appKey = LOB from `IAuthService.GetAppKey(token)`).
- `Infra.Hx/Content/Services/Folder/FolderService.cs` → `EnsureFolderPathExists` / `CreateFolder`
  (creates each missing path segment).
- MCP `UcebTools.cs` → `upload_document` / `upload_document_from_content` / `upload_pasted_image` →
  `AttachContentAsync` (Initiate → Complete → Attach).
- (Superseded) `MultipartFormCaptureService` + `WorkdayBusinessObjectTypeUtils` — **Workday** capture
  path; this is what misled Day‑4 §8.8 and does **not** apply to our CIC stack.
