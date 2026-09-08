# Day 20 — 2026-09-07

_Focus: cracked the **CIC viewer for OnBase (CFS) documents** end-to-end. Chased Dawid's guidance from a
bare URL format to a fully-working request chain — found the right **CFS broker/integration**, discovered
the real **`contentId`** (it's the doc's Attachment-ID GUID, **not** the OnBase handle), and traced the
last failure to a **server-side thumbnail/transform 500** on Dawid's backend. Everything on our side now
renders up to the transform step._

---

## 0. Where we started

Day-19 left inline preview/view **blocked** because `onbase_hcm_stg` ships no `renditionIds` and no
`viewerBaseUrl`. Dawid corrected the earlier "impossible" conclusion: the **CIC viewer** can render CFS
(OnBase/Nuxeo) docs — not just CIC-native ones — via a **`/cfs/` URL**, separate from the HxPR `/documents/`
path. Goal today: build and prove that CFS viewer URL for our OnBase docs.

**CFS viewer URL format (from Dawid):**
```
https://cic-viewer.<env>.app.hyland.com/#/cfs/{integrationId}/{contentId}/{logicalPartId}?envKey={envKey}
```
- Staging viewer host: `https://cic-viewer.staging.app.hyland.com/#/`
- `envKey` = `appintel-staging-prod`
- `logicalPartId` = 0 or 1 (both behave the same)

---

## 1. Finding the `integrationId` — the CFS Admin Portal

The `integrationId` is a **CFS integration id**, not the UCEB `systemConfigId` (`2412c2db-…` isn't in the
CFS list). Had to pull it from the **CFS Admin Portal**.

- Staging portal: `https://appintel-staging-test.admin.cfs.staging.app.hyland.com/connections`
- **Must switch environment to `appintel-staging-prod`** (where our docs live) — host rewrites to
  `appintel-staging-prod.admin.cfs.staging.app.hyland.com/connections`. The default `staging-test` env only
  shows Salesforce/RDV connections — **none of ours**.
- Sign in: **`yash.gupta+appintel-staging@hyland.com`**. First attempt → **"Not authorized"**; fixed by
  assigning the IAM group **`CFSAdmins_Workday`**.
- Tried to script it with the arizzo token first — **dead end**: `/api/connections` → 401, `/connections` →
  the CFS UI login HTML. The admin API only accepts the CFS UI OAuth client, not our bearer.

**Our OnBase connections (env `appintel-staging-prod`):**

| Connection | Integration ID | Notes |
|---|---|---|
| **CF_Broker_Workday_OnBase** | `532c245c-a412-4f1e-8878-3097588179e5` | ✅ **the one** — desc "Development broker for UCEB" |
| CF_Broker_HFW_OB_STG_011086 | `01cce4cb-5a76-4a0c-8f14-3053a4f1b2a8` | backup — 500s on fetch (wrong repo) |
| CFBroker_Workday_Nuxeo | `c3b06d7b-9f9b-4720-ac93-9e378a855079` | Nuxeo, not OnBase |

---

## 2. First attempts — auth works, but `/v1/cfs/document` 500s

Built the URL with the OnBase **document handle** (`14293`) as `contentId`:
```
…/#/cfs/532c245c-…/14293/1?envKey=appintel-staging-prod
```
- **Auth + integration resolved** (no "not authorised") — big step past the earlier cross-env boundary wall.
- But the viewer showed **"Error during processing document"** with two 500s:
  `/v1/cfs/document/532…` and `/v1/ts/cfs-document/…`.
- Backup broker `01cce4cb` → both 500 as well.

A **500 (not 404/401)** meant auth/integration were fine but the **`contentId` format was wrong** — the raw
OnBase handle isn't what CFS wants.

---

## 3. The real `contentId` — the Attachment-ID GUID

