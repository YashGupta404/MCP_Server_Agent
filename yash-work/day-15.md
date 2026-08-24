# Day 15

_Focus: make the two manual-entry forms **LOB-specific and page-driven**, chase down why the Workday
"Find WID" form kept showing on Salesforce pages (it was a **CSS `[hidden]` override**, not a JS bug),
add **in-panel preview for plain-text files**, and — at the end of the day — get the whole local stack
back up (all four tiers) so the extension could actually load a record._

---

## 1. Make the manual-entry forms LOB-specific

**Goal:** the side panel has two manual-entry forms — the Salesforce one (`type` + `record id`) and
the Workday one ("Find WID": worker name / employee id). Only **one** should ever show, and it must
match **the page I'm on**, not the MCP backend.

**First attempt (wrong):** I keyed the form off the MCP's document types as well as the page. That
backfired — `populateDocTypes()` overrode the page's LOB with the backend's doc-types guess. The MCP
currently serves Workday-style types, so it kept **flipping the form to Workday even on Salesforce
pages**.

**Fix — strictly page-driven** (`browser-extension/src/popup.js`):
- `lobFromSource(source)` decides the LOB from the **page host** only: host contains `workday` →
  `"workday"`; contains `force.com` / `salesforce` → `"salesforce"`; else `null`.
- `applyLobEntryForms()` toggles the two forms: `els.manualForm.hidden = workday;`
  `els.workerForm.hidden = !workday;`.
- `loadContextPanel()` sets `currentLob = lobFromSource(currentContext?.source)` fresh on every load.
- **Removed** `lobFromDocTypes()` and `refreshLobFormsIfShown()` — the backend no longer gets a say.
- `populateDocTypes()` still runs, but now **only** to fill the upload dropdown; it never touches the
  forms or the LOB.

---

## 2. The real reason "Find WID" wouldn't go away — a CSS `[hidden]` override

Even after the JS was correct, the Workday "Find WID" form **kept showing on Salesforce**. Setting
`els.workerForm.hidden = true` added the attribute, but the form stayed visible.

**Root cause:** `popup.css` had `.manual { display: flex; }`. An author `display: flex` **beats** the
UA rule `[hidden] { display: none }` on specificity, so the element ignored the `hidden` attribute.
This silently defeated **every** JS attempt — `get_errors` can't catch it because it's not an error,
it's CSS specificity.

**Fix:** added a global override near the top of `popup.css`:

```css
[hidden] { display: none !important; }
```

Now any element toggled via `.hidden` / the `hidden` attribute actually hides, regardless of its
class `display`. (Same class of bug I hit earlier with the collapsible panel — worth remembering.)

**Lesson:** if an element has an explicit class `display` and you toggle it with `.hidden`, you need
`[hidden] { display: none !important; }` (or a `.class[hidden]` rule). Neither JS nor `get_errors`
will reveal it.

---

## 3. In-panel preview for plain-text files

**Symptom:** clicking a `.txt` document showed nothing. The viewer only had two branches — image →
`<img>`, everything else → PDF.js — so text bytes (`text/plain`) went to PDF.js, which failed on
non-PDF content.

**Fix:**
- `popup.html` — added a dedicated text element in the viewer body:
  `<pre id="viewerText" class="viewer__text" hidden></pre>`.
- `popup.js` — new `viewerText` entry in the `els` map; hide/clear it in the open-reset and in
  `closeViewer()`; and a **new text branch** in `openDocumentPreview()` between the image and PDF
  branches:
  - triggers when the content-type includes `text` / `json` / `xml` / `csv`, **or** the file's
    extension is text-like (`txt`, `csv`, `json`, `xml`, `md`, `log`, `yaml`, `yml`, `html`, `htm`)
    and not a PDF — the extension fallback covers the backend returning a generic
    `application/octet-stream`.
  - reads the blob as text: `const text = await (await fetch(result.objectUrl)).text();`, revokes the
    object URL, sets `els.viewerText.textContent = text`, shows the `<pre>`, hides the loader/pager,
    and sets `currentViewerTrack = "text"`.
- `popup.css` — `.viewer__text` fills the viewer body (`position: absolute; inset: 0`), scrolls,
  wraps (`white-space: pre-wrap; word-break: break-word`), monospace font.

**Known limitation:** `agent.js fetchDocumentContent()` checks `contentType.includes("json")` **first**
and treats it as the Workday viewer-URL branch, so a genuine `.json` **document** served as
`application/json` would be parsed as `{viewerUrl}` instead of previewed as text. `text/plain` is
unaffected. Left as-is (out of scope).

---

## 4. "Why did you break it?" — actually the whole backend stack was down

After the viewer change, the panel showed **"No content linked to this record yet"** and
**"No document types found."** It looked like the text-preview edit broke document loading — it
**hadn't**. The text change only affects how a file displays **after** you click it; it never touches
document listing or doc types. Every empty/error state traced to a **tier below the extension being
down**, each one failing to reach the next:

```
extension  →  BFF :5010  →  MCP :5200  →  UCEB API :5000
   ✓ signed in   was down     was down      was down (root cause)
```

Worked back up the chain from the console/log errors:

1. **`ERR_CONNECTION_REFUSED` on `localhost:5010/auth/exchange`** → the **BFF wasn't running**.
   Started it → `https://localhost:5010`. Sign-in then worked.
