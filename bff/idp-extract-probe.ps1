$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
foreach ($p in @('/api/extraction','/api/extraction/job')) {
  Write-Host "=== POST $p (empty body) ===" -ForegroundColor Cyan
  curl.exe -s -i -H $auth -H "Content-Type: application/json" --data '{}' --max-time 30 "$b$p" 2>&1 | Select-Object -First 15
  Write-Host ''
}
