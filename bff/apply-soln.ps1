# Applies the prepared solution config ($env:TEMP\soln2.json) to the active MCP system, capturing results.
$ErrorActionPreference = "Stop"
$out = @()
$s = Join-Path $PSScriptRoot "mcp-admin.ps1"
try {
  $dataJson = Get-Content "$env:TEMP\soln2.json" -Raw
  $argsJson = @{ dataJson = $dataJson } | ConvertTo-Json -Depth 60 -Compress
  $out += "POST length: $($dataJson.Length)"
  $postResult = (& $s -Tool "set_solution_configuration" -ArgsJson $argsJson 2>&1 | Out-String)
  $out += "POST RESULT: $postResult"
  $j = (& $s -Tool "get_solution_configurations" -ArgsJson '{}') | ConvertFrom-Json
  $b = $j.data.configurations.businessObjectConfig
  $out += "--- additionalConfig ---"
  $b.additionalConfig | ForEach-Object { $out += ("  {0} / {1}" -f $_.busObject, $_.ecmContentTypeName) }
  $out += "--- queryConfig ---"
  $b.queryConfig | ForEach-Object { $out += ("  {0}: {1}" -f $_.busObject, ((($_.queries | ForEach-Object { $_.id }) -join ','))) }
} catch {
  $out += "EXCEPTION: $($_.Exception.Message)"
}
$out | Out-File "$env:TEMP\applyout3.txt" -Encoding ascii
