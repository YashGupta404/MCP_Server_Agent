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

.NOTES
    THIS SCRIPT AUTO-FLIPS THE IAM ENDPOINTS PER LOB.
    It swaps the confidential client id/secret + scopes (in MCP user-secrets) AND flips the MCP
    appsettings.json IAM endpoints (Auth:AuthorizeEndpoint/TokenEndpoint/EndSessionEndpoint +
    Uceb:NucleusApiBaseUrl/ContentBaseUrl) to match the selected LOB's environment (the Iam field in
    $LobConfig: 'dev' -> auth.dev / api.platform.dev / content.dev ; 'staging' -> the staging hosts).
    So 'workday'/'salesforce' (dev) and 'workday-staging' (staging) each land on the correct IAM in ONE
    command. Only one LOB runs per MCP process.

    SECRETS ARE NEVER STORED IN THIS FILE. Each LOB's client secret lives ONLY in the MCP user-secrets
    under Lob:<lob>:ClientSecret (set once via -SetSecret, or directly via
    `dotnet user-secrets set "Lob:<lob>:ClientSecret" "..."`). This file holds only the NON-secret
    client ids + scopes + Iam. On activate it copies the LOB's id+secret into Auth:ClientId/ClientSecret.
    (If you set the client via Auth:ClientId/ClientSecret directly, activating that same LOB auto-migrates
    the secret into its Lob:<lob>:ClientSecret slot when the ids match.)

    ONBASE/CFS FOR WORKDAY (separate from this script - it only swaps the MCP login client):
    To store into OnBase via Workday you ALSO need, on the UCEB API side (NOT the MCP):
      1. The CFS token-exchange client in the UCEB API user-secrets (UserSecretsId uceb-api-local-9f2c1a7e):
           dotnet user-secrets set "TokenExchange:ClientId" "<id>"     --project "<UCEB API csproj>"
           dotnet user-secrets set "TokenExchange:ClientSecret" "<secret>" --project "<UCEB API csproj>"
         GrantType lives in the UCEB API appsettings.Development.json TokenExchange section
         (v3 = urn:hyland:params:oauth:grant-type:subscription-token-exchange-v3; some envs use
         v2 = subscription_token_exchange_v2).
      2. MCP appsettings Uceb:WorkdayDocumentTypeGroupId = "101" for OnBase (CIC/native uses "hcmisbeemployee").
    The TokenExchange client AND the Auth:* endpoints must be the SAME environment as this login client
    (dev login client -> dev exchange client + auth.dev ; staging login client -> staging exchange client
    + auth.staging). GOTCHA: the UCEB Workday document-types 404 "Document types not found" MASKS CFS
    errors - always read the UCEB API log for the real cause (e.g. token exchange unsupported_grant_type).

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
    [ValidateSet('salesforce', 'salesforce-staging', 'workday', 'workday-staging')]
    [string]$Lob,

    [switch]$SetSecret,

    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'

# --- Fixed paths -----------------------------------------------------------------------------------
$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$McpProject = 'C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer\Hyland.Experience.UCEB.McpServer.csproj'
$McpAppSettings = 'C:\Users\ygupta\OneDrive - Hyland\Hyland.Experience.UCEB.Api\src\Hyland.Experience.UCEB.McpServer\appsettings.json'

