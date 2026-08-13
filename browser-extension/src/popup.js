// Popup chat controller: wires the UI to auth.js + agent.js.

import { interactiveLogin, getSession, clearTokens } from "./auth.js";
import { sendMessageToAgent, fetchContextDocuments, openInViewer } from "./agent.js";

const ext = globalThis.browser ?? globalThis.chrome;

const els = {
  messages: document.getElementById("messages"),
  emptyState: document.getElementById("emptyState"),
  form: document.getElementById("composer"),
  input: document.getElementById("input"),
  sendBtn: document.getElementById("sendBtn"),
  authBtn: document.getElementById("authBtn"),
  status: document.getElementById("status"),
  statusLabel: document.querySelector("#status .status__label"),
  attachBtn: document.getElementById("attachBtn"),
  fileInput: document.getElementById("fileInput"),
  attachments: document.getElementById("attachments"),
  contextPanel: document.getElementById("contextPanel"),
  contextType: document.getElementById("contextType"),
  contextName: document.getElementById("contextName"),
  contextDesc: document.getElementById("contextDesc"),
  contextStatus: document.getElementById("contextStatus"),
  contextRefresh: document.getElementById("contextRefresh"),
  contextToggle: document.getElementById("contextToggle"),
  docList: document.getElementById("docList"),
  docPane: document.getElementById("docPane"),
  metaPane: document.getElementById("metaPane"),
  dropzone: document.getElementById("dropzone"),
  actAttach: document.getElementById("actAttach"),
  tabDocuments: document.getElementById("tabDocuments"),
  tabMetadata: document.getElementById("tabMetadata"),
  manualForm: document.getElementById("manualForm"),
  manualType: document.getElementById("manualType"),
  manualId: document.getElementById("manualId"),
};

// Documents currently shown in the panel (used by the Metadata tab).
let loadedDocuments = [];

let signedIn = false;
/** @type {File[]} */
let pendingFiles = [];
/** The business object detected on the active browser tab, or null. */
let currentContext = null;

