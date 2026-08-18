# Day 9

_Focus: try to **embed the Hyland document viewer inside the extension side panel** (iframe), debug a
**misleading fallback bug**, prove the viewer actually loads, then swap to the **Standalone CIC Viewer**
and hit a hard **Content Security Policy (`frame-ancestors`) block** — which turns into a conversation
with the **viewer team** about allow-listing the extension._

---

## 1. Goal — show documents inside the panel

Instead of only opening documents in a new tab/window, we tried to render them **inside the extension
side panel** using an `<iframe>`. Added a viewer overlay in `popup.html` / `popup.css` / `popup.js`:

- `#viewerOverlay` with a top bar (Back, title, open-in-tab) and an iframe `#viewerFrame`.
- A loading spinner (`#viewerLoading`) shown while the iframe loads.
- A fallback panel (`#viewerFallback`) offering "Open in viewer window" / "open in a new tab" if the
  iframe doesn't come up in time.
- `openViewer(url, title)` sets the src, shows the spinner, starts a 30s timer to `showViewerFallback()`;
  `onViewerLoaded()` clears the timer and hides the spinner on the iframe `load` event.

---

## 2. The misleading fallback bug (CSS `display` vs `hidden`)

**Symptom:** the fallback message ("this document can't be shown inside the panel") appeared **instantly
and permanently**, even though the viewer had actually loaded fine underneath.

**Diagnosis:**

- `curl` on the IAM login page confirmed it *does* send `X-Frame-Options: SAMEORIGIN` +
  `frame-ancestors 'none'` — but that only matters if the viewer redirects to login.
- The panel DevTools console showed **no "Refused to display" errors**, and the Network tab showed
  **every request returning 200** (token, openid-config, jwks, the document blob, etc.) — so auth
  worked in the iframe and the document downloaded.
- Console even logged `[viewer] iframe 'load' fired`, yet the fallback still showed.

**Root cause:** `.viewer__fallback` had `display:flex` in CSS, which **overrides the HTML `hidden`
attribute**. So setting `viewerFallback.hidden = true` never actually hid it — the opaque fallback
(`position:absolute; inset:0`) just sat on top of a working viewer.

**Fix (`popup.css`):** added `.viewer__fallback[hidden]{display:none}` (and the same for
`.viewer__loading[hidden]`). Lesson: an element with an explicit CSS `display` value ignores the HTML
`hidden` attribute unless a `[hidden]{display:none}` rule exists.

Also along the way: bumped the fallback timer 8s → 30s, added the spinner, and made the fallback
non-destructive (it no longer hides the frame) so a slow load doesn't look broken.

---

## 3. Manager questions (plain-English answers)

- **"Can't you pass the authentication token to the iframe?"** — No. An iframe navigation (a GET) can't
  carry an `Authorization`/`Bearer` header; only `fetch`/XHR can. The Hyland viewer authenticates via a
  first-party **session cookie**, not a Bearer token, and the plugin's token is for the Agent
  Builder/UCEB audience, not the viewer app. Real fixes are Hyland-side (a tokenized/signed viewer URL,
  or partitioned cookies).
- **"Is there a query param to collapse the metadata panel by default?"** — It's Hyland's hash-routed
  SPA; any such param isn't documented on our side and would go after the `#`. Best to test by manually
  collapsing and watching the URL, or ask the viewer team. Wiring it is a one-line change once we know
  the param name.

---

## 4. Swapping to the Standalone CIC Viewer

Repointed the doc-row click from the HxViewer URL (via BFF `/api/viewer`) to the **CIC Viewer**:

```js
const CIC_VIEWER_BASE = "https://cic-viewer.dev.app.hyland.com/#/documents/";
const CIC_VIEWER_ENV_KEY = "appintel-dev-test";
function buildCicViewerUrl(docId) {
  return `${CIC_VIEWER_BASE}${encodeURIComponent(docId)}?envKey=${encodeURIComponent(CIC_VIEWER_ENV_KEY)}`;
}
```

