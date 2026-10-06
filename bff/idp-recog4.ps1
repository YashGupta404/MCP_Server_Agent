$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$cid = [guid]::NewGuid().ToString()
$fref = [guid]::NewGuid().ToString()

$body = @{ correlationId=$cid; fileReference=$fref; sourceUrl=$src; actions='Ocr' } | ConvertTo-Json
$f = Join-Path $env:TEMP 'idp_recog4.json'; Set-Content $f $body -Encoding utf8
Write-Host "cid=$cid fref=$fref" -ForegroundColor Green
Write-Host "=== POST /api/recognition/file -i (headers) ===" -ForegroundColor Cyan
curl.exe -s -i -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 30 "$b/api/recognition/file" | Select-Object -First 15
Write-Host ''
$q = "correlationId=$cid&fileReference=$fref"
for ($i=0; $i -lt 15; $i++) {
  Start-Sleep -Seconds 3
  Write-Host "--- poll ${i}: GET /api/recognition/file/metadata ---"
  $r = (curl.exe -s -H $auth "$b/api/recognition/file/metadata?$q" 2>&1) | Out-String
  Write-Host $r.Trim()
  if ($r -match 'Succeeded' -or $r -match 'pages' -or $r -match 'Failed') { break }
}
Write-Host "=== also try GET /api/recognition/file?correlationId..&fileReference.. ===" -ForegroundColor Cyan
(curl.exe -s -H $auth "$b/api/recognition/file?$q" 2>&1) | Out-String | Write-Host
