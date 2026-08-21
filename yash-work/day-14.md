# Day 14

_Focus: recap of the **in-panel document viewer** work finished on Day 13. Wrote down what the
viewer does, the two PDF.js bugs that stopped Workday documents from rendering, how I fixed each
one, and how the whole path hangs together now._

---

## 1. What I set out to do (recap from Day 13)

Make a document open **inside the extension side panel** when I click it in the Documents list —
a PDF on a `<canvas>`, an image in an `<img>` — the same in-panel experience for **every** LOB.

Salesforce documents already rendered in-panel. **Workday documents didn't** — they just showed
**"Preview not available."** So Day 13 was all about finding out why and fixing it.

---

## 2. How the viewer works

When I click a document, the extension asks the BFF for the raw content, and the BFF proxies to the
MCP server:

```
popup.js  openDocumentPreview(doc)
  -> agent.js  fetchDocumentContent(docId)
       GET  https://localhost:5010/api/document/content?docId=...      (BFF)
  -> BFF proxies to MCP  /documents/{id}/content                       (over the dev tunnel)
  -> MCP returns the bytes for that LOB
```

The **Content-Type** of the MCP response decides what happens next:

| MCP response Content-Type | Meaning                    | Extension does                 |
|---------------------------|----------------------------|--------------------------------|
| `application/json`        | no raw bytes → `{viewerUrl}` | show fallback "Open in viewer" |
| `application/pdf`         | raw PDF bytes              | render with PDF.js on `<canvas>` |
| `image/png`, `image/jpeg` | raw image bytes           | set `<img>.src` (no PDF.js)    |

Key point: the **content type is the single source of truth**, and it's preserved on every hop —
MCP captures the real `MediaType` from the HxP download, the BFF forwards it verbatim
(`Results.File(bytes, contentType)`), and the extension trusts `result.contentType`.

For Workday specifically, MCP can't use UCEB to get the bytes (the wdx token 403s on the CIC-only
download policy), so it calls the **HxP content platform directly**
(`.../api/download/{docId}/sysfile_blob`) with the user's bearer token — that's what actually
returns the file bytes.

---

## 3. The two bugs I fixed

The byte-download was already working — the failures were **entirely in PDF.js on the client**.

### Bug 1 — PDF.js v6 won't take a bare URL string

Error in the console:

```
getDocument - expected either 'data', 'range', or 'url' parameter.
```

The bundled PDF.js is **v6.2.108**. In v6, `getDocument()` no longer accepts a plain URL string —
it needs an options object. The old code passed the blob URL directly, so it threw right away and
dropped to the "Preview not available" fallback.

Fix (`popup.js`, `renderPdfPage`):

```js
// before
const loadingTask = pdfjsLib.getDocument(objectUrl);
// after
const loadingTask = pdfjsLib.getDocument({ url: objectUrl });
```

### Bug 2 — images were being sent into PDF.js

Once Bug 1 was fixed, the error **changed** to:

```
InvalidPDFException
```

The test document was `Hyland Logo (2).png` — a **PNG, not a PDF**. The viewer was throwing
**every** non-JSON document at PDF.js no matter its real type. Salesforce "worked" only because
those docs happened to be PDFs; the first Workday doc I tried was an image, so PDF.js correctly
said the bytes weren't a PDF.

Fix (`popup.js`, `openDocumentPreview`) — branch on the content type:

```js
if (result.type === "bytes") {
  const ctype = (result.contentType || "").toLowerCase();
  if (ctype.includes("image")) {
    // image/png, image/jpeg, … -> render directly in <img>
    els.viewerImage.src = result.objectUrl;
    els.viewerImage.hidden = false;
  } else {
    // PDF (or unknown) -> PDF.js
    await renderPdfPage(result.objectUrl, 1);
  }
}
```

No server change was needed — the real content type already flows all the way through.

---

## 4. Result

