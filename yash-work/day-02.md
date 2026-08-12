# Day 2

_Focus: Get the MCP server registered in Agent Builder Studio, create the agent, and
make the agent actually call tools end-to-end. Most of the day was debugging the chain
from the cloud agent all the way down to the local UCEB API._

---

## 1. Where Day 1 left off

- HTTP transport + API-key gate on the MCP server were working (401 without key, 200 with key).
- Dev tunnel was live, exposing the local server (`:5200`) to the internet.
- We were about to register the server in Agent Builder Studio.

Day 2 is basically: registration -> agent -> "why won't the tools work" debugging.

---

## 2. Blocker: 403 when registering the MCP server

- Registering the server hits `POST /bff/mcp-servers` in Studio.
- Got **403 "User does not have required permissions."**
- Root cause: **account-level permission**, nothing to do with our code or the tunnel.
- Fix: **mentor granted the permission.** After that, registration went through.

**Lesson:** a 403 on a platform endpoint usually means *your account*, not your setup. Check
permissions before you tear apart your own config.

---

## 3. Dev tunnel is fragile between sessions

Two things bit us:

1. **Tunnel URLs are ephemeral** - every time you restart the tunnel you get a *new* URL.
   Old URL (`s3v4mt50-5200.asse.devtunnels.ms`) went dead; new one is
   `j940mh01-5200.jpe1.devtunnels.ms`.
2. **The devtunnel login token expires** between sessions. Had to re-run
   `devtunnel user login -g` (GitHub login; Microsoft/Entra login is blocked by Hyland policy).

Because the URL changes, **the Server URL in Studio has to be updated every restart.**
(Future fix: a persistent *named* tunnel so the URL is stable.)

**Running set that must stay up:**
1. MCP server: `dotnet run --project <McpServer.csproj> -- --http` (listens on `:5200`)
2. Tunnel: `devtunnel host -p 5200 --allow-anonymous`

---

## 4. Registered in Studio + created the agent

- Registered the MCP server (Server URL = the `/mcp` tunnel URL, Authentication = **API Key**,
  Header Name = `X-API-Key`). HTTP header names are case-insensitive, so `X-API-Key` and our
  server's `X-Api-Key` match.
- With `validationMode=immediate`, the platform connected and listed our tools = success.
- Created the agent:
  - Name: **UCEB_MCP_Server_Agent**
  - Mode: **Conversational** (note: mode **cannot be changed after creation**)
  - Model: **Claude Sonnet 4.6**
  - Agent ID: `a4374edc-32b0-4d01-bc45-8dbc496ed9c6`

At this point the full chain exists: **Agent -> Hyland cloud -> dev tunnel -> local MCP server**.

---

## 5. First tool call failed - the "TaskGroup" error

First `get_health_status` blew up with "unhandled errors in a TaskGroup". Two separate bugs
were hiding behind that one ugly message:

### Bug A: content-root - appsettings.json wasn't loading
In HTTP mode the app's **content root** was the folder we launched from
(`MCP_Server_Agent`), not the project folder. So `appsettings.json` (which holds the Auth
endpoints and scopes) never loaded - only user-secrets did. The auth URL came out malformed,
so the browser login couldn't even open, the request hung, and eventually hit the 100s
HttpClient timeout -> `TaskCanceledException`.

