# BirkNext Real Data Coverage Matrix — 2026-11-07

Source: HEAD `50c6c0ba` (code tree `81b5fdb7`). Inputs read-only from `AIAssisted/testdata`. Test types: **E** exact deterministic, **S** source-derived
(independent count from the archive), **X** exploratory, **H** human review required. Evidence: scratch `realdata/api-results.json`,
`browser-results.json`, `shots/RD-UI-*.png` (not committed: may show real project content).

## Datasets

| Dataset | Size | SHA-256 (first 16) | Kind | Supported import | Expected modules |
|---|---|---|---|---|---|
| M2LB.zip | 8,248,351 | 22339207cd2ef2f7 | Spec-Kit + .NET (68 csproj, 28 feature folders, 6 XSD, 12 OpenAPI, 27 GraphQL) | ZIP | Import, Source Analysis, Delivery, Interpretation, Readiness, Integrations, M01 |
| M2LB-language.zip | 9,182,662 | a5c083c81e63c1a2 | Later export of the same solution (69 csproj, 33 feature folders) | ZIP | as above (authentic revision) |
| M2LB.Common.zip | 94,609 | 6015c521dbfe11ba | .NET library (2 csproj) | ZIP | Import, Source Analysis, tests |
| Terraform-BasicOpsInfra_M2LBM01.zip | 34,566 | 4228fef437b8cccb | Terraform | ZIP | Import, Infrastructure evidence |
| Terraform-BasicOpsInfra_Meldingsutveksler.zip | 27,712 | aaafd9e55241d344 | Terraform | ZIP | as above |
| Terraform-iac-m2lb.zip | 568,893 | aa8b1e87211f6813 | Terraform (129 .tf) + 1 csproj | ZIP | as above |
| Terraform-iac-m2lbm01.zip | 104,116 | 1623e18809b5542f | Terraform | ZIP | as above |
| Terraform-iac-meldingsutveksler.zip | 222,217 | 1f956ee62ca23dab | Terraform + 32 XSD | ZIP | as above + schemas |
| Tiltak 2 – Sikkert digitalt økosystem.zip | 725,097 | 283154f4b71002d0 | .NET (14 csproj), 24 XSD, 34 XML | ZIP | Import, Source Analysis, schemas |
| skolenaervaer.json | 86,846 | 3f8c6cb5008d66d6 | OpenAPI document | **UNSUPPORTED** as standalone import | (API Quality Review runtime — NOT RUN) |
| desktop.ini | 80 | 918aca43592e228d | Windows metadata | **UNSUPPORTED** (negative test) | — |

## Scenarios

| Test | Dataset | Module | Scenario | Expected | Actual | Type | Verdict |
|---|---|---|---|---|---|---|---|
| RD-IMP-1…9 | each ZIP above | Import Project | preview + commit (`/api/project-import`) | staged, snapshot Created | 9/9 Created | E | PASS |
| RD-PROV-1…9 | each ZIP | Import Project | archive identity | = file SHA-256 | 9/9 equal | E | PASS |
| RD-SA-1…9 | each ZIP | Source Analysis | snapshot re-read; projects; schemas | projects = csproj; schemas = xsd | 68/68, 69/69, 2/2, 14/14, 1/1, 0; 6/6, 6/6, 24/24, 32/32 | S | PASS |
| RD-SA-x | M2LB, M2LB-language, Common, Tiltak 2 | Test evidence | test projects / definitions discovered | (no independent oracle) | 29/1,710 · 30/2,043 · 1/113 · 4/327 | X | PASS (observed) |
| RD-DUP-1 | M2LB-language | Import Project | same bytes, other name | Reused, same id | Reused, same id | E | PASS |
| RD-REV-1 | M2LB → M2LB-language | Import Project | authentic revision | new snapshot, history kept | new id, old kept | E | PASS |
| RD-NEG-1 | DERIVED truncated Common | Import Project | corrupt ZIP | rejected | 400 ARCHIVE_INVALID_ZIP | E | PASS |
| RD-NEG-2 | DERIVED zip-slip | Import Project | `../` entry | rejected before staging | 400 ARCHIVE_PATH_TRAVERSAL | E | PASS |
| RD-NEG-3 | desktop.ini | Import Project | unsupported format | rejected | 400 ARCHIVE_UNSUPPORTED_FORMAT | E | PASS |
| RD-NEG-4 | DERIVED empty | Import Project | empty upload | rejected | 400 ARCHIVE_EMPTY_UPLOAD | E | PASS |
| RD-NEG-5 | — | Import Project | unknown staging id | 404 + guidance | 404 IMPORT_STAGING_EXPIRED | E | PASS |
| RD-REG-1/2 | M2LB, M2LB-language | Snapshots | registry by type + id | 200 | 200 | E | PASS |
| RD-DLV-1/2 | M2LB, M2LB-language | Delivery Reporting | Spec-Kit import | features = feature folders | 28/28, 33/33; tasks 1,415 / 1,742 | S | PASS |
| RD-DLV-3 | both | Delivery Reporting | revision kept | 2 snapshots, 2 fingerprints | 2 / 2 | E | PASS |
| RD-CAN-1 | M2LB-language | Canonical interpretation | source + delivery | version referencing both | 6,773 entities, 10,162 relations, 2 refs | E | PASS |
| RD-CAN-2 | M2LB-language | Canonical interpretation | unchanged re-run | reused, same counts | reused | E | PASS |
| RD-CAN-3 | M2LB-language | Canonical interpretation | identity uniqueness (200 sampled) | 0 duplicates | 0 | E | PASS |
| RD-REL-1 | M2LB-language | Relationship Review | review queue | questions for people, none auto-confirmed | 13 decisions, 2,430 suggestions; identity conflict open | H | PASS |
| RD-RR-1 | M2LB-language | Release Readiness | assessment | never Ready; execution Not assessed | Blocked (governance); execution NotAssessed | S | PASS |
| RD-RR-2 | M2LB-language | Release Readiness | freeze + re-read | unchanged | same decision and interpretation | E | PASS |
| RD-INT-1 | M2LB-language | Connected Systems | static discovery | candidates only | 54 candidates, 8 sources | X | PASS |
| RD-INT-2 | M2LB-language | Integration Catalog | nothing registered by discovery | 0 | 0 | E | PASS |
| RD-TOP-1 | M2LB-language + Terraform | Integration Topology | static view / contract discovery | view; discovery | view 200; discover **403** (capability) | X | PARTIAL |
| RD-M01-0 | — | M01 | static review without source | refused with reason | 400 "needs a Source Analysis snapshot" | E | PASS |
| RD-M01-1 | M2LB-language | M01 | static review with source | stored; runtime not passed | 200, latest = this run; no runtime pass | S | PASS |
| RD-TD-1 | — | Test Data | catalog | renders; nothing approved by import | as expected | X | PASS |
| RD-UI-01…21 | real imports | 19 modules + Git Sources + Source Analysis (Tiltak 2) | open, content, reload, errors | content, persisted, no errors | 21/21 | E/X | PASS |
| RD-UI-Z1 | — | Safety | external requests | none | none | E | PASS |
| RD-SAFE-1 | all 11 files | Safety | inputs unchanged | same SHA-256 | 11/11 | E | PASS |
| RD-XML-1 | Tiltak 2 | Test Data / XML | real XML vs real XSD | — | — | — | NOT RUN |
| RD-RT-1 | sources, skolenaervaer.json | AQR / FQR / Performance / IQR / Skolenærvær runtime | live execution | — | — | — | NOT RUN (external) |
