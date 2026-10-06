$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$fref = [guid]::NewGuid().ToString()

$body = @{
  correlationId = [guid]::NewGuid().ToString()
  fileReference = $fref
  sourceUrl = $src
  actions = @('Ocr')
} | ConvertTo-Json -Depth 6
$f = Join-Path $env:TEMP 'idp_recog.json'; Set-Content -Path $f -Value $body -Encoding utf8
Write-Host "=== POST /api/recognition/file (actions=Ocr) ===" -ForegroundColor Cyan
$res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 60 "$b/api/recognition/file" 2>&1) | Out-String
Write-Host $res
$job = ([regex]::Match($res,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
if ($job) {
  for ($i=0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 3
    $s = (curl.exe -s -H $auth "$b/api/recognition/file/job/status?jobId=$job" 2>&1) | Out-String
    Write-Host "  status: $($s.Trim())"
    if ($s -match 'Succeeded|Failed|Error') { break }
  }
  Write-Host "--- recognition result ---" -ForegroundColor Green
  (curl.exe -s -H $auth "$b/api/recognition/file?jobId=$job" 2>&1) | Out-String | Write-Host
}
