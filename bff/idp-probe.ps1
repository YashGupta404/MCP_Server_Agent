# IDP API probe — discovers auth + request schemas for the direct IDP REST API.
# Auth (pick ONE, set in the shell — never hardcode):
#   A) ready token:   $env:IDP_TOKEN = '<dev bearer token with IDP access>'
#   B) an Application: $env:IDP_CLIENT_ID='<app id>'; $env:IDP_CLIENT_SECRET='<api key>'
#      (optional) $env:IDP_TOKEN_URL='https://auth.iam.experience.hyland.com/idp/connect/token'; $env:IDP_SCOPE='idp'
# Then:
#   .\idp-probe.ps1                      # token check + schema hints
#   .\idp-probe.ps1 -File .\sample.pdf   # also try recognition (OCR) with a real file
#
# Status-code meaning: 401 -> token not accepted (wrong issuer); 403 -> valid but lacks IDP scope;
#   400 -> token GOOD, body wrong (the error usually LEAKS the schema); 200/202 -> worked, read the JSON.
param(
  [string]$Base = 'https://api.idp.dev.app.hyland.com',
  [string]$File
)

# Mint a token via client_credentials (creds in body) if no ready token was supplied — matches the Bruno collection.
if (-not $env:IDP_TOKEN -and $env:IDP_CLIENT_ID -and $env:IDP_CLIENT_SECRET) {
  if (-not $env:IDP_TOKEN_URL) { throw "Set `$env:IDP_TOKEN_URL (the IDP token endpoint) when minting from client id/secret." }
  $scope = if ($env:IDP_SCOPE) { $env:IDP_SCOPE } else { 'hxp.integrations hxp' }
  $resp = curl.exe -s --max-time 25 -X POST $env:IDP_TOKEN_URL -H "Content-Type: application/x-www-form-urlencoded" --data-urlencode "grant_type=client_credentials" --data-urlencode "client_id=$($env:IDP_CLIENT_ID)" --data-urlencode "client_secret=$($env:IDP_CLIENT_SECRET)" --data-urlencode "scope=$scope" 2>&1
  try { $env:IDP_TOKEN = ($resp | ConvertFrom-Json).access_token } catch {}
  if (-not $env:IDP_TOKEN) { throw "Token mint failed from $($env:IDP_TOKEN_URL) -> $resp" }
  Write-Host "Minted IDP token (len $($env:IDP_TOKEN.Length)) scope='$scope'" -ForegroundColor Green
}

if (-not $env:IDP_TOKEN) { throw "Set `$env:IDP_TOKEN, or `$env:IDP_CLIENT_ID + `$env:IDP_CLIENT_SECRET, first." }
$auth = "Authorization: Bearer $($env:IDP_TOKEN)"

function Probe([string]$method, [string]$path, [string]$body) {
  Write-Host "=== $method $path ===" -ForegroundColor Cyan
  if ($body) {
    curl.exe -s -i -X $method -H $auth -H "Content-Type: application/json" --data $body --max-time 30 "$Base$path" 2>&1 | Select-Object -First 45
  } else {
    curl.exe -s -i -X $method -H $auth --max-time 30 "$Base$path" 2>&1 | Select-Object -First 45
  }
  Write-Host ""
}

# 1) List configured EXECUTION PROFILES (the IDP "projects") — the key discovery once authed:
#    reveals profileId/versionId to use for classification/extraction, and proves the token works.
Probe 'GET'  '/api/classification/metadata/execution-profiles'
Probe 'GET'  '/api/extraction/metadata/execution-profiles'
Probe 'GET'  '/api/recognition/metadata/execution-profiles'

# 2) Schema hints (empty body on a real route -> 400 usually leaks the expected request shape).
Probe 'POST' '/api/classification' '{}'
Probe 'POST' '/api/classification/suggestions/class' '{}'
Probe 'POST' '/api/extraction/job' '{}'
Probe 'POST' '/api/separation/job' '{}'

# 3) Recognition (OCR) with a real file, if provided — reveals input format (multipart bytes vs content ref).
if ($File) {
  if (Test-Path $File) {
    Write-Host "=== POST /api/recognition/file (multipart file upload) ===" -ForegroundColor Cyan
    curl.exe -s -i -X POST -H $auth -F "file=@$File" --max-time 60 "$Base/api/recognition/file" 2>&1 | Select-Object -First 45
  } else {
    Write-Host "File not found: $File" -ForegroundColor Yellow
  }
}
