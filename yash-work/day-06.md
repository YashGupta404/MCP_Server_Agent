# Day 6

_Focus: fix the HxViewer environment (staging → dev), harden the plugin file‑upload path, bring the
whole stack up for a live demo, diagnose why the chatbot felt slow, and take in a **new manager
requirement**: make the chatbot **context‑aware of the browser screen** (content‑in‑context)._

---

## 1. HxViewer URL was pointing at STAGING → fixed to DEV

**Symptom:** opening a document in the Hyland viewer (`open_document_in_viewer`) redirected to a
**staging** sign‑in / viewer, even though everything else runs in **dev**.

**Root cause:** the viewer URL is **not** in code or `appsettings` — it is read from the UCEB
**solution config blob** at `data.configurations.operationConfig.viewer.baseUrl`
(`UcebApiClient.GetViewerUrlAsync`, which substitutes the `{doc_id}` placeholder). The dev config blob
had a **staging** URL baked in. It cannot be fixed by a naive staging→dev string replace: the
`key-<GUID>` subdomain, the `hfs-configurations-<id>` app id, and the `hyland-for-salesforc-<slug>` are
**per‑environment** identifiers.

**Real dev viewer URL (from mentor):**
```
https://key-a6cbaddb-984f-4c10-afec-f1a579396269.studio.dev.app.hyland.com/hfs-configurations-a94844a0/ui/hyland-for-salesforc-u8fcw/#/default/documents/{doc_id}
```

### Two fixes shipped (in `Hyland.Experience.UCEB.McpServer`)

1. **New MCP tool `set_viewer_url(viewerBaseUrl)`** → `UcebApiClient.SetViewerUrlAsync`: does a
   GET (current config) → set `operationConfig.viewer.baseUrl` (creates the `operationConfig`/`viewer`
   nodes if missing) → POST the whole `data` blob back (same round‑trip pattern as
   `add_business_object_config`). Normalizes an encoded `%7Bdoc_id%7D` → `{doc_id}` and requires the
   placeholder. This writes the **stored config** so it's correct for everyone.
2. **Zero‑touch override (what the user actually wanted):** added `Uceb:ViewerBaseUrl` to the MCP
   `appsettings.json` + a `ViewerBaseUrl` property on `UcebMcpOptions`. `GetViewerUrlAsync` now
   **prefers** this configured value (normalizes + substitutes `{doc_id}`) and only falls back to the
   config blob when it's empty. Result: `open_document_in_viewer` **auto‑opens the dev viewer** — no
   URL to type, ignores the stale staging URL still in the blob.

> Net: for the demo you never pass a URL. Just say "open document `<docId>` in the viewer".

---

## 2. Plugin file‑upload path hardened

Two bugs surfaced while testing attach‑and‑upload from the extension:

1. **`hfs_Name` always stamped →** `400 attributes do not exist … hfs_Name`. The `prescription`
   document type has **no** `hfs_Name` attribute, but `upload_staged_file` defaulted
   `effectiveName = documentName ?? fileName`, so it always stamped a name. **Fix:** pass
   `documentName` through **as‑is** (null when omitted), so `hfs_Name` is only set when explicitly
   requested.
2. **`stagingId` consumed on a failed upload.** `TryTake` removed the staged bytes even when the
   upload failed, so a retry said "staging ID already consumed". **Fix:** added `TryPeek` + `Remove`
   to `FileStagingStore`; `upload_staged_file` now peeks, uploads, and only `Remove`s the stagingId on
   **success** — a failed attempt keeps the stagingId valid for retry.

Recap of the MCP‑native staging design (deployment‑safe, bytes never go through the LLM):
BFF `/api/chat` relays each attachment to the MCP `POST /staging/upload` (behind `X‑Api‑Key`), gets a
short `stagingId`, and injects a note telling the agent to call `upload_staged_file` with it. Only the
short id travels through the model.

---

## 3. Brought the whole stack up for a live demo

Order (see `HOW-TO-RUN.md`): **UCEB API :5000 → MCP :5200 (sign in at warm‑up) → dev tunnel
`giant-ant-2f6br43` (fixed URL `https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`) → BFF :5010 →
extension**. All four local processes verified LISTENING; MCP warm‑up completed; tunnel hosting on the
fixed URL. Demo entry point: sign in in the extension, then "check the health status".

---

## 4. Why the chatbot felt slow (diagnosis)

The BFF log made it clear — the delay is **cloud‑side**, not the local stack:

| Message | BFF → Agent Builder invoke time |
|--------:|--------------------------------:|
| 1 | **135 s** |
| 2 | **92 s** |
| 3 | **timed out at 180 s → 504** |

That timer only covers the round trip to `api.agents.ai.dev.app.hyland.com`, i.e. the **Agent Builder
orchestrator + model** thinking and calling tools. Local pieces were fast (staging upload to the MCP =
**26 ms**). Three things stacked up:

1. **The Hyland cloud agent is slow right now** (90–135 s/turn) — outside our control; latency
   fluctuates.
2. **Cold UCEB API** — each tool call took **6–8 s** (`upload` 6.4 s, `complete` 8.4 s, `attach`
   3.8 s). The API also spams **failed OpenTelemetry/Datadog exports** to `127.0.0.1:8126` (2 s per
   failed attempt) — noise/overhead. Warms up after a few calls.
3. **Retry loop from a wrong content type** — the attach returned
   `400 EcmContentTypeName does not match any configured document type` because
   `ecmContentTypeName = dev-test-account` isn't a configured document type. Each failed try = another
   slow cloud→tunnel→MCP→UCEB round trip, pushing the turn past 180 s.

**Demo mitigations:** lead with a light single‑tool prompt ("check the health status") to warm up; use
the **correct** document type (`prescription` for the dev `account` mapping) to avoid the retry loop;
one instruction per message; avoid leading with the heavy upload path. (Optional: raise BFF invoke
timeout 180 s → 300 s so a slow‑but‑valid reply still lands instead of 504‑ing.)

---

## 5. NEW manager requirement — context‑aware chatbot ("content‑in‑context")

The manager showed reference designs (Outlook add‑in, ServiceNow, Workday) of a **Hyland Enterprise
Content / AI Content Assistant** side panel that **reads the business object on the current browser
page** and automatically surfaces the **related documents + actions** (Attach / History / Extract) for
that object — e.g. viewing "MacBook Pro 16 Asset" shows its Purchase Order, Invoice, contract, etc.,
each "Stored in CIC Workspace".

**Goal for our extension:** instead of a blank chat, **detect the business object from the active tab**
(URL + DOM), then show the documents UCEB has for that `businessObjectId` / `businessObjectType`, plus
quick actions. See the research + plan captured with this day's work.
