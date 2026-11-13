# Data Model Explorer — Shared model extraction audit (2026-11-13)

Branch `008-traceability-first`. Scope: why the M2LB-language / SkoleService **Shared model** reported 0 normalized structures, 0 fields
and 0 relationships, while the source plainly contains field tables.

## Source verified (read-only)

`M2LB-language.zip` → `SkoleService/autodoc/data-model.md` (16,325 bytes; generated from the EF Core model snapshots, SQL Server).
It was read with `unzip -p` into a scratch copy. `AIAssisted/testdata/` was not modified.

Shape of the document:

```
## Context: SkolenaervaerDbContext (schema skolenaervaer)      ← line 16
### report (Report)                                            ← line 18
| Column | Type | Nullable | Constraints |
| report_id | uniqueidentifier | No | PK |
| version_id ... | ... | ... | FK → report (Restrict); IX |
...
## Context: UtdanningDbContext (schema utdanning)              ← line 271
## Relationships                                               ← line 353 (From | Cardinality | To | Foreign Key)
```

## Root cause

The cause is **F: entity heading recognition (section level)**. Causes A–E, G–K were ruled out:

| Path step | Result |
|---|---|
| Pipe-table tokenizing | Correct. Rows are `TableRow` tokens with cells. |
| Header recognition (`IsColumnHeaderRow`) | Correct. `Column` is the first cell. |
| Column extraction (`ParseColumnRow`) | Correct once an entity is open: Type, Nullable (`No` → false), `PK`/`FK` declared. |
| **H2 `Context: SkolenaervaerDbContext (schema …)`** | **Not recognized.** `NormalizeSectionHeading` reduces it to `Context`, which is not in the persistent or runtime section lists. So `currentEntitySectionKind` stays empty. |
| H3 `report (Report)` | With no entity section, the H3 falls into the generic `else`. No entity is opened, and every following table row is classified as informational. |

So the source is well formed and structured. The structured data was lost at the section-classification step, before any table was
read. That explains the "0 structures, 0 fields, many preserved informational blocks" picture.

## Fix (same parser, no second engine)

Changes in `DataModelAnalysisService`:

1. **Explicit EF Core context sections.** `DbContextSectionRe` accepts an H2 that names a context ending in `DbContext`, with an optional
   `Context:` prefix and an optional `(schema s)`. Such a section is persistent. A bare `Context` heading, or `Context: shared conventions`,
   is *not* treated as one (tested).
2. **Table headings inside such a section.** `DbTableHeadingRe` matches `### table (ClrType[, qualifier])` and `### a / b (ClrType)`.
   - The table name is the identity, because FK arrows name tables. One CLR type over several tables keeps the type name and lists the
     tables in the description; no table is guessed.
   - The description records "EF Core entity X in YDbContext (schema s)" and any qualifier, for example "owned by ReportVersion".
3. **Same table in two contexts.** `processed_event` exists in both schemas. The later one is qualified as `processed_event (utdanning)`
   instead of being merged. Its "Same shape as …" prose copies no columns.
4. **Unique columns.** Only a standalone `UQ` declares a unique column. `UQ (a, b, …)` and `in UQ` are parts of a composite key and are
   not flagged.
5. **Relationship tables** (`From | Cardinality | To | Foreign Key`). A row becomes a relationship only when From and To each name a
   single structure and the key, if present, names one column.
   - `(logical)` rows keep their cardinality as the type and are never called FK.
   - Rows that only repeat a column's `FK →` declaration are de-duplicated.
   - List rows (`a, b`), wildcards (`dekning_basis*`) and prose (`every …`, `Person's child registration`) set the existing
     *unsupported relationship notation* flag. They are not converted.

Nothing is inferred from prose. Nullability stays Unknown where a table does not declare it.

## Real-source result (scratch probe; not committed, because it reads testdata)

| Measure | Before | After |
|---|---|---|
| Normalized structures | 0 | 25 tables (19 in `skolenaervaer`, 6 in `utdanning`) |
| Fields | 0 | 186 |
| Columns with unknown nullability | — | 0 (every row declares Yes/No) |
| Declared primary-key columns | 0 | 33 (composite `PK 1`/`PK 2` included) |
| Relationships | 0 | 4: the 3 declared FKs (`report_version.report_id → report`, `fravaer_line.version_id → report_version`, `lock_postponement.period → reporting_period`) plus the logical `report.current_version_id → report_version` |

The document itself says "Only three foreign keys are declared", which matches the result.

The Feature model (`specs/001-skoleservice/data-model.md`, UtdanningStatus) is parsed by the same code. Its behavior is unchanged
(the existing DataModel tests pass).

## Tests

`BirkNext.Web.Tests/Services/DataModelEfCoreContextTests.cs` uses a synthetic fixture with the same shape and no product data. It covers:

- context recognition;
- table identity and provenance line;
- the multi-table CLR type;
- the cross-context duplicate;
- declared types, nullability, PK/FK and UQ semantics;
- relationship-table rows: single, logical, and list/prose rows that stay unsupported;
- non-context headings.

The DataModel-focused suite passed 143/143. The full frontend suite passed 8,754/8,754.

## Remaining gaps (not done in this task)

The brief also asks for a full modern UX redesign of the explorer: schema workspace, entity explorer, relationship visualization and
focus views. That redesign is **not implemented here**. This task fixed the extraction defect that made the Shared model empty, which
had to come first. Also outstanding:

- Indexes declared inline in the Constraints column (`IX`, `IX (period, status)`) are not yet extracted as `DataIndex`. They remain in the
  column description.
- Composite `UQ (…)` declarations are not yet extracted as unique constraints. They remain in the description, which is honest but
  not yet structured.
- Browser acceptance of the Explorer with the real import was not run in this task.
