param(
  [Parameter(Mandatory = $true)][string]$File,
  [string]$JsonSchema = "FULL"
)
# One Data Curation run: presign -> PUT (with put_headers) -> poll status -> download get_url.
# Reads the client id/secret from the BFF user-secrets; the secret is never printed.

$ErrorActionPreference = "Stop"
$base = "https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/data-curation"
$secrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\uceb-bff-9f2c1a7e\secrets.json" -Raw | ConvertFrom-Json

$tok = Invoke-RestMethod -Method Post -Uri "https://auth.staging.app.hyland.com/idp/connect/token" `
  -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type    = "client_credentials"
    client_id     = $secrets.'DataCuration:ClientId'
    client_secret = $secrets.'DataCuration:ClientSecret'
    scope         = "environment_authorization"
  }
$auth = @{ Authorization = "Bearer $($tok.access_token)" }
Write-Host "1/4 token OK"

$opts = @{ pii = $false; chunking = $false; embedding = $false; json_schema = $JsonSchema } | ConvertTo-Json -Compress
$pre = Invoke-RestMethod -Method Post -Uri "$base/presign" -Headers $auth -ContentType "application/json" -Body $opts
Write-Host "2/4 presign OK  job_id=$($pre.job_id)"
Write-Host "    put_headers: $(($pre.put_headers | ConvertTo-Json -Compress))"
Write-Host "    options echoed: $(($pre.options | ConvertTo-Json -Compress -Depth 6))"

$putHeaders = @{}
if ($pre.put_headers) { $pre.put_headers.PSObject.Properties | ForEach-Object { $putHeaders[$_.Name] = $_.Value } }
$ct = if ($putHeaders.ContainsKey("Content-Type")) { $putHeaders["Content-Type"] } else { $null }
$putHeaders.Remove("Content-Type")
$putArgs = @{ Method = "Put"; Uri = $pre.put_url; InFile = (Resolve-Path $File); Headers = $putHeaders; UseBasicParsing = $true }
if ($ct) { $putArgs.ContentType = $ct }
$putResp = Invoke-WebRequest @putArgs
Write-Host "3/4 PUT $($putResp.StatusCode)"

$deadline = (Get-Date).AddMinutes(5)
do {
  Start-Sleep -Seconds 3
  $st = Invoke-RestMethod -Method Get -Uri "$base/status/$($pre.job_id)" -Headers $auth
  Write-Host "    status: $($st.status)"
} while ($st.status -notin @("Done", "Error", "Deleted") -and (Get-Date) -lt $deadline)

if ($st.status -ne "Done") {
  Write-Host "JOB DID NOT FINISH OK: $($st | ConvertTo-Json -Compress)"
  exit 1
}
Write-Host "4/4 Done  fileType=$($st.fileType) pages=$($st.page_count) size=$($st.fileSize)"

$out = [IO.Path]::ChangeExtension((Resolve-Path $File).Path, ".datacuration.$($JsonSchema.ToLower()).json")
Invoke-WebRequest -Uri $pre.get_url -OutFile $out -UseBasicParsing
Write-Host "Result saved: $out ($((Get-Item $out).Length) bytes)"
