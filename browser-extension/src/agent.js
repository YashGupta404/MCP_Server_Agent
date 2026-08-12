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
