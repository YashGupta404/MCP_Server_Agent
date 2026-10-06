$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'

function Invoke-Extract([string]$label, [hashtable]$body) {
  $json = $body | ConvertTo-Json -Depth 10
  $f = Join-Path $env:TEMP 'idp_x.json'; Set-Content -Path $f -Value $json -Encoding utf8
  Write-Host "=== $label ===" -ForegroundColor Cyan
  $res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 60 "$b/api/extraction" 2>&1) | Out-String
  $job = ([regex]::Match($res,'"jobId"\s*:\s*"([^"]+)"')).Groups[1].Value
  if (-not $job) { Write-Host "submit resp: $res"; return }
  Write-Host "jobId=$job"
  for ($i=0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 3
    $s = (curl.exe -s -H $auth "$b/api/extraction/job/status?jobId=$job" 2>&1) | Out-String
    if ($s -match 'Succeeded|Failed|Error') { Write-Host "status: $($s.Trim())"; break }
  }
  (curl.exe -s -H $auth "$b/api/extraction?jobId=$job" 2>&1) | Out-String | Write-Host
}

$classId = [guid]::NewGuid().ToString()
$fref = [guid]::NewGuid().ToString()
$fields = @(
  @{ id=[guid]::NewGuid().ToString(); name='Invoice Number'; description='The invoice number or ID' }
  @{ id=[guid]::NewGuid().ToString(); name='Invoice Date';   description='The date on the invoice' }
  @{ id=[guid]::NewGuid().ToString(); name='Vendor Name';    description='The company that issued the invoice' }
  @{ id=[guid]::NewGuid().ToString(); name='Total Amount';   description='The total amount due' }
)
$classDef = @{ documentClassId=$classId; name='Invoice'; description='Vendor invoice'; fieldDefinitions=$fields }
$exec = @{ profileId='078abc02-212b-42fa-92fb-f42edd6bb42d'; versionId='3.0'; recognitionProfile=@{ profileId='fe81797f-2d99-450a-92f1-fe4cde5bfc79'; versionId='1.0' } }

# Variant A: treatEachFileAsDocument=true, NO documents[]
Invoke-Extract 'A: treatEachFileAsDocument=true, no documents' @{
  correlationId=[guid]::NewGuid().ToString()
  configuration=@{ executionProfile=$exec; treatEachFileAsDocument=$true; pageLimit=10; classExtractionDefinitions=@($classDef) }
  contentFileReferences=@(@{ fileReference=$fref; sourceUrl=$src })
}

# Variant B: treatEachFileAsDocument=true, WITH documents[]
Invoke-Extract 'B: treatEachFileAsDocument=true, with documents' @{
  correlationId=[guid]::NewGuid().ToString()
  configuration=@{ executionProfile=$exec; treatEachFileAsDocument=$true; pageLimit=10; classExtractionDefinitions=@($classDef) }
  contentFileReferences=@(@{ fileReference=$fref; sourceUrl=$src })
  documents=@(@{ id=[guid]::NewGuid().ToString(); documentClassId=$classId; pages=@(@{ contentFileReferenceIndex=0; sourcePageIndex=0; rotation=0 }) })
}
