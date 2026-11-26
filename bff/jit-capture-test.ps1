param(
  [string]$File = "..\ke-poc\samples\invoice-scan.png",
  [string]$BusinessObjectType = "account",
  [string]$BusinessObjectId = "001gK00001KbuonQAB",
  [string]$DocTypeName = "Invoices",
  [string]$DocTypeId = "205",
  [string]$DocTypeGroupId = "116",
  [string]$RecordIdField = "223",
  # AI-filled fields (simulates the mapper output): ecmFieldId=value;label
  [string[]]$Fields = @("228=Acme Industrial Supply Co.;Vendor Name", "229=INV-2026-004821;Invoice #",
                        "227=2322.00;Invoice Total", "226=11/10/2026;Invoice Date")
)
# Just-in-time capture test: upsert ONE capture-only entry (busObject + doctype) into the active system's
# solution config, then stage + upload the file with explicit attribute values, then read the document back.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
. .\load-mcp-key.ps1 | Out-Null

$parsed = $Fields | ForEach-Object { $id, $rest = $_ -split "=", 2; $val, $label = $rest -split ";", 2; @{ id = $id; value = $val; label = $label } }

# 1) read the current config (data node) to a file
Write-Host "1/5 read solution config"
(.\mcp-admin.ps1 -Tool get_solution_configurations -ArgsJson '{}' | Out-String) | Set-Content jit-current.json -Encoding utf8

# 2) upsert the capture-only entry keyed on (busObject, doctype) — Node keeps the JSON lossless
Write-Host "2/5 upsert entry ($BusinessObjectType -> $DocTypeName/$DocTypeId)"
$spec = @{ busObject = $BusinessObjectType; name = $DocTypeName; id = $DocTypeId; groupId = $DocTypeGroupId; recordField = $RecordIdField; fields = $parsed } | ConvertTo-Json -Depth 5 -Compress
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "jit-spec.json"), $spec)
node -e @"
const fs=require('fs');
const g=JSON.parse(fs.readFileSync('jit-current.json','utf8').replace(/^\uFEFF/,''));
const s=JSON.parse(fs.readFileSync('jit-spec.json','utf8'));
const data=g.data; const boc=data.configurations.businessObjectConfig;
boc.additionalConfig=boc.additionalConfig||[];
boc.additionalConfig=boc.additionalConfig.filter(e=>!(String(e.busObject).toLowerCase()===s.busObject.toLowerCase()&&String(e.ecmContentTypeName).toLowerCase()===s.name.toLowerCase()));
const str=(v,t)=>({value:v,type:t||'String',format:''});
boc.additionalConfig.push({
  busObject:s.busObject, ecmContentTypeName:s.name, ecmContentTypeId:s.id, ecmContentTypeGroupId:s.groupId||null, contextParams:[],
  ecmMetadataFieldLabels:s.fields.map(f=>({ecmColumnName:f.label,ecmColumnId:f.id,localeStrings:[{culture:'en_US',name:f.label,description:'Default label for '+f.id}]})),
  displayColumns:{ecmColumnSets:[{formFactorId:'desktop',columns:s.fields.map(f=>f.id)}],defaultFormFactor:'desktop'},
  metadataFieldImportMappings:[{ecmFieldName:s.recordField,inputSource:'1',value:str('Id')}].concat(s.fields.map(f=>({ecmFieldName:f.id,inputSource:'6',value:str('')})))
});
fs.writeFileSync('jit-new.data.json',JSON.stringify(data));
console.log('    entries now:',boc.additionalConfig.length);
"@

# 3) write it back
Write-Host "3/5 write solution config"
$body = [string][IO.File]::ReadAllText((Join-Path $PSScriptRoot "jit-new.data.json"))
.\mcp-admin.ps1 -Tool set_solution_configuration -ArgsJson (@{ dataJson = $body } | ConvertTo-Json -Compress)

# 4) stage the file on the MCP, then upload with explicit attributes
Write-Host "4/5 stage + upload"
$bytes = [IO.File]::ReadAllBytes((Resolve-Path $File))
$name = [IO.Path]::GetFileName($File)
$stage = Invoke-RestMethod -Method Post -Uri "http://localhost:5200/staging/upload" -Headers @{ "X-Api-Key" = $env:MCP_API_KEY } `
  -ContentType "application/json" -Body (@{ fileName = $name; mime = "image/png"; dataBase64 = [Convert]::ToBase64String($bytes) } | ConvertTo-Json -Compress)
Write-Host "    stagingId=$($stage.stagingId)"
$attrs = @(@{ name = $RecordIdField; value = $BusinessObjectId }) + ($parsed | ForEach-Object { @{ name = $_.id; value = $_.value } })
$uploadArgs = @{
  stagingId = $stage.stagingId; businessObjectId = $BusinessObjectId; businessObjectType = $BusinessObjectType
  ecmContentTypeName = $DocTypeName; documentName = "zero-setup-$name"
  additionalAttributesJson = ($attrs | ConvertTo-Json -Compress)
} | ConvertTo-Json -Compress
$result = .\mcp-admin.ps1 -Tool upload_staged_file -ArgsJson $uploadArgs | Out-String
Write-Host "    $($result.Trim())"

# 5) read the stored document back
if ($result -match "documentId:\s*(\S+)") {
  Write-Host "5/5 read document $($Matches[1])"
  .\mcp-admin.ps1 -Tool get_document -ArgsJson (@{ documentId = $Matches[1] } | ConvertTo-Json -Compress) | Out-String
} else {
  Write-Host "5/5 skipped: no documentId in the upload result"
}
