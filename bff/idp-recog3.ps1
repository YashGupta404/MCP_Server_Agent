$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$fref = [guid]::NewGuid().ToString()

$body = @{ correlationId=[guid]::NewGuid().ToString(); fileReference=$fref; sourceUrl=$src; actions='Ocr' } | ConvertTo-Json
$f = Join-Path $env:TEMP 'idp_recog3.json'; Set-Content $f $body -Encoding utf8
Write-Host "=== POST /api/recognition/file (actions=Ocr) ===" -ForegroundColor Cyan
$res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 30 "$b/api/recognition/file" 2>&1) | Out-String
Write-Host "body: $res"
$m = [regex]::Match($res, '"jobId"\s*:\s*"([^"]+)"')
$job = if ($m.Success) { $m.Groups[1].Value } else { '' }
Write-Host "jobId=$job  fileReference=$fref" -ForegroundColor Green
if ($job) {
  for ($i=0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 3
    $s = (curl.exe -s -H $auth "$b/api/recognition/file/job/status?jobId=$job" 2>&1) | Out-String
    Write-Host "  status: $($s.Trim())"
    if ($s -match 'Succeeded' -or $s -match 'Failed' -or $s -match 'Error') { break }
  }
  Write-Host "--- recognition result (GET ?jobId) ---" -ForegroundColor Green
  (curl.exe -s -H $auth "$b/api/recognition/file?jobId=$job" 2>&1) | Out-String | Write-Host
}
