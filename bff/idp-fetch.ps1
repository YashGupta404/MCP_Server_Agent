$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$j = '78dba2de-a53f-478b-872e-ddaf120fd44b'
Write-Host '--- status ---'
curl.exe -s -H $auth "$b/api/classification/job/status?jobId=$j"
Write-Host ''
Write-Host '--- result ---'
curl.exe -s -H $auth "$b/api/classification?jobId=$j"
Write-Host ''
