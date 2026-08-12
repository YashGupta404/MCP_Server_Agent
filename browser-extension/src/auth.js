// Login for a browser extension backed by a BFF.
// The extension runs the interactive Authorization Code + PKCE step (redirect_uri = its own
// chromiumapp.org URL, which IAM accepts) to obtain an authorization CODE. It then hands the
// code + PKCE verifier to the BFF, which adds the CLIENT SECRET, exchanges for tokens, and keeps
// them server-side. The browser only ever holds an opaque BFF session id.

import { CONFIG } from "./config.js";

// Cross-browser API handle (Firefox exposes `browser`, Chromium exposes `chrome`).
const ext = globalThis.browser ?? globalThis.chrome;

const SESSION_KEY = "uceb_bff_session";

// Session-scoped storage: kept only in memory for the current browser session and automatically
// cleared when the browser is fully closed. This gives us "auto sign-out on close" — reopening the
// popup within the same browser session stays signed in, but closing the browser requires a fresh
// login. Falls back to local storage on the rare build without storage.session.
const sessionStore = ext.storage.session ?? ext.storage.local;

// ---------- PKCE helpers ----------

function base64UrlEncode(bytes) {
  let str = "";
  for (const b of new Uint8Array(bytes)) str += String.fromCharCode(b);
  return btoa(str).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function randomVerifier() {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return base64UrlEncode(bytes);
}

async function challengeFor(verifier) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64UrlEncode(digest);
}

// ---------- session storage ----------

async function saveSession(sessionId) {
  await sessionStore.set({ [SESSION_KEY]: sessionId });
  return sessionId;
}

/** Returns the stored BFF session id, or null if not signed in. */
export async function getSession() {
  const result = await sessionStore.get(SESSION_KEY);
  return result[SESSION_KEY] ?? null;
}

// ---------- public API ----------

/**
 * Runs the interactive PKCE login, then exchanges the code for a BFF session (server-side).
 */
export async function interactiveLogin() {
  const redirectUri = ext.identity.getRedirectURL();

  const verifier = randomVerifier();
  const challenge = await challengeFor(verifier);
  const state = randomVerifier();

  const authUrl =
    `${CONFIG.auth.authorizeEndpoint}?` +
    new URLSearchParams({
      response_type: "code",
      client_id: CONFIG.auth.clientId,
      redirect_uri: redirectUri,
      scope: CONFIG.auth.scopes,
      state,
      code_challenge: challenge,
      code_challenge_method: "S256",
      prompt: "login",
    }).toString();

  const redirectResponse = await ext.identity.launchWebAuthFlow({
    url: authUrl,
    interactive: true,
  });

  const returned = new URL(redirectResponse);
  if (returned.searchParams.get("state") !== state) {
    throw new Error("OAuth state mismatch — aborting.");
  }
  const code = returned.searchParams.get("code");
  if (!code) {
    const error = returned.searchParams.get("error") ?? "unknown_error";
    throw new Error(`Authorization failed: ${error}`);
  }

  // Hand the code + verifier to the BFF; it adds the secret and exchanges for tokens.
  const response = await fetch(`${CONFIG.bff.baseUrl}/auth/exchange`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ code, codeVerifier: verifier, redirectUri }),
  });

  const data = await response.json().catch(() => ({}));
  if (!response.ok || !data.session) {
    const detail = data?.detail || data?.error || `HTTP ${response.status}`;
    throw new Error(`Token exchange failed: ${detail}`);
  }

  return saveSession(data.session);
}

/** Signs out: asks the BFF to drop the server-side session, then clears local state. */
export async function clearTokens() {
  const sessionId = await getSession();
  if (sessionId) {
    try {
      await fetch(`${CONFIG.bff.baseUrl}/auth/logout`, {
        method: "POST",
        headers: { "X-BFF-Session": sessionId },
      });
    } catch {
      /* best-effort — clearing local state below is what matters */
    }
  }
  await sessionStore.remove(SESSION_KEY);
  // Also drop any legacy value that may have been persisted in local storage by older builds.
  await ext.storage.local.remove(SESSION_KEY);
}
