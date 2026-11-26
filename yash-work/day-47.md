# Day 47 — KE PoC Steps 4/5: config generation + diff (7/7 MATCH)

_Continues day-46. Built the mapping generator (Step 4) + config emit/diff (Step 5). The AI-generated
ServiceNow solution-config fragment reproduces the hand-written one exactly. Covers 2026-11-16._

## What was built (ke-poc/)

- `catalog/servicenow-ecm-keywords.json` — the "known" ECM keyword catalog (simulates UCEB
  `list_document_types?fetchMetadata=true` / the customer-provided set): `change-request-type` with
  `hfsn_recordNumber`, `hfsn_recordOwner`, `hfsn_documentName`, `hfsn_shortDescription`, `hfsn_changeState`,
  `hfsn_priority`.
- `CatalogModels.cs` — `EcmCatalog` / `EcmDoctype` / `EcmKeyword`.
- `MappingGenerator.cs` — the config-less logic:
  - Layer 1: classification token-match -> catalog doctype.
  - Layer 2: pick record-id keyword by name heuristic (`recordnumber`/`businessobjectid`), **confirmed by
    value-matching** the context busObject id against KE NER `ids_identifiers`; owner keyword by heuristic ->
    `contextParams.boContext`/`boUser`.
  - Layer 3: `documentName` -> `inputSource 4` (file name); other content keywords -> `inputSource 6`
    (user/IDP provided). Builds import mappings + labels + display columns + default query.
- `Program.cs` — new `generate` mode: `dotnet run -- generate <contextJson> <catalogJson> <busType> <busId>
  [owner] [fileName]`. Emits `catalog/generated-servicenow-config.json` and diffs vs the repo-root
  `servicenow-solution-config.json`.

## Run result (2026-11-16)

Input: `samples/change-request-sample.context.json` (from the KE run) + the catalog + context
(`change_request` / `CHG0012345`).

```
Layer 1 (doctype): "change request" -> change-request-type
Layer 2 (linkage): hfsn_recordNumber [value-match CONFIRMED vs NER ids_identifiers], owner hfsn_recordOwner
Layer 3 (import):  hfsn_documentName->4; shortDescription/changeState/priority->6

Diff vs servicenow-solution-config.json:
  [MATCH] boContext.ecmProp        gen=hfsn_recordNumber | real=hfsn_recordNumber
  [MATCH] boUser.ecmProp           gen=hfsn_recordOwner  | real=hfsn_recordOwner
  [MATCH] ecmContentTypeName       gen=change-request-type | real=change-request-type
  [MATCH] import[hfsn_changeState]      6 | 6
  [MATCH] import[hfsn_documentName]     4 | 4
  [MATCH] import[hfsn_priority]         6 | 6
  [MATCH] import[hfsn_shortDescription] 6 | 6
Result: 7/7 fields match the hand-written config.
```

**Config-less thesis proven for ServiceNow `change_request`:** KE (classify + NER) + the known keyword catalog
+ the busObject id from the context agent regenerate the manual solution-config with no admin setup. The
record-linkage keyword was identified automatically via value-match (NER `CHG0012345` == context busObject id).

## Caveats / honesty

