// Talks to the agent THROUGH the BFF. The BFF attaches the user's bearer token and calls
// the real Agent Orchestrator /invoke endpoint, then returns { reply }.
// The extension only sends its opaque BFF session id — no IAM token lives here.

import { CONFIG } from "./config.js";
import { getSession } from "./auth.js";

const ext = globalThis.browser ?? globalThis.chrome;

const CONVERSATION_KEY = "uceb_conversation_id";

// A stable per-install conversation id groups turns together (the BFF forwards it as X-Session-ID).
async function getConversationId() {
  const stored = await ext.storage.local.get(CONVERSATION_KEY);
  if (stored[CONVERSATION_KEY]) return stored[CONVERSATION_KEY];
  const id = crypto.randomUUID();
  await ext.storage.local.set({ [CONVERSATION_KEY]: id });
  return id;
}

/**
 * Sends a user message (and any attached files) to the agent via the BFF and returns the reply.
 * Attached files are base64-encoded and forwarded to the BFF, which saves them to disk and tells
 * the agent the on-server path so it can call the upload_document tool.
 * @param {string} message
 * @param {File[]} [files]
 * @returns {Promise<{ reply: string }>}
 */
export async function sendMessageToAgent(message, files = []) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");
  const conversationId = await getConversationId();

  const attachments = await Promise.all((files ?? []).map(fileToAttachment));

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/chat`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-BFF-Session": sessionId,
    },
    body: JSON.stringify({ message, conversationId, attachments }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Agent call failed (${response.status}): ${detail}`);
  }
  return { reply: data.reply ?? "(no reply)" };
}

/**
 * Asks the BFF for the documents UCEB holds for a given business object (the record on the current
 * browser page). Deterministic path (BFF -> MCP list_documents), so it's fast — no LLM round trip.
 * @param {{ businessObjectType: string, businessObjectId: string, onlyMine?: boolean }} context
 * @returns {Promise<{ businessObjectType: string, businessObjectId: string, documents: Array<{docId: string, name: string, attributes: Record<string,string>}>, raw: string }>}
 */
export async function fetchContextDocuments(context) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/context`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-BFF-Session": sessionId,
    },
    body: JSON.stringify({
      businessObjectType: context.businessObjectType,
      businessObjectId: context.businessObjectId,
      onlyMine: Boolean(context.onlyMine),
    }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Context lookup failed (${response.status}): ${detail}`);
  }
  return {
    businessObjectType: data.businessObjectType ?? context.businessObjectType,
    businessObjectId: data.businessObjectId ?? context.businessObjectId,
    documents: Array.isArray(data.documents) ? data.documents : [],
    raw: data.raw ?? "",
  };
}

/**
 * Resolves a Workday worker's WID from a name or Employee ID via the BFF
 * (BFF /api/worker/resolve -> Workday Staffing REST API). Lets the panel auto-fill the
 * businessObjectId (WID) instead of the user copying the 32-char id out of Workday.
 * @param {string} query a worker name or Employee ID
 * @returns {Promise<{ query: string, total: number, wid: string|null, matches: Array<{wid: string, name: string, employeeId: string, businessTitle: string|null, supervisoryOrganization: string|null}> }>}
 */