**Result:** "cic-viewer.dev.app.hyland.com refused to connect." The console showed the real reason:

```
Framing 'https://cic-viewer.dev.app.hyland.com/' violates the following Content Security Policy
directive: "frame-ancestors https://*.experience.hyland.com https://*.app.hyland.com".
The request has been blocked.
```

The document itself loads (the iframe even fires `load`), but the CIC Viewer's **`frame-ancestors`**
policy only allows it to be embedded by pages served from `*.experience.hyland.com` or
`*.app.hyland.com` — i.e. apps running inside the CIC/Hyland platform. Our extension runs from a
`chrome-extension://…` origin, which isn't on that list, so the browser blocks the frame.

**Takeaway:** this is not something we can fix in the extension. It needs the viewer team to either add
our origin to their `frame-ancestors` allow-list, or provide an embeddable/tokenized viewer URL.

---

## 5. Viewer-team conversation — allow-listing the extension

The viewer team confirmed the strict CSP is expected and offered to add our app URL to their approved
hosts. Key point raised back to them:

- Our PoC isn't a normal web app on an `https://` domain — it's a **browser extension** (Chromium side
  panel), so the embedding origin the browser reports is
  `chrome-extension://hmeanojcjlkalipmknanlcdimhhfjneb`.
- Open question to them: can their CSP allow-list accept a `chrome-extension://<id>` origin rather than
  an `https://` host?

**Is the extension ID constant?** Not yet. There's **no `key` field** in `manifest.json`, so this is an
unpacked/dev build and Chrome derives the ID from the load path. That means:

- It stays the same while loaded unpacked from the same folder on the same machine.
- It changes on a different path or machine.
- Once published to the Chrome Web Store, the store assigns a **permanent (but different)** ID.

To pin a stable value we can add a fixed `key` to the manifest, which locks the extension ID across
machines and reloads — the value we'd then give the viewer team for their allow-list.

---

## 6. State at end of day

- In-panel iframe viewer + spinner + fallback: implemented, CSS root-cause bug fixed (0 errors).
- CIC Viewer embedding: **blocked** by `frame-ancestors` CSP; waiting on the viewer team to either
  allow-list our `chrome-extension://` origin or provide an embeddable URL.
- Reliable demo path for now: open documents in a dedicated viewer **window/tab** rather than in-panel.
- Files touched: `browser-extension/src/popup.html`, `popup.css`, `popup.js` (still uncommitted; a few
  `[viewer]` debug `console.log`s remain).

---

## 7. CIC Viewer follow-up — the "bravo" allow-listed instance

The viewer team offered to help. Progression:

1. **They set up a dedicated instance ("bravo")** that allow-lists our extension origin:
   `https://bravo.cic-viewer.sandbox.app.hyland.com/#/`. Pointed the code at it
   (`CIC_VIEWER_BASE`). The `frame-ancestors` block **went away** — the extension can now embed bravo.
2. **New error inside the iframe:** the viewer's own login couldn't complete. Console showed
   `Executing inline script violates the CSP directive "script-src 'self'"` on the viewer's
   `callback?code=…` page, plus a `401` on `/user`. So the viewer loads but can't sign itself in when
   embedded.
3. **They sent test-user credentials + an example doc link via Kiteworks.** The example link revealed
   the real working values — note it points at the **dev** host with a **different envKey**:
   `https://cic-viewer.dev.app.hyland.com/#/documents/61b29d09-…?envKey=hxp-inc-unstable`.
   So the correct key is **`hxp-inc-unstable`** (not `appintel-dev-test`). Updated
   `CIC_VIEWER_ENV_KEY` accordingly (kept the bravo host, since only bravo allow-lists our origin).
4. **The example doc displays fine standalone** on the **dev** host with the test user.

### The core gap found

- **Works:** dev host (`cic-viewer.dev.app.hyland.com`) + `hxp-inc-unstable` + test user → document renders.
- **Needed for the plugin:** bravo host (`bravo.cic-viewer.sandbox.app.hyland.com`) — because **only
  bravo allow-lists the extension origin** for iframe embedding.
