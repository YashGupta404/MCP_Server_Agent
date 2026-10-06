# Option A: classify a document IDP downloads from a PUBLIC sourceUrl (no HxPR upload needed).
# Uses the cached token from idp-auth-test.ps1 (%TEMP%\idp_token.txt). Re-run idp-auth-test.ps1 if it's expired.
param(
  [string]$IdpBase   = 'https://api.idp.staging.app.hyland.com',
  [string]$SourceUrl = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png',
  [string]$FileRef   = 'github-sample-invoice'
)

$tokenPath = Join-Path $env:TEMP 'idp_token.txt'
if (-not (Test-Path $tokenPath)) { throw "No cached token. Run .\idp-auth-test.ps1 first." }
$token = (Get-Content $tokenPath -Raw).Trim()
$auth = "Authorization: Bearer $token"

$cid = [guid]::NewGuid().ToString(); $id1 = [guid]::NewGuid().ToString(); $id2 = [guid]::NewGuid().ToString(); $id3 = [guid]::NewGuid().ToString()
$body = '{"correlationId":"'+$cid+'","configuration":{"executionProfile":{"profileId":"078abc02-212b-42fa-92fb-f42edd6bb42d","versionId":"3.0"},"documentClassDefinitions":[' +
        '{"id":"'+$id1+'","name":"Invoice","description":"Vendor invoice with itemized charges and a billing total"},' +
        '{"id":"'+$id2+'","name":"Receipt","description":"Payment receipt or proof of purchase"},' +
        '{"id":"'+$id3+'","name":"Contract","description":"A legal agreement or signed contract between parties"}' +
        '],"treatEachFileAsDocument":true,"includeClassCandidateReasoning":true,"reviewThreshold":0,"classAssignmentThreshold":0,"classCandidatesMinDistance":0,"defaultClassId":"","pageLimit":10},' +
        '"contentFileReferences":[{"fileReference":"'+$FileRef+'","sourceUrl":"'+$SourceUrl+'"}]}'

Write-Host "POST $IdpBase/api/classification  (sourceUrl=$SourceUrl)" -ForegroundColor Cyan
$bodyFile = Join-Path $env:TEMP 'idp_classify_body.json'
Set-Content -Path $bodyFile -Value $body -Encoding ascii -NoNewline   # file avoids PowerShell quote-mangling in curl --data
$res = (curl.exe -s -i -X POST -H $auth -H "Content-Type: application/json" --data "@$bodyFile" --max-time 60 "$IdpBase/api/classification" 2>&1) | Out-String
Write-Host $res.Trim()
$jobId = $null; $m = [regex]::Match($res, '"jobId"\s*:\s*"([^"]+)"'); if ($m.Success) { $jobId = $m.Groups[1].Value }
if (-not $jobId) { Write-Host "!! No jobId (token expired? -> re-run idp-auth-test.ps1)." -ForegroundColor Yellow; return }

Write-Host "`n[poll] jobId = $jobId" -ForegroundColor Cyan
for ($i = 0; $i -lt 15; $i++) {
  Start-Sleep -Seconds 3
  $st = (curl.exe -s -H $auth --max-time 20 "$IdpBase/api/classification/job/status?jobId=$jobId" 2>&1) | Out-String
  $status = ''; try { $pj = $st | ConvertFrom-Json; if ($pj.jobStatus) { $status = [string]$pj.jobStatus } } catch {}
  Write-Host ("  [{0}] jobStatus = {1}" -f $i, $status)
  if ($status -match 'Succeeded|Failed|Completed|Error') { break }
}
Write-Host "`n=== FINAL RESULT: GET /api/classification?jobId=$jobId ===" -ForegroundColor Green
(curl.exe -s -H $auth --max-time 30 "$IdpBase/api/classification?jobId=$jobId" 2>&1) | Out-String
