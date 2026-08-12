# UCEB Agent Chatbot (browser extension)

A cross-browser (Chromium + Firefox) Manifest V3 popup chatbot that signs in with **OAuth
Authorization Code + PKCE** and talks to the Hyland Agent Builder agent
(`a4374edc-32b0-4d01-bc45-8dbc496ed9c6`).

## Status: scaffold

Working: popup chat UI, PKCE login flow, token storage/refresh, message send/receive plumbing.
**Two placeholders must be filled before it runs for real:**

1. **Agent endpoint** — `src/config.js` → `agent.chatUrl` (and `agent.streaming`).
   Capture the real endpoint from DevTools (see `../yash-work/day-03.md` §3), then adjust
   `buildRequest()` / `parseReply()` in `src/agent.js` to match the payload/response shape.
2. **OAuth public client** — `src/config.js` → `auth.clientId`.
   Register a **public/SPA client** in IAM (PKCE, no secret) and allow-list the extension
   **redirect URI**. The redirect URI is logged to the service-worker console on the first
   sign-in attempt (`chrome.identity.getRedirectURL()`), e.g. `https://<id>.chromiumapp.org/`.

## Files

| File | Purpose |
|---|---|
| `manifest.json` | MV3 manifest (identity + storage perms, host perms for IAM + agent host) |
| `src/config.js` | Endpoints, scopes, client id, agent id (**fill the TODOs**) |
| `src/auth.js` | PKCE login, token exchange/refresh, storage |
| `src/agent.js` | Sends a message to the agent, parses the reply |
| `src/popup.html/.css/.js` | The chat UI |
| `src/background.js` | Minimal service worker |

## Load it for testing

**Chrome/Edge:** `chrome://extensions` → enable **Developer mode** → **Load unpacked** →
select the `browser-extension/` folder.

**Firefox:** `about:debugging` → **This Firefox** → **Load Temporary Add-on** →
select `browser-extension/manifest.json`.

Then click the toolbar icon to open the popup, **Sign in**, and chat.

> Note: on the first sign-in, open the extension's service-worker console to copy the
> **redirect URI** that must be registered on the IAM client.
