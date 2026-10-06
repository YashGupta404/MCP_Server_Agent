# Day 28 — 2026-09-29

_Focus: researched **Hyland IDP + Process Automation (Automate)** and designed a new plugin feature —
**bulk upload with automatic document-type classification + metadata extraction** (so a user dropping
thousands of docs doesn't set type/metadata by hand)._

---

## 1. Research (read Hyland Confluence via signed-in browser)
Confluence needs Atlassian login, so used the integrated browser (I signed in, agent read the pages):
- **How to integrate IDP into Automate** — IDP only runs INSIDE an Automate (BPMN) process; `Build Process`
  generates a template with `contents` (files) + `batchState` (results JSON) + an IDP connector.
- **RFC-0063 (message-based integration)** — Automate is moving to SNS/message eventing (internal plumbing).
- **How to set up an IDP classification process** — a "class" = a name + description the LLM uses to classify;
  `className` in the output is literally whatever you name the class. Same idea for extraction field names.

## 2. batchState (the answer IDP returns)
- classification: `className`, `classId`, `classificationConfidence` (0-1), `classificationReviewStatus`.
- extraction: `fields[]` = `{ name, value, boundingBox, ocrConfidence, extractionConfidence, extractionReviewStatus }`.
- batch: `batchState.documents[documentIndex]` (loop sequentially).

## 3. Design decision — Option B (chosen)
The BPMN process's ONLY job = return **doc type + metadata** per doc. It does NOT capture / NOT touch OnBase.
The plugin auto-fills those into the upload grid; the user reviews; clicking **Upload** uses the EXISTING
capture path into OnBase. Automate just *thinks*; the plugin *acts*.

**Naming rule that makes mapping trivial:** name IDP **classes = OnBase doc types**, and IDP **fields = OnBase
keywords** -> `className`->docType and `fields[]`->attributes are 1:1 (plus an "Undefined" class for unknowns).

## 4. Automate REST contract (from Rovo)
1. Auth: OAuth2 client-credentials -> `POST https://auth.iam.experience.hyland.com/idp/connect/token`
   (Basic base64(client_id:client_secret), grant_type `urn:hyland:params:oauth:grant-type:api-credentials`) -> Bearer.
   SEPARATE client from UCEB + Workday.
2. Files: NOT multipart on start. Upload bytes to **HXP Content Services** (`POST /api/upload`) -> `sys_id` per file;
   pass `sys_id`s in the `contents` variable.
3. Start: `POST https://{accountkey}.studio.experience.hyland.com/{appname}-{first8ofenvkey}/rb/v1/process-instances`
   body `{payloadType:"StartProcessPayload", processDefinitionKey, name, variables:{contents:[{sys_id}]}}` -> processInstanceId.
4. ASYNC: poll `GET .../query/v1/process-instances/{id}` until COMPLETED, then `.../{id}/variables` -> batchState.
   (Alt: BPMN end-event webhook pushes batchState to the BFF.)
5. Limits: **500 docs/batch** (classification+extraction), 1000 pages/batch (separation), 500MB/file, 140 pages/doc.
   => "thousands of docs" REQUIRES the BFF to CHUNK into <=500-doc batches and merge results.

## 5. Key clarifications worked through (with Yash)
- **IDP can only read from CIC/HXP** — it has no pipe into OnBase, and the start call takes `sys_id`
  *references*, not raw bytes. So a copy MUST be staged in CIC first; the BFF (not the browser) does that upload.
- **Two targets, two shapes:**
  - **CIC-native target** -> single upload, then IDP enriches the SAME doc in place (Flavor A: patch metadata;
    Flavor B: stage-then-file the final doc with correct type/metadata).
  - **OnBase target (current demo)** -> CIC is a THROWAWAY "reading copy"; OnBase is the real home. Two uploads.
- Timeline is forced: **bytes into CIC -> IDP reads -> doctype+metadata -> real upload.** Can't get the answer
  before the file is in CIC. Metadata/type arrive BETWEEN the CIC stage and the OnBase upload.
- Open TODO: verify whether UCEB/MCP already has an "update document metadata/type after upload" tool
  (decides if CIC Flavor A is possible; today the plugin sets metadata only AT capture).

## 6. Build plan (not yet implemented)
- **BFF `/api/classify`**: auth -> upload files to HXP -> chunk<=500 -> start process -> poll -> get variables
  -> map className->docType + fields->attributes(+confidence) -> return `[{fileId,docType,attributes,confidence}]`.
- **Extension**: bulk review GRID (row per doc: prefilled doc-type dropdown + prefilled metadata + confidence badge,
  editable) + single **Upload all** reusing `/api/capture` (Workday) / `/api/upload` (Salesforce/CIC).
- **Config/secrets (dotnet user-secrets)**: Automate client_id/secret, accountkey, appname, envkey,
  processDefinitionKey, HXP content-services base URL.

## 7. Manual Studio Modeller steps
IDP project: classes = doc types, fields = keywords, + "Undefined" class, set confidence.
Build Process -> "Document Classification and Field Extraction" template.
Trim BPMN to: **Start (api/message) -> IDP Classification -> IDP Extraction -> End** (batchState readable via
`/variables`); DELETE any capture / OnBase connector nodes. Release + Deploy; record processDefinitionKey,
appname, envkey, accountkey. (Optional: end webhook -> BFF instead of polling.)

## Status
Design fully worked out and saved to repo memory (`/memories/repo/idp-bulk-classify-feature.md`).
NOT implemented yet — Yash is still understanding the flow before we build. Next: finish the walkthrough,
then decide scaffold-now vs wait-for-Studio-values.