// ---- UI helpers ----

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// ---- Minimal, safe Markdown renderer (for agent replies) ----
// Input is HTML-escaped FIRST, so raw HTML in the reply can never inject markup;
// only the tags this renderer emits are produced. Supports headings, bold, italic,
// inline code, fenced code, links (http/https only), bullet/numbered lists and tables.
function escapeHtml(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function renderInline(text) {
  // Protect inline code spans from other replacements.
  const codeTokens = [];
  text = text.replace(/`([^`]+)`/g, (_, c) => {
    codeTokens.push(c);
    return `\u0000C${codeTokens.length - 1}\u0000`;
  });
  // Links [label](http/https url) — only safe schemes.
  text = text.replace(
    /\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g,
    (_, label, url) => `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`
  );
  // Bold, then italic.
  text = text.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  text = text.replace(/__([^_]+)__/g, "<strong>$1</strong>");
  text = text.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  text = text.replace(/(^|[^_\w])_([^_\n]+)_/g, "$1<em>$2</em>");
  // Restore code spans.
  text = text.replace(/\u0000C(\d+)\u0000/g, (_, i) => `<code>${codeTokens[+i]}</code>`);
  return text;
}

function renderMarkdown(md) {
  const lines = escapeHtml(md).replace(/\r\n?/g, "\n").split("\n");
  const out = [];
  let listType = null;
  const closeList = () => {
    if (listType) {
      out.push(`</${listType}>`);
      listType = null;
    }
  };

  let i = 0;
  while (i < lines.length) {
    const line = lines[i];

    // Fenced code block.
    if (/^\s*```/.test(line)) {
      closeList();
      const code = [];
      i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) {
        code.push(lines[i]);
        i++;
      }
      i++; // skip closing fence
      out.push(`<pre><code>${code.join("\n")}</code></pre>`);
      continue;
    }

    // Table: header row with pipes followed by a |---|---| separator.
    if (
      line.includes("|") &&
      i + 1 < lines.length &&
      /^[\s|:-]+$/.test(lines[i + 1]) &&
      lines[i + 1].includes("-")
    ) {
      closeList();
      const parseRow = (r) =>
        r.replace(/^\s*\|/, "").replace(/\|\s*$/, "").split("|").map((c) => c.trim());
      const headers = parseRow(line);
      i += 2;
      let t = "<table><thead><tr>";
      for (const h of headers) t += `<th>${renderInline(h)}</th>`;
      t += "</tr></thead><tbody>";
      while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") {
        const cells = parseRow(lines[i]);
        t += "<tr>";
        for (let c = 0; c < headers.length; c++) t += `<td>${renderInline(cells[c] ?? "")}</td>`;
        t += "</tr>";
        i++;
      }
      t += "</tbody></table>";
      out.push(t);
      continue;
    }

    // Heading.
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      closeList();
      const level = heading[1].length;
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
      i++;
      continue;
    }

    // Unordered list item.
    const ul = /^\s*[-*+]\s+(.*)$/.exec(line);
    if (ul) {
      if (listType !== "ul") {
        closeList();
        out.push("<ul>");
        listType = "ul";
      }
      out.push(`<li>${renderInline(ul[1])}</li>`);
      i++;
      continue;
    }

    // Ordered list item.
    const ol = /^\s*\d+\.\s+(.*)$/.exec(line);
    if (ol) {
      if (listType !== "ol") {
        closeList();
        out.push("<ol>");
        listType = "ol";
      }
      out.push(`<li>${renderInline(ol[1])}</li>`);
      i++;
      continue;
    }

    // Blank line ends a block.
    if (line.trim() === "") {
      closeList();
      i++;
      continue;
    }

    // Paragraph: gather consecutive plain lines.
    closeList();
    const para = [line];
    i++;
    while (
      i < lines.length &&
      lines[i].trim() !== "" &&
      !/^\s*```/.test(lines[i]) &&
      !/^(#{1,6})\s+/.test(lines[i]) &&
      !/^\s*[-*+]\s+/.test(lines[i]) &&
      !/^\s*\d+\.\s+/.test(lines[i]) &&
      !lines[i].includes("|")
    ) {
      para.push(lines[i]);
      i++;
    }
    out.push(`<p>${renderInline(para.join("<br>"))}</p>`);
  }

  closeList();
  return out.join("");
}

function addMessage(text, kind, files) {
  if (els.emptyState) els.emptyState.hidden = true;
  const el = document.createElement("div");
  el.className = `msg msg--${kind}`;

  const body = document.createElement("div");
  body.className = "msg__text";
  if (kind === "agent") {
    // Agent replies are Markdown — render a safe subset (input is HTML-escaped first).
    body.classList.add("md");
    body.innerHTML = renderMarkdown(text);
  } else {
    body.textContent = text;
  }
  el.appendChild(body);

  if (files && files.length) {
    const wrap = document.createElement("div");
    wrap.className = "msg__files";
    for (const f of files) {
      const tag = document.createElement("span");
      tag.className = "msg__file";
      tag.textContent = `📄 ${f.name}`;
      wrap.appendChild(tag);
    }
    el.appendChild(wrap);
  }

  els.messages.appendChild(el);
  els.messages.scrollTop = els.messages.scrollHeight;
  return el;
}

function setSignedIn(state) {
  signedIn = state;
  els.statusLabel.textContent = state ? "Signed in" : "Signed out";
  els.status.className = `status ${state ? "status--in" : "status--out"}`;
  els.authBtn.textContent = state ? "Sign out" : "Sign in";
  els.input.disabled = !state;
  els.attachBtn.disabled = !state;
  updateSendEnabled();
  loadContextPanel();
}

function updateSendEnabled() {
  const hasText = els.input.value.trim().length > 0;
  els.sendBtn.disabled = !signedIn || (!hasText && pendingFiles.length === 0);
}

function autoGrow() {
  els.input.style.height = "auto";
  els.input.style.height = `${Math.min(els.input.scrollHeight, 120)}px`;
}

// ---- attachments ----

function renderAttachments() {
  els.attachments.innerHTML = "";
  els.attachments.hidden = pendingFiles.length === 0;

  pendingFiles.forEach((file, index) => {
    const chip = document.createElement("div");
    chip.className = "chip";

    const name = document.createElement("span");
    name.className = "chip__name";
    name.textContent = file.name;
    name.title = file.name;

    const size = document.createElement("span");
    size.className = "chip__size";
    size.textContent = formatSize(file.size);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "chip__remove";
    remove.textContent = "×";
    remove.title = "Remove";
    remove.addEventListener("click", () => {
      pendingFiles.splice(index, 1);
      renderAttachments();
      updateSendEnabled();
    });

    chip.append(name, size, remove);
    els.attachments.appendChild(chip);
  });
}

els.attachBtn.addEventListener("click", () => els.fileInput.click());

els.fileInput.addEventListener("change", () => {
  const chosen = Array.from(els.fileInput.files ?? []);
  if (chosen.length) {
    pendingFiles = pendingFiles.concat(chosen);
    renderAttachments();
    updateSendEnabled();
  }
  // Reset so picking the same file again still fires "change".
  els.fileInput.value = "";
});

// ---- context panel (content-in-context) ----

/** Asks the background worker for the business object on the active browser tab. */
async function getActiveContext() {
  try {
    const res = await ext.runtime.sendMessage({ type: "UCEB_GET_ACTIVE_CONTEXT" });
    return res?.context ?? null;
  } catch {
    return null;
  }
}

function setContextStatus(text) {
  if (!text) {
    els.contextStatus.hidden = true;
    els.contextStatus.textContent = "";
    return;
  }
  els.contextStatus.hidden = false;
  els.contextStatus.textContent = text;
}

function renderDocuments(documents) {
  loadedDocuments = documents;
  els.docList.innerHTML = "";
  if (!documents.length) {
    setContextStatus("No content linked to this record yet — use Attach or drop a file above.");
    return;
  }
  setContextStatus(null);

  for (const doc of documents) {
    const attrs = doc.attributes ? Object.entries(doc.attributes) : [];
    const extName = fileExtension(doc.name);
    const kind = iconKind(extName);

    const li = document.createElement("li");
    li.className = "docrow";
    li.title = `Open ${doc.name || doc.docId} in the Hyland viewer`;

    const icon = document.createElement("span");
    icon.className = `docrow__icon docrow__icon--${kind}`;
    icon.textContent = (extName || "doc").slice(0, 4).toUpperCase();

    const body = document.createElement("div");
    body.className = "docrow__body";
    const name = document.createElement("div");
    name.className = "docrow__name";
    name.textContent = doc.name || doc.docId;
    const sub = document.createElement("div");
    sub.className = "docrow__sub";
    sub.textContent = attrs.length
      ? attrs.map(([, v]) => v).join(" • ")
      : `docId ${doc.docId}`;
    body.append(name, sub);

    li.append(icon, body);

    const version = versionOf(doc.attributes);
    if (version) {
      const ver = document.createElement("span");
      ver.className = "docrow__ver";
      ver.textContent = version;
      li.appendChild(ver);
    }

    li.addEventListener("click", async () => {
      li.style.opacity = "0.6";
      try {
        const url = await openInViewer(doc.docId);
        await ext.tabs.create({ url });
      } catch (err) {
        setContextStatus(`Open failed: ${err.message}`);
      } finally {
        li.style.opacity = "";
      }
    });

    els.docList.appendChild(li);
  }
}

function fileExtension(name) {
  const m = /\.([a-z0-9]{1,5})$/i.exec(name || "");
  return m ? m[1].toLowerCase() : "";
}

function iconKind(ext) {
  if (ext === "pdf") return "pdf";
  if (["doc", "docx"].includes(ext)) return "doc";
  if (["xls", "xlsx", "csv"].includes(ext)) return "xls";
  if (["png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp", "webp"].includes(ext)) return "img";
  if (["txt", "rtf"].includes(ext)) return "txt";
  return "doc";
}

function versionOf(attributes) {
  if (!attributes) return null;
  for (const [k, v] of Object.entries(attributes)) {
    if (/version|rev\b|\bver\b/i.test(k) && v) return /^v/i.test(v) ? v : `v${v}`;
  }
  return null;
}

function renderMetadata() {
  els.metaPane.innerHTML = "";
  const rows = [];
  if (currentContext) {
    rows.push(["Type", currentContext.businessObjectType]);
    rows.push(["Record ID", currentContext.businessObjectId]);
  }
  rows.push(["Documents", String(loadedDocuments.length)]);
  for (const doc of loadedDocuments) {
    const attrs = doc.attributes ? Object.entries(doc.attributes) : [];
    rows.push([doc.name || doc.docId, attrs.length ? attrs.map(([k, v]) => `${k}: ${v}`).join(", ") : doc.docId]);
  }
  for (const [k, v] of rows) {
    const row = document.createElement("div");
    row.className = "hec__metaRow";
    const a = document.createElement("span");
    a.textContent = k;
    const b = document.createElement("span");
    b.textContent = v;
    row.append(a, b);
    els.metaPane.appendChild(row);
  }
}

function selectTab(tab) {
  const docs = tab === "documents";
  els.tabDocuments.classList.toggle("hec__tab--active", docs);
  els.tabMetadata.classList.toggle("hec__tab--active", !docs);
  els.docPane.hidden = !docs;
  els.metaPane.hidden = docs;
  if (!docs) renderMetadata();
}

async function loadContextPanel() {
  currentContext = signedIn ? await getActiveContext() : null;

  if (!signedIn) {
    els.contextPanel.hidden = true;
    els.docList.innerHTML = "";
    return;
  }

  // Signed in but the active tab isn't a supported record page (or is a chrome:// page).
  // Keep the panel visible with a hint + a manual entry so the feature is discoverable/testable.
  if (!currentContext) {
    els.contextPanel.hidden = false;
    selectTab("documents");
    els.contextType.textContent = "No record";
    els.contextName.textContent = "—";
    els.contextDesc.textContent = "Open a record page, or preview a UCEB record below.";
    els.docList.innerHTML = "";
    loadedDocuments = [];
    els.manualForm.hidden = false;
    setContextStatus(
      "Open a Salesforce / ServiceNow / Workday / Outlook record in this tab and press the refresh icon — or enter a UCEB record below to preview its content."
    );
    return;
  }

  els.contextPanel.hidden = false;
  els.manualForm.hidden = true;
  selectTab("documents");
  els.contextType.textContent = currentContext.businessObjectType;
  els.contextName.textContent = currentContext.businessObjectId;
  els.contextDesc.hidden = true;
  els.docList.innerHTML = "";
  setContextStatus("Loading related content…");

  try {
    const { documents } = await fetchContextDocuments(currentContext);
    renderDocuments(documents);
  } catch (err) {
    setContextStatus(`Couldn't load related content: ${err.message}`);
  }
}

