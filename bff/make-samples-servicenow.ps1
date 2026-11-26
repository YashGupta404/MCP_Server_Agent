# Generates OCR-friendly ServiceNow sample documents (PNG) for the IDP demo, into bff/samples-servicenow/.
#   change-request-form.png -> should classify as change-request-type and pre-fill
#                              hfsn_shortDescription / hfsn_changeState / hfsn_priority.
#   grocery-receipt.png     -> unrelated document; should come back Undefined (below the 0.6 class threshold).
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'samples-servicenow'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-Doc {
  param([string]$File, [string]$Title, [string[]]$Lines)
  $w = 1000; $h = 1300
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $black = [System.Drawing.Brushes]::Black
  $titleFont = New-Object System.Drawing.Font('Arial', 32, [System.Drawing.FontStyle]::Bold)
  $bodyFont  = New-Object System.Drawing.Font('Arial', 20, [System.Drawing.FontStyle]::Regular)
  $penBlue = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40,80,160)), 3
  $g.DrawString($Title, $titleFont, $black, 60, 60)
  $g.DrawLine($penBlue, 60, 120, ($w-60), 120)
  $y = 170
  foreach ($ln in $Lines) { $g.DrawString($ln, $bodyFont, $black, 60, $y); $y += 46 }
  $path = Join-Path $outDir $File
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "  wrote $path"
}

Write-Host "Generating ServiceNow sample documents..." -ForegroundColor Cyan

New-Doc -File 'change-request-form.png' -Title 'IT CHANGE REQUEST FORM' -Lines @(
  'Change Request Number:  CHG0040019',
  'Short Description:  Upgrade SAP payment processing module',
  'Change State:  Assess',
  'Priority:  2 - High',
  'Change Type:  Normal',
  'Requested By:  System Administrator',
  'Configuration Item:  SAP Enterprise Services',
  'Risk:  Moderate          Impact:  2 - Medium',
  'Planned Start:  2026-11-15 22:00',
  'Planned End:  2026-11-16 02:00',
  '',
  'Justification:',
  '  Apply vendor patch for SOX compliance on the payment module.',
  'Implementation Plan:',
  '  1. Take database backup   2. Deploy patch   3. Run smoke tests',
  'Backout Plan:',
  '  Restore backup and roll back the module version.',
  'Approval:  Change Advisory Board (CAB)'
)

New-Doc -File 'grocery-receipt.png' -Title 'FRESH MART GROCERY' -Lines @(
  '123 Market Street, Springfield',
  'Receipt #: 88412        Date: 2026-11-07  18:42',
  'Cashier: Maria',
  '',
  'Bananas 1 kg                      2.49',
  'Whole Milk 2 L                    3.19',
  'Sourdough Bread                   4.50',
  'Free-range Eggs (12)              5.99',
  'Cheddar Cheese 400 g              6.25',
  'Tomatoes 500 g                    1.89',
  '',
  'Subtotal                         24.31',
  'Sales Tax 5%                      1.22',
  'TOTAL                            25.53',
  '',
  'Paid by Visa ****4821',
  'Thank you for shopping with us!'
)

Write-Host "Done. Files in $outDir" -ForegroundColor Green
