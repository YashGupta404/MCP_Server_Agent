# Sets active system to the Integrations Agent, then lists queries to confirm the stack is ready.
$out = @()
$s = Join-Path $PSScriptRoot "mcp-admin.ps1"
$out += "set_active: " + ((& $s -Tool "set_active_system_configuration" -ArgsJson '{"friendlyName":"OnBase9714_Integrations_Agent"}') | Out-String).Trim()
$q = (& $s -Tool "list_queries" -ArgsJson '{}') | ConvertFrom-Json
$out += "queries: " + (($q.data.queries | ForEach-Object { $_.id + '=' + $_.name }) -join '; ')
$out += "resolvedSystem: " + $q.resolvedContext.systemFriendlyName
$out | Out-File "$env:TEMP\ready.txt" -Encoding ascii
