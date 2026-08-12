# How to Run the Project (Runbook)

_A standalone guide to start everything and demo the UCEB MCP agent end-to-end._

---

## What you're starting (4 processes + cloud)

| # | Process | Where | Port |
|---|---------|-------|------|
| 1 | **UCEB API** | local | `:5000` |
| 2 | **MCP server** | local | `:5200` |
| 3 | **Dev tunnel** | local -> public HTTPS | exposes `:5200` |
| 4 | **BFF** (extension backend) | local | `:5010` |
| - | **Agent Builder** | Hyland cloud | (the agent chat) |
| - | **Browser extension** | Chrome | (the chat UI) |

**All 4 local processes must stay running.** If any stops, the chain breaks. The extension talks to
the **BFF**, the BFF calls the **Agent Builder API**, the agent calls your **MCP server** through the
**tunnel**, and the MCP server calls the **UCEB API**.

> Full paths on this machine:
> - dotnet = `C:\Program Files\dotnet\dotnet.exe`
> - devtunnel = `C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe`
> - repo root = `C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api`

**Order matters:** UCEB API -> MCP server (sign in at warm-up) -> tunnel -> update Studio URL -> BFF -> extension sign-in -> chat.

---

## Step 1 - Start the UCEB API (Terminal 1)

Runs on `:5000` in **Development**, which already points at the **dev** environment
(`auth.dev`, `api.platform.dev`, `content.dev` — see `appsettings.Development.json`). Do **not** add
any `Security__…` overrides; those are what forced it onto staging before.

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:ASPNETCORE_URLS="http://localhost:5000"
& "C:\Program Files\dotnet\dotnet.exe" run --no-launch-profile `
  --project "C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.Api\Hyland.Experience.UCEB.Api.csproj"
```

> If you ever need staging again, re-add the `Security__AuthorityUrl` / `Security__NucleusApiBaseUrl`
> / `Security__ContentBaseUrl` = `…staging…` env vars **and** flip the MCP `appsettings.json` Auth
> endpoints back to `auth.staging…`. Dev and staging must always match on both sides.

Wait until it's listening on `:5000`. Verify (in any terminal):
```powershell
Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
```

---

## Step 2 - Start the MCP server (Terminal 2)

Runs on `:5200` with the **HTTP transport** (so the cloud can reach it). The API key is read
from user-secrets on startup.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run `
  --project "C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer\Hyland.Experience.UCEB.McpServer.csproj" -- --http
```

> **One-time only** (never through the assistant): set the API key in the McpServer project:
> `dotnet user-secrets set "McpApiKey:ApiKey" "<your-key>"`
>
> **Dev login client (must match the environment):** the MCP server signs in to UCEB using the
> **dev MCP client** (confidential, redirect `https://uceb-mcp-local.dev.hyland.com:5005/callback`).
> Its id/secret live in user-secrets — set them once:
> `dotnet user-secrets set "Auth:ClientId" "<dev-mcp-client-id>"`
> `dotnet user-secrets set "Auth:ClientSecret" "<dev-mcp-client-secret>"`
> If these are still the **staging** client, the startup sign-in fails with `invalid_client` — swap
> them for the dev ones.

Verify it's listening on `:5200`:
```powershell
Get-NetTCPConnection -LocalPort 5200 -State Listen -ErrorAction SilentlyContinue
```

> **Sign-in pops up at startup.** The MCP server **warms up the UCEB login the moment it starts** —
> a browser tab opens for the (staging) IAM sign-in. Complete it once. This is deliberate: the Agent
> Builder platform cancels any tool call that takes longer than ~100s, so the login must finish
> **before** the agent ever calls a tool. After this one sign-in the cached token (~15 min) serves
> every tool call instantly. If a later message hangs after a long idle, just restart this MCP
> terminal to re-run the warm-up.

---

## Step 3 - Start the dev tunnel (Terminal 3)

Gives `:5200` a public HTTPS URL so Hyland cloud can reach it.

We already have a **persistent, named tunnel** so the URL never changes. Just host it by name:

```powershell
$dt = "C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe"
& $dt user login -g               # login (token expires between sessions)
& $dt host giant-ant-2f6br43      # always host THIS tunnel by name
```

