<#
.SYNOPSIS
    Option A LOB switch: point the single UCEB MCP server at either the Salesforce or the Workday
    confidential client, then (re)start it.

.DESCRIPTION
    The MCP derives its record-document route ("api" vs "bow") automatically from the signed-in
    token's app key (see LobRoutingContext), so switching line-of-business only requires swapping the
    IAM confidential client id/secret (and the requested scopes). This script does exactly that by
    updating the MCP's user-secrets, then launches the MCP with `dotnet run -- --http`.

    Secrets are NEVER stored in this file or in source control. Each LOB's client secret is kept in the
    MCP's own user-secrets under `Lob:<lob>:ClientSecret`. Set it once per LOB with -SetSecret (you type
    the secret directly into the terminal; it is read as a SecureString and never echoed).

.PARAMETER Lob
    Which line of business to activate: 'salesforce' or 'workday'.

.PARAMETER SetSecret
    Prompt for this LOB's confidential client secret (typed securely into the terminal) and store it
    under `Lob:<lob>:ClientSecret` before activating. Use this the first time you switch to a LOB, or
    whenever the secret rotates.

.PARAMETER NoRun
    Update the user-secrets for the selected LOB but do NOT start the MCP.

.EXAMPLE
    # First time on Salesforce: fill in the ClientId below, then store the secret and run.
    ./switch-lob.ps1 -Lob salesforce -SetSecret

.EXAMPLE
    # Switch back to Workday (secret already stored) and start the MCP.
    ./switch-lob.ps1 -Lob workday
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('salesforce', 'workday')]
    [string]$Lob,

    [switch]$SetSecret,

    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'

# --- Fixed paths -----------------------------------------------------------------------------------
$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$McpProject = 'C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer\Hyland.Experience.UCEB.McpServer.csproj'

# --- Per-LOB IAM configuration ---------------------------------------------------------------------
# Edit ClientId/Scopes to match your IAM app registrations. The Workday values are already filled in
# from the current setup; fill in the Salesforce confidential client id before first use.
$LobConfig = @{
    workday    = @{
        ClientId = 'wsc-6f1759c9-08b0-4404-a0ab-31002fcf3cd3'
        Scopes   = 'openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp wdx'
    }
    salesforce = @{
        ClientId = 'wsc-dc7e0e46-06d2-4166-874f-149dc8614012'
        Scopes   = 'openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp'
    }
}

$config = $LobConfig[$Lob]
if ($config.ClientId -like '*<SET-*') {
    throw "The '$Lob' ClientId is not configured yet. Edit `$LobConfig in switch-lob.ps1 and set the real IAM confidential client id."
}

# --- Locate the MCP user-secrets store -------------------------------------------------------------
if (-not (Test-Path $McpProject)) {
    throw "MCP project not found at: $McpProject"
}

$csproj = Get-Content $McpProject -Raw
if ($csproj -notmatch '<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>') {
    throw "Could not find <UserSecretsId> in $McpProject"
}
$userSecretsId = $Matches[1]
$secretsPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\$userSecretsId\secrets.json"

# Load existing secrets (as an ordered hashtable of flat "A:B" keys) or start empty.
$secrets = [ordered]@{}
if (Test-Path $secretsPath) {
    $raw = Get-Content $secretsPath -Raw
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $obj = $raw | ConvertFrom-Json
        foreach ($p in $obj.PSObject.Properties) { $secrets[$p.Name] = $p.Value }
    }
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path $secretsPath) | Out-Null
}

function Save-Secrets {
    ($secrets | ConvertTo-Json -Depth 10) | Set-Content -Path $secretsPath -Encoding UTF8
}

$secretKey = "Lob:$Lob`:ClientSecret"

# One-time convenience: if this LOB's per-LOB secret slot is empty but the currently-active
# Auth:ClientSecret already belongs to this LOB (same ClientId), migrate it into the per-LOB slot so
# you don't have to re-type a secret you already had configured.
if (-not $secrets.Contains($secretKey) `
        -and $secrets['Auth:ClientSecret'] `
        -and ($secrets['Auth:ClientId'] -eq $config.ClientId)) {
    $secrets[$secretKey] = $secrets['Auth:ClientSecret']
    Save-Secrets
    Write-Host "Seeded $secretKey from the currently active Auth:ClientSecret." -ForegroundColor DarkGray
}

# --- Optionally (re)store this LOB's client secret -------------------------------------------------
if ($SetSecret) {
    Write-Host "Enter the confidential client secret for '$Lob' (input is hidden):" -ForegroundColor Cyan
    $secure = Read-Host -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    if ([string]::IsNullOrWhiteSpace($plain)) {
        throw "No secret entered - nothing changed."
    }
    $secrets[$secretKey] = $plain
    Save-Secrets
    Write-Host "Stored secret under $secretKey." -ForegroundColor Green
}

if (-not $secrets.Contains($secretKey) -or [string]::IsNullOrWhiteSpace($secrets[$secretKey])) {
    throw "No stored secret for '$Lob'. Run again with -SetSecret to enter it first."
}

# --- Activate the selected LOB ---------------------------------------------------------------------
$secrets['Auth:ClientId'] = $config.ClientId
$secrets['Auth:ClientSecret'] = $secrets[$secretKey]
$secrets['Auth:Scopes'] = $config.Scopes   # user-secrets override appsettings.json
Save-Secrets

Write-Host ""
Write-Host "Active LOB : $Lob" -ForegroundColor Green
Write-Host "ClientId   : $($config.ClientId)"
Write-Host "Scopes     : $($config.Scopes)"
Write-Host "Route      : $(if ($Lob -eq 'workday') { 'bow (auto-detected from token appkey wdx)' } else { 'api (auto-detected)' })"
Write-Host ""

if ($NoRun) {
    Write-Host "-NoRun set: secrets updated, MCP not started. Start it with:" -ForegroundColor Yellow
    Write-Host "  & `"$Dotnet`" run --project `"$McpProject`" -- --http"
    return
}

Write-Host "Starting the MCP (a browser window will open for login)..." -ForegroundColor Cyan
& $Dotnet run --project $McpProject -- --http
