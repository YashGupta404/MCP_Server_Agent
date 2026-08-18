# Day 10

_Focus: restart the full demo stack, get blocked by a hard **IAM "Unable to perform authorization"**
error on both the MCP warm-up login and the plugin sign-in, chase it down to a **stuck CIC test-user
session** + a **third-party "Redirect Blocker" Chrome extension**, then **revert the viewer back to
HxViewer** and drop the CIC approach entirely._

---

## 1. Restarting the stack

Started the day with everything down. Brought the stack back up in order:

1. **UCEB API** — `:5000` (Development). Listening.
2. **MCP server** — `:5200` (HTTP) + `:5005` (LoginCallbackServer). Runs a **warm-up interactive
   browser login** at startup (~900s token).
3. **Dev tunnel** — named tunnel `giant-ant-2f6br43` → `https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`.
4. **BFF** — `:5010`.

Two snags on the way up (both easy):

- The **MCP warm-up login timed out** the first time (120s window) because the browser login errored
  (see below). Fix = kill + rerun the MCP so a fresh 120s login window opens.
- The **devtunnel login token had expired** ("Login token expired"). Fix = `devtunnel user login -g`
  (GitHub, logged in as YashGupta404), then `devtunnel host giant-ant-2f6br43`.

---

## 2. The blocker — IAM "Unable to perform authorization"

Both the **MCP warm-up login** and the **plugin sign-in** hit the same IAM error page at
`auth.dev.app.hyland.com/idp/home/error`:

> An error occurred. Please reach out to your system administrator or first line of support.
> Additional details: **Unable to perform authorization.**

In the extension panel this surfaced as **"Sign-in failed: The user did not approve access."** — i.e.
IAM was *auto-denying* the request (returning `access_denied`), not actually asking anyone to approve.

Key trait: it errored **immediately, with no login page**. That means IAM was silently reusing an
existing **SSO session cookie in the browser profile** and rejecting that account.

### 2a. MCP side — fixed by using the right account

The MCP warm-up login was reusing a leftover **CIC test-user** session. Fix:

- Log out at `https://auth.dev.app.hyland.com/idp/Account/Logout`.
- Restart the MCP so a fresh warm-up login opens.
- Sign in with **my own dev account** (NOT the CIC test-user).

Result: `[Warmup] Pre-authentication complete — tool calls will use the cached token.` MCP good.

### 2b. Plugin side — two overlapping causes

The plugin sign-in kept failing with the same error even after MCP was fixed. Chased it via the
IAM tab's DevTools console and found the real culprits:

**Cause 1 — a third-party "Redirect Blocker" Chrome extension.** The console showed:

```
[Redirect Blocker] Stopping to prevent same tab redirects    content.js:148
... chrome-extension://epgmgebeelgaakhaoodlmnimbfemfgdah/dist/content.js:82
```

That extension (`epgmgebeelgaakhaoodlmnimbfemfgdah`) was intercepting and **killing the IAM OAuth
same-tab redirect**, so the login callback never completed → "Unable to perform authorization" /
"user did not approve access". This is NOT our code (grep confirmed we have no "Redirect Blocker" /
`content.js` — ours is `detector.js`). Removed the extension → console went clean.

**Cause 2 — the stuck CIC test-user IAM session in this Chrome profile.** Even with the blocker gone,
it still errored immediately (no login page), because the CIC testing had left a **test-user logged
into this profile's IAM session**. `prompt: "login"` wasn't forcing a fresh login. Fix that finally
worked:

- Hit `https://auth.dev.app.hyland.com/idp/connect/endsession` (and/or `/idp/Account/Logout`).
- Clear cookies for `auth.dev.app.hyland.com` via `chrome://settings/content/all`.
- Click **Sign in** on the plugin → a **real login page finally appeared** → signed in with my dev
  account → **working**.

**Lesson:** none of this was our code or my account entitlement. It was environmental —
(1) a redirect-blocker extension breaking OAuth, and (2) a leftover CIC test-user session in Chrome's
IAM cookies. The CIC experiment is what introduced both.

---

## 3. Decision — revert to HxViewer, drop CIC

The CIC embedding attempt caused more trouble than it was worth (frame-ancestors blocks, bravo env
mismatch, PKCE/sessionStorage issues, and now the auth mess above). Reverted the panel viewer back to
the **original HxViewer in-panel path**, which always worked:

Changes in `browser-extension/src/popup.js`:

- Doc-row click handler now uses the original path again:
  `const url = await openInViewer(doc.docId);` → BFF `/api/viewer` → MCP `open_document_in_viewer`
  → HxViewer URL, opened in the in-panel iframe via `openViewer(url, title)`.
