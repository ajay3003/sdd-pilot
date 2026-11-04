# Project glossary — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed.

## PROJECT GLOSSARY OWNERSHIP AUDIT MAP

**Column key**

| Short name | Meaning |
|---|---|
| Proj-scoped | Whether data is scoped to one project |
| Leakage risk | Risk that one project's terms appear in another project |

| Implementation | Owner | Scope | Input → Output | Persistence | Proj-scoped | Global | Consumers | Leakage risk | Decision |
|---|---|---|---|---|---|---|---|---|---|
| (none before this task) glossary detection | — | — | — | — | — | — | — | — | **new**: `ProjectGlossaryAnalyzer` inside Source Analysis |
| Security Classification `ClassificationTerminologyMention` | Security Classification | per snapshot | Kode 6/7 wording in documents → mentions | SA snapshot | yes | no | Security Classification | none | keep (unrelated: classification vocabulary, not a project glossary) |
| Documentation Catalog "Legacy terminology" | BirkNext guide | BirkNext's own guide text | guide → findings | none | no (BirkNext product) | yes, by design | User guide | none: BirkNext's own words | keep (BirkNext terminology, separate system) |
| Canonical aliases (CR-K declared / acronym / conflict) | Interpretation engine | canonical scope | declared aliases → alias facts | canonical store | yes | no | correlation | low | reuse later as the alias channel; glossary aliases are **not** fed in (no identity merges) |
| Markdown parsing | `MarkdownTokenizer` (shared front/back) | per document | Markdown → tokens incl. table rows | none | — | — | document classification, Delivery parser, **glossary** | none | **reused**, no new parser |
| Source Analysis snapshot evidence sections | Source Analysis | per immutable snapshot | archive → evidence | `iqr_source_snapshots` JSON | yes (environment / project import) | no | every review | none | **extended** with `Glossary` |
| Document Quality / Documentation Drift / Generated Documentation | their engines | project documents | — | — | yes | no | — | — | not integrated in this pass (see Not done) |
| BirkNext status vocabulary (SemanticStatusBadge etc.) | BirkNext UI | product | — | — | — | yes | all | none | untouched: project terms never rename BirkNext UI/status words |

## What was built

**Ownership**
- BirkNext owns the glossary *capability*; the imported project owns the *content*.
- The glossary is **evidence of the project's Source Analysis snapshot** (`IqrSourceSnapshot.Glossary`), detected when the snapshot is analyzed, from the Markdown that analysis already read (`DocumentationCandidates`): no ZIP re-read, the shared `MarkdownTokenizer`, no new parser.
- It is never stored in System Settings, never global, and never applied to BirkNext UI or status words.

**Model** (`shared/ProjectGlossaryContracts.cs`)
- `ProjectGlossarySnapshot`: SourceSnapshotId, ProjectName, analyzer version, glossaries, entries, limitations.
- `ProjectGlossaryArtifact`: GlossaryId (path), SourcePath, Title, **Authority**, AuthorityEvidence, ContentHash, source Sections, ReferencedBy, EntryCount.
- `ProjectGlossaryEntry`: term (first column), PreferredForm / Meaning only when the table has such columns, Decision only when the row has a Status cell or a ✅ / ⚠ / ❌ marker, every cell by its own header, the source section as Category, and the line.
- No invented semantics.

**Authority** (from document evidence; a file name alone is never enough)

| Class | When |
|---|---|
| Proposed | The document names itself a proposal / pull-request item list. |
| Authoritative | It states its authority ("single list for the whole platform", "source of truth", "required by …"), or ≥ 2 other project documents refer to it **by its own path** (never a generic tail such as `checklists/requirements.md`). |
| Conflict | Several authoritative claimants: every one is marked, and no glossary resolves until reviewed. |
| Unknown | Glossary-like, with neither kind of evidence. |

A glossary-like document is one whose file name says glossary / ordliste / terminology, or whose **title is a glossary title**. A feature titled "Reference Data Correction and Terminology Fix" is not one; this real false positive was found and fixed in acceptance.

**Terminology queries** (`ProjectTerminology`, project-scoped)
- `Resolve` (authoritative rows only; proposals and conflicts never resolve), `Aliases` (term + preferred forms), `Proposals`, `TermsOf`.
- Terminology equivalence is **not** identity: nothing feeds the correlation engine's identity rules, so no Barn/Child entity merge is possible.

