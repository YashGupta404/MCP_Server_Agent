# Day 11

_Focus: received a large **Postman collection** (`HCMISBE_System_API_Tests_HX_Staging`, ~4000 lines)
for the Workday HCM ↔ Hyland integration. Instead of pasting it around, saved it into the workspace,
git-ignored it (it contains a real bearer token), and extracted the **document capture endpoint** plus
the **token flow** so we can wire capture into our stack._

---

## 1. How we handled the collection

- Saved it at the workspace root: `HCMISBE_System_API_Tests_HX_Staging.postman_collection.json`.
- **Git-ignored** it — added `*.postman_collection.json` / `*.postman_environment.json` to `.gitignore`.
  The collection embeds a **real JWT bearer token** (in the "Fetch IDP token" requests) and would
  otherwise leak a credential if committed.
- Parsed it with search/read instead of manual scrolling.

> Security note: that hard-coded JWT is a live-ish credential. It's already expired-by-time, but treat
> the file as sensitive — never commit it, never paste the token.

---

## 2. The capture endpoint (what we were looking for)

**Upload / capture a document:**

```
POST {{baseUrl}}/core/documents
```

- `{{baseUrl}}` = `https://api.workday.dev.experience.hyland.com/bow` (dev)
  or `https://api.workday.staging.experience.hyland.com/bow` (staging). The `/bow` suffix matters.
- **Auth:** `Authorization: Bearer {{token}}`
- **Content-Type:** `multipart/form-data`
- **Body (form-data):**
  - `file` — the document binary (a 400 "No file is provided for upload" comes back if omitted).
  - `captureData` — a **JSON string** describing the document + business-object metadata.
  - `businessObjectType` — present but usually `disabled` (the value is inside `captureData` instead).

**`captureData` shape:**

```jsonc
{
  "businessObjectType": "employee",         // or "supplier", etc.
  "documentType": { "id": "new-hire-checklist" },  // OfferLetter / vendor-setup-form / ...
  "documentId": null,                        // null = new doc; set an id + createNewVersion to version
  "createNewVersion": false,
  "businessObjectAttributes": [
    {
      "id": "hcmisbeemp_department",
      "name": "DEPARTMENT",
      "value": "Human Resources",
      "dataType": "string",                  // string | date | decimal | double | ...
      "currency": null,
      "dateFormat": null,
      "isSystemData": false
    }
    // ... one object per attribute
  ]
}
```

**Success response (200):**

```json
{
  "data": {
    "documentId": "<string>",
    "thumbnailId": "<string>",
    "capturePreviewId": "<string>"
  },
  "total": 0,
  "error": { "code": "", "message": "", "messages": [] }
}
```

The test scripts stash `data.documentId` into a `documentId` collection variable for later requests
(e.g. versions, viewer URL).

**Related capture variants in the collection:** `Upload a Document for capture_DOCX / _Emp / _Sup /
_Inv`, `Upload document_Create versions` (POST `/core/documents/{id}/versions`), and
`Upload document_Working copy latest` (GET `/core/documents/{id}/versions`).

---

## 3. The token flow that feeds `{{token}}`

**Fetch IDP token:**

```
POST https://auth.iam.{env}.experience.hyland.com/idp/connect/token
Content-Type: application/x-www-form-urlencoded
```

Body (url-encoded):

```
grant_type   = urn:hyland:params:oauth:grant-type:api-credentials
client_id    = {{client_id}}
client_secret= {{client_secret}}
scope        = environment_authorization hxp hxp.integrations hxp.nucleus.account hxpr wdx openid
```

- A test script saves `access_token` → `{{token}}`, which every `/bow/...` call then uses as a bearer.
- **This is a different IAM than our plugin/MCP.** Capture uses
  `auth.iam.dev.experience.hyland.com` (the Workday/experience IAM), NOT
  `auth.dev.app.hyland.com` that the plugin + MCP use.
- Grant type is **api-credentials** (service client_id/client_secret) — machine-to-machine, not the
  interactive PKCE flow. The decoded JWT shows the permissions we need for capture:
  `wdx.document.capture`, `wdx.document.query`, `wdx.document.preview`, `wdx.configuration.read/write`.

---

## 4. Other endpoints in the collection (the map)

**Setup**
- `POST {{baseUrl}}/config/solution-configurations` — Updates the configurations (employee mappings,
  dynamic columns, roles, viewer base URL, security policies).
- `POST https://auth.iam.staging.experience.hyland.com/idp/connect/token` — Fetch IDP token (+ Staging copy).

**End-to-end tests**
- `GET  {{baseUrl}}/config/solution-configurations?businessObjectType=employee` — get config for a BO type.
- `GET  {{baseUrl}}/config/all-dynamic-columns?retrieveSavedColumnsOnly=false&businessObjectType=employee`
- `GET  {{baseUrl}}/core/document-types?documentTypeGroupId=hcmisbeemployee` — related document types.
- `POST {{baseUrl}}/core/document-types/:documentTypeId/default-attributes` — default attrs for a type / document.
- `GET  https://api.workday.dev.experience.hyland.com/bow/config/viewer-base-url?businessObjectType=employee`
  — Document Viewer URI.
