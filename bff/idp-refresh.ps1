# Silently mints a fresh IDP access token from the saved refresh token (grant_type=refresh_token).
# NO browser, NO :5005 — so it never clashes with a running MCP. Run this whenever the IDP token expires.
param(
  [string]$Authority = 'https://auth.staging.app.hyland.com/idp'
)
$refresh = (Get-Content (Join-Path $env:TEMP 'idp_refresh.txt') -Raw -ErrorAction SilentlyContinue)
$clientId = (Get-Content (Join-Path $env:TEMP 'idp_clientid.txt') -Raw -ErrorAction SilentlyContinue)
if (-not $refresh) { throw "No saved refresh token. Run bff/idp-feature-test.ps1 once (while MCP is stopped) to sign in." }
$refresh = $refresh.Trim(); if ($clientId) { $clientId = $clientId.Trim() } else { $clientId = '' }

$bff = 'C:\Users\ygupta\OneDrive - Hyland\MCP_Server_Agent\bff\UcebBff.csproj'
$secrets = & 'C:\Program Files\dotnet\dotnet.exe' user-secrets list --project $bff 2>$null
$secret = (($secrets | Where-Object { $_ -match '^Idp:ClientSecret' }) -replace '^[^=]+=\s*','')
if (-not $clientId) { $clientId = (($secrets | Where-Object { $_ -match '^Idp:ClientId' }) -replace '^[^=]+=\s*','') }
if (-not $secret -or -not $clientId) { throw "Idp:ClientId / Idp:ClientSecret not found in user-secrets." }

$tok = curl.exe -s --max-time 25 -X POST "$Authority/connect/token" -H "Content-Type: application/x-www-form-urlencoded" `
  --data-urlencode "grant_type=refresh_token" `
  --data-urlencode "refresh_token=$refresh" `
  --data-urlencode "client_id=$clientId" `
  --data-urlencode "client_secret=$secret" 2>&1
$parsed = ($tok | ConvertFrom-Json)
if (-not $parsed.access_token) { throw "refresh failed -> $tok" }
Set-Content -Path (Join-Path $env:TEMP 'idp_token.txt') -Value $parsed.access_token -NoNewline
# Refresh tokens may rotate; persist the new one.
if ($parsed.refresh_token) { Set-Content -Path (Join-Path $env:TEMP 'idp_refresh.txt') -Value $parsed.refresh_token -NoNewline }
Write-Host "IDP access token refreshed silently (len=$($parsed.access_token.Length), expires_in=$($parsed.expires_in)s)." -ForegroundColor Green