**UI**
- `ProjectGlossaryPanel` sits in **Source Analysis → Overview** for the selected snapshot. There is no new sidebar item.
- The summary shows project, authoritative glossary and term count, proposal rows and documents, and the snapshot.
- Tabs:
  - **Terms**: authoritative rows outside known-inconsistency sections.
  - **Proposals**: never mixed into Terms.
  - **Known inconsistencies**: the glossary's own section.
  - **Sources & authority**: every document with its authority and evidence.
- Search covers term, preferred form, meaning and section. Term detail shows every source column plus provenance (project · file · line · snapshot).
- With no glossary: the neutral "No project glossary detected. BirkNext still analyzes the project…".

**Snapshots**
- The glossary is versioned with the source snapshot.
- A new analysis is a new interpretation; earlier snapshots and their analyses keep theirs. No glossary-specific history tables.

## Real data (throwaway `birknext_ci2`, real archives from Downloads, localhost only)

**M2LB-language**
- `docs/glossary.md` is **Authoritative**:
  - stated authority ("single list for the whole platform, required by PS-10 … ADR-029");
  - referred to by 104 project documents;
  - 180 rows in its own sections (2.1–2.8 kept domain nouns, 3 pinned names, 4 looks-like-domain, 5 two meanings, 6 generic words, 7 known inconsistencies, 8 sources).
- 4 `glossary-pull-request-items.md` / `glossary-part2-items.md` files are **Proposed**: 81 proposal rows, kept apart.

**M2LB.Common:** no glossary, so nothing is inherited.

**Browser** (1440/390 × light/dark):
- Terms 171, proposals 81, known inconsistencies 9.
- Term detail and provenance are shown.
- The M2LB.Common empty state is neutral.
- No overflow, no errors, **0 external requests**.

## Tests

- **Backend** (`ProjectGlossaryAnalyzerTests`, 7):
  - A, authoritative glossary from statement + references, with source sections, rows, ✅ decision and line;
  - C/E/H, resolution from authoritative only, proposals stay proposals, preferred forms as aliases with no identity merge;
  - B/D, another project inherits nothing;
  - F, two claimants become a conflict with no winner;
  - a file name alone gives Unknown;
  - G, each snapshot keeps its own glossary;
  - the real-data false positives (terminology feature titles, generic path tails).
- Source Analysis / Project Import: 389 passed; the analyzer stage count rose to 18.
- **Frontend** (`ProjectGlossaryPanelTests`, 2): tabs kept apart, provenance, neutral empty state, conflict shown with no terms.
- Full frontend suite: 8012/8012 (one load flake passed on rerun).

## Not done in this pass (follow-ups)

- **Manual authority override:** "Mark as authoritative project glossary", as a project-scoped reviewed association shared by consumers.
- **Usage view:** where a term occurs across source symbols, documents and contracts.
- **Findings:** approved usage, preferred-form deviation, pinned-name and known-inconsistency matching in Source Analysis / code identifiers.
- **Consumer integration:** Document Quality "Terminology" tab, Documentation Drift terminology drift, Generated Documentation preference.
- **Project Evidence:** a "Glossary / terminology source" role.
- **Correlation engine:** TRANSLATES_TO / PREFERRED_TERM / PINNED_NAME relations.
- **Glossary diff** between two snapshots.

All of these read the same `IqrSourceSnapshot.Glossary` through `ProjectTerminology`; no second store or scanner is needed.

## Confirmations

- BirkNext owns the glossary capability, not the M2LB terminology; M2LB owns its content and decisions.
- M2LB glossary data is scoped to the M2LB snapshot and its project. Another project inherits nothing (tested, and verified with M2LB.Common).
- BirkNext UI and status vocabulary are untouched.
- The glossary is detected from the project's own Markdown with the shared tokenizer; no new Markdown parser.
- Authoritative glossary and proposals are separate; proposals never resolve as approved terms; aliases never merge identities.
- Pinned names and known inconsistencies keep the glossary's own sections as governed project evidence.
- Glossary evidence is snapshot-versioned; historical snapshots keep theirs.
- Nothing is stored in System Settings, and no global M2LB term database exists.
- No real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
