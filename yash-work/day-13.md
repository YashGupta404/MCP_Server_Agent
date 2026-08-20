# Day 13

_Focus: made the **in-panel document viewer** actually render for Workday. The doc would not open
in the side panel ("Preview not available"), even though Salesforce worked. Turned out to be **two
separate client-side PDF.js bugs** stacked on top of the (already-solved) Workday byte-download.
Documented the full viewer data flow end-to-end._

---

## 1. Goal

Click a document in the extension's Documents list → render it **inside the side panel** (on a
`<canvas>` for PDFs, in an `<img>` for images) — the same in-panel experience for **every** LOB
(Salesforce, Workday, etc.), not a pop-out window.

Salesforce documents rendered fine. Workday documents showed **"Preview not available."**

---

## 2. How the viewer works (data flow)

```
Extension (popup.js)
  openDocumentPreview(doc)
    -> agent.js fetchDocumentContent(docId)
        GET  BFF  https://localhost:5010/api/document/content?docId=...
    -> BFF proxies to MCP  /documents/{id}/content   (over the dev tunnel)
    -> MCP resolves the raw bytes for the LOB and streams them back
  Response:
    - Content-Type: application/json  -> { viewerUrl }  (no raw bytes available -> open viewer)
    - Content-Type: application/pdf   -> raw PDF bytes
    - Content-Type: image/png|jpeg…   -> raw image bytes
```

- **BFF** (`bff/Program.cs`, `/api/document/content`) is a thin proxy. It forwards the MCP response
  **Content-Type verbatim** (`mcpResp.Content.Headers.ContentType?.MediaType`) and streams the bytes
  with `Results.File(bytes, contentType)`. JSON bodies (viewer-URL fallback) pass straight through.

- **MCP** (`/documents/{id}/content`):
  - **Workday**: UCEB has no byte path for wdx (the `api/core/download` route's `CanPreviewDocuments`
    policy is CIC-only → 403). So MCP calls the **HxP content platform directly** with the user's
    bearer token: `GET https://{envKey}.content.dev.app.hyland.com/api/download/{docId}/sysfile_blob`.
    `envKey` is resolved via Nucleus (`GET .../environments/{environment_id}` → `.key`). It captures
    the response's real `MediaType` onto `DownloadedFile.ContentType` and returns
    `Results.File(bytes, contentType, fileName)`.
  - If no bytes are available it falls back to `{ viewerUrl }` JSON.

- **Extension** (`popup.js`, `openDocumentPreview`):
  - `type: "bytes"` + `image/*` → set `<img>.src` to the object URL (no PDF.js).
  - `type: "bytes"` + PDF/other → `renderPdfPage()` → PDF.js renders page 1 onto `<canvas>`.
  - `type: "viewer"` → show the fallback panel with "Open in viewer window / new tab".

So the **content type** is the single source of truth, and it is preserved across all three hops:
MCP → BFF → extension.

---

## 3. What was wrong (two stacked bugs)

The Workday **download itself was already working** (verified: MCP `/content` returns
`200 application/pdf` / `image/png` with real bytes). The failures were **100% client-side in
PDF.js**.

### Bug 1 — PDF.js v6 rejects a bare URL string

Console error:

```
[viewer] PDF render error: Error: getDocument - expected either 'data', 'range', or 'url' parameter.
```

The bundled PDF.js is **v6.2.108**. In v5+/v6, `getDocument()` **no longer accepts a bare URL
string** — it requires an options object. The old call passed the blob URL string directly, so it
threw synchronously and fell through to the "Preview not available" fallback.

**Fix** (`popup.js`, `renderPdfPage`):

```js
// before
const loadingTask = pdfjsLib.getDocument(objectUrl);
// after
const loadingTask = pdfjsLib.getDocument({ url: objectUrl });
```

(`page.render({ canvasContext, viewport })` is still valid in v6 — `canvas` defaults to
`canvasContext.canvas` — so no change was needed there.)

### Bug 2 — images were being fed into PDF.js

After fixing Bug 1 the error **changed** to:

```
[viewer] PDF render error: InvalidPDFException
```

The document was `Hyland Logo (2).png` — a **PNG image, not a PDF**. The viewer was sending
**every** non-JSON document into PDF.js regardless of its real type. Salesforce "worked" only
because those test docs happened to be PDFs; the first Workday doc tested was an image, so PDF.js
correctly reported the bytes weren't a PDF.

**Fix** (`popup.js`, `openDocumentPreview`) — branch on the content type:

```js
if (result.type === "bytes") {
  const ctype = (result.contentType || "").toLowerCase();
  if (ctype.includes("image")) {
    // image/png, image/jpeg, … -> render directly in <img>
    revokePreviewUrl();
    currentPreviewObjectUrl = result.objectUrl;
    els.viewerImage.src = result.objectUrl;
    els.viewerImage.hidden = false;
    els.viewerLoading.hidden = true;
    els.viewerPager.hidden = true;
  } else {
    // PDF (or unknown) -> PDF.js
    await renderPdfPage(result.objectUrl, 1);
  }
}
```

No server change was required — the real content type already flows through the whole chain.

---

## 4. Result

- Workday PDFs render on the `<canvas>` in-panel, exactly like Salesforce.
- Workday images (PNG/JPEG) render in the `<img>` in-panel.
- Documents with no downloadable bytes still fall back to the first-party viewer window.

**Reload steps:** only `popup.js`/`agent.js` changed → reload the extension at `chrome://extensions`
(refresh icon). No server or tunnel restart needed.

---

## 5. Key takeaways

- The bundled **PDF.js is v6.2.108** — `getDocument()` needs `{ url }` / `{ data }`, never a bare
  string.
- Never assume a document is a PDF. **Branch on `Content-Type`**: `image/*` → `<img>`, PDF → PDF.js,
  JSON → viewer-URL fallback.
- The content type is captured at the source (HxP download `MediaType`) and forwarded unchanged by
  the BFF, so the client can trust `result.contentType`.
