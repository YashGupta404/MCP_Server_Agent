$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$fref = [guid]::NewGuid().ToString()
$classId = [guid]::NewGuid().ToString()
$docId = [guid]::NewGuid().ToString()

# Attempt 1: classification-style body with documentFieldDefinitions
$body = @{
  correlationId = [guid]::NewGuid().ToString()
  configuration = @{
    executionProfile = @{ profileId='078abc02-212b-42fa-92fb-f42edd6bb42d'; versionId='3.0'; recognitionProfile=@{ profileId='fe81797f-2d99-450a-92f1-fe4cde5bfc79'; versionId='1.0' } }
    treatEachFileAsDocument = $false
    pageLimit = 10
    classExtractionDefinitions = @(
      @{
        documentClassId=$classId; name='Invoice'; description='Vendor invoice with itemized charges and a billing total'
        fieldDefinitions = @(
          @{ id=[guid]::NewGuid().ToString(); name='Invoice Number'; description='The invoice number or ID' }
          @{ id=[guid]::NewGuid().ToString(); name='Invoice Date';   description='The date on the invoice' }
          @{ id=[guid]::NewGuid().ToString(); name='Vendor Name';    description='The company that issued the invoice' }
          @{ id=[guid]::NewGuid().ToString(); name='Total Amount';   description='The total amount due on the invoice' }
        )
      }
    )
  }
  contentFileReferences = @(@{ fileReference=$fref; sourceUrl=$src })
  documents = @(
    @{
      id=$docId
      documentClassId=$classId
      pages=@(@{ contentFileReferenceIndex=0; sourcePageIndex=0; rotation=0 })
    }
  )
} | ConvertTo-Json -Depth 10
$f = Join-Path $env:TEMP 'idp_extract.json'; Set-Content -Path $f -Value $body -Encoding utf8
Write-Host "=== POST /api/extraction (documentFieldDefinitions) ===" -ForegroundColor Cyan
$res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 60 "$b/api/extraction" 2>&1) | Out-String
Write-Host $res
$job = ([regex]::Match($res,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
if ($job) {
  Write-Host "extraction jobId = $job" -ForegroundColor Green
  for ($i=0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 3
    $s = (curl.exe -s -H $auth "$b/api/extraction/job/status?jobId=$job" 2>&1) | Out-String
    Write-Host "  status: $s"
    if ($s -match 'Succeeded|Failed|Error') { break }
  }
  Write-Host "--- extraction result ---" -ForegroundColor Green
  (curl.exe -s -H $auth "$b/api/extraction?jobId=$job" 2>&1) | Out-String | Write-Host
}
