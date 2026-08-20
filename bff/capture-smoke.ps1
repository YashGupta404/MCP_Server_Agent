param(
  [string]$DocumentTypeId = "new-hire-checklist",
  [string]$BusinessObjectType = "employee",
  [string]$EmployeeId = "21021",
  [string]$AttributesJson = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$mcp     = "http://localhost:5200"
$endpoint = "$mcp/mcp"
$apiKey   = "lo1uLULmaPHg5dKLJGvLGLd1j8hF/ZQ6T7lokyuxvlA="

$handler = New-Object System.Net.Http.HttpClientHandler
$client  = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)

# --- 1) Stage a small valid PDF to the MCP staging endpoint ---
$pdf = @(
  '%PDF-1.4',
  '1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj',
  '2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj',
  '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj',
  '4 0 obj<</Length 58>>stream',
  'BT /F1 24 Tf 72 700 Td (Capture smoke test) Tj ET',
  'endstream endobj',
  '5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj',
  'trailer<</Size 6/Root 1 0 R>>',
  '%%EOF'
) -join "`n"
$bytes   = [System.Text.Encoding]::ASCII.GetBytes($pdf)
$b64     = [Convert]::ToBase64String($bytes)
$fileName = "capture-smoke-test.pdf"

$stageBody = @{ fileName = $fileName; mime = "application/pdf"; dataBase64 = $b64 } | ConvertTo-Json -Compress
$stageReq  = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$mcp/staging/upload")
$stageReq.Headers.TryAddWithoutValidation("X-Api-Key", $apiKey) | Out-Null
$stageReq.Content = New-Object System.Net.Http.StringContent($stageBody, [System.Text.Encoding]::UTF8, "application/json")
$stageResp = $client.SendAsync($stageReq).GetAwaiter().GetResult()
$stageText = $stageResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
Write-Host "STAGE [$([int]$stageResp.StatusCode)]: $stageText"
$stagingId = ($stageText | ConvertFrom-Json).stagingId
if (-not $stagingId) { throw "No stagingId returned" }
Write-Host "stagingId = $stagingId"

# --- helpers for the MCP JSON-RPC handshake ---
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
$initReq = @{ jsonrpc="2.0"; id=1; method="initialize"; params=@{ protocolVersion="2025-06-18"; capabilities=@{}; clientInfo=@{ name="capture-smoke"; version="0.1" } } } | ConvertTo-Json -Depth 10 -Compress
$r1 = Send-Rpc $initReq $null $null
$sid = $r1.SessionId
$init = Parse-Sse $r1.Body
$protocol = "2025-06-18"
if ($init.result.protocolVersion) { $protocol = $init.result.protocolVersion }

# notifications/initialized
$noteReq = @{ jsonrpc="2.0"; method="notifications/initialized" } | ConvertTo-Json -Compress
[void](Send-Rpc $noteReq $sid $protocol)

# --- build businessObjectAttributes ---
if (-not $AttributesJson) {
  # Real Workday field ids (from get_capture_default_attributes) that tie the doc to the employee record.
  $attrs = @(
    @{ id = "hcmisbebo_businessObjectId"; name = "hcmisbebo_businessObjectId"; value = "4bc212416f234ba1b4749e4bebe4c2eb"; dataType = "String" },
    @{ id = "hcmisbeemp_employeeId";      name = "hcmisbeemp_employeeId";      value = $EmployeeId; dataType = "String" },
    @{ id = "hcmisbeemp_firstName";       name = "hcmisbeemp_firstName";       value = "Anthony";   dataType = "String" },
    @{ id = "hcmisbeemp_lastName";        name = "hcmisbeemp_lastName";        value = "Rizzo";     dataType = "String" },
    @{ id = "HCMISBE_DOC_TYPE_ID";        value = $DocumentTypeId }
  )
  $AttributesJson = ConvertTo-Json $attrs -Depth 5 -Compress
  if (-not $AttributesJson.TrimStart().StartsWith("[")) { $AttributesJson = "[$AttributesJson]" }
}
Write-Host "businessObjectAttributesJson = $AttributesJson"

$arguments = @{
  stagingId                    = $stagingId
  documentTypeId               = $DocumentTypeId
  businessObjectType           = $BusinessObjectType
  businessObjectAttributesJson = $AttributesJson
  documentName                 = $fileName
}

$callReq = @{ jsonrpc="2.0"; id=2; method="tools/call"; params=@{ name="capture_document"; arguments=$arguments } } | ConvertTo-Json -Depth 20 -Compress
Write-Host "`n--- tools/call capture_document ---"
$r3 = Send-Rpc $callReq $sid $protocol
$call = Parse-Sse $r3.Body
if ($call.result.content) {
  foreach ($c in $call.result.content) {
    if ($c.type -eq "text") { Write-Output $c.text }
  }
} else {
  Write-Output ($call | ConvertTo-Json -Depth 30)
}
