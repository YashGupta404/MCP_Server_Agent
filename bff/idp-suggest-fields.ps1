$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$fref = [guid]::NewGuid().ToString()
$docId = [guid]::NewGuid().ToString()

$body = @{
  correlationId = [guid]::NewGuid().ToString()
  class = @{ id=[guid]::NewGuid().ToString(); name='Invoice'; description='Vendor invoice with itemized charges and a billing total' }
  options = @{ numberOfSuggestions = 10; fieldTypes = @(); fieldsToExclude = @() }
  contentFileReferences = @(@{ fileReference=$fref; sourceUrl=$src })
  document = @{ id=$docId; pages=@(@{ contentFileReferenceIndex=0; sourcePageIndex=0; rotation=0 }) }
} | ConvertTo-Json -Depth 10
$f = Join-Path $env:TEMP 'idp_sugg.json'; Set-Content -Path $f -Value $body -Encoding utf8
Write-Host "=== POST /api/classification/suggestions/fields ===" -ForegroundColor Cyan
$res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 60 "$b/api/classification/suggestions/fields" 2>&1) | Out-String
Write-Host $res
$job = ([regex]::Match($res,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
if ($job) {
  Write-Host "suggest jobId = $job" -ForegroundColor Green
  for ($i=0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 3
    $s = (curl.exe -s -H $auth "$b/api/classification/suggestions/fields/job/status?jobId=$job" 2>&1) | Out-String
    Write-Host "  status: $($s.Trim())"
    if ($s -match 'Succeeded|Failed|Error') { break }
  }
  Write-Host "--- suggestions result ---" -ForegroundColor Green
  (curl.exe -s -H $auth "$b/api/classification/suggestions/fields?jobId=$job" 2>&1) | Out-String | Write-Host
}