- **Deleted** the CIC constants/helper (`CIC_VIEWER_BASE`, `CIC_VIEWER_ENV_KEY`, `buildCicViewerUrl`).

No CIC code remains in the extension. Verified 0 errors after the edits.

---

## 4. End state

Full stack up and demo-ready:

- UCEB API `:5000`, MCP `:5200` (dev-account token cached), tunnel `giant-ant-2f6br43`, BFF `:5010`.
- Plugin signed in with my dev account.
- Viewer = **HxViewer**, in-panel, CIC removed.

### Takeaways

- "Unable to perform authorization" here = IAM **auto-denying a stale/wrong SSO session**, not a real
  approval prompt. Immediate error + no login page = a leftover session cookie is being reused.
- Third-party browser extensions (redirect blockers / privacy tools) can silently break OAuth by
  stopping the same-tab redirect. Check the IAM tab's DevTools console for foreign `content.js` logs.
- When an OAuth login misbehaves after switching test accounts, **hard-logout + clear the IAM host
  cookies** before blaming code or entitlements.

---
---

# Day 10 (continued) — Getting Workday `list_documents` working

_Focus: switch the MCP to the **Workday** line-of-business in dev-prod and get "list a record's
documents" working for a real employee. Chased the bug through **three wrong guesses**, found the **real
two root causes** (wrong URL prefix + different response shape), fixed both, and got **`list_documents`
returning real Workday employee documents end-to-end** through MCP → UCEB._

---

## 5. Goal — make Workday `list_documents` actually work

Everything so far had been tested against Salesforce/CIC. Now the job was to run the MCP as the
**Workday** LOB in dev-prod and list a real employee's documents.

The setup for a Workday run:

- MCP points at the dev-prod UCEB API, Workday client.
- Sign in as `arizzo@aurahyland.onmicrosoft.com` at startup (warm-up login, token good for 900s).
- Test employee: **Anthony Rizzo**, employee id `21021`, business-object id
  `4bc212416f234ba1b4749e4bebe4c2eb`.

---

## 6. The bug — Workday content calls came back empty / failing

`list_document_types` worked (returned `bp-attachments`, `employee-application`, `new-hire-checklist`),
but listing a specific employee's documents did not. We went through three explanations before landing
on the truth.

**Wrong guess #1 — "it's the system name."** I thought the working call left out a `systemFriendlyName`
and the failing one sent it. Checked the code: **both** calls send the same `systemFriendlyName=CIC`. Not
it.

**Wrong guess #2 — "it's a permission problem."** The mentor and I first agreed it was probably a
role/permission gap for `arizzo` in dev-prod. Also wrong — the token was valid and the account had
access.

**The real answer (from the mentor):** _"This business object and URL is not valid. For Workday, the
base URL is `/bow` instead of `/api`."_ He gave the correct URL:

```
GET https://api.uceb…/bow/core/business-objects/4bc212416f234ba1b4749e4bebe4c2eb/documents?businessObjectType=employee
```

Two things were wrong at once:

1. **Wrong URL prefix.** Salesforce reads documents under `/api/...`; **Workday reads them under
   `/bow/...`**.
2. **Wrong test id.** I'd been using a fake id (`TEST-EMP-001`); the real record id is
   `4bc212416f234ba1b4749e4bebe4c2eb`.

---

## 7. Confirming exactly which routes live where

Rather than trust one URL, I probed the local UCEB API to map which routes exist under `/api` vs `/bow`
(a `401` means "route exists but needs auth", a `404` means "no such route"):

| Endpoint | `/api` | `/bow` |
|---|---|---|
| `core/business-objects/{id}/documents` (list a record's docs) | 401 (exists) | **401 (exists)** |
| `core/document-types` | 401 | 401 |
| `config/solution-configurations/uceb` | 401 | **404 (missing)** |
| `core/documents/upload` | 405 | **404 (missing)** |

**Takeaway:** `/bow` is a **targeted Workday route group for reading a record's documents only**. Config
and upload still live under `/api`. So it's not "Workday = /bow for everything" — only the record-
document read moved.

---

## 8. Fix part 1 — make the base path configurable

Added a setting so the MCP can be told which prefix to use per LOB, instead of hard-coding `api`:

- `UcebMcpOptions.cs`: new `BusinessObjectBasePath` (defaults to `"api"`).
- `appsettings.json`: set `"BusinessObjectBasePath": "bow"` for the Workday run.
- `UcebApiClient.cs`: builds the read/attach URLs as
  `/{BusinessObjectBasePath}/core/business-objects/{id}/documents`.

Salesforce keeps `"api"`; Workday uses `"bow"`. One knob, no code branching.

---

## 9. Fix part 2 — Workday sends back a different shape

After the `/bow` fix, the call returned **HTTP 200** — but then failed while reading the JSON, because
**Workday's response shape is different from Salesforce's.**

- **Salesforce/CIC:** `data` is an object → `{ columns: [...], documents: [...] }`.
- **Workday:** `data` is a **flat list** of documents, each carrying a `simpleDocumentAttributes` bag
  (`firstName`, `lastName`, `department`, `manager`, etc.).

Fix: made `ListDocumentsAsync` **shape-aware** —

- If `data` is a **list** → it's Workday → run new `ParseWorkdayDocumentList(...)` which flattens each
  document's attributes into the same internal model the tool already prints (dropping internal `sys*`
  fields and tidying attribute names for display).
