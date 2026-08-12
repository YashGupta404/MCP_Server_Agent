// Popup chat controller: wires the UI to auth.js + agent.js.

import { interactiveLogin, getSession, clearTokens } from "./auth.js";
import { sendMessageToAgent } from "./agent.js";

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
};

let signedIn = false;
/** @type {File[]} */
let pendingFiles = [];

// ---- UI helpers ----

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function addMessage(text, kind, files) {
  if (els.emptyState) els.emptyState.hidden = true;

  const el = document.createElement("div");
  el.className = `msg msg--${kind}`;

  const body = document.createElement("div");
  body.className = "msg__text";
  body.textContent = text;
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
    // Send the raw text plus the attached files. The BFF saves each file to disk and tells the
    // agent the on-server path so it can upload via the upload_document tool.
    const { reply } = await sendMessageToAgent(text, files);
    typing.remove();
    addMessage(reply, "agent");
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

