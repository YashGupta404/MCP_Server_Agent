# Day 24 — 2026-09-16

_Focus: diagnosed why the **Workday (staging)** demo login broke, ran the **Salesforce** demo instead to
test the new doc-list features, and shipped a small display fix so the **file name** (with a colored
extension icon) is the row title for both Salesforce and Workday._

---

## 1. Workday (staging) login blocker — arizzo lost `environment_authorization`

Bringing up the Workday staging stack, the arizzo warm-up login failed at IAM with
**"Unable to perform authorization"** (Request IDs e.g. `98666d97…`, `f31212a1…`).

Diagnosis (proven, not guessed):
- **With** `environment_authorization` requested → IAM authenticates arizzo but fails the authorization
  step → error page.
- **Without** it (dropped scope) → login succeeds, but `get_token_claims` shows the token has **no
  `hxp_authorization` claim** (no `appkey=wdx`, no `environment_id`), and the deployed UCEB then returns
  **403 "token is valid but was denied"** on every `/bow` call.

So it's a **catch‑22 at the identity layer**:

| Scope requested | Login | Token env context | Deployed UCEB `/bow` |
|---|---|---|---|
| with `environment_authorization` | ❌ "Unable to perform authorization" | — | never reached |
| without it | ✅ | ❌ none | ❌ 403 |

**Root cause:** nothing changed in the client (`wsc-c8e114b2`, Appintel-Staging Prod) or its Allowed
Scopes. `arizzo` is a **shared** account and its **per-user `environment_authorization` grant in
`Appintel-Staging Prod`** was removed/expired upstream. "Client Allowed Scopes" ≠ "user is authorized in
the environment." `environment_authorization` is what delivers the `appkey=wdx` + `environment_id` claims
the deployed UCEB requires — it was **always** required for Workday, so this is not new behavior.

**Fix is IAM-side (not code/config):** re-grant `arizzo` `environment_authorization` in
`Appintel-Staging Prod`; then re-add the scope and the deployed path works unchanged. Sent the mentor a
short note. (Local UCEB is NOT an alternative: its CFS token-exchange client is a login client, not the
real internal/AWS exchange client, and it would 403 on the same missing-env token anyway.)

---

## 2. Ran the Salesforce (staging) demo instead — features #1/#2 are LOB-agnostic

Switched the stack to `salesforce-staging` (client `wsc-f3ec0fd9`, deployed UCEB, `api` route). Login OK,
`get_build_info` 200, `list_document_types` returned the 7 CIC types. Confirmed the two Day‑23 list
features work for **Salesforce** with **no code change** (they're client-side and LOB-agnostic):
- **#1** — each doc row renders doc **type + every configured metadata field** as chips (`renderDocRows`).
- **#2** — the search box filters loaded docs by name / type / docId / any metadata value (`filterDocs`).

How many fields show for Salesforce depends on what the solution config's list query returns as display
columns.

---

## 3. Display fix — file name as the row title + colored extension icon (both LOBs)

Problem: rows showed the numeric docId (`280`, `281`) as the title with a generic `DOC` icon, even though
the stored **file name** was present as a "Document" metadata chip.

Fix (`bff/Program.cs` `ParseDocumentList`, BFF-only):
- Added **"Document"** to the name-source list, so the stored filename becomes the doc's display name.
- Added "Document" to the columns dropped from the chip list (so the title isn't duplicated as a chip).
- The extension already derives the **row title** and the **colored extension icon** from `doc.name`
  (`fileExtension` + `iconKind`), so this now yields e.g. `INVOICE.TXT` as the title with a `TXT`-colored
  icon — for **both Salesforce and Workday**, wherever a filename value exists. Auto-named docs (no
  filename) keep their existing name/generic icon.

BFF restarted to apply (restart wipes sessions → re-sign-in in the panel).

---

## 4. Clarifications captured (for productization)

- **Label vs value:** the OnBase keyword field 229 is still "Invoice #"; we only relabel the **display**
  column to "Document" in the **solution config**, and we **write the filename value** into 229 at upload
  (`NameKeywordFieldByContentType: { "Invoices": "229" }`). Repurposing an unrelated field is a **hack**;
  a production build should use a proper name field.
- **Shared config:** `set_query_display_columns` writes the **shared** UCEB solution config, so its label
  change is visible to anyone using the same system config in that environment. The name-keyword stamping
  and today's BFF change are **local** (only affect our uploads/plugin).
- **Productization:** end-user plugin should be **config-read-only**; config (import mapping + display
  columns) is an **admin/provisioning** step, not a runtime action exposed to users.

---

## 5. Deferred (future) — extend the "filename → Document column" trick to ALL doc types

Considered replicating the Invoices recipe (store filename in a keyword + relabel that column to
"Document") for **every** doc type of both LOBs. **Decision: NOT doing it now** — user will decide later.
Reasons / constraints captured for when we revisit:

- **Display is already type-agnostic** — `ParseDocumentList` picks the name from any name-like column
  (`hfs_Name`/`Document`/`Name`/`Document Name`/`File Name`/`Title`) for any type/LOB. No change needed
  there; the gap is only whether a **filename value** exists to show.
- **Salesforce / CIC-native types:** already covered — the upload stamps `hfs_Name` with the filename for
  every type that has it. Nothing to do.
- **Salesforce / OnBase types:** each OnBase type has a **fixed keyword schema with no spare name field**.
  Invoices worked only because we **sacrificed** the "Invoice #" field (229). Doing this per type means
  **giving up one real keyword per type** (e.g. "COM - Application" would lose Loan Number or Entity Name).
  Also the display relabel writes the **shared** solution config → affects everyone in that environment.
- **Workday:** capture **broker rejected** a filename keyword with a **500** (proven earlier — e.g. "File
  Name" keyword id 2, and 162). So the raw upload filename likely **can't be stored** on the Workday
  config; it keeps its auto-name `"<Type> - <Date>"`. Would need per-field testing to confirm any keyword
  accepts it.

**If revisited:** (1) list each OnBase type's keyword fields, pick the least-important one to repurpose per
type, add to `NameKeywordFieldByContentType`, relabel that type's query column; (2) test whether the
Workday broker accepts any filename keyword; (3) prefer a proper name field per type over repurposing —
and treat all of this as **admin/provisioning** config, not a runtime plugin action.
