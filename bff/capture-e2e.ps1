param(
  [string]$DocumentTypeId = "new-hire-checklist",
  [string]$BusinessObjectType = "employee",
  [string]$BusinessObjectId = "4bc212416f234ba1b4749e4bebe4c2eb"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$mcp      = "http://localhost:5200"
$endpoint = "$mcp/mcp"
$apiKey   = "lo1uLULmaPHg5dKLJGvLGLd1j8hF/ZQ6T7lokyuxvlA="

$handler = New-Object System.Net.Http.HttpClientHandler
$client  = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)

# --- stage a tiny PDF ---
$pdf = @(
  '%PDF-1.4','1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj',
  '2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj',
  '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj',
  'trailer<</Size 4/Root 1 0 R>>','%%EOF'
) -join "`n"
$bytes = [System.Text.Encoding]::ASCII.GetBytes($pdf)
$b64   = [Convert]::ToBase64String($bytes)
$fileName = "e2e-agent-test.pdf"
$stageBody = @{ fileName = $fileName; mime = "application/pdf"; dataBase64 = $b64 } | ConvertTo-Json -Compress
$stageReq  = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$mcp/staging/upload")
$stageReq.Headers.TryAddWithoutValidation("X-Api-Key", $apiKey) | Out-Null
$stageReq.Content = New-Object System.Net.Http.StringContent($stageBody, [System.Text.Encoding]::UTF8, "application/json")
$stageResp = $client.SendAsync($stageReq).GetAwaiter().GetResult()
$stagingId = (($stageResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json).stagingId
Write-Host "stagingId = $stagingId"

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
function Tool-Text($call) {
  if ($call.result.content) { return (($call.result.content | Where-Object { $_.type -eq "text" }) | ForEach-Object { $_.text }) -join "`n" }
  return ($call | ConvertTo-Json -Depth 30)
}

$init = Parse-Sse (Send-Rpc (@{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2025-06-18"; capabilities=@{}; clientInfo=@{ name="e2e"; version="0.1" } } } | ConvertTo-Json -Depth 10 -Compress) $null $null).Body
$r1 = Send-Rpc (@{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2025-06-18"; capabilities=@{}; clientInfo=@{ name="e2e"; version="0.1" } } } | ConvertTo-Json -Depth 10 -Compress) $null $null
$sid = $r1.SessionId
$protocol = "2025-06-18"
[void](Send-Rpc (@{ jsonrpc="2.0"; method="notifications/initialized" } | ConvertTo-Json -Compress) $sid $protocol)

# --- 1) get_capture_default_attributes (agent step 1) ---
$singleValued = ConvertTo-Json @(@{ name = "businessObjectId"; value = $BusinessObjectId }) -Compress
if (-not $singleValued.TrimStart().StartsWith("[")) { $singleValued = "[$singleValued]" }
$gcda = @{ jsonrpc="2.0"; id=2; method="tools/call"; params=@{ name="get_capture_default_attributes"; arguments=@{ documentTypeId=$DocumentTypeId; businessObjectType=$BusinessObjectType; singleValuedBusinessObjectAttributesJson=$singleValued } } } | ConvertTo-Json -Depth 20 -Compress
$attrText = Tool-Text (Parse-Sse (Send-Rpc $gcda $sid $protocol).Body)
$brace = $attrText.IndexOf('{')
$attrObj = ($attrText.Substring($brace) | ConvertFrom-Json)
$dataArrayJson = ($attrObj.data | ConvertTo-Json -Depth 10 -Compress)
if (-not $dataArrayJson.TrimStart().StartsWith("[")) { $dataArrayJson = "[$dataArrayJson]" }
Write-Host "`n--- attribute array handed to capture_document (agent step 2) ---"
Write-Host $dataArrayJson

# --- 2) capture_document with that array ---
$cap = @{ jsonrpc="2.0"; id=3; method="tools/call"; params=@{ name="capture_document"; arguments=@{ stagingId=$stagingId; documentTypeId=$DocumentTypeId; businessObjectType=$BusinessObjectType; businessObjectAttributesJson=$dataArrayJson; documentName=$fileName } } } | ConvertTo-Json -Depth 30 -Compress
Write-Host "`n--- capture_document result ---"
Write-Host (Tool-Text (Parse-Sse (Send-Rpc $cap $sid $protocol).Body))
