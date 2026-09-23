# Reports stack status: ports 5200 (MCP) and 5010 (BFF), and the MCP active system.
$out = @()
foreach ($p in 5200, 5010) {
  $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
  $out += ("PORT {0}: {1}" -f $p, $(if ($c) { "LISTENING (PID " + ($c[0].OwningProcess) + ")" } else { "DOWN" }))
}
try {
  $s = Join-Path $PSScriptRoot "mcp-admin.ps1"
  $j = (& $s -Tool "list_system_configurations" -ArgsJson '{}') | ConvertFrom-Json
  $out += "MCP reachable: yes"
} catch {
  $out += "MCP reachable: NO - $($_.Exception.Message)"
}
$out | Out-File "$env:TEMP\stack.txt" -Encoding ascii