- The descriptive content fields (shortDescription/changeState/priority) land as `inputSource 6` by the
  "not an id/name/owner" rule — correct here, but on richer docs these would need true field->keyword matching
  (KE NER types don't map 1:1 to business keywords). That's the accuracy-limited part to harden next.
- Catalog is a local stand-in; wire it to the live UCEB `list_document_types?fetchMetadata=true` next.
- Still Context-API only; Data Curation (OCR) deferred for image/scanned docs.

## Next (day 48)

1. Replace the static catalog with a live UCEB `list_document_types?fetchMetadata=true` pull (per active
   system; iterate CIC + OnBase).
2. Test on a scanned/image change request (add the Data Curation OCR client) and on a 2nd doctype (incident).
3. Harden Layer 3: real field->keyword matching (semantic) for descriptive fields + confidence + review step.
4. Emit the full solution-config (operationConfig + queryConfig) and POST via `set_solution_configuration`.

## OnBase test — Salesforce `Invoices` (numeric keywords), 6/8 match

Stress-tested the generator against **Salesforce on OnBase**, where keyword ids are opaque numbers (228, 229,
...) instead of `hfs_*` names. Added:
- `EcmKeyword.Role` (optional `recordId`/`owner`/`documentName` hint) — needed because name heuristics can't
  read numeric ids. Generator prefers the role hint, else the name heuristic.
- Lenient doctype matching (prefix: "invoice" ~ "Invoices").
- `generate` now takes `[realConfigPath] [ecmContentTypeName]` and diffs the matching `additionalConfig`
  entry; context-param ids match against real `ecmProp` OR `ecmPropId` (OnBase numeric).
- `catalog/salesforce-onbase-invoices.json` (223 sObject Id=recordId, 224 owner, 228 Vendor Name, 229 Invoice
  #, 227 Invoice Total, 226 Invoice Date, 234 currency) + `samples/invoice-sample.txt`.

KE on the invoice: `textClassification="invoice"`; NER orgs/dates/monetary_values + `ids_identifiers=[INV-2026-004821]`.

Generate + diff vs `solution-config.json` `Invoices`:
```
Layer 2: recordId=223 [value-match NOT found -> role hint used], owner=224
Layer 3: 228/229/227/226/234 -> all inputSource 6
  [MATCH] boContext 223 (==real ecmPropId 223)   [MATCH] boUser 224   [MATCH] ecmContentTypeName Invoices
  [MATCH] import[227]=6  [MATCH] import[229]=6  [MATCH] import[234]=6
  [DIFF]  import[226] gen=6 | real=2   (real = hard-coded static date 08/20/2026 — not inferable)
  [DIFF]  import[228] gen=6 | real=1   (real quirkily uses "Vendor Name" as the record link)
Result: 6/8 match.
```

Findings:
- **Role hints work for opaque OnBase numeric keywords** — 223/224 identified correctly even though
  value-match FAILED (the invoice doc doesn't contain the Salesforce account id, only the invoice #). This
  proves the cold-start need for a provided BO-id designation when the id isn't in the document.
- Both DIFFs are **demo-config artifacts, not generator errors**: 226 is a hard-coded static date (AI sensibly
  defaults to user-provided 6); 228 "Vendor Name" is quirkily used as the record link (the generator correctly
  refuses to, picking 223). Arguably the generated config is cleaner than the hand-written one here.
- Validates: CIC/ServiceNow named keywords -> name heuristic + value-match; OnBase numeric keywords -> role
  hint (value-match is a bonus when the id appears in the doc).

## Next (day 48, updated)

1. Live UCEB `list_document_types?fetchMetadata=true` catalog (per active system; CIC + OnBase) with role
   hints derived server-side.
2. Add Data Curation OCR client for scanned/image docs; test incident + a 2nd OnBase doctype.
3. Harden Layer 3 descriptive field->keyword matching (semantic + confidence + review).
4. Emit full solution-config and POST via `set_solution_configuration`.

## OCR test — Context API OCRs images internally (no Data Curation needed)

Generated a **raster PNG** of the invoice via `System.Drawing` (text drawn as pixels, no text layer ->
forces real OCR): `samples/invoice-scan.png`. Ran it through the existing Context pipeline
(`dotnet run -- samples\invoice-scan.png`, contentType image/png).

Result (`samples/invoice-scan.context.json`, status SUCCESS):
- `textClassification = "invoice"`, full NER (orgs, dates, `monetary_values`, `ids_identifiers=[INV-2026-004821]`),
  accurate summary — all extracted from a pixels-only image.

**Conclusion: the KE Context API does OCR internally.** One pipeline (`cin-context-api`) handles text, PDF,
and scanned/image docs. So:
- The **Data Curation API is NOT needed** for this use case (and its submit/result schema was unverified anyway).
- **No second external-app client** (`cin-data-curation-api`) required.
- Last open dependency removed — the KE PoC is fully self-sufficient on the Context API.

## Next (day 48, revised)

1. Live UCEB `list_document_types?fetchMetadata=true` catalog (per active system; CIC + OnBase) + server-side
   role hints.
2. Wire IDP-style per-document value auto-fill: push KE extracted values into the `inputSource 6` keywords at
   upload (with confidence + review); harden label-based field->keyword matching.
3. Test incident + a 2nd OnBase doctype.
4. Emit full solution-config and POST via `set_solution_configuration`.
