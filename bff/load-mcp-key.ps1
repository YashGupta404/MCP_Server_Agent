# Loads the MCP API key from the BFF's dotnet user-secrets into $env:MCP_API_KEY for THIS session.
# Dot-source it so the variable persists in your shell:   . .\load-mcp-key.ps1
# The key is the single source of truth in user-secrets; no script hardcodes it anymore.
$proj = Join-Path $PSScriptRoot 'UcebBff.csproj'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$line = & $dotnet user-secrets list --project $proj 2>$null | Where-Object { $_ -match '^Mcp:ApiKey\s*=' }
if (-not $line) {
  throw "Mcp:ApiKey not found in user-secrets. Set it once with:`n  & '$dotnet' user-secrets set 'Mcp:ApiKey' '<key>' --project '$proj'"
}
$env:MCP_API_KEY = ($line -split '=', 2)[1].Trim()
Write-Host "MCP_API_KEY loaded into this session from dotnet user-secrets."
