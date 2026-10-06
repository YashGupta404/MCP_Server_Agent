# Generates OCR-friendly sample documents (PNG) for testing the plugin's IDP auto-classify.
# Each file's content maps to one of the active 'cic' system document types.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'samples'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-Doc {
  param([string]$File, [string]$Title, [string[]]$Lines)
  $w = 1000; $h = 1300
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $black = [System.Drawing.Brushes]::Black
  $gray  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90,90,90))
  $titleFont = New-Object System.Drawing.Font('Arial', 34, [System.Drawing.FontStyle]::Bold)
  $bodyFont  = New-Object System.Drawing.Font('Arial', 20, [System.Drawing.FontStyle]::Regular)
  $penBlue = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40,80,160)), 3

  $g.DrawString($Title, $titleFont, $black, 60, 60)
  $g.DrawLine($penBlue, 60, 120, ($w-60), 120)
  $y = 170
  foreach ($ln in $Lines) {
    $g.DrawString($ln, $bodyFont, $black, 60, $y)
    $y += 46
  }
  $g.DrawString('(Sample document generated for IDP classification testing.)', $bodyFont, $gray, 60, ($h-80))

  $path = Join-Path $outDir $File
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "  wrote $path"
}

Write-Host "Generating sample documents..." -ForegroundColor Cyan

# OnBase account types: "Invoices" (IDP fills Invoice # / Invoice Total / Currency) and "COM - Application"
# (IDP fills Loan Number). Vendor Name / Entity Name auto-fill from the account; Invoice Date is static.

# ---- Invoices ----
New-Doc -File 'invoice-acme.png' -Title 'INVOICE' -Lines @(
  'Vendor Name:  ACME Supplies Inc.',
  'Invoice #:  INV-20471',
  'Invoice Date:  2026-10-06',
  'Invoice Total:  $4,815.00',
  'Currency:  USD',
  'Bill To:  Globex Corporation, Accounts Payable',
  'Description:  Office equipment and supplies',
  'Subtotal:  $4,500.00        Tax (7%):  $315.00',
  'Payment Due Date:  2026-11-05'
)

New-Doc -File 'invoice-globex.png' -Title 'INVOICE' -Lines @(
  'Vendor Name:  Globex Industrial Ltd.',
  'Invoice #:  INV-33820',
  'Invoice Date:  2026-09-22',
  'Invoice Total:  $12,340.50',
  'Currency:  USD',
  'Bill To:  Initech Corporation',
  'Description:  CNC machine parts and maintenance',
  'Subtotal:  $11,530.00        Tax (7%):  $810.50',
  'Payment Due Date:  2026-10-22'
)

New-Doc -File 'invoice-initech.png' -Title 'INVOICE' -Lines @(
  'Vendor Name:  Initech Services LLC',
  'Invoice #:  INV-77215',
  'Invoice Date:  2026-10-01',
  'Invoice Total:  EUR 980.00',
  'Currency:  EUR',
  'Bill To:  Umbrella Corporation',
  'Description:  Software licensing - annual',
  'Subtotal:  EUR 980.00        Tax:  EUR 0.00',
  'Payment Due Date:  2026-10-31'
)

New-Doc -File 'invoice-wayne.png' -Title 'INVOICE' -Lines @(
  'Vendor Name:  Wayne Enterprises',
  'Invoice #:  INV-10044',
  'Invoice Date:  2026-08-14',
  'Invoice Total:  $58,900.00',
  'Currency:  USD',
  'Bill To:  Stark Industries',
  'Description:  Security systems installation',
  'Subtotal:  $55,046.00        Tax (7%):  $3,854.00',
  'Payment Due Date:  2026-09-14'
)

# ---- COM - Application (loan applications) ----
New-Doc -File 'loan-application-globex.png' -Title 'LOAN APPLICATION' -Lines @(
  'Entity Name:  Globex Corporation',
  'Loan Number:  LN-558120',
  'Application Date:  2026-10-06',
  'Loan Type:  Commercial Equipment Financing',
  'Requested Amount:  $250,000',
  'Term:  60 months',
  'Applicant:  A. Rodriguez, CFO',
  'Collateral:  Manufacturing equipment',
  'Status:  Under Review'
)

New-Doc -File 'loan-application-umbrella.png' -Title 'LOAN APPLICATION' -Lines @(
  'Entity Name:  Umbrella LLC',
  'Loan Number:  LN-660947',
  'Application Date:  2026-09-18',
  'Loan Type:  Commercial Real Estate',
  'Requested Amount:  $1,200,000',
  'Term:  120 months',
  'Applicant:  S. Birkin, Director of Finance',
  'Collateral:  Downtown office building',
  'Status:  Submitted'
)

New-Doc -File 'loan-application-wayne.png' -Title 'LOAN APPLICATION' -Lines @(
  'Entity Name:  Wayne Enterprises',
  'Loan Number:  LN-402318',
  'Application Date:  2026-10-02',
  'Loan Type:  Working Capital Line of Credit',
  'Requested Amount:  $75,000',
  'Term:  24 months',
  'Applicant:  L. Fox, Treasurer',
  'Collateral:  Accounts receivable',
  'Status:  Pending Documents'
)

Write-Host "Done. Files are in: $outDir" -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
