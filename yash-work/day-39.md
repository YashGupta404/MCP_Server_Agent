# Day 39 — ServiceNow (hfsn) LOB: Studio Modeller CIC setup + solution config, learned part-by-part

_Goal: stand up a brand-new **ServiceNow** line of business (`appKey = hfsn`) end-to-end. ServiceNow
reuses the **CIC** content surface (same as Salesforce), so the work is: (1) build the matching content
model in **Studio Modeller** (CIC side), (2) create the UCEB **solution config** that references it, and
(3) learn to drive the solution config (create/get/edit/post) in **Bruno**. Today is mostly deep learning:
fully understanding the Salesforce (`hfs`) CIC setup + its solution config so I can replicate it for
ServiceNow. Mentor confirmed the ServiceNow solution-config schema is identical to Salesforce._

---

## 0. Bruno task — ON HOLD (will resume)

We started a step-by-step Bruno walkthrough to create/get/edit/post the ServiceNow solution config, then
paused it to first understand the CIC + solution-config setup. State so far:

- **Step 1 (done/ready):** install Bruno, create collection `ServiceNow UCEB`, create a `Staging`
  environment with vars:
  - `host = https://api.uceb.app-intel.staging.app.hyland.com`
  - `authUrl = https://auth.staging.app.hyland.com/idp/connect/authorize`
  - `tokenUrl = https://auth.staging.app.hyland.com/idp/connect/token`
  - `clientId = <hfsn client id>`
  - `scopes = openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp`
  - `systemName = OnBase9714_Integrations_Agent`
- **Auth plan (Step 2, pending):** Bruno OAuth2 **Authorization Code + PKCE**. The MCP's registered
  redirect is `https://uceb-mcp-local.dev.hyland.com:5005/callback` (loopback host mapped to 127.0.0.1
  via hosts file; HTTPS self-signed; IAM REJECTS bare `localhost` redirects). For the hfsn client the
  user registered their own localhost redirect — in Bruno the **Callback URL must match that registered
  redirect char-for-char**; Bruno's embedded browser captures the code. If IAM rejects localhost, fall
  back to the hostname trick.
- Blank template already written: `servicenow-solution-config.json` (the `data` node for
  `set_solution_configuration`).

**RESEARCH CONFIRMED (UCEB src):** solution config is keyed per-appKey
(`uceb_lob-name='{appKey}'` + `uceb_sys-config-id` + folder `{appKey}-solution-configurations`), so
`hfsn` and `hfs` sharing one `systemFriendlyName` (e.g. `OnBase9714_Integrations_Agent`) do NOT collide.
ServiceNow gets its own `hfsn-solution-configurations` + `hfsn-documents`. Reuse is SAFE.

---

## 1. Mentor's ServiceNow instructions (what we're executing)

1. ServiceNow is NEW → we must set up matching config on BOTH ECMs: **CIC first** (Studio Modeller), then
   **OnBase** later. The ECM content model must MATCH what the solution config references.
2. **Do CIC first:** in Studio Modeller (staging), create the ServiceNow content model (schemas +
   content types + base mixin), mirroring the Salesforce `hfs-configurations` project.
3. **boProp for ServiceNow = `number`** (the human-readable record number, e.g. `CHG0040019` for a
   change_request), NOT the `sys_id` GUID. Tasks the mentor set:
   - Verify we can fetch `number` from the ServiceNow DOM for **every** business-object type
     (incident / change_request / sc_req_item / …).
   - Confirm every record type exposes the SAME DOM pattern, e.g.:
     `<input type="hidden" id="sys_original.change_request.number" name="sys_original.change_request.number" value="CHG0040019">`
     i.e. `sys_original.<table>.number`.
4. After CIC content model + solution config exist (and later OnBase matches), the LOB is usable.

---

## 2. Salesforce (`hfs`) CIC setup in Studio Modeller — mapped from screenshots

Project: `hfs-configurations` at `studio.staging.app.hyland.com`. Relevant trees:

