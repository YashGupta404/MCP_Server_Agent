# Day 22 — 2026-09-09

_Focus: made the plugin **general** — **any system config, any environment, any LOB (Salesforce or Workday)
→ list, upload, and view in the CIC viewer inside the panel**. Fixed upload for the deployed (staging)
UCEB, moved the CIC-viewer URL resolution into the **MCP** (dynamic envKey + per-system integration id +
per-system envKey override), added a **`salesforce-staging`** LOB, and got **Salesforce/OnBase (`OnBase9714`)**
rendering in the panel._

---

## 0. Goal

"Any sys config I select, for any env, for any LOB (Salesforce or Workday), I should be able to **list**,
**upload**, and **view** its documents using the CIC viewer — in the same panel iframe."

Three capabilities, three fixes.

---

## 1. Upload — the `singleValue` type bug (dev vs staging)

**Symptom (staging):** upload 400s —
```
The JSON value could not be converted to System.String. Path: $.businessObjectAdditionalAttribute[0].singleValue
```
**Cause:** `CoerceSingleValue` (in `Tools/UcebTools.cs`) sent **numeric** attributes as JSON **numbers**
(`2000`), but the platform attach DTO types `singleValue` as a **string**. The **dev** UCEB accepted numbers
(so it worked in earlier Salesforce demos); the **deployed staging** UCEB is stricter and rejects the whole
`documentRequest`.

**Fix:** `CoerceSingleValue` now **always returns the string value**. Upload works across all envs/LOBs.

---

## 2. View — CIC viewer URL resolution moved into the MCP

Hardcoding the viewer host/envKey/integration per system in the extension didn't generalize (the extension
can't know the environment). So the **MCP** now builds the full CIC viewer URL in `open_document_in_viewer`:

- **`Clients/UcebApiClient.cs` → `GetCicViewerUrlAsync`:**
  - **envKey** resolved dynamically from the signed-in token (Nucleus `/environments/{id}.key`), cached.
  - **host** from `Uceb:CicViewerBaseUrl` (appsettings, flipped per env by switch-lob).
  - **route** — if the active `systemFriendlyName` is in `Uceb:CfsIntegrationIds` → `/#/cfs/{integrationId}/{docId}/1`;
    otherwise CIC-native → `/#/documents/{docId}`.
  - **per-system envKey override** (`Uceb:CfsEnvKeyOverrides`) — used when a CFS connection's content lives in
    a DIFFERENT environment than the token resolves to (see §5).
- **`Configuration/UcebMcpOptions.cs`:** new `CicViewerBaseUrl`, `CfsIntegrationIds`, `CfsEnvKeyOverrides`.
- **`Tools/UcebTools.cs` `open_document_in_viewer`:** try `GetCicViewerUrlAsync` first, fall back to the old
  Studio `GetViewerUrlAsync`.
- **`browser-extension/src/popup.js`:** reverted to `openInViewer(docId)` (loads the MCP-resolved URL) and
  removed the client-side `buildViewerUrl`. The extension no longer holds any per-system viewer config.

**Result:** the extension just loads whatever URL the MCP returns → works for any env/LOB, CIC-native or CFS.

---

## 3. `salesforce-staging` LOB

Salesforce previously only ran on **dev** (`salesforce` LOB, local UCEB `:5000`). Added a
**`salesforce-staging`** LOB to `bff/switch-lob.ps1`:
- ClientId `wsc-f3ec0fd9-47a3-4f03-a67f-28b70100141b`, `Iam='staging'`, deployed staging UCEB
  (`https://api.uceb.app-intel.staging.app.hyland.com`), Salesforce scopes (no `wdx`).
- `switch-lob` now also **flips `CicViewerBaseUrl` per env** (staging → `cic-viewer.staging`, dev →
  `bravo.cic-viewer.sandbox`) alongside the IAM endpoints.
- Secret stored once via `dotnet user-secrets set "Lob:salesforce-staging:ClientSecret" …` (the earlier
  `salesforce` switch had overwritten the active `Auth:ClientSecret`, so staging LOBs needed their own slot).

---

## 4. Config findings (Salesforce content is OnBase, not CIC)

Enumerated all 24 system configs (`list_system_configurations`). For the Salesforce Account record:
- **`CIC`** → `systemConfigId` all-zeros (`00000000-…`) = **unconfigured** → upload 400s
  ("EcmContentTypeName does not match any configured document type"), list empty.
- **`CIC466`** / **`CICNEW`** → real CIC configs, but **no column configuration for the `Account` object
  type** → 422 "Column Configuration for the requested Business object type is not available". So no CIC
  config works for these Account records.
