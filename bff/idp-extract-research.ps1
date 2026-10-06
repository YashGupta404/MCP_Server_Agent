$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'

Write-Host "=== token check (list classification profiles) ===" -ForegroundColor Cyan
curl.exe -s -o NUL -w "classification/metadata/execution-profiles -> %{http_code}`n" -H $auth "$b/api/classification/metadata/execution-profiles"

Write-Host "`n=== extraction execution profiles (variants) ===" -ForegroundColor Cyan
foreach ($p in @(
  '/api/extraction/metadata/execution-profiles',
  '/api/extraction/execution-profiles',
  '/api/extraction/metadata/profiles'
)) {
  Write-Host "[$p]"
  curl.exe -s --max-time 25 -H $auth "$b$p"
  Write-Host ''
}

Write-Host "`n=== recognition execution profiles ===" -ForegroundColor Cyan
foreach ($p in @(
  '/api/recognition/metadata/execution-profiles',
  '/api/recognition/execution-profiles'
)) {
  Write-Host "[$p]"
  curl.exe -s --max-time 25 -H $auth "$b$p"
  Write-Host ''
}

Write-Host "`n=== recognition actions schema probes ===" -ForegroundColor Cyan
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'
$fref = [guid]::NewGuid().ToString()
$variants = @(
  @{ label='actions=[{type:Ocr}]';        actions=@(@{ type='Ocr' }) },
  @{ label='actions=[{action:Ocr}]';      actions=@(@{ action='Ocr' }) },
  @{ label='actions=[{name:Ocr}]';        actions=@(@{ name='Ocr' }) },
  @{ label='actions=[{actionType:Ocr}]';  actions=@(@{ actionType='Ocr' }) }
)
foreach ($v in $variants) {
  $body = @{
    correlationId = [guid]::NewGuid().ToString()
    fileReference = $fref
    sourceUrl = $src
    actions = $v.actions
  } | ConvertTo-Json -Depth 6
  $f = Join-Path $env:TEMP 'idp_recog2.json'; Set-Content -Path $f -Value $body -Encoding utf8
  Write-Host "[$($v.label)]"
  curl.exe -s -o NUL -w "  -> %{http_code}`n" -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 25 "$b/api/recognition/file"
  curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 25 "$b/api/recognition/file" | ForEach-Object { if ($_ -match 'jobId|actions|detail') { Write-Host "  $_" } }
}
