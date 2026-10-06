# Uses the cached IDP token (from idp-feature-test.ps1 login). No re-login.
param(
  [string]$IdpBase = 'https://api.idp.staging.app.hyland.com',
  [string]$SourceUrl = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
)
$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
if (-not $token) { throw "no cached token" }
$auth = "Authorization: Bearer $token"
$fref = [guid]::NewGuid().ToString()

Write-Host "=== (1) CLASSIFY among: Invoice / Receipt / Purchase Order / Contract / Clinical Fax ===" -ForegroundColor Cyan
$classes = @(
  @{id=[guid]::NewGuid().ToString(); name='Invoice';        description='Vendor invoice with itemized charges and a billing total'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Receipt';        description='Payment receipt or proof of purchase'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Purchase Order'; description='Purchase order authorizing a purchase, with PO number and line items'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Contract';       description='Legal agreement or contract between parties with terms and signatures'; ignoreForAuto=$false}
  @{id=[guid]::NewGuid().ToString(); name='Clinical Fax';   description='Medical or clinical fax cover sheet or patient document'; ignoreForAuto=$false}
)
$cbody = @{
  correlationId = [guid]::NewGuid().ToString()
  configuration = @{
    executionProfile = @{ profileId='078abc02-212b-42fa-92fb-f42edd6bb42d'; versionId='3.0' }
    documentClassDefinitions = $classes
    treatEachFileAsDocument = $true
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
Write-Host "POST /api/classification ->" $cres
$cjob = ([regex]::Match($cres,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
for ($i=0; $i -lt 15 -and $cjob; $i++) {
  Start-Sleep -Seconds 3
  $s = (curl.exe -s -H $auth "$IdpBase/api/classification/job/status?jobId=$cjob" 2>&1) | Out-String
  $st=''; try { $st=($s|ConvertFrom-Json).status } catch {}
  Write-Host "  status: $st"
  if ($st -and $st -notmatch 'Processing|Pending|Running|InProgress|Queued|Created') { break }
}
Write-Host "--- classification result ---" -ForegroundColor Green
(curl.exe -s -H $auth "$IdpBase/api/classification?jobId=$cjob" 2>&1) | Out-String | Write-Host

Write-Host "`n=== (2) EXTRACTION schema probe: POST /api/extraction (empty body) ===" -ForegroundColor Cyan
(curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$IdpBase/api/extraction" 2>&1) | Select-Object -First 25 | Write-Host
Write-Host "`n=== (2b) EXTRACTION alt path probe: POST /api/extraction/job ===" -ForegroundColor Cyan
(curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$IdpBase/api/extraction/job" 2>&1) | Select-Object -First 12 | Write-Host