**Fix (in `Program.cs`, `RunHttpAsync`):** pin the content root to the app's own folder:
```csharp
WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
```
After this, `appsettings.json` loads correctly (content root is now `...\bin\Debug\net8.0\`).

### Bug B: nobody was logged in yet
Our auth uses an **interactive user login** (Authorization Code + PKCE) - a browser opens and
*you* log in. The token is then cached in memory. Fix: run the **`login`** tool first so the
token is cached, *then* other tools reuse it.

After both fixes, **`get_health_status` worked end-to-end.** So did **`get_solution_configurations`**,
which returned real CIC data (business objects like `account`, `opportunity`, `yash_test`, etc.),
with a viewer URL on `studio.staging.app.hyland.com` -> confirming our data lives in **staging**.

---

## 6. The big one: `list_document_types` - a 500, then a 401

### First it 500'd (AWS / LaunchDarkly)
The UCEB API stack trace showed:
`Amazon.Runtime.AmazonClientException: Failed to resolve AWS credentials`
inside `LaunchDarklyExtensions.AddLaunchDarklyClient`.

Why: the API was running in **Production** (ASPNETCORE_ENVIRONMENT was unset, which defaults
to Production). In Production it tries to pull the LaunchDarkly SDK key from **AWS Secrets
Manager**, and we have no AWS creds locally -> 500. In Development it skips all that and uses a
local feature-flag store.

Fix attempt: run the API in **Development**. The 500 disappeared...

### ...then it 401'd (environment mismatch)
In Development, `document-types` returned **401 Unauthorized**. Root cause = **IAM environment
mismatch**:

| | Production (`appsettings.json`) | Development (`appsettings.Development.json`) |
|---|---|---|
| Token authority | `auth.**staging**.app.hyland.com` | `auth.**dev**.app.hyland.com` |
| Content | `content.**staging**` | `content.**dev**` |
| Nucleus | `api.platform.**staging**` | `api.platform.**dev**` |

Our MCP server logs the user in against **staging**, and the user's CIC data is in **staging**.
But Development mode makes the API validate tokens against **dev** IAM -> the staging token is
rejected -> 401.

So: **Production** = right environment (staging) but breaks on AWS. **Development** = no AWS but
wrong environment (dev). Neither works out of the box.

---

## 7. The fix: Development mode + force the Security URLs back to staging

Key realization: the auth/content settings all live under the **`Security`** config section,
and **environment variables outrank appsettings**. So we can run in **Development** (skips
AWS/LaunchDarkly) but **override the `Security` URLs to staging** (matches our token + data):

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:Security__AuthorityUrl="https://auth.staging.app.hyland.com/idp"
$env:Security__NucleusApiBaseUrl="https://api.platform.staging.app.hyland.com"
$env:Security__ContentBaseUrl="content.staging.app.hyland.com"
& "C:\Program Files\dotnet\dotnet.exe" run --project "<path>\Hyland.Experience.UCEB.Api.csproj"
```

Best of both worlds:
- **Development** -> no AWS/LaunchDarkly (fixes the 500).
- **Security -> staging** -> matches the MCP login token and the staging CIC data (should fix the 401).

No code change, launch-time only, fully reversible.

---

## 8. Status at end of Day 2

- ✅ MCP server registered in Studio; conversational agent created.
- ✅ Content-root bug fixed and build verified.
- ✅ Interactive login working (token caches).
- ✅ `get_health_status` works end-to-end.
- ✅ `get_solution_configurations` returns real staging CIC data.
- ✅ AWS/LaunchDarkly 500 resolved by running in Development.
- ✅ **`list_document_types` now works** - returns the 7 CIC document types
  (`hfs-base-type`, `bills-content-type`, `case-content-type`, `dev-test-account`,
  `multivalue-content-type`, `opportunity-content-type`, `prescription`).

### How the last 401 got fixed
- Checked `get_token_claims`: token was **good** (issuer = staging, valid, admin roles,
  `uceb` scope, `document.query` + `cic_configuration.read` permissions).
- Read the API's JWT setup (`ServicesExtensions.cs`): **`ValidateAudience = false`** - only the
  **issuer** is validated (`ValidIssuer = Security:authorityUrl`). So audience was never the problem.
- A 401 with a valid **staging** token therefore meant the running API was still validating against
  **dev** - the earlier `Security__...` env overrides had been set in a *different* terminal than the
  one running the API, so they never reached the process.
- Fix: stopped the running API (PID on :5000) and **relaunched it in one controlled shell** with:
  ```powershell
  $env:ASPNETCORE_ENVIRONMENT="Development"
  $env:ASPNETCORE_URLS="http://localhost:5000"
  $env:Security__AuthorityUrl="https://auth.staging.app.hyland.com/idp"
  $env:Security__NucleusApiBaseUrl="https://api.platform.staging.app.hyland.com"
  $env:Security__ContentBaseUrl="content.staging.app.hyland.com"
  dotnet run --no-launch-profile --project <...Hyland.Experience.UCEB.Api.csproj>
  ```
- Retried `list_document_types` -> **200 with data.** Chain proven end-to-end.

**Full chain now working:** Agent -> Hyland cloud -> dev tunnel -> MCP server -> staging user token -> UCEB API -> CIC content.

---

## 9. Lessons from Day 2

- A 403 on a platform endpoint = **check account permissions first**, not your own config.
- Dev tunnel URLs are **ephemeral** and the login **expires** - both need refreshing each session.
- One ugly error ("TaskGroup") can hide **multiple** root causes - peel them one at a time.
- ASPNETCORE_ENVIRONMENT controls **two** things at once here: which appsettings loads **and**
  whether AWS/LaunchDarkly runs. That coupling is what made this tricky - env-var overrides
  let us decouple it.
- Keep **secrets off the assistant** - API key and client secret are set in the user's own
  terminal / user-secrets.

---

## 10. How the whole thing works (plain English, real terms)

**Goal:** let a cloud AI agent (Hyland **Agent Builder**) use the tools on my **local MCP server**,
so chatting with the agent can actually query the **UCEB API** on my behalf.

**The pieces:**
- **MCP server** (`Hyland.Experience.UCEB.McpServer`) - a .NET app that wraps UCEB API calls as
  **MCP tools** (23 of them). MCP = **Model Context Protocol**, a standard way to expose tools to LLMs.
- **UCEB API** - the real backend the tools call, on `localhost:5000`.
- **Agent Builder platform** - Hyland's cloud **agent runtime**; it's the **MCP client** that runs
  the reason-act loop, consults the LLM, and calls my tools.
- **The agent** (`UCEB_MCP_Server_Agent`) - a **config** (model Claude Sonnet 4.6, system prompt,
  attached tools) that the platform executes. The *platform* is the actor; the *agent* is its script.
- **Dev tunnel** - gives my local MCP server (`localhost:5200`) a **public HTTPS URL** so the cloud
  can reach it.

**Who calls the tools:** the **LLM decides** which tool; the **platform actually calls it** (MCP
client) over HTTPS; **my MCP server runs** the C# tool code. LLM = decides, platform = calls,
server = runs.

**One request end-to-end:**
1. I type a message; platform sends it + the tool list to the LLM.
2. LLM returns a *decision* ("call `list_document_types`") - it doesn't call anything itself.
3. Platform turns that into a **JSON-RPC `tools/call`** over **HTTPS** with the **`X-API-Key`**,
   through the **tunnel**, to my MCP server.
4. MCP server runs the tool -> calls the **UCEB API** -> returns the result back up.
5. Platform feeds the result to the LLM, which writes the final answer. (reason -> call -> observe -> repeat)

**Two auth layers:**
- **API key** (`X-API-Key`) gates *who can reach* the MCP server (the platform must present it).
- **User token** decides *who the tools act as*. Interactive **OAuth Auth Code + PKCE**: the `login`
  tool opens a browser, I sign into **staging IAM**, token is **cached**, and every UCEB call sends
  `Authorization: Bearer <token>`. So the agent calls the tool, but the tool acts as **me**.

---

## 11. Runbook - how to run everything and demo it

> Full paths (this machine): dotnet = `C:\Program Files\dotnet\dotnet.exe`;
> devtunnel = `C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe`.
> Repo root = `C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api`.

**Order matters:** UCEB API -> MCP server -> tunnel -> update Studio URL -> login -> chat.

### Terminal 1 - UCEB API (:5000, Development + staging override)
```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:ASPNETCORE_URLS="http://localhost:5000"
$env:Security__AuthorityUrl="https://auth.staging.app.hyland.com/idp"
$env:Security__NucleusApiBaseUrl="https://api.platform.staging.app.hyland.com"
$env:Security__ContentBaseUrl="content.staging.app.hyland.com"
& "C:\Program Files\dotnet\dotnet.exe" run --no-launch-profile `
  --project "C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.Api\Hyland.Experience.UCEB.Api.csproj"
```
Wait for it to listen on :5000. (Development = no AWS/LaunchDarkly; staging override = accepts my staging token.)

### Terminal 2 - MCP server (:5200, HTTP transport)
```powershell
& "C:\Program Files\dotnet\dotnet.exe" run `
  --project "C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer\Hyland.Experience.UCEB.McpServer.csproj" -- --http
```
The **API key** is read from user-secrets on startup (set once with `dotnet user-secrets set "McpApiKey:ApiKey" "<key>"` in the McpServer project - never through the assistant).

### Terminal 3 - Dev tunnel (public HTTPS -> :5200)
```powershell
$dt = "C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe"
& $dt user login -g          # GitHub login; token expires between sessions
& $dt host -p 5200 --allow-anonymous
```
Copy the printed **tunnel URL** (e.g. `https://xxxx-5200.jpe1.devtunnels.ms`). The MCP endpoint is that URL **+ `/mcp`**. This URL is **ephemeral** - it changes every restart.

### Studio - point the agent at the current tunnel URL
- In Agent Builder Studio, open the registered MCP server and set **Server URL** =
  `https://<current-tunnel>/mcp`, Authentication = **API Key** (same key), Header Name = `X-API-Key`.
- (Must be redone whenever the tunnel URL changes.)

### Demo in Agent Chat
1. Run **`login`** -> finish the browser sign-in (staging IAM). Token caches.
2. `get_health_status` -> proves the chain is live.
3. `List the document types` -> returns the 7 CIC types.
4. `get_solution_configurations` / `list_business_object_types` -> shows real config.
5. `List documents for record <real-Salesforce-id> of type <object>` -> the multi-step tool call.

### Quick sanity checks if something breaks
- **401 on tool calls** -> token expired: re-run `login`; or API validating wrong IAM: confirm the
  staging `Security__...` env vars are in the **same** terminal as the API.
- **Studio can't connect** -> tunnel URL changed or tunnel/host not running, or `X-API-Key` mismatch.
- **500 on document tools** -> API not in Development (AWS/LaunchDarkly); restart with the block above.
- **Nothing on :5000 / :5200** -> that process isn't running; check with
  `Get-NetTCPConnection -LocalPort 5000 -State Listen`.