- **But bravo fails:** even **standalone** (no iframe), same doc ID + same envKey + same test user, bravo
  returns **"You are not authorised to perform this operation"** and logs you out (logout goes to
  `auth.iam.dev.experience.hyland.com` — dev IAM, so both hosts use the same IAM).

So: **the host that can be embedded (bravo) doesn't authorise; the host that authorises (dev) can't be
embedded.** That's the gap handed back to the viewer team.

Extra findings along the way:
- In the **panel/iframe**, bravo throws **"Missing PKCE code_verifier in sessionStorage"** + `401` — the
  viewer stashes its PKCE `code_verifier` in `sessionStorage`, which doesn't survive the login redirect
  inside the embedded/partitioned iframe context. Viewer-side OAuth bug, not fixable from the extension.
- **Browser mismatch trap:** logging in as the test user in **Edge** doesn't help the plugin, which runs
  in **Chrome** — the iframe uses Chrome's cookies. Must sign in as the test user in the **same** browser
  as the extension.
- **Mentor's note:** dev and bravo are identical except **CORS + CSP** changes — auth handling is the
  same. So the bravo `401`/"not authorised" is most likely a **side effect of the new CORS config**
  (the `/user` or token call being blocked cross-origin), not a real authorization difference. Suggested
  they check CORS headers/preflight on `/user` + token endpoints for the viewer origin; offered a HAR.
