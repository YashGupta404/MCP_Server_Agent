$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
Write-Host '--- execution profiles ---'
curl.exe -s -H $auth "$b/api/classification/metadata/execution-profiles"
Write-Host ''
Write-Host '--- recognition endpoint probe (empty) ---'
curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$b/api/recognition/file" 2>&1 | Select-Object -First 12
