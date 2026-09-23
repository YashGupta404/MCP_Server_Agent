# Rebuilds soln2.json from the pristine soln.json using JavaScriptSerializer (which preserves arrays,
# unlike PowerShell ConvertFrom-Json which collapses single-element arrays). Clones account's
# additionalConfig entries + queries onto opportunity and campaign.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue

$raw = Get-Content "$env:TEMP\soln.json" -Raw
$root = $ser.DeserializeObject($raw)
$data = $root["data"]
$boc = $data["configurations"]["businessObjectConfig"]

$addlList = New-Object System.Collections.ArrayList
foreach ($e in $boc["additionalConfig"]) { [void]$addlList.Add($e) }
$acctAddl = @($boc["additionalConfig"] | Where-Object { $_["busObject"] -eq "account" })
$acctQ = (@($boc["queryConfig"] | Where-Object { $_["busObject"] -eq "account" })[0])["queries"]

function DeepClone($o) { return $ser.DeserializeObject($ser.Serialize($o)) }

foreach ($obj in "opportunity", "campaign") {
  foreach ($e in $acctAddl) { $c = DeepClone $e; $c["busObject"] = $obj; [void]$addlList.Add($c) }
  foreach ($q in $boc["queryConfig"]) { if ($q["busObject"] -eq $obj) { $q["queries"] = DeepClone $acctQ } }
}
$boc["additionalConfig"] = $addlList.ToArray()

$dataJson = $ser.Serialize($data)
[System.IO.File]::WriteAllText("$env:TEMP\soln2.json", $dataJson, (New-Object System.Text.UTF8Encoding($false)))

# quick verification summary
$log = @("len=" + $dataJson.Length)
$log += "additionalConfig:"
$boc["additionalConfig"] | ForEach-Object { $log += ("  " + $_["busObject"] + " / " + $_["ecmContentTypeName"]) }
$log += "queryConfig:"
$boc["queryConfig"] | ForEach-Object {
  $ids = @($_["queries"] | ForEach-Object { $_["id"] }) -join ","
  $log += ("  " + $_["busObject"] + ": " + $ids)
}
$log | Out-File "$env:TEMP\buildout.txt" -Encoding ascii