- The bravo CORS/CSP change is in PR
  `https://github.com/HylandExperience/viewer-standalone/pull/604` (GitHub was having an outage at the
  time, so couldn't review it).

**Current status:** CIC Viewer still not renderable in-panel — blocked on the viewer team resolving the
bravo authorise/CORS gap (or allow-listing our origin on the working dev host). Code currently points at
bravo + `hxp-inc-unstable`. Reliable demo path remains open-in-tab/window.

---

# Workday integration — research + plan

_Second focus of the day: **research the Workday integration**. Understand end‑to‑end how the
**Salesforce** plugin was wired up (MCP → UCEB → plugin), then work out what changes for **Workday**:
the **new auth client + "Hyland for Workday" application routing**, how to **configure Workday business
object types**, and — the real gotcha — **how to get the record id / context on Workday when URL
parsing (which we rely on for Salesforce) does not work.**_

> This half of the day is **research + a plan**, not code changes. It turns the mentor conversation
> about Workday into concrete next steps.

---

## 7. Recap — how the whole thing hangs together (so the Workday delta is clear)

The stack has four moving parts:

```
Browser plugin (side panel)  →  BFF (:5010)  →  Agent Builder (cloud)  →  dev tunnel  →  MCP server (:5200)  →  UCEB API (:5000)  →  Hyland CIC / HFS content
        │                          │                                                         │                        │
   detects the record       holds OAuth secret +                                     MCP "tools" the           content‑services broker:
   on the page, shows        per‑user IAM tokens,                                     agent calls               upload / list / view / config
   its documents             proxies chat + upload                                   (list_documents, …)
```

Two things matter for Workday:

- **UCEB is a content broker.** It never creates the Salesforce/Workday *record* — it files documents
  **under a folder named after the `businessObjectId`**. The HFS layout is
  **`{appKey}-documents/{businessObjectId}/<docs>`** and the per‑id folder is **auto‑created on first
  upload** (`DocumentArchiveService` → `EnsureFolderPathExists`). We never create folders by hand.
- **`{appKey}` = the LOB (line of business) baked into the token.** `IAuthService.GetAppKey(token)`
  derives it from the signed‑in identity. For Salesforce today that resolves to the HFS folder
  (`hfs-documents/…`). **This is the hook that makes Workday routing automatic** — see §9.

---

## 8. How the **Salesforce** plugin was added (the template we copy for Workday)

Adding Salesforce took **four** distinct pieces. Workday needs the same four, with different values.

### 8.1 Page detection — read `{type, id}` from the URL
`browser-extension/src/content/detector.js`, `detectSalesforce()`:

```js
// Salesforce Lightning: .../lightning/r/<Object>/<RecordId>/view
const m = loc.pathname.match(/\/lightning\/r\/([^/]+)\/([a-zA-Z0-9]{15,18})(?:\/|$)/);
// → rawType = <Object> (e.g. "Account"), businessObjectId = <18‑char RecordId>
```

Salesforce is easy because **both the object type and the record id are literally in the URL path**
(`/lightning/r/Account/001XXXXXXXXXXXXXXX/view`). `SITE_TYPE_MAP` normalizes the sObject name
(`account`, `contact`, `opportunity`, `case`, `lead`, `campaign`) to the UCEB `businessObjectType`.
The content script publishes `{businessObjectType, businessObjectId}` to the background worker; the
side panel calls `list_documents(type, id)` and renders the record's docs.

> **This URL trick is exactly what breaks on Workday (§10).**

### 8.2 `manifest.json` — host + content‑script matches
Salesforce hosts (`*.lightning.force.com`, `*.salesforce.com`, `*.force.com`) are added to both
`host_permissions` and `content_scripts.matches`, so `detector.js` runs on those pages. **Workday
hosts (`*.workday.com`, `*.myworkday.com`) are already present** in the manifest.

### 8.3 UCEB **business object config** — map the type so it can be uploaded/listed
`add_business_object_config(busObject, ecmContentTypeName, displayColumn)` (an **upsert**) maps a
business object type → an ECM content type + a display column. For Salesforce `account` we use
`add_business_object_config(busObject=account, ecmContentTypeName=dev-test-account,
displayColumn=hfs_Name)`. Without this mapping, uploads fail (`23400 EcmContentTypeName does not
match any configured document type`) and lists return nothing.

### 8.4 Auth / routing — the token's LOB decides the folder
The MCP signs in as the user via a **confidential Auth‑Code+PKCE client**
(`wsc-dc7e0e46‑06d2‑4166‑874f‑149dc8614012`, id+secret in user‑secrets). That client is registered
under a Salesforce‑flavoured **Application** in IAM, so its token routes documents to the HFS
(`hfs-documents`) folder. **This is the piece Workday changes the most** (§9).

---

## 9. Workday auth — **new client + "Hyland for Workday" application** (from the mentor)

Mentor's guidance, distilled:

> "For Workday you should **create a client id and secret** and **select application _Hyland for
> Workday_** in the admin portal. Use auth using this client. This will **automatically route the
> documents to the workday‑specific folder**. Don't create folders manually — these folders are
> sensitive to user permissions, so let UCEB create/update them automatically."

What this means in our architecture:

- The **Application** you pick for the IAM client is what stamps the **LOB/appKey** into the token.
  Pick **Hyland for Workday (HFW)** → `GetAppKey(token)` resolves to the Workday LOB →
  `DocumentArchiveService` files everything under the **`hfw-documents/…`** (Workday) folder tree
  instead of `hfs-documents/…`. **No code change, no manual folders** — the routing is entirely a
  function of which app the token belongs to.
- So: **do NOT create a "Hyland for Workday" folder by hand** (the mentor was explicit). Just
  authenticate with an HFW client and upload — the folder appears with the right permissions.

### What to configure when creating the client (the "how" the mentor didn't spell out)
Create a **confidential Web Server Application** client in the **DEV** IAM admin portal
(`https://admin.dev.app.hyland.com/`), mirroring the working MCP Salesforce client but with
**Application = Hyland for Workday**:

1. **Application:** `Hyland for Workday` (this is the routing selector — the whole point).
2. **Grant types:** Authorization Code **+ PKCE + Refresh Token** (we do per‑user interactive login,
   same as the current MCP client — machine‑to‑machine was explicitly rejected earlier because every
   user would share one identity/permission set).
3. **Redirect URI (exact):** `https://uceb-mcp-local.dev.hyland.com:5005/callback`
   (the MCP `LoginCallbackServer` loopback; needs the hosts entry
   `127.0.0.1 uceb-mcp-local.dev.hyland.com`).
4. **Scopes:** same set the MCP already requests —
   `openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp`.
5. Assign yourself to the group/role that has **CanWriteUCEBConfiguration** (needed for
   `add_business_object_config`) and content read/write.

**Then, to run the MCP against Workday**, point it at the new client:
```powershell
# in ...\Hyland.Experience.UCEB.McpServer
dotnet user-secrets set "Auth:ClientId"     "<HFW client id>"
dotnet user-secrets set "Auth:ClientSecret" "<HFW client secret>"   # type the secret yourself; never paste it here
```
Restart the MCP (`dotnet run -- --http`), complete the browser sign‑in at warm‑up, confirm
`[Warmup] Pre‑authentication complete`. From that point every upload lands in the Workday folder.

> ⚠️ **Only one LOB per running MCP.** The appKey comes from the single signed‑in token, so one MCP
> process serves **either** Salesforce **or** Workday, not both at once. For a combined demo you'd run
> two MCP instances (different client secrets / ports / tunnels) or switch the user‑secret + restart
> between demos. Worth raising with the mentor if both are needed simultaneously.

---

## 10. **The Workday record‑id problem — URL parsing won't work**

The part flagged up front: _"for record ids/context the browser depends on URLs for Salesforce, but
that won't work on Workday — figure out a way."_ Correct, and here's exactly why.

### Why the Salesforce URL trick fails on Workday
- Salesforce puts a clean, stable, human‑usable id in the path:
  `/lightning/r/Account/001XXXXXXXXXXXXXXX/view`.
- **Workday URLs are opaque OMS (Object Management System) instance ids**, e.g.
  `https://wd5.myworkday.com/<tenant>/d/inst/1$37/9925$198.htmld` or `#TASK_...` fragments. The
  `9925$198`‑style tokens are **internal Workday WIDs**, they are **not the Employee ID**, they are
  **not human‑meaningful**, and they can **change per navigation task**. Using one as the
  `businessObjectId` would produce an unstable, meaningless folder name — re‑opening the same worker
  could map to a different id and "lose" the previously uploaded docs.
- Our current `detectWorkday()` is a naive `loc.hash` regex placeholder — it will grab garbage, not
  the employee id.

### The fix: **detect from the DOM, not the URL** (Workday‑specific adapter)
The reliable, human‑meaningful identifier on a Workday worker page is the **Employee ID** rendered in
the page, so the Workday adapter should **scrape the DOM** instead of the URL:

1. **Confirm we're on a worker/employee profile** (host is `*.workday.com`/`*.myworkday.com` and the
   page shows a worker header).
