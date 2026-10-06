$token = Get-Content (Join-Path $env:TEMP 'idp_token.txt') -Raw
$auth = "Authorization: Bearer $token"
$b = 'https://api.idp.staging.app.hyland.com'
$src = 'https://raw.githubusercontent.com/YashGupta404/MCP_Server_Agent/main/bff/sample-invoice.png'

function Try-Actions([string]$label, $actionsValue) {
  $body = [ordered]@{
    correlationId = [guid]::NewGuid().ToString()
    fileReference = [guid]::NewGuid().ToString()
    sourceUrl = $src
    actions = $actionsValue
  }
  $json = $body | ConvertTo-Json -Depth 6 -Compress
  $f = Join-Path $env:TEMP 'idp_ra.json'; Set-Content -Path $f -Value $json -Encoding utf8
  $res = (curl.exe -s -X POST -H $auth -H "Content-Type: application/json" --data "@$f" --max-time 20 "$b/api/recognition/file" 2>&1) | Out-String
  $snippet = ''
  if ($res -match '"jobId"\s*:\s*"([^"]+)"') { $snippet = "JOBID=$($matches[1])  <<< WORKS" }
  elseif ($res -match '"detail"\s*:\s*"([^"]+)"') {
    $d = $matches[1]
    $argErr = ([regex]::Matches($res,'"argument":"([^"]+)","errors":\["([^"]+)"') | ForEach-Object { "$($_.Groups[1].Value)=$($_.Groups[2].Value)" }) -join '; '
    $snippet = "$d | $argErr"
  } else { $snippet = ($res -replace '\s+',' ').Substring(0, [Math]::Min(120,$res.Length)) }
  Write-Host ("{0,-28} -> {1}" -f $label, $snippet)
}

Write-Host "=== recognition actions shape brute-force ===" -ForegroundColor Cyan
Try-Actions 'string "Ocr"'            'Ocr'
Try-Actions 'int 1'                   1
Try-Actions 'int[] [1]'               @(1)
Try-Actions 'str[] ["FullText"]'      @('FullText')
Try-Actions 'str[] ["Text"]'          @('Text')
Try-Actions 'str[] ["Recognition"]'   @('Recognition')
Try-Actions 'str[] ["ocr"]'           @('ocr')
Try-Actions 'str[] ["Svg"]'           @('Svg')
Try-Actions 'obj {type:Ocr}'          @{ type='Ocr' }
Try-Actions 'obj[] [{actionType:FullText}]' @(@{ actionType='FullText' })
Try-Actions 'obj {ocr:true}'          @{ ocr=$true }
Try-Actions 'str[] ["OpticalCharacterRecognition"]' @('OpticalCharacterRecognition')
Try-Actions 'str[] ["LayoutAnalysis"]' @('LayoutAnalysis')
Try-Actions 'str[] ["ExtractText"]'   @('ExtractText')
