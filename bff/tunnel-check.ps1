$ErrorActionPreference = "Continue"
$dt = "C:\Users\ygupta\AppData\Local\Microsoft\WinGet\Packages\Microsoft.devtunnel_Microsoft.Winget.Source_8wekyb3d8bbwe\devtunnel.exe"

Write-Host "=== devtunnel process ==="
Get-CimInstance Win32_Process -Filter "Name='devtunnel.exe'" | Select-Object ProcessId, CommandLine | Format-List

Write-Host "=== devtunnel show giant-ant-2f6br43 ==="
& $dt show giant-ant-2f6br43 2>&1

Write-Host "`n=== probe public tunnel URL (what the cloud agent hits) ==="
Add-Type -AssemblyName System.Net.Http
$h = New-Object System.Net.Http.HttpClient
$h.Timeout = [TimeSpan]::FromSeconds(15)
$url = "https://4kw1kpcm-5200.asse.devtunnels.ms/mcp"
try {
  $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $url)
  $req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") | Out-Null
  $req.Headers.TryAddWithoutValidation("X-API-Key", "lo1uLULmaPHg5dKLJGvLGLd1j8hF/ZQ6T7lokyuxvlA=") | Out-Null
  $req.Content = New-Object System.Net.Http.StringContent('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tunnel-probe","version":"0.1"}}}', [System.Text.Encoding]::UTF8, "application/json")
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $resp = $h.SendAsync($req).GetAwaiter().GetResult()
  $sw.Stop()
  $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  Write-Host "HTTP $([int]$resp.StatusCode) in $($sw.ElapsedMilliseconds)ms"
  Write-Host "Body (first 400 chars): $($body.Substring(0, [Math]::Min(400, $body.Length)))"
} catch {
  Write-Host "REQUEST FAILED: $($_.Exception.Message)"
  if ($_.Exception.InnerException) { Write-Host "INNER: $($_.Exception.InnerException.Message)" }
}
