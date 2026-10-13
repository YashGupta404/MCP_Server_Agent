# Live read-only probe: health, active system, and timed execution of query 115 vs light queries.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'load-mcp-key.ps1')
$admin = Join-Path $PSScriptRoot 'mcp-admin.ps1'
$RECORD = '001gK00001KbuonQAB'

function Timed([string]$label, [string]$tool, [string]$argsJson) {
  Write-Host ''
  Write-Host "=== $label ===" -ForegroundColor Cyan
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $out = & $admin -Tool $tool -ArgsJson $argsJson
    $sw.Stop()
    $first = ($out | Select-Object -First 3) -join "`n"
    Write-Host $first
  } catch {
    $sw.Stop()
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
  }
  Write-Host ("--- {0} took {1:N1}s ---" -f $label, $sw.Elapsed.TotalSeconds) -ForegroundColor Yellow
}

Write-Host '=== BUILD INFO (health) ===' -ForegroundColor Cyan
& $admin -Tool 'get_build_info' -ArgsJson '{}'

Timed '115 REAL RECORD maxResults=500' 'query_documents' ('{"businessObjectType":"account","queryId":"115","filterFieldId":"223","filterValue":"' + $RECORD + '","filterOperator":"EqualsCaseInsensitive","maxResults":500}')
Timed '115 REAL RECORD maxResults=10'  'query_documents' ('{"businessObjectType":"account","queryId":"115","filterFieldId":"223","filterValue":"' + $RECORD + '","filterOperator":"EqualsCaseInsensitive","maxResults":10}')
Timed '109 Invoices (light)' 'query_documents' '{"businessObjectType":"account","queryId":"109","filterFieldId":"228","filterValue":"ACME SUPPLIES","filterOperator":"EqualsCaseInsensitive","maxResults":25}'
Timed '110 Commercial Lending (light)' 'query_documents' '{"businessObjectType":"account","queryId":"110","filterFieldId":"232","filterValue":"Name","filterOperator":"EqualsCaseInsensitive","maxResults":25}'