- `POST {{baseUrl}}/core/documents` — **capture / upload** (the one above).
- `POST {{baseUrl}}/core/documents/{id}/versions` — upload a new version.
- `GET  {{baseUrl}}/core/documents/{id}/versions` — list versions / working copy latest.

---

## 5. Takeaways / next steps

- The capture path is simple: `POST /bow/core/documents` with `file` + `captureData` (multipart),
  bearer token from the experience IAM via api-credentials.
- To wire this into our stack we'd need a **service client_id/client_secret** for the Workday
  experience environment (separate from the plugin/MCP IAM), then build the multipart request.
- Watch the **two-IAM split**: `auth.iam.*.experience.hyland.com` (Workday/capture) vs
  `auth.dev.app.hyland.com` (plugin/MCP). Tokens are not interchangeable.
- Keep the Postman file out of git (already ignored) — it carries a bearer token.

---

## 6. Source-level validation: does Workday really have its own controllers/endpoints/configs?

Mentor + Workday team said: *Workday cannot use the UCEB configs/endpoints — it has its own set of
controllers, endpoints and configs, and needs those URLs only; so we may need a separate Workday MCP
tool list and tell the Agent Builder agent to use only the Workday tools on Workday.* I read the UCEB
API source to check this.

**Verdict: they are CORRECT.** Confirmed directly in `Hyland.Experience.UCEB.Api\src`:

- There is a dedicated project `Hyland.Experience.UCEB.Workday.Api.Library` with **10 Workday-only
  controllers** (auth-status, build-info, configuration, content-platform, document-capture,
  documents, document-versioning, file-preview, metadata, user-roles).
- Workday routes live in `Models\WorkdayRoutePaths.cs` and are ALL under **`bow/*`**:
  `CoreBase = "bow/core"`, `ConfigurationBase = "bow/config"`, `CommonBase = "bow/common"`,
  `SolutionConfig = "bow/config/solution-configurations"` (note: **no `/uceb` suffix**).
- UCEB routes live in `Api.Library\Models\OpenApi\RoutePaths.cs` under **`api/*`**:
  `api/core`, `api/config`, and `api/config/solution-configurations/uceb`.
- Separate authorization policies (`ViewDocumentsPolicy_Workday`, `CaptureDocumentsPolicy_Workday`),
  separate audit events (`wdx.*`, e.g. `wdx.document.capture.{0}`), separate DI services
  (`IWorkday*Service`), `appKey = "wdx"`.

So this matches the Postman collection above (everything is `/bow/...`) and explains the **config
403s** we saw with a Workday token hitting `api/config/.../uceb`: that token is scoped to `bow`/`wdx`
and is simply not authorized on the UCEB config route.

### 6a. What our MCP client already does vs what it gets wrong

`McpServer\Clients\UcebApiClient.cs` already implements token-derived LOB routing
(`BusinessObjectBasePath` = `api` for Salesforce, `bow` for Workday). But only the **document**
methods were switched to it:

- LOB-correct already (hit `bow/*` for Workday): `list_documents`, `capture_document`,
  `get_capture_default_attributes`. → this is why capture + list already work.
- Still hard-coded to `/api/...` (wrong for Workday, causes the 403s): all config tools
  (`get_solution_configurations`, `list_business_object_types`, `add_business_object_config`,
  `set_viewer_url`) plus `download_document`, `get_platform_capabilities`, `get_content_platform`,
  `list_document_types`, `get_document_type_metadata`, `list_document_type_groups`, and the upload
  steps.

So our LOB routing is the right pattern but only ~half finished.

### 6b. What to do (recommendation)

Two valid paths:

- **Option A (recommended): finish LOB-awareness in the single tool set.** Replace the remaining
  hard-coded `/api/...` in `UcebApiClient.cs` with LOB base paths (add a `ConfigBasePath` =
  `api/config/solution-configurations/uceb` for SF vs `bow/config/solution-configurations` for
  Workday, and a `CoreBasePath` = `api/core` vs `bow/core`). The token already decides the LOB, so the
  same tools auto-route to the Workday controllers, the config 403s go away, and the Agent Builder
  agent needs **no** "use only Workday tools" rule. Least duplication, no chance of the model picking
  the wrong tool.
- **Option B (what the mentor literally described): a separate `workday_*` tool set** that always
  targets `bow/*`, registered in Agent Builder with a prompt rule "on Workday use only `workday_*`
  tools." Explicit, but doubles the tool surface and pushes the LOB choice onto the model every turn.

Recommendation: **Option A** — it reaches the exact same `bow/*` Workday controllers the mentor is
pointing at, fixes the config 403s, and keeps the agent simple because the LOB is authoritative from
the token. Keep Option B as a fallback if Agent Builder ever needs the split to be explicit in the
tool list.

### 6c. Refinement to the original understanding

"I have to create another MCP tool list specific for Workday" is only *partly* required: Workday must
hit the `bow/*` routes (true), but you don't need a second, separately-named tool set to do that — the
existing tools can carry the LOB from the token (Option A). A separate named set is only needed if
Studio can't reliably infer the LOB from context.
