# Source Analysis — functional audit, part 1: Project Glossary extraction (2026-11-13)

Branch `008-traceability-first`. The starting HEAD was 9a8bdb61.

## Glossary defect (Phase 36): confirmed and fixed

**Source verified (read-only):** `M2LB-language.zip` → `docs/glossary.md` (30,635 bytes). Its tables, with the term-column header of each:

| Section | Term-column header | What the rows are |
|---|---|---|
| §1 How to name something | `Kind` (Kind \| Convention \| Example) | naming conventions: Timestamp, Date, Plural, Kept noun in snake/kebab case, Raw æ, ø, å |
| §2.1–§2.7 Kept domain nouns | `Norwegian` | **terms** (e.g. `barn`) |
| §3 Pinned names | `Owner` | pinned-name owner groups (BiRK, …) |
| §4 Looks like a domain word | `Norwegian` | terms (Norwegian → English) |
| §5 Words with two meanings | `Word` | terms |
| §6 Common generic words | `Norwegian` | terms |
| §7 Known inconsistencies | `Issue` | known inconsistencies |
| §8 Sources | `Source` | the glossary's inputs |

**Root cause.** `ProjectGlossaryAnalyzer.Entries` turned *every* table row into a glossary entry. Consumers then treated every
authoritative row whose section was not "inconsistencies" as a term. As a result, "Timestamp", "Date", "Plural", "Kept noun in
snake/kebab case" and "Raw æ, ø, å" appeared as terms in the Terms tab, alongside §3 owner groups and §8 source rows. `ProjectTerminology.Resolve`
could also resolve "Timestamp" as approved terminology. `barn` is a genuine term (§2.1), so it was correct.

**Fix (same analyzer, no second parser):**

- `GlossaryRowKind` (Term, NamingConvention, PinnedNames, KnownInconsistency, Source) is set from the table's own term-column header.
  - `Kind`/`Convention` → NamingConvention.
  - `Owner` → PinnedNames.
  - `Issue` → KnownInconsistency.
  - `Source` → Source.
  - Any other header stays Term, as before. No other glossary format changes meaning.
- Every row is kept, with its source section and line. Structural rows are evidence, not terms.
- `ProjectTerminology.Resolve` resolves Term rows only. The new `AuthoritativeTerms`, `KnownInconsistencies` and `StructuralRows` queries
  give the panel its lists.
- The analyzer version is now **2**. A snapshot analyzed with v1 has every row as Term:
  - the panel falls back to the section name for known inconsistencies;
  - it says that a re-analysis separates structural tables.
- Proposals stay proposals, because the authority of a proposal document is unchanged. Nothing is written back to the imported glossary.

**Tests:**

- Backend `ProjectGlossaryAnalyzerTests.Structural_glossary_tables_are_classified_by_their_header_and_are_never_terms`, with a real-shaped
  fixture of §1, §2.1, §3, §5, §7 and §8.
- Frontend `ProjectGlossaryPanelTests.Naming_conventions_pinned_names_and_sources_are_glossary_structure_not_terms`.
- The focused glossary suites passed: backend 8/8, frontend 9/9.

**Not verified here:** re-analysis of the real snapshot through the API, and browser acceptance. The real file's headers were checked by
inspection against the rule.

## Not yet done (rest of the Source Analysis brief)

- Compact snapshot header.
- Review Queue.
- Architecture, Data and ER-diagram redesign.
- Quality Evidence, contracts, tests and configuration explorers.
- Count-semantics audit (contracts 58 vs 54, "4 areas" vs findings).
- Real-source and browser acceptance.

These remain open.