- The Salesforce records actually use **OnBase** (`systemFriendlyName = OnBase9714`, "OnBase residing in VM
  RDV-009714").

So for these Salesforce records the working backend is an **OnBase/CFS** config, viewed via `/#/cfs/…`.

---

## 5. Getting `OnBase9714` to render — integration id + the envKey gotcha

The CFS viewer needs the connection's **integration id** (no API — CFS Admin Portal only):
- `OnBase9714` had **no `9714` connection** in `appintel-staging-prod` (only `9675`/`9676`). Mentor supplied
  the mapping: `OnBase9714` → CFS integration **`4873af36-dd9a-4e90-9b54-962dc4079fb1`** (`CF_Broker_Salesforce`).
- With the right integration id, `/properties` returned **404 "CFS document not found"** (NOT a 401/broker
  500 → so **no `wdxorbis`/`aurahyland` domain issue** here, unlike Workday).
- **Root cause: wrong environment.** The `OnBase9714` connection + docs live in **`appintel-staging-test`**,
  but the token resolves to `appintel-staging-prod`. Mentor's working URL used `envKey=appintel-staging-test`.

**Fix:** per-system **`CfsEnvKeyOverrides`** — `{ "OnBase9714": "appintel-staging-test" }`. The MCP now builds:
```
https://cic-viewer.staging.app.hyland.com/#/cfs/4873af36-…/{docId}/1?envKey=appintel-staging-test
```

### Seeded CFS map (appsettings `Uceb`)
| System config | Integration id | envKey |
|---|---|---|
| `onbase_hcm_stg` (Workday) | `532c245c-…` | (token) `appintel-staging-prod` |
| `OnBase9675` (Salesforce) | `14f19da9-…` | (token) |
| `OnBase9714` (Salesforce) | `4873af36-…` | override `appintel-staging-test` |

---

## 6. Files changed

**MCP (`Hyland.Experience.UCEB.Api`, branch `feature/uceb-mcp-server-poc`):**
- `Configuration/UcebMcpOptions.cs` — `CicViewerBaseUrl`, `CfsIntegrationIds`, `CfsEnvKeyOverrides`.
- `Clients/UcebApiClient.cs` — `GetCicViewerUrlAsync` + `ResolveEnvKeyAsync` (+ cached env key).
- `Tools/UcebTools.cs` — `CoerceSingleValue` → string; `open_document_in_viewer` prefers the CIC viewer URL.
- `appsettings.json` — `CicViewerBaseUrl`, `CfsIntegrationIds`, `CfsEnvKeyOverrides`.

**`MCP_Server_Agent` (branch `main`):**
- `bff/switch-lob.ps1` — `salesforce-staging` LOB + `CicViewerBaseUrl` per-env flip.
- `browser-extension/src/popup.js` — `openInViewer` (MCP-resolved URL); removed `buildViewerUrl`.
- `browser-extension/src/config.js` — viewer config no longer used by the plugin (MCP resolves it).

---

## 7. Status

- ✅ **List** — any system config.
- ✅ **Upload** — `singleValue` fix → any env/LOB.
- ✅ **View in CIC viewer (panel iframe)** — MCP resolves host + envKey + route; Workday (`onbase_hcm_stg`)
  and Salesforce (`OnBase9714`, `OnBase9675`) both render.
- The only per-connection inputs (no API for them, CFS Admin Portal / mentor): a new OnBase connection's
  **integration id**, and — if its content is in a different env — its **envKey override**. Both are one-line
  `appsettings` additions.

---

## 8. Gotchas / notes for next time

- **`singleValue` must be a string** — dev UCEB tolerated numbers, staging UCEB rejects them.
- **envKey ≠ always the token env.** A CFS connection can live in a different environment
  (`OnBase9714` → `appintel-staging-test`). A **404 "CFS document not found"** (vs 401/broker-500) is the
  wrong-env signal.
- **integration id ≠ systemConfigId** — read the Integration ID off the connection row in the CFS Admin
  Portal (no API).
- **Salesforce records here are OnBase-backed** (`OnBase9714`), not CIC — plain `CIC` is unconfigured, and
  `CIC466`/`CICNEW` have no `Account` column config.
- Each **appsettings** change needs an MCP **rebuild + restart** (IOptions is bound at startup). The `.exe`
  is locked while running → stop the MCP (kill the pid on `:5200`) before building.
- `salesforce-staging` uses the **deployed** UCEB — the local UCEB `:5000` is NOT needed for it.
