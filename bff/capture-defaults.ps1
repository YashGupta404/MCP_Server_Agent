param(
  [string]$DocumentTypeId = "new-hire-checklist",
  [string]$BusinessObjectType = "employee",
  [string]$SingleValuedJson = "[]"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$endpoint = "http://localhost:5200/mcp"
$apiKey   = "lo1uLULmaPHg5dKLJGvLGLd1j8hF/ZQ6T7lokyuxvlA="

$handler = New-Object System.Net.Http.HttpClientHandler
$client  = New-Object System.Net.Http.HttpClient($handler)
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
  $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  return @{ Body = $body; SessionId = $sid; Status = [int]$resp.StatusCode }
}
function Parse-Sse([string]$body) {
  $last = $null
  foreach ($line in ($body -split "`n")) {
    $t = $line.Trim()
    if ($t.StartsWith("data:")) { $last = $t.Substring(5).Trim() }
  }
  if (-not $last) { $last = $body.Trim() }
  return $last | ConvertFrom-Json
}

# initialize
$initReq = @{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2025-06-18"; capabilities=@{}; clientInfo=@{ name="disc"; version="0.1" } } } | ConvertTo-Json -Depth 10 -Compress
$r1 = Send-Rpc $initReq $null $null
$sid = $r1.SessionId
$init = Parse-Sse $r1.Body
$protocol = "2025-06-18"
if ($init.result.protocolVersion) { $protocol = $init.result.protocolVersion }

# notifications/initialized
$noteReq = @{ jsonrpc="2.0"; method="notifications/initialized" } | ConvertTo-Json -Compress
[void](Send-Rpc $noteReq $sid $protocol)

$arguments = @{
  documentTypeId                          = $DocumentTypeId
  businessObjectType                      = $BusinessObjectType
  singleValuedBusinessObjectAttributesJson = $SingleValuedJson
}
$callReq = @{ jsonrpc="2.0"; id=2; method="tools/call"; params=@{ name="get_capture_default_attributes"; arguments=$arguments } } | ConvertTo-Json -Depth 20 -Compress
$r3 = Send-Rpc $callReq $sid $protocol
$call = Parse-Sse $r3.Body
if ($call.result.content) {
  foreach ($c in $call.result.content) { if ($c.type -eq "text") { Write-Output $c.text } }
} else {
  Write-Output ($call | ConvertTo-Json -Depth 30)
}