Dumped the full raw document JSON for the employee (arizzo token, `raw_get` diagnostic tool):
```
GET {uceb}/bow/core/business-objects/{WID}/documents?businessObjectType=employee&systemFriendlyName=onbase_hcm_stg
```
Each doc carries `simpleDocumentAttributes."Attachment ID".value`. For CFS-backed docs (only the
**"Application"** type — Offer Letters have it blank) this is:
```
oms-attachments/{GUID}     e.g. 14293 → oms-attachments/0fcb77bf-df31-4eb0-bacd-0ec131829da6
```

**`contentId` = the plain GUID** (`0fcb77bf-…`) — **not** the OnBase handle, and **do NOT** URL-encode the
`oms-attachments/` prefix (that yields "An unknown error occurred").

**Result with `532c245c` + GUID:** the `/v1/cfs/document` call **dropped out of the errors = it succeeded**.
Only `/v1/ts/cfs-document` still 500'd. The document fetch works. ✅

| Broker | contentId | `/v1/cfs/document` (fetch) | `/v1/ts/cfs-document` (thumbnail) |
|---|---|---|---|
| 532c245c | `14293` (handle) | ❌ 500 | ❌ 500 |
| 01cce4cb | GUID | ❌ 500 | ❌ 500 |
| **532c245c** | **GUID** | ✅ **success** | ❌ 500 |
| 532c245c | `oms-attachments%2FGUID` | — | "unknown error" |

---

## 4. The last blocker — thumbnail/transform 500 (backend)

The one remaining failing call, from the Network tab:
```
GET https://cic-viewer.staging.app.hyland.com/v1/ts/cfs-document/532c245c-…/0fcb77bf-…/0?thumbnailFileHeight=150&thumbnailFileWidth=150&envKey=appintel-staging-prod
→ 500 Internal Server Error
```
`/v1/ts/` is the **thumbnail/transform service** (note `thumbnailFileHeight=150&thumbnailFileWidth=150`).
Opened the URL directly to read the 92-byte body:
```json
{"message":"Failed to render CFS document","error":"Internal Server Error","statusCode":500}
```

- **Same for multiple docs** (14293, 14288) and **both `logicalPartId` 0 and 1** → not a param issue.
- Lines up with day-19 §4: `onbase_hcm_stg` docs have **no renditions** (every rendition type 404s). The
  thumbnail service appears to have nothing to render → 500.

**This is now a backend issue on Dawid's team** — the transform/thumbnail service can't render our
OnBase-via-CFS docs. Sent him the full trace + error body; asked him to check the transform-service logs
for broker `532c245c` and whether a rendition is required.

---

## 5. Known-good CFS URL (once backend fixed)
```
https://cic-viewer.staging.app.hyland.com/#/cfs/532c245c-a412-4f1e-8878-3097588179e5/0fcb77bf-df31-4eb0-bacd-0ec131829da6/1?envKey=appintel-staging-prod
```

---

## 6. Status

- ✅ CFS viewer URL format fully worked out (host, `envKey`, broker, `contentId`, auth).
- ✅ Right broker: **CF_Broker_Workday_OnBase** (`532c245c-…`).
- ✅ Right `contentId`: the doc's **Attachment-ID GUID** (not the OnBase handle).
- ✅ `/v1/cfs/document` fetch **succeeds**.
- ⛔ `/v1/ts/cfs-document` (thumbnail/transform) **500 "Failed to render CFS document"** — **backend
  (Dawid)**. Waiting on his logs / rendition answer.

---

## 7. Gotchas / notes for next time

- **CFS Admin Portal env selector matters** — our connections only appear under env `appintel-staging-prod`,
  not the default `appintel-staging-test`. Needs IAM group **`CFSAdmins_Workday`** on the staging account.
- **`contentId` ≠ OnBase handle** — it's the plain GUID from `Attachment ID` (`oms-attachments/{GUID}`);
  don't URL-encode the prefix.
- The arizzo token can't touch the CFS admin API (401 / login HTML) — must use the portal UI.
- **Cleanup owed:** the `raw_get` diagnostic tool (`UcebApiClient.RawGetAsync` + `UcebTools.raw_get`) is
  still in the MCP and **must be removed before any commit** (SSRF risk).
- Full trace also saved to repo memory `cic-viewer-cfs.md`.
