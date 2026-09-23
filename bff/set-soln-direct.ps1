# Direct MCP JSON-RPC client for set_solution_configuration with a LARGE payload.
# Avoids PowerShell 5.1 ConvertTo-Json on the big object (which hangs) by building the
# tools/call body via string concatenation, escaping only the dataJson string.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$endpoint = "http://localhost:5200/mcp"
$apiKey = $env:MCP_API_KEY
if (-not $apiKey) { throw "MCP_API_KEY not set. Run:  . `"$PSScriptRoot\load-mcp-key.ps1`"  first (loads it from dotnet user-secrets)." }
$handler = New-Object System.Net.Http.HttpClientHandler
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(180)

function Send-Rpc([string]$json, [string]$sid, [string]$proto) {
  $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $endpoint)
  $req.Headers.TryAddWithoutValidation("X-Api-Key", $apiKey) | Out-Null
  $req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") | Out-Null
  if ($sid) { $req.Headers.TryAddWithoutValidation("Mcp-Session-Id", $sid) | Out-Null }
  if ($proto) { $req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", $proto) | Out-Null }
  $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
  $resp = $client.SendAsync($req).GetAwaiter().GetResult()
  $rsid = $null
  if ($resp.Headers.Contains("Mcp-Session-Id")) { $rsid = ($resp.Headers.GetValues("Mcp-Session-Id"))[0] }
  $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  return @{ Body = $body; SessionId = $rsid }
}
function Parse-Sse([string]$body) {
  $last = $null
  foreach ($line in ($body -split "`n")) { $t = $line.Trim(); if ($t.StartsWith("data:")) { $last = $t.Substring(5).Trim() } }
  if (-not $last) { $last = $body.Trim() }
  return $last | ConvertFrom-Json
}

$log = @()
try {
  $init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"direct","version":"0.1"}}}'
  $r1 = Send-Rpc $init $null $null
  $sid = $r1.SessionId
  $proto = "2025-06-18"
  $initObj = Parse-Sse $r1.Body
  if ($initObj.result.protocolVersion) { $proto = $initObj.result.protocolVersion }
  [void](Send-Rpc '{"jsonrpc":"2.0","method":"notifications/initialized"}' $sid $proto)

  $dataJson = Get-Content "$env:TEMP\soln2.json" -Raw
  # Escape the config as a JSON string LITERAL (quoted) via .NET, so dataJson is passed as a string, not an object.
  Add-Type -AssemblyName System.Web.Extensions
  $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
  $ser.MaxJsonLength = [int]::MaxValue
  $escaped = $ser.Serialize([string]$dataJson)  # yields "....." (quoted, escaped)
  $log += ("escaped starts with: " + $escaped.Substring(0, 1))
  $callBody = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"set_solution_configuration","arguments":{"dataJson":' + $escaped + '}}}'
  $r3 = Send-Rpc $callBody $sid $proto
  $call = Parse-Sse $r3.Body
  if ($call.result.content) { foreach ($c in $call.result.content) { if ($c.type -eq "text") { $log += ("RESULT: " + $c.text) } } }
  else { $log += ("RAW: " + ($call | ConvertTo-Json -Depth 8 -Compress)) }
} catch {
  $log += "EXCEPTION: $($_.Exception.Message)"
}
$log | Out-File "$env:TEMP\setdirect.txt" -Encoding ascii
