# Generates OCR-friendly sample documents (PNG) for the CIC system's document types, into bff/samples-cic/.
# CIC configured-per-record types seen earlier: account->prescription, contact->case-content-type,
# opportunity->prescription. These samples cover the main CIC types for classification + extraction testing.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'samples-cic'
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
  foreach ($ln in $Lines) { $g.DrawString($ln, $bodyFont, $black, 60, $y); $y += 46 }
  $g.DrawString('(Sample document generated for IDP classification testing.)', $bodyFont, $gray, 60, ($h-80))
  $path = Join-Path $outDir $File
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "  wrote $path"
}

Write-Host "Generating CIC sample documents..." -ForegroundColor Cyan

# prescription (account/opportunity) -> IDP fills Medication Name / Dosage / Multi Speciality / Pharmacy / Physician Name / Test
New-Doc -File 'prescription.png' -Title 'MEDICAL PRESCRIPTION (Rx)' -Lines @(
  'Patient Name:  John A. Miller',
  'Physician Name:  Dr. Sarah Lin, MD',
  'Medication Name:  Amoxicillin 500 mg capsules',
  'Dosage:  1 capsule three times daily',
  'Pharmacy:  Riverside Pharmacy, 22 Oak Street',
  'Multi Speciality:  Cardiology, Pediatrics',
  'Test:  Blood Panel',
  'Date Issued:  2026-10-06'
)

# case-content-type (contact) -> IDP fills Doc Name (+ Case Reason/Amount/Date/Status if configured)
New-Doc -File 'case-file.png' -Title 'LEGAL CASE FILE' -Lines @(
  'Doc Name:  Smith v. Globex Case File',
  'Case Number:  CV-2026-00817',
  'Case Reason:  Breach of contract and damages',
  'Case Amount:  15000',
  'Case Date:  2026-09-18',
  'Case Status:  Pending',
  'Court:  Superior Court, County of Marion',
  'Plaintiff:  Jane Doe        Defendant:  Globex Corporation'
)

# bills-content-type -> Billing Month / Billing Serial ID / Bill Date
New-Doc -File 'bill.png' -Title 'BILL / STATEMENT' -Lines @(
  'Billing Month:  October 2026',
  'Billing Serial ID:  BILL-4471',
  'Bill Date:  2026-10-05',
  'Account:  Globex Corporation',
  'Service:  Monthly utilities',
  'Amount Due:  $1,240.00',
  'Due Date:  2026-11-01'
)

# opportunity-content-type -> Opportunity Name / Type / Date / Id
New-Doc -File 'opportunity.png' -Title 'SALES OPPORTUNITY' -Lines @(
  'Opportunity Name:  Enterprise Cloud Platform Deal',
  'Opportunity Type:  New Business',
  'Opportunity Id:  OPP-558120',
  'Opportunity Date:  2026-12-20',
  'Account:  Globex Corporation',
  'Stage:  Negotiation',
  'Amount:  $250,000'
)

# dev-test-account -> Name / Date / Integer Id / Floating Point / Boolean
New-Doc -File 'account-test.png' -Title 'ACCOUNT RECORD' -Lines @(
  'Name:  Globex Test Account',
  'Date:  2026-08-15',
  'Integer Id:  12',
  'Floating Point:  42.75',
  'Boolean:  true',
  'Owner:  A. Rodriguez',
  'Status:  Active'
)

Write-Host "Done. Files are in: $outDir" -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
