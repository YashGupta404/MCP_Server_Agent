# IDP FEATURE feasibility test (Option A: public URL). Proves the two things the plugin feature needs:
#   (1) auto doc-TYPE classification against a set of candidate types (like a sysconfig's doc types)
#   (2) auto METADATA extraction (the fields that would auto-fill the upload form)
# Reuses the local authorization_code login (MCP's uceb-mcp-local:5005 HTTPS cert) + the public sample on GitHub.
param(
  [string]$Authority = 'https://auth.staging.app.hyland.com/idp',
  [string]$IdpBase   = 'https://api.idp.staging.app.hyland.com',
  [int]   $Port      = 5005,
  [string]$Scope     = 'openid profile offline_access hxp hxp.integrations hxpr hxps',
  [string]$SourceUrl = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
)
$CallbackHost = 'uceb-mcp-local.dev.hyland.com'
$Redirect = "https://$($CallbackHost):$Port/callback"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -match 'uceb-mcp-local' } | Select-Object -First 1
$bff = 'C:\Users\ygupta\OneDrive - Hyland\MCP_Server_Agent\bff\UcebBff.csproj'
$secrets = & 'C:\Program Files\dotnet\dotnet.exe' user-secrets list --project $bff 2>$null
$secret = (($secrets | Where-Object { $_ -match '^Idp:ClientSecret' }) -replace '^[^=]+=\s*','')
$ClientId = (($secrets | Where-Object { $_ -match '^Idp:ClientId' }) -replace '^[^=]+=\s*','')
if (-not $secret -or -not $ClientId) { throw "Set Idp:ClientId + Idp:ClientSecret in user-secrets first." }

# --- login (PKCE + TLS loopback catcher) ---
$vb = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($vb)
$verifier = ([Convert]::ToBase64String($vb)).TrimEnd('=').Replace('+','-').Replace('/','_')
$sha = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))
$challenge = ([Convert]::ToBase64String($sha)).TrimEnd('=').Replace('+','-').Replace('/','_')
$state = [Guid]::NewGuid().ToString('N')
$authz = "$Authority/connect/authorize?response_type=code&client_id=$ClientId&redirect_uri=$([uri]::EscapeDataString($Redirect))&scope=$([uri]::EscapeDataString($Scope))&state=$state&code_challenge=$challenge&code_challenge_method=S256"
$tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port); $tcp.Start()
Write-Host "Opening browser to sign in..." -ForegroundColor Cyan; Start-Process $authz
$client = $tcp.AcceptTcpClient()
$ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false)
$ssl.AuthenticateAsServer($cert, $false, [System.Security.Authentication.SslProtocols]::Tls12, $false)
$reqLine = (New-Object System.IO.StreamReader($ssl)).ReadLine()
$w = New-Object System.IO.StreamWriter($ssl); $w.WriteLine("HTTP/1.1 200 OK"); $w.WriteLine("Connection: close"); $w.WriteLine(""); $w.WriteLine("Login captured. Return to the terminal."); $w.Flush(); $ssl.Dispose(); $client.Close(); $tcp.Stop()
if ($reqLine -notmatch 'code=([^&\s]+)') { throw "No auth code: $reqLine" }
$code = [uri]::UnescapeDataString($matches[1])
$tok = curl.exe -s --max-time 25 -X POST "$Authority/connect/token" -H "Content-Type: application/x-www-form-urlencoded" --data-urlencode "grant_type=authorization_code" --data-urlencode "code=$code" --data-urlencode "redirect_uri=$Redirect" --data-urlencode "client_id=$ClientId" --data-urlencode "client_secret=$secret" --data-urlencode "code_verifier=$verifier" 2>&1
$parsed = ($tok | ConvertFrom-Json)
$token = $parsed.access_token
if (-not $token) { throw "token exchange failed -> $tok" }
Set-Content -Path (Join-Path $env:TEMP 'idp_token.txt') -Value $token -NoNewline
# Save the refresh token so idp-refresh.ps1 can mint new access tokens silently (no browser, no :5005 clash).
if ($parsed.refresh_token) {
    Set-Content -Path (Join-Path $env:TEMP 'idp_refresh.txt') -Value $parsed.refresh_token -NoNewline
    Set-Content -Path (Join-Path $env:TEMP 'idp_clientid.txt') -Value $ClientId -NoNewline
    Write-Host "Saved refresh token -> future refreshes via bff/idp-refresh.ps1 (no browser)." -ForegroundColor DarkGray
}
$auth = "Authorization: Bearer $token"
Write-Host "Logged in. Source doc: $SourceUrl`n" -ForegroundColor Green

$fref = [guid]::NewGuid().ToString()

# ===== (1) CLASSIFICATION against a candidate type set (simulating a sysconfig's doc types) =====
Write-Host "=== (1) CLASSIFY among: Invoice / Receipt / Purchase Order / Contract / Clinical Fax ===" -ForegroundColor Cyan
$classes = @(
  @{id=[guid]::NewGuid().ToString(); name='Invoice';         description='Vendor invoice with itemized charges and a billing total'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Receipt';         description='Payment receipt or proof of purchase'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Purchase Order';  description='Purchase order authorizing a purchase, with PO number and line items'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Contract';        description='Legal agreement or contract between parties with terms and signatures'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Clinical Fax';    description='Medical or clinical fax cover sheet or patient document'; ignoreForAuto=$false}
)
$cbody = @{
  correlationId = [guid]::NewGuid().ToString()
  configuration = @{
    executionProfile = @{ profileId='078abc02-212b-42fa-92fb-f42edd6bb42d'; versionId='3.0' }
    documentClassDefinitions = $classes
    treatEachFileAsDocument = $false
    includeClassCandidateReasoning = $true
    reviewThreshold = 0.8
    classAssignmentThreshold = 0.8
    classCandidatesMinDistance = 0.05
    pageLimit = 10
  }
  contentFileReferences = @(@{ fileReference=$fref; sourceUrl=$SourceUrl })
} | ConvertTo-Json -Depth 8
$cfile = Join-Path $env:TEMP 'idp_classify.json'; Set-Content -Path $cfile -Value $cbody -Encoding utf8
$cres = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$cfile" --max-time 60 "$IdpBase/api/classification" 2>&1) | Out-String
$cjob = ([regex]::Match($cres,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
Write-Host "classify jobId = $cjob"
for ($i=0; $i -lt 12 -and $cjob; $i++) {
  Start-Sleep -Seconds 3
  $s = (curl.exe -s -H $auth "$IdpBase/api/classification/job/status?jobId=$cjob" 2>&1) | Out-String
  $st = ''; try { $st = ($s|ConvertFrom-Json).status } catch {}
  if ($st -and $st -notmatch 'Processing|Pending|Running|InProgress|Queued') { break }
}
Write-Host "--- classification result ---" -ForegroundColor Green
(curl.exe -s -H $auth "$IdpBase/api/classification?jobId=$cjob" 2>&1) | Out-String

# ===== (2) EXTRACTION schema probe (then we build a real extraction request) =====
Write-Host "`n=== (2) EXTRACTION schema probe: POST /api/extraction/job (empty) ===" -ForegroundColor Cyan
(curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$IdpBase/api/extraction/job" 2>&1) | Select-Object -First 20
