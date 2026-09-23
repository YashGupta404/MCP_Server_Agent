# Dumps the active system's full solution config (pretty JSON) to a file.
$s = Join-Path $PSScriptRoot "mcp-admin.ps1"
$raw = & $s -Tool "get_solution_configurations" -ArgsJson '{}'
$j = $raw | ConvertFrom-Json
$j | ConvertTo-Json -Depth 100 | Out-File "$env:TEMP\soln_full.json" -Encoding utf8