function describeContext(ctx) {
  const src = ctx.source || "the current system";
  return `A specific ${ctx.businessObjectType} record's content, stored in the CIC Workspace via ${src}.`;
}

// ---- panel actions (Attach / History / Extract / tabs / drag-drop) ----
els.tabDocuments.addEventListener("click", () => selectTab("documents"));
els.tabMetadata.addEventListener("click", () => selectTab("metadata"));

els.actAttach.addEventListener("click", () => els.fileInput.click());
els.dropzone.addEventListener("click", () => els.fileInput.click());

els.dropzone.addEventListener("dragover", (e) => {
  e.preventDefault();
  els.dropzone.classList.add("is-drag");
});
els.dropzone.addEventListener("dragleave", () => els.dropzone.classList.remove("is-drag"));
els.dropzone.addEventListener("drop", (e) => {
  e.preventDefault();
  els.dropzone.classList.remove("is-drag");
  const dropped = Array.from(e.dataTransfer?.files ?? []);
  if (dropped.length) {
    pendingFiles = pendingFiles.concat(dropped);
    renderAttachments();
    updateSendEnabled();
    els.input.focus();
    setContextStatus(`${dropped.length} file(s) ready — press send to attach to this record.`);
  }
});

// Manual "test record" entry: preview any UCEB business object's documents without a live LOB page.
els.manualForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const businessObjectType = els.manualType.value.trim();
  const businessObjectId = els.manualId.value.trim();
  if (!businessObjectType || !businessObjectId) return;

  currentContext = {
    businessObjectType,
    businessObjectId,
    displayName: `${businessObjectType} ${businessObjectId}`,
    source: "manual",
  };
  els.manualForm.hidden = true;
  selectTab("documents");
  els.contextType.textContent = businessObjectType;
  els.contextName.textContent = businessObjectId;
  els.contextDesc.hidden = true;
  els.docList.innerHTML = "";
  setContextStatus("Loading related content…");

  try {
    const { documents } = await fetchContextDocuments(currentContext);
    renderDocuments(documents);
  } catch (err) {
    setContextStatus(`Couldn't load related content: ${err.message}`);
    els.manualForm.hidden = false;
  }
});