export async function resolveWorker(query) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const response = await fetch(
    `${CONFIG.bff.baseUrl}/api/worker/resolve?q=${encodeURIComponent(query)}`,
    { method: "GET", headers: { "X-BFF-Session": sessionId } }
  );

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Worker lookup failed (${response.status}): ${detail}`);
  }
  return {
    query: data.query ?? query,
    total: Number(data.total ?? 0),
    wid: data.wid ?? null,
    matches: Array.isArray(data.matches) ? data.matches : [],
  };
}

/**
 * Lists the available UCEB document (content) types (BFF -> MCP list_document_types) to populate
 * the panel's upload doc-type dropdown.
 * @returns {Promise<string[]>}
 */
export async function fetchDocumentTypes() {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/doctypes`, {
    method: "GET",
    headers: { "X-BFF-Session": sessionId },
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Doc types lookup failed (${response.status}): ${detail}`);
  }
  return Array.isArray(data.types) ? data.types : [];
}

/**
 * Uploads one or more files DIRECTLY to a record's content in UCEB (BFF -> MCP staging ->
 * upload_staged_file), without going through the chatbot/LLM. Used by the panel's Upload section.
 * @param {{ businessObjectType: string, businessObjectId: string }} context
 * @param {string} docType the UCEB document (content) type, e.g. "dev-test-account"
 * @param {File[]} files
 * @returns {Promise<{ uploaded: string[], errors: string[] }>}
 */
export async function uploadDocuments(context, docType, files) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const attachments = await Promise.all((files ?? []).map(fileToAttachment));

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/upload`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-BFF-Session": sessionId,
    },
    body: JSON.stringify({
      businessObjectType: context.businessObjectType,
      businessObjectId: context.businessObjectId,
      ecmContentTypeName: docType,
      attachments,
    }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Upload failed (${response.status}): ${detail}`);
  }
  return {
    uploaded: Array.isArray(data.uploaded) ? data.uploaded : [],
    errors: Array.isArray(data.errors) ? data.errors : [],
  };
}

/**
 * Captures one or more files into a Workday LOB record via the Workday single-POST capture path
 * (BFF /api/capture -> MCP staging -> capture_document -> local UCEB /bow/core/documents), without
 * going through the chatbot/LLM. Used by the panel's Capture (Workday) section.
 * @param {{ businessObjectType?: string }} context
 * @param {string} documentTypeId the Workday document type id
 * @param {File[]} files
 * @param {object[]} [businessObjectAttributes] record-identifying attributes ([{id,name,value,dataType}])
 * @param {{ documentId?: string, createNewVersion?: boolean }} [options]
 * @returns {Promise<{ captured: string[], errors: string[] }>}
 */
export async function captureDocument(context, documentTypeId, files, businessObjectAttributes = [], options = {}) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const attachments = await Promise.all((files ?? []).map(fileToAttachment));

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/capture`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-BFF-Session": sessionId,
    },
    body: JSON.stringify({
      businessObjectType: context?.businessObjectType || "employee",
      businessObjectId: context?.businessObjectId || null,
      documentTypeId,
      businessObjectAttributes: Array.isArray(businessObjectAttributes) ? businessObjectAttributes : [],
      documentId: options?.documentId || null,
      createNewVersion: Boolean(options?.createNewVersion),
      attachments,
    }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Capture failed (${response.status}): ${detail}`);
  }
  return {
    captured: Array.isArray(data.captured) ? data.captured : [],
    errors: Array.isArray(data.errors) ? data.errors : [],
  };
}

/**
 * Resolves the Hyland viewer URL for a document (BFF -> MCP open_document_in_viewer) so the popup
 * can open it in a new browser tab.
 * @param {string} docId
 * @returns {Promise<string>} the viewer URL
 */
export async function openInViewer(docId) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/viewer`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-BFF-Session": sessionId,
    },
    body: JSON.stringify({ docId }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok || !data.url) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Open in viewer failed (${response.status}): ${detail}`);
  }
  return data.url;
}

/**
 * Fetches a rendered PREVIEW IMAGE for a document (BFF -> MCP -> UCEB file-preview) and returns a
 * blob: object URL the popup can drop straight into an <img>. This renders the document INSIDE the
 * panel without embedding the login-gated viewer SPA, so it works for both Salesforce and Workday.
 * The caller MUST revoke the returned URL (URL.revokeObjectURL) when done to avoid leaks.
 * A 404 means the rendition isn't provisioned for this doc — the caller can offer the viewer fallback.
 * @param {string} docId
 * @param {number} [pageNo] 1-based page (multi-page docs page through this)
 * @param {string} [renditionType] "preview" (page image) or "thumbnail"
 * @returns {Promise<string>} a blob: object URL for the preview image
 */
export async function fetchDocumentPreview(docId, pageNo = 1, renditionType = "preview") {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const params = new URLSearchParams({
    docId,
    pageNo: String(pageNo),
    renditionType,
  });

  const response = await fetch(`${CONFIG.bff.baseUrl}/api/document/preview?${params.toString()}`, {
    method: "GET",
    headers: { "X-BFF-Session": sessionId },
  });

  if (!response.ok) {
    let detail = `HTTP ${response.status}`;
    try {
      const data = await response.json();
      detail = data?.detail || data?.error || detail;
    } catch {
      /* non-JSON error body */
    }
    const err = new Error(`Preview failed (${response.status}): ${detail}`);
    err.status = response.status;
    throw err;
  }

  const blob = await response.blob();
  return URL.createObjectURL(blob);
}

/**
 * Fetches the raw document bytes for PDF.js rendering (Salesforce) or a viewer URL (Workday).
 * - Salesforce: returns { type: "bytes", objectUrl, contentType } — caller renders with PDF.js.
 *   The caller MUST call URL.revokeObjectURL(objectUrl) when done.
 * - Workday: returns { type: "viewer", viewerUrl } — caller opens the URL first-party.
 * @param {string} docId
 * @returns {Promise<{ type: "bytes", objectUrl: string, contentType: string } | { type: "viewer", viewerUrl: string }>}
 */
export async function fetchDocumentContent(docId) {
  const sessionId = await getSession();
  if (!sessionId) throw new Error("Not signed in.");

  const response = await fetch(
    `${CONFIG.bff.baseUrl}/api/document/content?docId=${encodeURIComponent(docId)}`,
    { method: "GET", headers: { "X-BFF-Session": sessionId } }
  );

  if (!response.ok) {
    let detail = `HTTP ${response.status}`;
    try { const d = await response.json(); detail = d?.detail || d?.error || detail; } catch {}
    const err = new Error(`Content fetch failed (${response.status}): ${detail}`);
    err.status = response.status;
    throw err;
  }

  const contentType = response.headers.get("Content-Type") ?? "";

  if (contentType.includes("json")) {
    const data = await response.json();
    return { type: "viewer", viewerUrl: data.viewerUrl };
  }

  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  return { type: "bytes", objectUrl, contentType };
}

/** Reads a File into a base64 payload the BFF can decode and save to disk. */
async function fileToAttachment(file) {
  const buffer = await file.arrayBuffer();
  return {
    name: file.name,
    mime: file.type || "application/octet-stream",
    dataBase64: bufferToBase64(buffer),
  };
}

/** Base64-encodes an ArrayBuffer in chunks (avoids call-stack limits on large files). */
function bufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  const chunkSize = 0x8000;
  let binary = "";
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}
