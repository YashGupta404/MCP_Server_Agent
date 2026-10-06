$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$j = '606403ba-dc0a-4502-8f9b-69fc8635f537'
Write-Host '--- status variants ---'
foreach ($p in @("/api/classification/suggestions/fields/job/status","/api/classification/suggestions/job/status","/api/suggestions/fields/job/status")) {
  Write-Host "[$p]"; curl.exe -s -H $auth "$b$p`?jobId=$j"; Write-Host ''
}
Write-Host '--- result variants ---'
foreach ($p in @("/api/classification/suggestions/fields","/api/classification/suggestions","/api/suggestions/fields")) {
  Write-Host "[$p]"; curl.exe -s -H $auth "$b$p`?jobId=$j"; Write-Host ''
}