els.contextRefresh.addEventListener("click", () => loadContextPanel());

// Collapse/expand the context panel so the chat can use the full height.
els.contextToggle.addEventListener("click", () => {
  const collapsed = els.contextPanel.classList.toggle("hec--collapsed");
  els.contextToggle.setAttribute("aria-expanded", String(!collapsed));
  els.contextToggle.title = collapsed ? "Show panel" : "Hide panel";
});

// ---- auto-refresh: re-fetch related content whenever the active tab or record changes ----
// (In the side panel this keeps content in sync as you click through emails / open records.)
let contextReloadTimer = null;
function scheduleContextReload() {
  if (!signedIn) return;
  // Don't clobber a manual entry or a message the user is actively typing.
  const active = document.activeElement;
  if (active && (els.manualForm.contains(active) || active === els.input)) return;
  clearTimeout(contextReloadTimer);
  contextReloadTimer = setTimeout(() => loadContextPanel(), 300);
}

ext.tabs?.onActivated?.addListener(() => scheduleContextReload());
ext.tabs?.onUpdated?.addListener((_tabId, changeInfo, tab) => {
  if (tab?.active && (changeInfo.url || changeInfo.status === "complete")) scheduleContextReload();
});
ext.windows?.onFocusChanged?.addListener(() => scheduleContextReload());
// The content script re-detects on SPA navigation (e.g. opening another email) and broadcasts this.
ext.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "UCEB_CONTEXT") scheduleContextReload();
});