- Workday **PDFs** render on the `<canvas>` in-panel, exactly like Salesforce.
- Workday **images** (PNG/JPEG) render in the `<img>` in-panel.
- Documents with no downloadable bytes still fall back to the first-party viewer window.

Only `popup.js` / `agent.js` changed, so the fix ships by just reloading the extension at
`chrome://extensions` — no server or tunnel restart.

I committed and pushed all of this on Day 13
(`Fix in-panel document viewer for Workday (PDF.js v6 + image content types)`), including the
bundled PDF.js lib and `day-13.md`.

---

## 5. Takeaways

- Bundled **PDF.js is v6.2.108** — always pass `getDocument({ url })` / `{ data }`, never a bare
  string.
- **Never assume a document is a PDF.** Branch on `Content-Type`: `image/*` → `<img>`,
  PDF → PDF.js, JSON → viewer-URL fallback.
- Capturing the content type at the source (HxP download `MediaType`) and forwarding it unchanged
  is what lets the client render correctly without guessing.

---

# Day 14 (cont.) — Workday WID auto-resolution

_Focus: make a Workday worker's **UCEB document context load automatically**. The user should just
open a worker in Workday and see that worker's Hyland documents — **no typing, no buttons**. To do
that I had to (1) turn a worker's **name / Employee ID** into the real 32-hex **WID** that UCEB keys
documents on, and (2) scrape that identity straight off the Workday page._

---

## 6. The problem

UCEB stores a worker's documents under their **WID** — a 32-hex id like
`4bc212416f234ba1b4749e4bebe4c2eb`. But a normal Workday worker page never shows that WID. Its URL is
an **instance ref** (`.../d/inst/1$37/247$21.htmld`) which is **not** the WID and resolves to no real
worker. The page only shows human things: the worker's **name** ("Anthony Rizzo 1") and, on the **Job
Details** tab, the **Employee ID** (`21021`).

So the flow had to be: scrape name/Employee ID from the page → look up the WID → drop it into context.

---

## 7. BFF endpoint — `GET /api/worker/resolve`

New endpoint in `bff/Program.cs`. Requires a signed-in session (`X-BFF-Session`), takes a `q` query
(name **or** Employee ID), calls the Workday **Staffing REST API**, and returns the matching worker(s):

```
GET /api/worker/resolve?q=Anthony%20Rizzo      (or q=21021)
  -> GET {StaffingBaseUrl}/workers?search={q}&limit=20   (Bearer <Workday token>)
  -> { query, total, wid, matches: [ { wid, name, employeeId, businessTitle, supervisoryOrganization } ] }
```

`wid` is set when it's **unambiguous** — an exact Employee ID match, or a single lone result —
otherwise it's null and the caller gets the full `matches` list to disambiguate.

Supporting pieces added:

- **`WorkdayOptions`** — bound from the `"Workday"` config section (`StaffingBaseUrl`, `TokenUrl`,
  `ClientId`, `ClientSecret`, `RefreshToken`, `Scope`). Secrets live in **user-secrets**, not
  `appsettings.json`.
- **`GetWorkdayAccessTokenAsync`** — gets an OAuth token: an `X-Workday-Token` header override (for
  testing) → an in-memory **`WorkdayTokenCache`** → otherwise a `refresh_token` grant (HTTP Basic
  `clientId:clientSecret` to `TokenUrl`), cached until ~60s before expiry.
- **`WorkerMatch`** record — maps the Staffing JSON (`id` → wid, `descriptor` → name, `workerId` →
  employeeId, `primaryJob.businessTitle`, `primaryJob.supervisoryOrganization.descriptor`).

---

## 8. Getting a Workday credential (and the 401 that ate an hour)

The Workday Staffing API is a **separate credential** from the UCEB wdx token. I registered an
**API Client for Integrations** ("Hyland WID Lookup") in tenant `hylandbow_wcpdev1`:

- Grant: **Authorization Code Grant**, Bearer access token, **non-expiring refresh token**.
- Scopes: **Staffing** + **Contact Information**.
- Copied the **Client ID / Client Secret**, generated a **non-expiring refresh token**, and set all
  three into BFF **user-secrets**.