2. **Read the Employee ID from the DOM.** Workday tags elements with stable **`data-automation-id`**
   attributes, and worker fields are consistently **labelled**. Strategy (most‑stable first):
   - **Label‑text lookup (most robust across tenants/versions):** find the element whose visible text
     is `Employee ID` (or `Worker ID`), then read its adjacent value node.
   - **`data-automation-id` fallback:** the worker header name is
     `data-automation-id="pageHeaderTitleText"`; field value cells are labelled — used to anchor the
     value cell next to the "Employee ID" label.
   - **Last‑resort:** the URL WID (`9925$198`) — but flag it as unstable; prefer never to rely on it.
3. Set `businessObjectType = "employee"`, `businessObjectId = <Employee ID>` (e.g. `21001`).
   Employee IDs are short → **no hashing needed** (unlike the 150‑char Outlook ids from Day 7).
4. Because Workday is a heavy SPA that swaps the worker asynchronously, drive re‑detection off the
   existing **MutationObserver + history‑API hooks** (already in `detector.js`), plus a short retry/
   poll until the Employee ID field has rendered.

**Manual fallback (already built):** the context panel's upload section has an editable
**Record id** field (`#uploadRecordId`). If DOM detection can't find the Employee ID on an unusual
Workday view, the user can type it in and still upload/list — so the demo is never blocked. (Day 8.)