// ---- auth ----

async function refreshAuthState() {
  const session = await getSession();
  setSignedIn(Boolean(session));
}

els.authBtn.addEventListener("click", async () => {
  if (signedIn) {
    await clearTokens();
    setSignedIn(false);
    addMessage("Signed out.", "typing");
    return;
  }
  try {
    els.authBtn.disabled = true;
    await interactiveLogin();
    setSignedIn(true);
    addMessage("Signed in. Ask me anything.", "typing");
  } catch (err) {
    addMessage(`Sign-in failed: ${err.message}`, "error");
  } finally {
    els.authBtn.disabled = false;
  }
});

// ---- send message ----

els.input.addEventListener("input", () => {
  autoGrow();
  updateSendEnabled();
});

els.input.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    els.form.requestSubmit();
  }
});

els.form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const text = els.input.value.trim();
  const files = pendingFiles;
  if (!text && files.length === 0) return;

  addMessage(text || "(no message)", "user", files);

  // Reset composer state.
  els.input.value = "";
  autoGrow();
  pendingFiles = [];
  renderAttachments();
  els.input.disabled = true;
  els.attachBtn.disabled = true;
  els.sendBtn.disabled = true;

  const typing = addMessage("Agent is thinking…", "typing");

  try {
    // Send the raw text plus the attached files. When the popup knows which record is on the
    // browser screen, append a target-record hint so uploads and questions apply to that object
    // without the user typing its id.
    let outgoing = text;
    if (currentContext) {
      const hint =
        `\n\n[Context — the user is viewing this record in the browser: ` +
        `businessObjectType=${currentContext.businessObjectType}, ` +
        `businessObjectId=${currentContext.businessObjectId}. ` +
        `Use these when listing or uploading documents for "this record".]`;
      outgoing = (text + hint).trim();
    }
    const { reply } = await sendMessageToAgent(outgoing, files);
    typing.remove();
    addMessage(reply, "agent");
    // An upload may have changed the record's documents — refresh the panel.
    if (currentContext && files.length) loadContextPanel();
  } catch (err) {
    typing.remove();
    addMessage(`Error: ${err.message}`, "error");
  } finally {
    els.input.disabled = !signedIn;
    els.attachBtn.disabled = !signedIn;
    updateSendEnabled();
    els.input.focus();
  }
});

// ---- init ----
refreshAuthState();

