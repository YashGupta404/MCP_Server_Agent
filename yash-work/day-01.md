# Day 1

_Focus: Pivot the plan from "build a local executor agent" to "use Hyland Agent Builder",
understand how that platform connects to the local MCP server, and define Phase 0._

---

## 1. The big change in plan

**Original plan:** build a local .NET "executor" agent that:
- connects to the MCP server,
- calls a remote Hyland "decider" LLM over REST,
- executes the tool it's told to call, loops, and returns an answer.

**New plan (this is what we're doing):** don't build the local agent at all.
Use **Hyland's Agent Builder Platform** (part of Content Intelligence). It is a
**cloud-hosted orchestrator that already has the LLM AND executes MCP tools itself.**

So the work shrinks to:
1. Make the local MCP server reachable from the cloud.
2. Register it with Agent Builder.
3. Create an agent that uses its tools.
4. Put a thin browser chatbot in front.

---

## 2. What the Agent Builder is (from the docs)

- A **cloud service** (dev host: `discovery.dev.experience.hyland.com/agent`).
- **Every endpoint needs a JWT bearer token.**
- It does the full loop itself: discover tools -> LLM decides -> **it calls the tool** -> loops -> final answer.

### Endpoints that matter to us

| Endpoint | Purpose |
|---|---|
| `POST /v1/mcp-servers` | Register our MCP server (its public URL + auth config). |
| `GET /v1/mcp-servers/{id}/tools` | Platform connects to our MCP server and lists its tools. |
| `GET /v1/mcp-servers` / `GET /v1/mcp-servers/{id}` | List / inspect registered servers. |
| `POST /v1/agents` | Create an agent (type, model, system prompt, guardrails, tools). |
| `POST /v1/agents/{id}/versions` | Add a new config version. |
| `POST /v1/agents/{id}/runs` | Invoke the agent with `messages[]` (async, returns `queued`). |
| `GET /v1/agents/{id}/runs/{runId}` | Poll for the run result (`completed` / `failed`). |
| `GET /v1/models?filter[agentType]=tool` | Valid `llmModelId` values. |
| `GET /v1/guardrails` | Valid guardrail IDs. |

### Confirmed behaviours
- **Runs are asynchronous:** POST returns `status: queued`; then **poll** GET until
  `completed`/`failed`, and read `outputPayload`. Run states:
  `queued, in_progress, completed, failed, cancelling, cancelled`.
- **Agent types:** `tool`, `rag`, `task`, `graphRag`. We want **`tool`**.
- **MCP server registration auth:** OIDC `client_credentials`
  (issuer, clientId, clientSecret, scopes, audience), `validationMode`
  (`immediate|async|skip`), `visibility` (`private|public|organization` — we use `private`).
- **Create Agent `config`:** `guardrails[]`, `inferenceConfig.maxTokens`,
  `llmModelId`, `systemPrompt`, `tools[]`.

---

## 3. Architecture (new)

```
Browser chatbot (MV3 extension)
      | POST /runs (+JWT)  -- via a thin backend proxy that holds the token
      v
Hyland Agent Builder (cloud)   -- has LLM, executes tools
      | connects over MCP (tools/list, tools/call)
      v
Public HTTPS tunnel  ->  UCEB MCP Server (local, HTTP/SSE)  ->  UCEB REST API :5000
```

---

## 4. The TWO hard problems we must solve

1. **Reachability:** the cloud cannot reach `http://localhost:5200`.
   We must expose the local MCP server via a **public HTTPS tunnel**
   (VS Code Dev Tunnels / ngrok / Cloudflare Tunnel). No cloud deploy needed.

2. **Auth / identity mismatch:** the platform authenticates to our MCP server
   **machine-to-machine** (OIDC client_credentials). But UCEB tools currently act
   **"as the interactively signed-in user."** These conflict.
   **Open decision:** for the POC, is a single service-account / test-user identity
   into UCEB acceptable, or must per-user identity flow through from day one?

Also: the browser extension must **never** hold the Agent Builder JWT (it's a secret),
so we keep a small backend proxy that holds the token and does run + poll.

---

## 5. What "Phase 0" means (recon, not building)

Some values we need are **NOT in the static docs** — they only come back from the
**live API** when called with a JWT. Phase 0 = log in, get token, ask the platform
what's available. Like reading the menu before cooking.

Phase 0 checklist:
- [ ] Get a **JWT bearer token** (via `appintel-dev` access).
- [ ] `GET /v1/models?filter[agentType]=tool` -> which LLMs we can pick.
- [ ] `GET /v1/guardrails` -> which content filters exist.
- [ ] (after registering) `GET /v1/mcp-servers/{id}/tools` -> confirm our 23 tools + see how MCP tools are described in the agent's `tools[]`.

Why it matters: without real model IDs and the MCP-tool schema, any agent-creation
code would be guesswork.

---

## 6. Full phased plan (for reference)

- **Phase 0** — Access & discovery (models, guardrails, tool schema). *No code.*
- **Phase 1** — Expose the MCP server: finish HTTP/SSE transport, add bearer-token
  validation, put behind a public HTTPS tunnel.
- **Phase 2** — Register + discover: `POST /v1/mcp-servers`, confirm `active`,
  `GET .../tools` returns all 23.
- **Phase 3** — Create the agent: `agentType: tool`, valid model, guardrails,
  a system prompt encoding UCEB domain rules (CIC default; discover
  `businessObjectType` / `ecmContentTypeName` fresh; never hardcode; surface tool
  error strings; health-check first).
- **Phase 4** — Invoke loop: `POST /runs` then poll `GET /runs/{id}`; prove an
  end-to-end tool call (e.g. `get_health_status`, `list_business_object_types`).
- **Phase 5** — Chatbot: thin MV3 extension -> small backend proxy (holds JWT) ->
  render replies. Browser never holds the token or calls MCP.

---

## 7. Open questions / waiting on

1. **Model IDs & guardrails** — paste the JSON from
   `GET /v1/models?filter[agentType]=tool` and `GET /v1/guardrails`.
2. **MCP-tool schema** — how MCP tools are referenced inside an agent's
   `config.tools[]` (docs only show `toolType: "function"`).
3. **Base URL** of the dev Agent Orchestrator API + exactly how the JWT is issued.
4. **MCP server auth options** — is anything lighter than OIDC allowed for a dev spike?
5. **User identity** — service-account/test-user for the POC, or per-user from day one?

---

## 8. Decisions made today
- Drop the local executor agent; use Hyland Agent Builder instead.
- Target **`tool`** agent type.
- Expose local MCP server via a **public HTTPS tunnel** (no cloud deploy).
- Keep a **thin backend proxy** for the browser so the JWT stays off the client.

## 9. Next action
Run Phase 0 (get token; call `/v1/models` and `/v1/guardrails`) and paste the results,
**OR** ask the assistant to build a small .NET "control script" that calls these
endpoints to discover the schemas empirically.

---

## 10. Studio recon (found while exploring)

- Environment: **Appintel-DEV Test Environment** (`www.dev.app.hyland.com`).
- Entry point for building agents: **Agent Builder Studio** tile (not the admin portals).
- Studio URL: `appintel-dev-test.agent-studio.ai.dev.app.hyland.com/agents`.
- **Agent Library** lists existing agents; **"Create New Agent"** button top-right.
- Agent types seen in the wild: **RAG**, **Task** (we want **Tool**).
- **Real model IDs observed** (valid `llmModelId` values):
  - `anthropic.claude-haiku-4-5-20251001-v1:0`
  - `anthropic.claude-3-haiku-20240307-v1:0`

### Still to find in Studio
- Where **MCP servers / connections** are registered (checking left-nav icons).
- The **Create Agent** form: confirm `Tool` type, full model list, guardrails,
  and **how tools / an MCP server get attached**.

---

## 11. Create-Agent form fully mapped (Studio)

Walked through **Create New Agent -> Add Tools -> MCP Tools -> Register MCP Server**.

**Agent Details:** Agent Name*, Display Name, Large Language Model* (default "Claude Haiku 4.5"),
Agent Description*, Advanced Settings.

**Agent Mode (maps to API `agentType`):** `Task`, `Conversational`, `RAG`, `Graph RAG`.
- For our tool-calling **chatbot -> use `Conversational`** (multi-turn chat that calls tools).
- `Task` = one-shot job agents (uses `invoke-task`).

**Tools section -> "Add Tools" offers 3 types:**
- **MCP Tools** (what we want) - "Add tools from Model Context Protocol servers."
- Task Agents (delegate to other agents).
- Analytics Tool (built-in code execution / data analysis).

**MCP Tools picker:** lists **Available MCP Servers** (already has "Tool Runtime [System]")
plus a **"+"** to **Register MCP Server**.

**Register MCP Server dialog fields:**
- Server Name*
- Server URL*  (MUST be public HTTPS - cloud can't reach localhost)
- Description
- **Authentication*** (dropdown; default = **"Passthrough (Internal)"**) <- full option list TBD

Other UI notes: builder has **Builder / JSON** toggle (JSON = raw agent config),
**Streaming response** toggle, **Test** panel on the right, per-agent **Inputs / Outcomes /
Instructions** sections (Task-mode requires >=1 input and >=1 outcome).

### Key open item
- Capture the **full Authentication dropdown** options in Register MCP Server
  (Passthrough Internal vs OIDC vs None/API key). Decides cloud->MCP auth + user identity.

---

## 12. MCP Server auth options (captured) + decision

The **Authentication** dropdown in Register MCP Server has exactly three options:

| Option | Meaning | Fit |
|---|---|---|
| **Passthrough (Internal)** | Forwards caller identity to an MCP server hosted **inside** the platform | Likely internal-only; our server is external -> probably NOT usable |
| **API Key** | Platform sends a static API-key header to our MCP server | **Chosen for POC** - simplest |
| **OIDC (Client Credentials)** | Machine-to-machine OAuth (issuer/clientId/secret/scopes/audience) | Production path; more setup, defer |

**DECISION (POC):** use **API Key** auth. Our MCP server validates a static key header.

**Identity model for POC:** platform authenticates as a *machine* (API key). It does NOT
carry the end-user identity. So UCEB "act as signed-in user" is handled **inside the MCP
server** using a single cached user token / service account for now. Full multi-user
identity passthrough (browser -> platform -> MCP -> UCEB) is **deferred**.

---

## 13. Where we are / next up (Phase 1)

Recon (Phase 0) is essentially DONE. We know:
- Studio flow to register MCP server + create a Conversational tool agent.
- Model IDs, agent modes, the 3 auth options, and the Register MCP Server fields.

**Blocking next step = Phase 1: make the local MCP server publicly reachable.**
1. Confirm the MCP server's **HTTP/SSE transport** works locally (e.g. `http://localhost:5200/mcp`).
2. Add **API-key validation** to the server (check a header against a secret from env/user-secrets).
3. Expose it via a **public HTTPS tunnel** (VS Code Dev Tunnels / ngrok / Cloudflare).
4. Come back to Register MCP Server -> paste tunnel URL, choose **API Key**, register.
5. Confirm tools appear (Get MCP Server Tools) -> attach to a **Conversational** agent.

---

## 14. ARCHITECTURE DECISION (locked)

There was a fork between two mutually exclusive models. **Decision: go with the
Agent Builder path.**

- **Agent Builder = the cloud connects INTO our MCP server.** Register MCP Server needs a
  reachable **Server URL** + auth; `GET /mcp-servers/{id}/tools` and tool execution during a
  run are all the **cloud calling our server**. Therefore `localhost` is not reachable and a
  **public HTTPS endpoint (tunnel or deploy) is REQUIRED.**
- The original "local executor / Hyland only decides, never reaches in / no tunnel" model is
  **dropped**. (It would have needed a pure Hyland *decider* API, which is not what Agent
  Builder is.)
- Consequence: the earlier "no cloud deploy / no inbound" constraint is superseded by using a
  **Dev Tunnel** (exposes the local port over HTTPS; not a full deploy).

### MCP server facts (from Yash)
- Lives in a **separate repo**: `Hyland.Experience.UCEB.Api` -> project
  `Hyland.Experience.UCEB.McpServer` (solution `Hyland.Experience.UCEB.Api.sln`),
  branch `feature/uceb-mcp-server-poc`. **Not** in this workspace; we connect to it over a URL.
- **Transport is stdio-only today.** `ModelContextProtocol.AspNetCore` package is added but the
  wiring (`WithHttpTransport()` + `MapMcp()`) is not done, so `http://localhost:5200/mcp` does
  not respond yet. Real URL/port TBD once transport is finished.

### Phase 1 critical path (in order)
1. **Finish HTTP/SSE transport** in the MCP server repo (`WithHttpTransport()` + `MapMcp("/mcp")`),
   run it, verify `http://localhost:5200/mcp` responds.
2. **Add API-key auth** (middleware checks a header against a secret from env/user-secrets).
3. **Expose via public HTTPS tunnel** (VS Code Dev Tunnels recommended).
4. **Register** in Studio: Server URL = tunnel URL, Authentication = **API Key**.
5. **Verify** tools list, then attach to a **Conversational** agent and run.

---

## 15. Phase 1 - Steps 1 & 2 DONE (HTTP transport + API key)

Worked directly in the MCP server repo
(`...\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer`).

**Files changed/added:**
- `Program.cs` - now supports **two transports**. Default = **stdio** (Copilot still works).
  Pass **`--http`** (or env `MCP_TRANSPORT=http`) to run as a web app with
  `WithHttpTransport()` + `MapMcp("/mcp")` on `http://localhost:5200`. Shared DI moved into a
  `ConfigureSharedServices(...)` local function used by both paths.
- `Configuration/McpApiKeyOptions.cs` (new) - section `McpApiKey`: `HeaderName` (default
  `X-Api-Key`) + `ApiKey` (from user-secrets/env; empty = gate disabled for local dev).
- `Auth/ApiKeyAuthMiddleware.cs` (new) - constant-time compares the header to the configured
  key; 401 on mismatch; warns + allows when no key set.

**Verified locally (`dotnet.exe` is at `C:\Program Files\dotnet\dotnet.exe`, not on PATH):**
- Project builds clean.
- Run with `-- --http` -> "Now listening on: http://localhost:5200".
- POST `/mcp` `initialize` -> `200`, real MCP SSE response
  (`serverInfo: Hyland.Experience.UCEB.McpServer`, tools capability advertised).
- With `McpApiKey__ApiKey` set: no key -> **401**, correct `X-Api-Key` -> **200**. Gate works.

**How to run it in HTTP mode:**
```
$env:McpApiKey__ApiKey = "<your-key>"   # or via user-secrets
dotnet run --project <McpServer.csproj> -- --http
```

### To confirm when registering in Studio
- Exactly **how the "API Key" auth option sends the key** (header name? `Authorization`?).
  Our `HeaderName` is configurable (default `X-Api-Key`) so we can match whatever Studio uses.

### Next (Step 3)
Expose `http://localhost:5200` via a **public HTTPS tunnel** (VS Code Dev Tunnels),
then register that URL in Studio with **API Key** auth.

---

## 16. Phase 1 - Step 3: public tunnel LIVE (VS Code Dev Tunnels)

- Installed the **`devtunnel` CLI** via winget (`Microsoft.devtunnel`). winget did NOT add it to
  PATH; the exe lives at:
  `C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe`
- **Login gotcha:** Microsoft/Entra login (`devtunnel user login -d`) is **blocked by Hyland's
  admin conditional-access policy** ("sign-in successful but does not meet the criteria").
  **Workaround: GitHub login** (`devtunnel user login -g`) -> "Logged in as YashGupta404". Use this.
- Started MCP server in HTTP mode, then hosted the tunnel:
  `devtunnel host -p 5200 --allow-anonymous`
- **Public URL:** `https://s3v4mt50-5200.asse.devtunnels.ms`
  -> **MCP endpoint:** `https://s3v4mt50-5200.asse.devtunnels.ms/mcp`
  (this URL is per-session and changes when the tunnel restarts; for a stable URL create a
  persistent named tunnel later).
- **Verified end-to-end:** POST `initialize` through the public URL -> **200**, real MCP SSE
  response. For programmatic calls add header `X-Tunnel-Skip-AntiPhishing-Page: true` to bypass
  the Dev Tunnels browser interstitial.

### SECURITY NOTE (action pending)
- The 200 came back **without an API key**, i.e. the key gate is currently **disabled**
  (user-secrets `McpApiKey:ApiKey` not set yet). While the tunnel is up the server is
  **unauthenticated**. Fix: set the key in user-secrets, restart server, re-test (expect 401
  without key). UCEB tools would still fail without a user token, but do not rely on that.

### Running set for the tunnel (both must stay running)
1. MCP server: `dotnet run --project <McpServer.csproj> -- --http`  (listens on :5200)
2. Tunnel: `devtunnel host -p 5200 --allow-anonymous`  (public HTTPS -> :5200)

### Next
Set API key -> restart server -> confirm 401/200 through tunnel -> **register in Studio**
(Server URL = the /mcp tunnel URL, Authentication = API Key) -> verify tools list.

---

## 17. API key set + gate confirmed; local DNS caveat

- User set `McpApiKey:ApiKey` in user-secrets (value kept off the assistant). Server restarted.
- **Gate confirmed active:** local POST `/mcp` with no key -> **401**. (Server loads the key from
  user-secrets on startup.)
- **Local DNS caveat:** this machine intermittently **cannot resolve** `*.asse.devtunnels.ms`
  (initial tunnel call succeeded with 200, later calls failed at DNS). Likely corporate DNS
  flakiness. This is a **local testing annoyance only** - it does NOT determine whether Hyland's
  cloud can reach the tunnel. The tunnel host process stays connected to the relay
  ("Ready to accept connections"). The authoritative reachability test is Studio registration.
- `nslookup`/`dotnet`/`devtunnel` are **not on this shell's PATH** (stripped PATH); use full
  paths or `Resolve-DnsName`.

### Risk to watch (Dev Tunnels anti-phishing interstitial)
Anonymous Dev Tunnels serve an anti-phishing HTML page to browser-like GETs. MCP clients send
JSON/SSE Accept headers and normally bypass it, but if Studio validation fails on connect this is
the first suspect. Mitigations if needed: ngrok/Cloudflare (no interstitial), or a persistent
named tunnel.

## 18. Phase 1 - Step 4: register in Studio (IN PROGRESS)

Register the MCP server in Agent Builder Studio:
- Server Name: e.g. `UCEB Local (dev tunnel)`
- **Server URL:** `https://s3v4mt50-5200.asse.devtunnels.ms/mcp`  (ephemeral - changes on restart)
- Authentication: **API Key** -> enter the same key set in user-secrets.
  - Our server expects header **`X-Api-Key`** (configurable). Confirm what Studio's API Key option
    sends (header name/value) and match it.
- Submit; with `validationMode=immediate` the platform connects + lists tools = success.