> **Net rule of thumb:** Salesforce = **URL‑driven** detection; Workday = **DOM‑driven** detection
> (Employee ID), because Workday's URL carries only opaque OMS ids. Same content script, different
> adapter strategy.

---

## 11. Workday **business object types** — configure them ourselves (from the mentor)

Mentor's guidance, distilled:

> "When you use a token for HFW (Hyland for Workday), you will need to **configure the business object
> types yourself**. You can hypothetically use **`employee`** as a business object type and the `boId`
> will be the **employee id**. Start with one — Workday has 3 product families: **HR, Financials,
> Student**. For HR, `employee` is a type; for Financials, `Invoice`, `Vendor`; for Student, `Student`,
> `Department`."

Concrete plan:

1. **Start with just `employee`.** Map it with the same upsert tool we use for Salesforce:
   ```
   add_business_object_config(busObject=employee,
                              ecmContentTypeName=dev-test-account,   # clean type: optional hfs_Name, no required fields
                              displayColumn=hfs_Name)
   ```
   `dev-test-account` is our proven‑clean content type (optional `hfs_Name`, **no required fields**),
   so uploads with just a file name succeed and `list_documents` shows the readable name. (We can
   later create a Workday‑specific content type if the demo wants nicer metadata columns.)
2. **`boId` = the Employee ID** read from the DOM (§10). Upload prompt stays the same style:
   `Upload the attached file to this record using document type dev-test-account`.
3. **Add more types as the Workday team confirms them.** The mentor is checking the real Workday
   object list; when it lands, add each with another `add_business_object_config(...)` call and extend
   `SITE_TYPE_MAP` in `detector.js`. Likely candidates by product family:

   | Workday product | Example business object types |
   |---|---|
   | **HR** | `employee` (worker) |
   | **Financials** | `invoice`, `vendor` |
   | **Student** | `student`, `department` |

---

## 12. Workday go‑live checklist (what I'll do next)

- [ ] Create the **DEV IAM confidential client** with **Application = Hyland for Workday**
      (Auth‑Code+PKCE+Refresh, redirect `…:5005/callback`, scopes as §9). — _user_
- [ ] `dotnet user-secrets set Auth:ClientId / Auth:ClientSecret` to the HFW client, restart MCP,
      sign in at warm‑up. — _user_
- [ ] `add_business_object_config(busObject=employee, ecmContentTypeName=dev-test-account,
      displayColumn=hfs_Name)`.
- [ ] **Rewrite `detectWorkday()`** in `detector.js` to **DOM‑scrape the Employee ID** (label lookup
      + `data-automation-id` fallback + SPA re‑detect), set `type=employee`, `id=<Employee ID>`; add
      `employee` to `SITE_TYPE_MAP`.
- [ ] Verify end‑to‑end: open a worker in Workday → panel shows `employee / <id>` → Attach a file →
      it lands in the **`hfw-documents/<id>/`** folder automatically → re‑open the worker → docs
      auto‑list.

---

## Outcome (Workday research)

- **Salesforce → Workday is the same 4‑part recipe** (detect, manifest, business‑object config, auth),
  with two real differences:
  1. **Auth/routing:** a **new confidential client under the _Hyland for Workday_ application** — its
     token's LOB auto‑routes documents to the Workday folder; **never create the folder manually.**
     One LOB per MCP process.
  2. **Record id:** Workday URLs carry only **opaque OMS WIDs**, so the Salesforce URL‑parsing
     approach fails — Workday must **read the Employee ID from the DOM** (label lookup +
     `data-automation-id`), with the panel's manual Record‑id field as the fallback.
- **Business object types** for Workday are **self‑configured** via `add_business_object_config`;
  **start with `employee`** (`boId` = Employee ID), expand to HR/Financials/Student types once the
  Workday team confirms the list.

**No files changed for the Workday work (research).** Next code step: the `detectWorkday()` DOM rewrite
+ the `employee` config mapping, once the HFW client exists.