- If `data` is an **object** → it's Salesforce → parse as before.

One tool now handles both LOBs without the caller knowing the difference.

---

## 10. Result — it works end-to-end

Rebuilt (0 errors), restarted the MCP, signed in, and ran `list_documents` for the real employee
**through the full MCP → UCEB path** (not a direct endpoint call). Output:

```
Found 13 document(s) for '4bc212416f234ba1b4749e4bebe4c2eb' (type 'employee'):
- docId: 9e7b7843-… (Name=…-new-hire-checklist…, Type=new-hire-checklist, department=Information Technology,
  employeeId=21021, firstName=Anthony, lastName=Rizzo, manager=Oliver Reynolds, createdBy=Anthony Rizzo)
- docId: 44c2b343-… (Type=employee-application, …)
- … 11 more (new-hire-checklists, employee-applications, bp-attachments)
```

Real documents, real employee, clean formatting. **Workday document listing is done.**

---

## 11. A good question that came up — "isn't UCEB supposed to hide this?"

Fair point. UCEB is meant to be the **one layer that unifies the LOBs**, so ideally the MCP would call
**one** URL with **one** response shape and never care whether it's Workday or Salesforce underneath.
Today we had to absorb **two** LOB differences ourselves (the `/bow` prefix and the array shape), which
means UCEB isn't fully normalizing Workday yet. Flagged as a question for the mentor: _should the MCP
keep handling these per-LOB differences, or is there a unified UCEB endpoint we should call instead?_

---

## 12. Still open

- **Upload for Workday is unknown.** The read path is solved, but uploading a document to a Workday
  record isn't — `/bow/core/documents/upload` = 404 and `/api/core/documents/upload` = 405. We need the
  mentor to give us the **correct Workday upload route + payload format** so we can point the MCP's
  upload code at it (same way we learned `/bow` for reads). This is the current blocker.
- **Browser detector.** `detectWorkday()` in the extension still needs to read the real employee record
  id from the Workday page (currently a placeholder).
- **One LOB per MCP process.** This MCP is now the Workday build (`BusinessObjectBasePath=bow`).
  Switching back to Salesforce means setting it back to `api` and restoring the Salesforce client
  id/secret.

---

## 13. How much has Workday progressed? (plain language)

- **Before today:** Workday could only list document *types* (the catalog). Listing an actual employee's
  documents failed, and we didn't know why.
- **After today:** We can **list a real Workday employee's documents end-to-end**, cleanly formatted,
  through the proper MCP → UCEB path.
- **In simple terms:** the **"read / see documents" half of Workday now works.** The **"upload
  documents" half is not done yet** — it's waiting on the correct upload URL from the mentor. So Workday
  is roughly **half integrated**: reading works, writing (upload) is the next milestone.

---
---

# Day 10 (continued) — Upload payload found + a login/account mess

_Focus: track down the Workday **upload** contract (got the metadata payload from a mentor's Confluence
page), then fight a long **login/account** problem caused by mixing a Microsoft test account (Anthony
Rizzo) with a native Hyland dev account — ending in an nginx **"cookie too large"** 400 that a cookie
purge fixed._

---

## 14. Workday upload — found the metadata payload (but not the endpoint yet)

A second Workday mentor pointed to a Confluence page: **"CIC: Document Metadata Payload - Employee
(new-hire-checklist)"**. It gives the exact JSON body for creating a Workday document:

```
{
  "businessObjectType": "employee",
  "documentId": null,
  "createNewVersion": false,
  "documentType": { "id": "new-hire-checklist" },
  "businessObjectAttributes": [
    { "id": "hcmisbebo_businessObjectId", "name": "BUSINESS_OBJECT_ID", "value": "4bc21241…", "dataType": "string", … },
    { "id": "hcmisbeemp_department",      "name": "DEPARTMENT",        "value": "Human Resources", … },
    { "id": "hcmisbeemp_employeeId",      "name": "EMPLOYEE_ID",       "value": "21001", … },
    …
    { "id": "nhc_notes", "name": "NOTES", "value": "Resume Uploaded", "dataType": "string", … }
  ]
}
```