2. **BFF returned `502` "target machine actively refused it (localhost:5200)"** → the **MCP server
   wasn't running**. First start came up in **stdio** mode (never bound 5200). It needs the HTTP
   transport: `dotnet run -- --http` → `http://localhost:5200`. (Gotcha: without `--http` it defaults
   to stdio and never listens on 5200.)
3. **MCP log then showed `list_documents` / `list_document_types` failing with
   `ERR_CONNECTION_REFUSED (localhost:5000)`** → the **UCEB API wasn't running**. Started it →
   `http://localhost:5000`.

Once all four tiers were up (UCEB API → MCP `--http` → BFF → signed-in extension), refreshing the
panel loaded document types and linked content, and `.txt` documents preview inline.

**Lesson:** an empty/"no content" panel almost always means a **lower tier is down**, not a
front-end regression. Read the errors from the bottom up: extension console → BFF → MCP log →
UCEB API.

---

## 5. Start order for the local stack (for next time)

```
1. UCEB API   :5000   dotnet run --urls http://localhost:5000          (ASPNETCORE_ENVIRONMENT=Development)
2. MCP server :5200   dotnet run -- --http                              (MUST pass --http, else stdio)
3. BFF        :5010   dotnet run --urls http://localhost:5010           (listens on https)
4. Extension          sign in, then refresh the "Hyland Enterprise Content" panel
```

Keep all four running — closing any one brings back connection-refused / 502 / empty-panel one tier
up. When editing the extension, reload it at `chrome://extensions` **and** close + reopen the side
panel (reloading alone doesn't refresh an already-open panel).

---

## 6. Committed + pushed

Committed the day's front-end work and pushed to `main`:
`Add in-panel text-file preview; make manual-entry forms page-driven (LOB-specific); fix [hidden]
CSS override` (`popup.js`, `popup.html`, `popup.css`, plus `auth.js` and `bff/switch-lob.ps1`).

---

## 7. Per-extension document badges (real extension + unique colours)

**Symptom:** every document row showed a blue **"DOC"** badge regardless of file type.

**Root cause:** `fileExtension()` only matched an extension at the **end** of the name, but UCEB
composes names with the real extension **in the middle** (e.g.
`Hyland Logo (2).png-employee-application-2026_08_24`), so it returned `""` → kind `"doc"` → blue
"DOC" for everything.

**Fix (`popup.js`):**
- `fileExtension()` now also matches an **embedded** extension mid-string, not just end-of-string.
- The badge text is the real extension uppercased (`PDF`, `PNG`, `JPEG`, `XLSX`, …) instead of a
  hardcoded "DOC".
- `iconKind()` returns a **unique kind per extension** and `popup.css` gives each its own colour
  (pdf red, doc blue, xls green, csv teal, ppt orange, png purple, jpg pink, gif indigo, tiff brown,
  bmp cyan, webp olive, svg violet, txt slate, md, json, xml, html, log, zip, generic). Every
  extension now reads correctly with a distinct colour (previously all images shared one colour).

---

## 8. Multi-page PDF test asset

Built a valid **5-page** test PDF at `yash-work/demo-docs/multipage-test.pdf` (each page reads
"Page N of 5" + a caption) to verify the in-panel viewer's page navigation. Hand-built with a correct
xref table; verified all offsets and that PDF.js parses all 5 pages.

---

## 9. PDF pager — "next button vanishes when the panel is wide" (the real bug)

**Symptom:** open the 5-page PDF → page 1 renders, pager shows **"Page 1 / 5"**, but the `›` next
button is **missing and pages won't advance** — and only when the side panel is **wide**. Collapse
the panel and the next button reappears.

**Ruled out by direct testing (not guessing):**
- Rendered `popup.html` in a real browser and **measured** the next button at both 380px and 1014px
  widths — it renders, is enabled and clickable in both.
- Ran **PDF.js 6.2.108 in Node** against `multipage-test.pdf` — all 5 pages parse fine.
- `node --check popup.js` — clean; `manifest.json` loads the exact edited files.

**True root cause — CSS overflow.** `.viewer__body` had **no `overflow`** set (defaulted to
`visible`), and `.viewer__canvas` has `max-width: 100%` but **no height cap**. When the panel is wide,
the canvas scales to that width and becomes **tall** (a 612×792 page at ~790px wide ≈ 1020px tall), so
it **overflows below the body and paints over the pager**, covering the next button. Narrow panel →
short canvas → fits → pager clear.

**Fix (`popup.css`):**
```css
.viewer__body { overflow: auto; }              /* tall canvas scrolls inside, never covers the pager */
.viewer__pager { position: relative; z-index: 2; }  /* keep the pager above the canvas */
```

**Also (`popup.js`):**
- Extracted `goToPrevPage()` / `goToNextPage()` and wired **keyboard ← / →** paging (in addition to
  the click buttons); typing in an input/textarea is ignored, **Esc** still closes the viewer.
- Switched PDF loading from `getDocument({ url: blobUrl })` + immediate `revokeObjectURL` to
  **buffering the bytes into an ArrayBuffer** then `getDocument({ data })`, so every page's data stays
  available (the old blob-revoke broke `getPage(2+)`), and clamped `pageNo` to `[1, numPages]`.

**Lesson:** a CSS canvas overflow can visually **cover** sibling controls; `get_errors`/JS can't reveal
it. Reproduce the **layout** (render the HTML, measure the control at the actual panel width) instead of
only reading code.

---

## 10. Committed + pushed (afternoon)

Recorded sections 7–9 and pushed the doc-badge, multi-page test PDF, and PDF-pager fixes to `main`.
