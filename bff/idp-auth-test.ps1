# IDP authorization_code login + API test — runs the whole OAuth dance locally, then calls staging IDP.
#
# PREREQS (do both first):
#   1) On the app (yash_hia_idp) ADD this redirect URI (keep Bruno's too) and SAVE:
#        https://uceb-mcp-local.dev.hyland.com:5005/callback
#   2) Put the secret in user-secrets (paste locally, not in chat):
#        & 'C:\Program Files\dotnet\dotnet.exe' user-secrets set "Idp:ClientSecret" "<SECRET>" `
#            --project "C:\Users\ygupta\OneDrive - Hyland\MCP_Server_Agent\bff\UcebBff.csproj"
#
# Then just run:  .\idp-auth-test.ps1
# It opens a browser -> you log in -> it captures the code -> gets a token -> lists IDP execution profiles.
param(
  [string]$ClientId  = 'wsc-cfd5492e-0b47-4c70-8091-d87b2ac459f8',
  [string]$Authority = 'https://auth.staging.app.hyland.com/idp',
  [string]$IdpBase   = 'https://api.idp.staging.app.hyland.com',
  [int]   $Port      = 5005,
  [string]$Scope     = 'openid profile offline_access hxp hxp.integrations hxpr hxps'
)

$CallbackHost = 'uceb-mcp-local.dev.hyland.com'   # -> 127.0.0.1 via hosts file; IAM rejects plain http localhost
$Redirect = "https://$($CallbackHost):$Port/callback"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -match 'uceb-mcp-local' } | Select-Object -First 1
if (-not $cert) { throw "Local HTTPS cert for uceb-mcp-local.dev.hyland.com not found in CurrentUser\My." }
$bff = 'C:\Users\ygupta\OneDrive - Hyland\MCP_Server_Agent\bff\UcebBff.csproj'
$secrets = & 'C:\Program Files\dotnet\dotnet.exe' user-secrets list --project $bff 2>$null
$secret = (($secrets | Where-Object { $_ -match '^Idp:ClientSecret' }) -replace '^[^=]+=\s*','')
$cidFromSecrets = (($secrets | Where-Object { $_ -match '^Idp:ClientId' }) -replace '^[^=]+=\s*','')
if ($cidFromSecrets) { $ClientId = $cidFromSecrets }
if (-not $secret) { throw "Set Idp:ClientSecret in user-secrets first (see header)." }

# --- PKCE (S256) ---
$vb = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($vb)
$verifier  = ([Convert]::ToBase64String($vb)).TrimEnd('=').Replace('+','-').Replace('/','_')
$sha = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))
$challenge = ([Convert]::ToBase64String($sha)).TrimEnd('=').Replace('+','-').Replace('/','_')
$state = [Guid]::NewGuid().ToString('N')

$authz = "$Authority/connect/authorize?response_type=code&client_id=$ClientId" +
         "&redirect_uri=$([uri]::EscapeDataString($Redirect))&scope=$([uri]::EscapeDataString($Scope))" +
         "&state=$state&code_challenge=$challenge&code_challenge_method=S256"

# --- local loopback catcher (TLS via the MCP's uceb-mcp-local cert; IAM requires https) ---
$tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$tcp.Start()
Write-Host "Opening browser to sign in (you have ~2 min)..." -ForegroundColor Cyan
Start-Process $authz
Write-Host "Waiting for the HTTPS redirect on $Redirect ..." -ForegroundColor DarkGray
$client = $tcp.AcceptTcpClient()
$ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false)
$ssl.AuthenticateAsServer($cert, $false, [System.Security.Authentication.SslProtocols]::Tls12, $false)
$reader = New-Object System.IO.StreamReader($ssl)
$requestLine = $reader.ReadLine()
$writer = New-Object System.IO.StreamWriter($ssl)
$writer.WriteLine("HTTP/1.1 200 OK"); $writer.WriteLine("Content-Type: text/html"); $writer.WriteLine("Connection: close"); $writer.WriteLine("")
$writer.WriteLine("<html><body style='font-family:sans-serif'>IDP login captured. You can close this tab and return to the terminal.</body></html>")
$writer.Flush(); $ssl.Dispose(); $client.Close(); $tcp.Stop()

if ($requestLine -match 'error=([^&\s]+)') { throw "Authorize error: $($matches[1])" }
if ($requestLine -notmatch 'code=([^&\s]+)') { throw "No auth code in redirect: $requestLine" }
$code = [uri]::UnescapeDataString($matches[1])
Write-Host "Got auth code." -ForegroundColor Green

# --- exchange code for an access token ---
$tok = curl.exe -s --max-time 25 -X POST "$Authority/connect/token" -H "Content-Type: application/x-www-form-urlencoded" `
  --data-urlencode "grant_type=authorization_code" --data-urlencode "code=$code" `
  --data-urlencode "redirect_uri=$Redirect" --data-urlencode "client_id=$ClientId" `
  --data-urlencode "client_secret=$secret" --data-urlencode "code_verifier=$verifier" 2>&1
try { $token = ($tok | ConvertFrom-Json).access_token } catch {}
if (-not $token) { throw "Token exchange failed -> $tok" }
Write-Host "Got access token (len $($token.Length))." -ForegroundColor Green
Set-Content -Path (Join-Path $env:TEMP 'idp_token.txt') -Value $token -NoNewline
Write-Host "(token cached to $env:TEMP\idp_token.txt for diagnostics)" -ForegroundColor DarkGray

# --- call staging IDP ---
$auth = "Authorization: Bearer $token"
Write-Host "`n=== GET /api/classification/metadata/execution-profiles ===" -ForegroundColor Cyan
curl.exe -s -i -H $auth --max-time 30 "$IdpBase/api/classification/metadata/execution-profiles" 2>&1 | Select-Object -First 40
Write-Host "`n=== POST /api/classification  (empty body -> schema hint) ===" -ForegroundColor Cyan
curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$IdpBase/api/classification" 2>&1 | Select-Object -First 8