**Key finding:** the attribute `id`s in this write payload (`hcmisbebo_businessObjectId`,
`hcmisbeemp_department`, `hcmisbeemp_employeeId`, `hcmisbeemp_manager`, …) are the **same keys** that
come back in the working `list_documents` read. So reads and writes share the same Workday attribute
vocabulary — this is definitely the right contract.

**But it's a different shape than our Salesforce upload model.** Our current `AttachDocumentRequest`
(in `UploadModels.cs`) is the Salesforce shape (`ecmContentTypeName`, `businessContext`,
`businessObjectAdditionalAttribute[]` with `{name, singleValue, listValue, type, format}`). Workday
uses `businessObjectType` + `documentType.id` + `createNewVersion` + `businessObjectAttributes[]` with
`{id, name, value, dataType, currency, dateFormat, isSystemData}`. So — same story as the read — we'll
need a **Workday variant of the attach body**.

**Still missing:** this page only shows the **metadata body**, not the **Capture (file-bytes) upload
endpoint**. Our code currently hardcodes `/api/core/documents/upload` (initiate) + `/complete`, and we
already know `/bow/core/documents/upload` = 404 and `/api` = 405 for Workday. The payload has **no
`uploadId`**, which hints Workday's flow may differ (maybe file + metadata in one call). Drafted a
message asking the mentor for the **Capture** and **ViewRelatedDocs** pages (endpoint URL, method,
multipart vs base64, one call or two).

---

## 15. The login/account mess (Microsoft vs native Hyland IAM)

Signing in got painful because two very different account types were in play:

- `yash.gupta+appintel-dev@hyland.com` — a **native Hyland IAM** account (username box on the IAM page).
- `arizzo@aurahyland.onmicrosoft.com` — a **Microsoft-federated** account (Anthony Rizzo, the Workday
  test user).

**Symptom:** ever since logging in as Anthony, the dev IAM stopped showing the Hyland IAM login page and
**jumped straight to the Microsoft "Pick an account" screen** — where only Anthony/Microsoft accounts
exist, so there was no way to type the native Hyland account.

**Two code experiments (in `InteractiveLoginService.cs`):**

1. Changed the login request from `prompt=login` to **`prompt=select_account`** so the account chooser
   always appears instead of silently reusing the last account. This worked — the picker now shows every
   time — but it's still the **Microsoft** picker.
2. Added an optional **private/incognito browser launch** (`UsePrivateBrowserForLogin`) so login opens
   with no cookies. Tried it, but **Edge InPrivate still did Windows SSO** (the machine is Entra-joined),
   so it went to Microsoft anyway. Per the user's preference, **reverted the default back to the normal
   browser window** (`UsePrivateBrowserForLogin = false`; the flag remains for future use).

**The real explanation (not a bug):** the login URL was redirecting to
`login.microsoftonline.com/5150c46e-…/saml2` — i.e. the Hyland IAM **federates this client's login to
Microsoft**. For the **Workday dev-prod client** (aura tenant, `wdx` scope), Microsoft *is* the login
provider — there is no native Hyland username page for it. The native IAM page the user remembered
belongs to the **CIC/Salesforce client** (different `client_id`). So for Workday, the correct account is
simply **Anthony Rizzo**, and clicking it is the intended flow — not a workaround.

---

## 16. The nginx "cookie too large" 400

After many login attempts and account switches, the browser accumulated a huge cookie pile for
`*.hyland.com` (648 KB / 61 cookies; `admin.dev.app.hyland.com` alone was 145 KB). The IAM authorize
request then failed with:

> **400 Bad Request — Request Header Or Cookie Too Large** (nginx)

**Fix:** cleared all `hyland.com` cookies (Edge → Cookies → search "hyland" → **Remove all shown**).
Purely a browser-side cookie-bloat issue, nothing to do with the MCP. After the purge the 400 was gone
and login worked again.

**Lesson:** repeated OAuth logins + account switching can bloat the IAM host cookies past nginx's header
limit → 400. When that happens, clear the `hyland.com` cookies (the whole group is safe; sign-in
recreates the few it needs).

---

## 17. State at end of day

- **Workday reads:** fully working (`list_documents` returns real employee docs via `/bow`).
- **Workday upload:** metadata payload known; **Capture endpoint still needed** from the mentor.
- **Login:** understood — Workday dev-prod = Microsoft/Anthony Rizzo login (federated); CIC/Salesforce =
  native Hyland IAM (`+appintel-dev`). Choose the account per environment.
- **Code:** `prompt=select_account` (account chooser every time); `UsePrivateBrowserForLogin` option
  added but defaulted **off** (normal browser window). All builds clean (0 errors).