# --- Per-LOB IAM configuration ---------------------------------------------------------------------
# Edit ClientId/Scopes to match your IAM app registrations. The Workday values are already filled in
# from the current setup; fill in the Salesforce confidential client id before first use.
#
# =====================================================================================================
# !!! DO NOT ADD 'environment_authorization' TO THE workday-staging SCOPES !!!  (verified 2026-09-15)
# -----------------------------------------------------------------------------------------------------
# The workday-staging login user (arizzo@aurahyland.onmicrosoft.com) is NOT granted the
# 'environment_authorization' scope on the wsc-c8e114b2 client. If that scope is requested, IAM
# authenticates arizzo fine BUT then fails the consent/authorization step with the error page
# "Unable to perform authorization" (the callback never returns a code, so the MCP just times out).
# Proven by isolation: full scopes => fails; drop ONLY environment_authorization => login succeeds.
#
# CONSEQUENCE / GOTCHA: the token's app-key claim (hxp_authorization -> appkey=wdx, which auto-routes
# Workday record-doc calls to 'bow') is delivered BY the environment_authorization scope, NOT by the
# 'wdx' scope. With environment_authorization removed the token has NO appkey, so routing would fall
# back to 'api'. That is why every LOB below now pins an explicit 'BasePath' (workday* => 'bow',
# salesforce* => 'api') which this script writes to Uceb:BusinessObjectBasePath so routing stays
# correct WITHOUT relying on the token claim.
#
# If arizzo ever gets the environment_authorization grant back (ask IAM/mentor), you MAY re-add the
# scope AND it becomes harmless because BasePath already forces the right route.
# =====================================================================================================
$LobConfig = @{
    workday    = @{
        ClientId    = 'wsc-6f1759c9-08b0-4404-a0ab-31002fcf3cd3'
        Scopes      = 'openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp wdx'
        Iam         = 'dev'
        UcebBaseUrl = 'http://localhost:5000'
        BasePath    = 'bow'
    }
    # Staging Hyland-for-Workday confidential client (Appintel-Staging env, user arizzo). Activating
    # this LOB AUTO-FLIPS the MCP appsettings IAM endpoints to staging (Iam='staging') AND points Uceb:BaseUrl
    # at the DEPLOYED staging UCEB - which does the CFS token exchange INTERNALLY (creds in AWS), so NO local
    # UCEB API and NO local TokenExchange creds are needed for Workday. The secret is NOT here - it lives in
    # MCP user-secrets (Lob:workday-staging:ClientSecret / Auth:ClientSecret).
    # NOTE: NO 'environment_authorization' scope here on purpose - see the big warning above. Routing to
    # 'bow' is forced via BasePath below (the appkey claim is gone without that scope).
    'workday-staging' = @{
        ClientId    = 'wsc-c8e114b2-4e5f-4f18-829e-063561998bbb'
        Scopes      = 'openid profile offline_access uceb hxp.nucleus.account hxp wdx'
        Iam         = 'staging'
        UcebBaseUrl = 'https://api.uceb.app-intel.staging.app.hyland.com'
        BasePath    = 'bow'
    }
    salesforce = @{
        ClientId    = 'wsc-dc7e0e46-06d2-4166-874f-149dc8614012'
        Scopes      = 'openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp'
        Iam         = 'dev'
        UcebBaseUrl = 'http://localhost:5000'
        BasePath    = 'api'
    }
    # Staging Salesforce confidential client (Appintel-Staging Prod env). Iam='staging' + deployed staging
    # UCEB (handles CIC content by token; no local UCEB/CFS exchange needed for Salesforce/CIC-native).
    'salesforce-staging' = @{
        ClientId    = 'wsc-f3ec0fd9-47a3-4f03-a67f-28b70100141b'
        Scopes      = 'openid profile offline_access uceb environment_authorization hxp.nucleus.account hxp'
        Iam         = 'staging'
        UcebBaseUrl = 'https://api.uceb.app-intel.staging.app.hyland.com'
        BasePath    = 'api'
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

# Flip the MCP appsettings IAM endpoints (Auth:* + Uceb Nucleus/Content) to match the LOB's environment.
# Literal string replace so JSON formatting + unrelated URLs (e.g. Studio ViewerBaseUrl on *.studio.dev.*)
# are untouched. NO SECRETS are written here.
function Set-McpIamEndpoints([string]$iam) {
    if (-not (Test-Path $McpAppSettings)) {
        Write-Host "MCP appsettings not found at $McpAppSettings - skipping endpoint flip." -ForegroundColor DarkYellow
        return
    }
    $txt = Get-Content $McpAppSettings -Raw
    if ($iam -eq 'staging') {
        $txt = $txt.Replace('https://auth.dev.app.hyland.com/idp', 'https://auth.staging.app.hyland.com/idp')
        $txt = $txt.Replace('https://api.platform.dev.app.hyland.com', 'https://api.platform.staging.app.hyland.com')
        $txt = $txt.Replace('"content.dev.app.hyland.com"', '"content.staging.app.hyland.com"')
        $txt = $txt.Replace('https://bravo.cic-viewer.sandbox.app.hyland.com', 'https://cic-viewer.staging.app.hyland.com')
    }
    else {
        $txt = $txt.Replace('https://auth.staging.app.hyland.com/idp', 'https://auth.dev.app.hyland.com/idp')
        $txt = $txt.Replace('https://api.platform.staging.app.hyland.com', 'https://api.platform.dev.app.hyland.com')
        $txt = $txt.Replace('"content.staging.app.hyland.com"', '"content.dev.app.hyland.com"')
        $txt = $txt.Replace('https://cic-viewer.staging.app.hyland.com', 'https://bravo.cic-viewer.sandbox.app.hyland.com')
    }
    Set-Content -Path $McpAppSettings -Value $txt -NoNewline -Encoding UTF8
    Write-Host "MCP appsettings IAM endpoints -> $iam" -ForegroundColor DarkGray
}

# Point the MCP Uceb:BaseUrl at this LOB's UCEB target (local http://localhost:5000, or the deployed staging
# UCEB which does the CFS token exchange internally). Regex replace only the standalone "BaseUrl" key.
function Set-McpUcebBaseUrl([string]$baseUrl) {
    if ([string]::IsNullOrWhiteSpace($baseUrl) -or -not (Test-Path $McpAppSettings)) { return }
    $txt = Get-Content $McpAppSettings -Raw
    $txt = [regex]::Replace($txt, '"BaseUrl"\s*:\s*"[^"]*"', ('"BaseUrl": "' + $baseUrl + '"'))
    Set-Content -Path $McpAppSettings -Value $txt -NoNewline -Encoding UTF8
    Write-Host "MCP Uceb:BaseUrl -> $baseUrl" -ForegroundColor DarkGray
}

# Pin the record-document route explicitly per LOB (workday* => 'bow', salesforce* => 'api'). This is
# REQUIRED because workday-staging cannot request the 'environment_authorization' scope (arizzo isn't
# granted it - see the warning near $LobConfig), and that scope is what used to carry the token appkey
# claim the router auto-detects from. Forcing Uceb:BusinessObjectBasePath keeps routing correct without
# the claim. Regex replace only the standalone "BusinessObjectBasePath" key.
function Set-McpBusinessObjectBasePath([string]$basePath) {
    if ([string]::IsNullOrWhiteSpace($basePath) -or -not (Test-Path $McpAppSettings)) { return }
    $txt = Get-Content $McpAppSettings -Raw
    $txt = [regex]::Replace($txt, '"BusinessObjectBasePath"\s*:\s*"[^"]*"', ('"BusinessObjectBasePath": "' + $basePath + '"'))
    Set-Content -Path $McpAppSettings -Value $txt -NoNewline -Encoding UTF8
    Write-Host "MCP Uceb:BusinessObjectBasePath -> $basePath" -ForegroundColor DarkGray
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

# Flip the MCP appsettings IAM endpoints + Uceb:BaseUrl to this LOB's environment (dev/staging, local/deployed).
Set-McpIamEndpoints $config.Iam
Set-McpUcebBaseUrl $config.UcebBaseUrl
Set-McpBusinessObjectBasePath $config.BasePath

Write-Host ""
Write-Host "Active LOB : $Lob" -ForegroundColor Green
Write-Host "ClientId   : $($config.ClientId)"
Write-Host "Scopes     : $($config.Scopes)"
Write-Host "Route      : $($config.BasePath) $(if ($Lob -like 'workday*') { '(Workday record-doc route, forced - no environment_authorization scope)' } else { '(Salesforce/CIC route)' })"
Write-Host "IAM        : $($config.Iam) (MCP Auth/Nucleus/Content endpoints flipped to match)"
Write-Host "Uceb       : $($config.UcebBaseUrl)"
Write-Host ""

if ($Lob -like 'workday*') {
    $isLocalUceb = $config.UcebBaseUrl -match 'localhost'
    Write-Host "OnBase/CFS reminder (this script set the MCP login client + Uceb:BaseUrl + IAM endpoints):" -ForegroundColor Yellow
    if ($isLocalUceb) {
        Write-Host "  Uceb:BaseUrl = LOCAL ($($config.UcebBaseUrl)) -> the local UCEB API does the CFS token exchange, so it"
        Write-Host "  needs a REAL exchange client (NOT a login/user client) in its user-secrets (uceb-api-local-9f2c1a7e):"
        Write-Host "    dotnet user-secrets set 'TokenExchange:ClientId' 'uceb-iam-token-exchange-staging' --project '<UCEB API csproj>'"
        Write-Host "    dotnet user-secrets set 'TokenExchange:ClientSecret' '<secret>' --project '<UCEB API csproj>'"
        Write-Host "  Run the local UCEB API (:5000) with $($config.Iam) Security overrides. A login client => unsupported_grant_type." -ForegroundColor DarkYellow
    }
    else {
        Write-Host "  Uceb:BaseUrl = DEPLOYED ($($config.UcebBaseUrl)) -> it does the CFS token exchange INTERNALLY (creds in AWS)." -ForegroundColor DarkGreen
        Write-Host "  NO local UCEB API and NO local TokenExchange creds are needed for this LOB." -ForegroundColor DarkGreen
    }
    Write-Host "  MCP appsettings Uceb:WorkdayDocumentTypeGroupId = '101' for OnBase ('hcmisbeemployee' = CIC/native)."
    Write-Host "  Doc-types 404 'Document types not found' MASKS CFS errors -> read the UCEB (or deployed) log for the real cause." -ForegroundColor DarkYellow
    Write-Host ""
}

if ($NoRun) {
    Write-Host "-NoRun set: secrets updated, MCP not started. Start it with:" -ForegroundColor Yellow
    Write-Host "  & `"$Dotnet`" run --project `"$McpProject`" -- --http"
    return
}

Write-Host "Starting the MCP (a browser window will open for login)..." -ForegroundColor Cyan
& $Dotnet run --project $McpProject -- --http
