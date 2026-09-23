# Dumps account vs opportunity queryConfig.queries from soln2.json for comparison.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$data = $ser.DeserializeObject((Get-Content "$env:TEMP\soln2.json" -Raw))
$qc = $data["configurations"]["businessObjectConfig"]["queryConfig"]
$acct = @($qc | Where-Object { $_["busObject"] -eq "account" })[0]
$opp  = @($qc | Where-Object { $_["busObject"] -eq "opportunity" })[0]
$out = @()
$out += "ACCOUNT queries type: " + $acct["queries"].GetType().FullName
$out += "OPP queries type: " + $opp["queries"].GetType().FullName
$out += "ACCOUNT queries json:"
$out += $ser.Serialize($acct["queries"])
$out += ""
$out += "OPP queries json:"
$out += $ser.Serialize($opp["queries"])
$out | Out-File "$env:TEMP\inspect.txt" -Encoding ascii