Fixed MCP endpoint (already set in Studio, don't change it):
**`https://4kw1kpcm-5200.asse.devtunnels.ms/mcp`**

> ✅ Because the tunnel is persistent and hosted **by name**, the URL stays the same across laptop
> restarts — you never have to touch the Studio Server URL again.
>
> ⚠️ **Do NOT** run a bare `devtunnel host -p 5200` (that creates a *new* random tunnel with a
> different URL), and **do NOT** run `devtunnel delete giant-ant-2f6br43`. Either would break the
> fixed URL and force you to re-add the MCP tool in Studio.
>
> Sanity check the tunnel + its URL anytime: `& $dt show giant-ant-2f6br43`

---

## Step 4 - Point the agent at the tunnel (Agent Builder Studio)

**One-time setup — already done.** Because the tunnel URL is now fixed, you should not need to touch
this again.

1. Open the registered MCP server in **Agent Builder Studio**.
2. **Server URL** = `https://4kw1kpcm-5200.asse.devtunnels.ms/mcp` (fixed — leave it).
3. Authentication = **API Key**, Header Name = `X-API-Key`, value = the same key from Step 2.
4. Save. (Only redo if you ever recreate the tunnel.)

---

## Step 4b - Start the BFF (Terminal 4)

The BFF is the extension's backend: it holds the plugin client secret, does the per-user PKCE token
exchange, and proxies chat to the Agent Builder API. Runs on `:5010` (HTTPS, dev cert).

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project "C:\Users\ygupta\OneDrive - Hyland\MCP_Server_Agent\bff"
```

> **One-time only** (never through the assistant): trust the dev cert (`dotnet dev-certs https
> --trust`) and set the plugin client secret (`dotnet user-secrets set "Auth:ClientSecret" "<secret>"`
> in the `bff` project).
>
> ⚠️ Restarting the BFF **wipes its in-memory sessions** — you must sign in again in the extension.

---

## Step 5 - Demo in the extension (Chrome)

1. Open the extension popup and **Sign in** (per-user IAM login via the BFF).
2. Ask the agent. The MCP server is already signed into UCEB (Step 2 warm-up), so replies are fast.

Suggested demo sequence:

1. **"Check the health status"** -> proves the whole chain is live.
2. **"List the document types"** -> returns the CIC document types.
3. **"Show the solution configurations"** -> real config data.
4. **"List the business object types"** -> the configured types.
5. **"List documents for record `<real-Salesforce-id>` of type `<object>`"** -> multi-step tool call.

When you're done, stopping the MCP server (Ctrl+C) **auto-logs-out** (clears the cached token), so
the next run starts fresh.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|--------|--------------|-----|
| **401** on tool calls | token expired, or API validating wrong IAM | run any tool again to re-login; confirm the staging `Security__...` vars are in the **same** terminal as the API |
| **500** on document tools | API not in Development (AWS/LaunchDarkly) | restart the API with the Step 1 block |
| **Studio can't connect** | tunnel not hosting, or `X-API-Key` mismatch | re-host with `devtunnel host giant-ant-2f6br43`, check the key (URL is fixed — don't change it) |
| **Tunnel URL suddenly different** | a bare `devtunnel host -p 5200` created a new random tunnel | stop it; host the named one: `devtunnel host giant-ant-2f6br43` |
| **Browser login doesn't open** | callback host mapping | ensure hosts file has `127.0.0.1 uceb-mcp-local.dev.hyland.com` |
| **Nothing on :5000 / :5200** | that process isn't running | check with `Get-NetTCPConnection -LocalPort <port> -State Listen` |
| **Agent "hangs" ~100s then errors** | login happened *during* a tool call (platform aborts at ~100s) | make sure you completed the **startup** sign-in (Step 2 warm-up); restart the MCP terminal to re-run it |
| **Every message re-prompts login** | MCP token not cached (old bug) | ensure MCP server is the current build (warm-up + `UcebApiClient` infinite timeout) and restarted |
| **Extension says not authenticated** | BFF restarted -> sessions wiped | sign in again in the extension |

---

## Quick reference - what each piece is

- **UCEB API** - the real backend the tools call.
- **MCP server** - wraps UCEB API calls as MCP tools; the thing the cloud connects to.
- **Dev tunnel** - public HTTPS bridge to the local MCP server.
- **BFF** - the extension's backend; holds the plugin client secret, does the per-user PKCE login, and proxies chat to the agent API.
- **Browser extension** - the chat UI the user talks to.
- **Agent Builder platform** - the cloud runtime (MCP client) that runs the agent loop and calls the tools.
- **The agent** - your saved config (model, prompt, tools) that the platform executes.

_For the full story of how it works and why, see `day-02.md` (§10 How it works, §11 Runbook)._
