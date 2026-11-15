# Architecture review

## Reconstructed architecture

Separate backend and frontend solutions. Backend: ASP.NET Core .NET 8, controllers, EF Core/Npgsql, JWT bearer plus local-session authentication, domain services and file stores. Frontend: Blazor WebAssembly, Razor pages/components, typed API services, browser workspace stores and shared contracts. Test projects include xUnit, bUnit, Playwright, and real-project acceptance.

Text outline: Blazor pages/components → API clients → controllers → domain services → EF contexts and file stores. Project Import uses a shared archive reader, bounded staging and source snapshots. Test planning stores editable drafts and immutable snapshots; Manual Testing consumes saved cases and persists pinned run records. Delivery Reporting and Canonical Interpretation use separate schemas and snapshot/read interfaces.

## Strengths and boundaries

Project Import explicitly shares validated archive evidence and ownership boundaries. Manual runs read the plan service and pin executable content. Delivery and canonical interpretation have separately owned schemas and source readers. These are coherent boundaries.

However, Program.cs registers many contexts, stores and services across domains. LocalDataReset coordinates multiple owners. A related project can have records in source snapshots, scenario/guide data, interpretation, delivery, and run history. The design makes domain-specific evolution possible but raises the burden of migration/reset/provenance coordination.

## ARCH-01 — Distributed persistence ownership (P2, confirmed)

Evidence: backend Program.cs lines 174–204 register the primary context and multiple domain contexts; Services/LocalDataReset/LocalDataReset.cs lines 215–240 orchestrate different schema reset owners.

Root cause: domain isolation is spread across contexts/services without a concise ownership map tying entities to schema, migrations, reset owner, provenance key and tests.

Consequence: cross-domain changes can leave stale or orphaned evidence if one owner is missed. This is a maintenance risk, not a claim of current data corruption.

Remediation: publish the ownership matrix and add an architecture test requiring every registered context/schema to declare migrations, reset behavior and domain owner.

Regression test: CI assertion over registered DbContexts and reset/migration ownership, plus cross-store project/snapshot key tests.

Confidence: high.

No duplicate engine was proven in this bounded review. Do not consolidate discovery/interpretation/coverage solely by assumption. The composition root could later be split by domain after ownership is documented. A full dependency-cycle analysis was not run.
