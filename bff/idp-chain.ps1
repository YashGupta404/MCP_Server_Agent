$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$cid = [guid]::NewGuid().ToString()
$fref = [guid]::NewGuid().ToString()
$classId = [guid]::NewGuid().ToString()

# 1) RECOGNITION
$rbody = @{ correlationId=$cid; fileReference=$fref; sourceUrl=$src; actions='Ocr' } | ConvertTo-Json
$rf = Join-Path $env:TEMP 'idp_c_r.json'; Set-Content $rf $rbody -Encoding utf8
Write-Host "=== 1) recognition (cid=$cid) ===" -ForegroundColor Cyan
curl.exe -s -o NUL -w "  POST recognition -> %{http_code}`n" -X POST -H $auth -H "Content-Type: application/json" --data "@$rf" --max-time 30 "$b/api/recognition/file"
$q = "correlationId=$cid&fileReference=$fref"
$recOk = $false
for ($i=0; $i -lt 15; $i++) {
  Start-Sleep -Seconds 3
  $r = (curl.exe -s -H $auth "$b/api/recognition/file/metadata?$q" 2>&1) | Out-String
  if ($r -match 'Succeeded') { Write-Host "  recognition Succeeded"; $recOk = $true; break }
  if ($r -match 'Failed') { Write-Host "  recognition FAILED: $r"; break }
}
if (-not $recOk) { Write-Host "recognition did not succeed; aborting"; exit }

# 2) EXTRACTION reusing the SAME correlationId + fileReference
$fields = @(
  @{ id=[guid]::NewGuid().ToString(); name='Invoice Number'; description='The invoice number or ID' }
  @{ id=[guid]::NewGuid().ToString(); name='Invoice Date';   description='The date on the invoice' }
  @{ id=[guid]::NewGuid().ToString(); name='Vendor Name';    description='The company that issued the invoice' }
  @{ id=[guid]::NewGuid().ToString(); name='Total Amount';   description='The total amount due' }
)
$xbody = @{
  correlationId = $cid
  configuration = @{
    executionProfile = @{ profileId='078abc02-212b-42fa-92fb-f42edd6bb42d'; versionId='3.0'; recognitionProfile=@{ profileId='fe81797f-2d99-450a-92f1-fe4cde5bfc79'; versionId='1.0' } }
    treatEachFileAsDocument = $false
    pageLimit = 10
    classExtractionDefinitions = @(@{ documentClassId=$classId; name='Invoice'; description='Vendor invoice'; fieldDefinitions=$fields })
  }
  contentFileReferences = @(@{ fileReference=$fref; sourceUrl=$src })
  documents = @(@{ id=[guid]::NewGuid().ToString(); classId=$classId; pages=@(@{ contentFileReferenceIndex=0; sourcePageIndex=0; rotation=0 }) })
} | ConvertTo-Json -Depth 10
$xf = Join-Path $env:TEMP 'idp_c_x.json'; Set-Content $xf $xbody -Encoding utf8
Write-Host "=== 2) extraction (same cid+fref) ===" -ForegroundColor Cyan
$xres = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$xf" --max-time 60 "$b/api/extraction" 2>&1) | Out-String
$m = [regex]::Match($xres, '"jobId"\s*:\s*"([^"]+)"')
$job = if ($m.Success) { $m.Groups[1].Value } else { '' }
if (-not $job) { Write-Host "extraction submit: $xres"; exit }
Write-Host "  extraction jobId=$job"
for ($i=0; $i -lt 18; $i++) {
  Start-Sleep -Seconds 3
  $s = (curl.exe -s -H $auth "$b/api/extraction/job/status?jobId=$job" 2>&1) | Out-String
  Write-Host "  status: $($s.Trim())"
  if ($s -match 'Succeeded' -or $s -match 'Failed' -or $s -match 'Error') { break }
}
Write-Host "--- EXTRACTION RESULT ---" -ForegroundColor Green
(curl.exe -s -H $auth "$b/api/extraction?jobId=$job" 2>&1) | Out-String | Write-Host
