param(
  [string]$BusinessObjectId = "4bc212416f234ba1b4749e4bebe4c2eb",
  [string]$BusinessObjectType = "employee"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$endpoint = "http://localhost:5200/mcp"
$apiKey   = "lo1uLULmaPHg5dKLJGvLGLd1j8hF/ZQ6T7lokyuxvlA="
$client   = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(120)
function Send-Rpc([string]$json, [string]$sessionId, [string]$protocol) {
  $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $endpoint)
  $req.Headers.TryAddWithoutValidation("X-Api-Key", $apiKey) | Out-Null
  $req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") | Out-Null
  if ($sessionId) { $req.Headers.TryAddWithoutValidation("Mcp-Session-Id", $sessionId) | Out-Null }
  if ($protocol)  { $req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", $protocol) | Out-Null }
  $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
  $resp = $client.SendAsync($req).GetAwaiter().GetResult()
  $sid = $null
  if ($resp.Headers.Contains("Mcp-Session-Id")) { $sid = ($resp.Headers.GetValues("Mcp-Session-Id"))[0] }
  return @{ Body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); SessionId = $sid }
}
function Parse-Sse([string]$body) {
  $last = $null
  foreach ($line in ($body -split "`n")) { $t = $line.Trim(); if ($t.StartsWith("data:")) { $last = $t.Substring(5).Trim() } }
  if (-not $last) { $last = $body.Trim() }
  return $last | ConvertFrom-Json
}
$init = @{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2025-06-18"; capabilities=@{}; clientInfo=@{ name="list"; version="0.1" } } } | ConvertTo-Json -Depth 10 -Compress
$r1 = Send-Rpc $init $null $null
$sid = $r1.SessionId
$p = "2025-06-18"; $i = Parse-Sse $r1.Body; if ($i.result.protocolVersion) { $p = $i.result.protocolVersion }
[void](Send-Rpc (@{ jsonrpc="2.0"; method="notifications/initialized" } | ConvertTo-Json -Compress) $sid $p)
$args = @{ businessObjectId = $BusinessObjectId; businessObjectType = $BusinessObjectType }
$call = @{ jsonrpc="2.0"; id=2; method="tools/call"; params=@{ name="list_documents"; arguments=$args } } | ConvertTo-Json -Depth 20 -Compress
$r3 = Send-Rpc $call $sid $p
$res = Parse-Sse $r3.Body
if ($res.result.content) { foreach ($c in $res.result.content) { if ($c.type -eq "text") { Write-Output $c.text } } } else { Write-Output ($res | ConvertTo-Json -Depth 30) }
