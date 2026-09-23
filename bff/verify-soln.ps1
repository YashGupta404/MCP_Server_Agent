# Verifies the active system's solution config: lists additionalConfig + queryConfig by business object.
$ErrorActionPreference = "Stop"
$s = Join-Path $PSScriptRoot "mcp-admin.ps1"
$j = (& $s -Tool "get_solution_configurations" -ArgsJson '{}') | ConvertFrom-Json
$b = $j.data.configurations.businessObjectConfig
$o = @('=== additionalConfig ===')
$b.additionalConfig | ForEach-Object { $o += ('  {0} / {1}' -f $_.busObject, $_.ecmContentTypeName) }
$o += '=== queryConfig ==='
$b.queryConfig | ForEach-Object { $o += ('  {0}: {1}' -f $_.busObject, ((($_.queries | ForEach-Object { $_.id }) -join ','))) }
$o | Out-File "$env:TEMP\verify3.txt" -Encoding ascii