The token endpoints (from the tenant's "View API Clients" page) went into `appsettings.json`:

```
Token:    https://wcpdev-services1.wd101.myworkday.com/ccx/oauth2/hylandbow_wcpdev1/token
Staffing: https://wcpdev-services1.wd101.myworkday.com/ccx/api/staffing/v7/hylandbow_wcpdev1
```

**The blocker:** every token exchange returned `401 {"error":"invalid_client"}` — with **both**
HTTP Basic auth *and* client creds in the body. `invalid_client` is specifically a **client
authentication rejection**, not a bad refresh token. The clue was a tenant banner:

> _"API clients are currently disabled. Run Tenant Setup - Security to enable them."_

**Fix:** in Workday, ran **`Edit Tenant Setup - Security`** and enabled OAuth 2.0 / API clients.
After that the exact same request returned a **valid access token** immediately.

Then I verified the Staffing search directly — `?search=Anthony Rizzo` returned exactly one worker
with `id`=`4bc212416f234ba1b4749e4bebe4c2eb`, `workerId`=`21021`, and the JSON shape matched
`WorkerMatch` one-for-one. End-to-end confirmed.

> Note on ownership: the credential was registered under user **lmcneil**, but that's just the account
> that authorized the integration. The client can look up **any** worker its Staffing scope allows
> (e.g. **arizzo**) — the credential owner and the looked-up worker are independent by design.

---

## 9. Auto-detection — zero typing

First cut added a "Find WID" box in the panel. The user (rightly) didn't want to type anything, so I
made it fully automatic and **removed the box entirely**.

**`browser-extension/src/content/detector.js`** — `detectWorkerProfile()`:

- Dropped the old `/inst/` URL fallback — it produced junk ids like `1-37-247-21` that matched no
  real worker.
- **Primary signal:** scrape the **Employee ID** from the page text (the `Employee ID 21021` on the
  **Job Details** tab) — `body.innerText.match(/\bEmployee ID\b[:#-]?\s*(\d{3,})/i)`.
- **Fallback:** the worker's **name** from the profile/sidebar header (several
  `data-automation-id` selectors, since it varies by Workday build).
- Emits a **`needsResolve`** context (name/Employee ID, **no** WID yet) instead of a business object
  id. `detect()` passes those through and `contextKey` keys on the query so switching workers
  re-fires.

**`browser-extension/src/popup.js`** — `loadContextPanel()`:

- New `needsResolve` branch: automatically calls `resolveWorker(query)` → on a lone/exact match it
  runs `applyResolvedWorker()`, which **pastes the real WID into the context** and loads that
  worker's documents. Ambiguous names show a one-click pick-list.
- The manual "Find WID" form is now **hidden in every branch** — no button, no input.

`agent.js` got a small `resolveWorker(query)` helper (GET the BFF endpoint with the session header).

---

## 10. Result

Open a worker's **Job Details** tab in Workday → the panel auto-scrapes `Employee ID 21021` →
resolves it to WID `4bc212416f234ba1b4749e4bebe4c2eb` → shows that worker's Hyland documents. No
typing, no buttons.

Extension-only changes ship with a `chrome://extensions` reload. The BFF change needed a restart to
pick up the new Workday endpoints (which also wipes sessions, so re-sign-in in the panel).

---

## 11. Takeaways

- **`invalid_client` ≠ bad token.** It means the *client* was rejected — here the whole tenant had
  API clients disabled (`Edit Tenant Setup - Security`). Read the tenant banners.
- The Workday **Staffing API is a separate OAuth credential** from the UCEB wdx token; the credential
  owner is independent of the worker you look up.
- The **Employee ID lives on the Job Details tab**, not the Summary tab — it's the most reliable,
  unambiguous signal, so prefer it over the name.
- A Workday worker URL's **instance ref is not the WID** — you have to resolve name/Employee ID
  through the Staffing API to get the id UCEB actually keys documents on.