- **Content > Schemas** (the metadata field sets; each has a **Prefix** `hfs`):
  - `hfs-base-schema` (prefix `hfs`, desc "the base schema for HFS that contains the LOB business
    context") — has EXACTLY 2 fields: `hfs_sObject_Id` (id 223) + `hfs_sObject_owner` (id 224). These are
    the scoping fields; via `hfs-lob-mixin` every content type inherits them. _(screenshot confirmed)_
  - `bill-schema` → `hfs_billing-doc-name`, `hfs_billing-month`, `hfs_bill-date`, `hfs_BillingSerialID`
  - `case-schema` (desc "Experience Testing with Case") → `hfs_DocName`, `hfs_CaseReason`,
    `hfs_CaseStatus`, `hfs_CaseDate`, `hfs_CaseAmount`, `hfs_CaseFloat`, `hfs_CaseLastDate`
  - plus `dev-test-account-schema`, `multi-value-field-schema`, `opportunity-schema`,
    `prescription-schema`, `test-schema`
- **Content > Mixins**: `hfs-lob-mixin` — "Any content type that uses this mixin would be able to
  participate in HFS integration. This uses hfs-base-schema." (selected schema: `hfs-base-schema`). This
  is how every content type inherits the base scoping fields.
- **Content > Content Types**: `bills-content-type` (parent `hfs-base-type`; inherited schemas
  `hfs-base-schema, sysfile, sysfiles, system`; selected schema `bill-schema`; inherited mixins
  `hfs-lob-mixin, SysFilish`), `case-content-type`, `dev-test-account`, `prescription`, `abc`,
  `hfs-base-type`, `opportunity-content-type`, `multivalue-content-type`.
- Also present: Renditions (Capture Preview, Doc List Preview), Security Policies, IDP Document AI
  Configurations, Governance.

**Key insight — the chain:** `Schema` (fields, prefix `hfs`) → added to a `Content Type` → the Content
Type also includes `hfs-lob-mixin` (which carries `hfs-base-schema` = the `hfs_sObject_Id` /
`hfs_sObject_owner` scoping fields) → the solution config then references that content type by name and
its columns. So Studio Modeller DEFINES the fields/types; the solution config MAPS business objects →
those types + how to populate them.

---

## 3. Salesforce solution config — full structure (researched line-by-line)

File: `solution-config.json` (this is the GET response wrapper `{data, total, error}`; writes take the
`.data` node). Shape of `data`:

- `configurations.operationConfig`
  - `query { pageSize:10, maxResults:null }`
  - `capture { fileSizeLimit:2000, fileTypesAllowed:[bmp,jpg,jpeg,png,pdf,tiff,docx,pptx,xlsx,txt,json,mp4,mp3] }`
  - `rendition: null`
  - `viewer { baseUrl: "https://oifsdev.hyland.com/QuickAccessViewer/viewer/document/{doc_id}" }`
- `configurations.businessObjectConfig`
  - `contextParams` (the SCOPING keys — how a record links to its docs):
    - `boContext { boProp:"Id", ecmProp:"hfs_sObject_Id", ecmPropId:"223" }`  ← record Id → ECM field
    - `boUser    { boProp:"CreatedById", ecmProp:"hfs_sObject_owner", ecmPropId:"224" }`
  - `additionalConfig[]` — one entry PER (business object → content type) pairing. Each entry:
    - `busObject` (LOB object: opportunity/campaign/case/contact/account/yash_test/…)
    - `ecmContentTypeName` / `ecmContentTypeId` / `ecmContentTypeGroupId`
    - `ecmMetadataFieldLabels[]` — `{ecmColumnName, ecmColumnId, localeStrings[{culture,name,description}]}`
    - `displayColumns { ecmColumnSets[{formFactorId, columns[]}], defaultFormFactor:"desktop" }`
    - `metadataFieldImportMappings[]` — `{ecmFieldName, inputSource, value{value,type,format}}`
      (how each ECM field is filled on upload)
    - `defaultListQuery` (null, or `{name,id,type,default}`)
  - `queryConfig[]` — one entry PER busObject, each with `queries[]`:
    - `{name,id,label,description,type,default,filterClauses[],displayColumns,displayColumnConfig[]}`
    - `filterClauses[]`: `{ecmFieldName,ecmFieldId,inputSource,value{value,type,format},editable,operator}`
    - `type` ∈ `ContentType` (CIC-native) / `PreConfigured` (OnBase saved search)
- `configurations.endUserGroupConfig[]` — `{id,name,role}` (adminUser / contentUser groups)
- `configVersion: "1"`

### 3a. TWO content surfaces live in one Salesforce config
- **CIC-native** (built in Studio Modeller): `ecmContentTypeName` is a NAME (`bills-content-type`,
  `case-content-type`, `dev-test-account`, `prescription`); `ecmContentTypeId` = same name;
  `ecmContentTypeGroupId = null`; column ids are STRINGS (`hfs_billing-month`). ← this is what we build
  for ServiceNow first.
- **OnBase via CFS**: `ecmContentTypeName` = `Invoices` / `COM - Application`; NUMERIC
  `ecmContentTypeId` (205 / 206) + `ecmContentTypeGroupId` (116 / 117); column ids are NUMBERS
  (226–234); queries are `type:"PreConfigured"` (OnBase saved searches id 109/110). ← the OnBase side we
  replicate LATER.

### 3b. `inputSource` enum (how an ECM field gets its value on import)
- `1` = from the record/scoping (auto; value = the LOB field name, e.g. `Id`, `CreatedDate`, `Name`)
- `2` = static literal value
- `3` = (query filter) user-entered search value
- `4` = file-related (e.g. File Name)
- `5` = file-related (e.g. File Type)
- `6` = user-entered at upload time

---

## 4. ServiceNow replication plan (CIC first) — derived

In Studio Modeller, create a ServiceNow project/content model mirroring `hfs-configurations`, prefix
`hfsn`:
- `hfsn-base-schema` + `hfsn-lob-mixin` carrying the scoping fields. **boProp = `number`**, so the base
  scoping field is the ServiceNow record **number** (e.g. `hfsn_recordNumber`) instead of a sObject Id;
  plus an owner field. (Confirm exact base fields against `hfs-base-schema`.)
- Per-table schemas + content types (incident / change_request / sc_req_item) with their metadata fields.
- Then the solution config: `boContext.boProp = "number"`, `ecmProp = hfsn_...`, `additionalConfig[]`
  per table → content type, `queryConfig[]` as needed.
- Extension detector must pull `number` from the DOM `sys_original.<table>.number` hidden input.

---

## 5. Learning session — part-by-part (in progress)

Teaching the Salesforce solution config in small parts, each paired with the ServiceNow equivalent.
_(Log each part as we go.)_

- Part 1: big picture — what a solution config is, the Studio Modeller ↔ solution config ↔ extension
  relationship, and the two content surfaces. _(covered)_
  - **DOM homework DONE (boProp=number verified on dev456505 PDI):** the hidden input
    `sys_original.<table>.number` is present for all three tables:
    - change_request → `sys_original.change_request.number = "CHG0040019"` (in a nested, unnamed iframe
      `top > frame0` — opened via the Next Experience `now/nav/ui/classic` shell)
    - incident → `sys_original.incident.number = "INC0009009"` (in `top` — opened via direct `incident.do`)
    - sc_req_item → `sys_original.sc_req_item.number = "RITM0010001"` (in `top > frame0`)
  - Each also has the visible field `<table>.number` with the same value. Location (top vs nested frame)
    depends only on HOW you open it (direct `.do` = top; Next Experience classic shell = nested frame),
    so the detector must scan **all frames** (`all_frames: true`). ⇒ `boProp = "number"` is viable.
- Part 2: `contextParams` (`boContext`/`boUser`) = the scoping block. Salesforce:
  `boContext{boProp:Id, ecmProp:hfs_sObject_Id, ecmPropId:223}` +
  `boUser{boProp:CreatedById, ecmProp:hfs_sObject_owner, ecmPropId:224}`. These two ecmProps ARE the two
  fields in `hfs-base-schema` (via `hfs-lob-mixin`). Flow: on upload UCEB writes record Id→hfs_sObject_Id,
  CreatedById→hfs_sObject_owner; on list it queries docs WHERE hfs_sObject_Id = this record's Id.
  ServiceNow equivalent: build `hfsn-base-schema` (prefix `hfsn`) with `hfsn_recordNumber` +
  `hfsn_recordOwner`, wrap in `hfsn-lob-mixin`; config `boContext{boProp:number, ecmProp:hfsn_recordNumber,
  ecmPropId:<assigned at publish>}`, `boUser{boProp:<SN owner field: sys_created_by/opened_by — TBD>,
  ecmProp:hfsn_recordOwner}`. OPEN: new `hfsn-configurations` project vs reuse — confirm.