# ===== FULL PIPELINE (Rovo Option B): HxPR upload -> document node -> download URL -> classify -> poll =====
$plat = 'https://api.platform.staging.app.hyland.com'
$sample = Join-Path $PSScriptRoot 'sample-invoice.png'
if (-not (Test-Path $sample)) {
  Add-Type -AssemblyName System.Drawing
  $bmp = New-Object System.Drawing.Bitmap 900,520
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
  $f = New-Object System.Drawing.Font('Arial',28)
  $g.DrawString("INVOICE`n`nACME Corp`nInvoice No: INV-1007`nDate: 2026-10-05`nTotal: `$1,234.56", $f, [System.Drawing.Brushes]::Black, 40, 40)
  $g.Flush(); $bmp.Save($sample,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}

Write-Host "`n[1/4] Upload binary -> $plat/api/upload" -ForegroundColor Cyan
$upRaw = (curl.exe -s -i -X POST -H $auth -F "file=@$sample" --max-time 60 "$plat/api/upload" 2>&1) | Out-String
Write-Host $upRaw.Trim()
$uploadId = $null; $m = [regex]::Match($upRaw, '"id"\s*:\s*"([^"]+)"'); if ($m.Success) { $uploadId = $m.Groups[1].Value }
if (-not $uploadId) { Write-Host "!! No uploadId (see status above). Likely scope (hxp.content) or wrong host/path." -ForegroundColor Yellow; return }
Write-Host "uploadId = $uploadId" -ForegroundColor Green

Write-Host "`n[2/4] Create document node -> $plat/api/documents/path/automate/tmp_testing" -ForegroundColor Cyan
$docBody = '{"sys_primaryType":"SysFile","sys_name":"sample-invoice.png","sysfile_blob":{"uploadId":"' + $uploadId + '"}}'
$doc = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data $docBody --max-time 30 "$plat/api/documents/path/automate/tmp_testing" 2>&1) | Out-String
Write-Host $doc.Trim()
$sysId = $null; try { $dj = $doc | ConvertFrom-Json; $sysId = if ($dj.sys_id) { $dj.sys_id } else { $dj.id } } catch {}
if (-not $sysId) { Write-Host "!! No sys_id (doc node not created)." -ForegroundColor Yellow; return }
Write-Host "sys_id (fileReference) = $sysId" -ForegroundColor Green

Write-Host "`n[3/4] Get download URL -> $plat/api/download/url/$sysId/sysfile_blob" -ForegroundColor Cyan
$durl = (curl.exe -s -H $auth --max-time 30 "$plat/api/download/url/$sysId/sysfile_blob" 2>&1) | Out-String
Write-Host $durl.Trim()
$sourceUrl = $durl.Trim().Trim('"')
try { $uj = $durl | ConvertFrom-Json; if ($uj.url) { $sourceUrl = $uj.url } elseif ($uj.downloadUrl) { $sourceUrl = $uj.downloadUrl } } catch {}

Write-Host "`n[4/4] POST /api/classification (General Purpose profile + inline Invoice/Receipt classes)" -ForegroundColor Cyan
$cid = [guid]::NewGuid().ToString(); $id1 = [guid]::NewGuid().ToString(); $id2 = [guid]::NewGuid().ToString()
$body = '{"correlationId":"'+$cid+'","configuration":{"executionProfile":{"profileId":"078abc02-212b-42fa-92fb-f42edd6bb42d","versionId":"3.0"},"documentClassDefinitions":[{"id":"'+$id1+'","name":"Invoice","description":"Vendor invoice with itemized charges and a billing total"},{"id":"'+$id2+'","name":"Receipt","description":"Payment receipt or proof of purchase"}]},"contentFileReferences":[{"fileReference":"'+$sysId+'","sourceUrl":"'+$sourceUrl+'"}]}'
$res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data $body --max-time 60 "$IdpBase/api/classification" 2>&1) | Out-String
Write-Host $res.Trim()
$jobId = $null; $m = [regex]::Match($res, '"jobId"\s*:\s*"([^"]+)"'); if ($m.Success) { $jobId = $m.Groups[1].Value }
if (-not $jobId) { Write-Host "!! No jobId returned." -ForegroundColor Yellow; return }

Write-Host "`n[poll] classification jobId = $jobId" -ForegroundColor Cyan
for ($i = 0; $i -lt 12; $i++) {
  Start-Sleep -Seconds 3
  $st = (curl.exe -s -H $auth --max-time 20 "$IdpBase/api/classification/job/status?jobId=$jobId" 2>&1) | Out-String
  $status = ''; try { $pj = $st | ConvertFrom-Json; if ($pj.jobStatus) { $status = [string]$pj.jobStatus } } catch {}
  Write-Host ("  [{0}] jobStatus = {1}" -f $i, $status)
  if ($status -match 'Succeeded|Failed|Completed|Error') { break }
}
Write-Host "`n=== FINAL RESULT: GET /api/classification?jobId=$jobId ===" -ForegroundColor Green
(curl.exe -s -H $auth --max-time 30 "$IdpBase/api/classification?jobId=$jobId" 2>&1) | Out-String
