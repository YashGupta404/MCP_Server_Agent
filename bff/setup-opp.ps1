# Sets active system, applies prepared solution config, and verifies. Writes results to a file.
$ErrorActionPreference = "Stop"
$out = @()
$s = Join-Path $PSScriptRoot "mcp-admin.ps1"
try {
  $r1 = (& $s -Tool "set_active_system_configuration" -ArgsJson '{"friendlyName":"OnBase9714_Integrations_Agent"}' 2>&1 | Out-String)
  $out += "ACTIVE: $r1"

  $dataJson = Get-Content "$env:TEMP\soln2.json" -Raw
  $argsJson = @{ dataJson = $dataJson } | ConvertTo-Json -Depth 60 -Compress
  $out += "POST length: $($dataJson.Length)"
  $r2 = (& $s -Tool "set_solution_configuration" -ArgsJson $argsJson 2>&1 | Out-String)
  $out += "POST RESULT: $r2"

  $j = (& $s -Tool "get_solution_configurations" -ArgsJson '{}') | ConvertFrom-Json
  $b = $j.data.configurations.businessObjectConfig
  $out += "--- additionalConfig ---"
  $b.additionalConfig | ForEach-Object { $out += ("  {0} / {1}" -f $_.busObject, $_.ecmContentTypeName) }
  $out += "--- queryConfig ---"
  $b.queryConfig | ForEach-Object { $out += ("  {0}: {1}" -f $_.busObject, ((($_.queries | ForEach-Object { $_.id }) -join ','))) }
} catch {
  $out += "EXCEPTION: $($_.Exception.Message)"
}
$out | Out-File "$env:TEMP\setupopp.txt" -Encoding ascii
