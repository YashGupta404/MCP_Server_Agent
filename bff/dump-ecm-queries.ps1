# Writes the active system's ECM query catalog (same shape the BFF sends the mapper agent as ecmQueries)
# to ke-poc/agent-test/ecmQueries.txt. Run:  . .\load-mcp-key.ps1; .\dump-ecm-queries.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$list = (& .\mcp-admin.ps1 -Tool list_queries -ArgsJson '{}' | Out-String) | ConvertFrom-Json
$out = @()
foreach ($q in $list.data.queries) {
  $argsJson = '{"queryId":"' + $q.id + '"}'
  $d = ((& .\mcp-admin.ps1 -Tool get_query_metadata -ArgsJson $argsJson | Out-String) | ConvertFrom-Json).data
  $out += [ordered]@{
    id = "$($q.id)"; name = $q.name; type = $q.type; maxResults = $d.maxResults
    inputs = @($d.inputs | ForEach-Object { [ordered]@{ id = "$($_.id)"; name = $_.name; dataType = $_.dataType; required = $_.required; supportedOperators = @($_.supportedOperators) } })
    resultColumns = @($d.resultColumns | ForEach-Object { [ordered]@{ id = "$($_.id)"; name = $_.name; dataType = $_.dataType } })
  }
}
$json = ConvertTo-Json -InputObject $out -Depth 10 -Compress
$path = Join-Path $PSScriptRoot "..\ke-poc\agent-test\ecmQueries.txt"
[System.IO.File]::WriteAllText($path, $json)
"Wrote $($out.Count) queries ($($json.Length) chars) to $path"
