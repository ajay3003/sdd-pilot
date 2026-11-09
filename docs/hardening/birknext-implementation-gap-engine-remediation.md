# BirkNext — implementation gap engine — remediation (2026-11-09)

Branch `008-traceability-first` (local only). Audit: `docs/audits/birknext-real-project-implementation-gap-audit.md`.

## Design

One deterministic engine, fed by existing parsers and existing snapshot evidence; no new interpretation engine, no keyword similarity.

```
Project Import commit (documents still in memory)
  └─ ImplementationGapExtractor  (TaskExplorerService.Parse + CorrelationRules.NamedSymbols + DecisionReferences + RequirementReferenceParser)
       tasks (id, line, checkbox, continuation, expected paths, declared types, packages, gate markers, task refs, contracts, applicability)
       gate tables (Gate | Blocks), decision registers (section **Status:** / index rows), contract + spec requirement ids
  └─ ImplementationGapEngine × snapshot evidence (TargetIndex files, Roslyn symbols, dependency evidence, test inventory, projects)
       × recorded executions (imported TRX, supplied on re-evaluation) × runtime observations (none recorded here)
  └─ persisted: implementation_gap_evaluations (extracted inputs — never document text — and report)
  └─ human review: ManualReviewDecisionStore, module "implementation-gaps", subject = stable finding id, scope = "import:{id}"
```

### Three dimensions per task, never one badge

| Task state | Implementation evidence | Verification evidence |
|---|---|---|
| Not started / In progress / Completed / Unknown (checkbox only) | Not assessed / No matching source / Candidate / Partial / Confirmed | Not assessed / Not applicable / Test not found / Test defined / Test executed / Test passed / Runtime verified |

### Artifact checks (relationship classes)

| Expected | Confirmed | Candidate | Ambiguous | Missing | Unresolved |
|---|---|---|---|---|---|
| File named on the task line, in its module | exact or module-relative path | same file name elsewhere **in the same module** (moved/renamed) | several same-named files in the module | not found anywhere in the module and the inventory is complete | inventory incomplete |
| Type the task says to Implement/Create/Add | declared in the file the task names (or anywhere when it names none) | declared elsewhere; renamed in the named file; only in generated source | declared in several namespaces | not declared and the symbol inventory is complete | no/truncated symbol inventory |
| Package the task adds/pins to a project | declared by that project (path mapped repository → archive) | declared by a moved copy of that project | — | not declared; or the named project is absent (another module's declaration is no evidence) | no dependency inventory |
| Decision a task drafts | entry in the decision register | — | — | not in the register | no register supplied |

**Scope rule.** A path's implementation scope is its module folder up to `src/` or `tests/`, else its own folder; a task naming only types/packages
uses its module. SDD material (`specs/`, `.specify/`) never counts as implementation present. When no implementation file of the declared scope is
in the archive, the task is *No matching source — not evidenced in supplied archive*, and one scope finding replaces per-task "missing" claims.

**Applicability.** Tasks under a Deferred/Optional/Future/Out-of-scope heading or marked so are excluded from readiness and findings (counted as
excluded).

### Gates

Gate table rows and inline `**Gate: …**` markers attach a gate to tasks (ranges `T119–T122` expanded). A gate naming a decision is
APPROVED only when the register records an accepted/current status (Gjeldende, Godkjent, Vedtatt, Approved, Accepted); Utkast/Foreslått/Proposed →
PENDING; Avvist/Rejected → REJECTED; superseded → UNKNOWN; a documented decision gate absent from a supplied register → PENDING (the documents say
the sign-off is required and none is recorded); no register supplied → UNKNOWN; a gate naming no decision record → UNKNOWN. A task or a draft never
approves a decision.

### Features and readiness

Features are (a) the tasks a decision gate governs (named by the drafting task's title, e.g. "Parquet.Net for the Bufdata delivery") and (b) user
stories. Readiness: no snapshot → NOT ASSESSED; any gate of its tasks not approved → BLOCKED; all assessable implementation tasks confirmed → VERIFIED
if every associated test task has passing recorded executions, else IMPLEMENTED BUT NOT VERIFIED; some → PARTIALLY IMPLEMENTED; none →
NOT IMPLEMENTED. Nine dimensions are reported with numerator, denominator (null = not assessed) and exclusions — no percentage.

### Findings (rules GAP-A … GAP-P, GAP-S)

Each finding has a stable id (hash of type + subject), type, severity, feature, snapshot and archive hash, expected artifact/behaviour, actual
evidence, missing evidence, detection rule, confidence, human-review flag, review status, guidance and evaluation time; source lines only where
the document or the analyzer recorded them. Low-confidence rules (moved/renamed, several candidates, unchecked-but-implemented, code without task)
always need human review.

## Code

| Area | Files |
|---|---|
| Contracts | `shared/ImplementationGapContracts.cs`; `shared/ManualReviewContracts.cs` (module `implementation-gaps`); `shared/ProjectImportContracts.cs` (`ImplementationGapEvaluationId`) |
| Extraction | `backend/BirkNext.Api/Services/ImplementationGaps/ImplementationGapExtractor.cs` |
| Engine | `backend/BirkNext.Api/Services/ImplementationGaps/ImplementationGapEngine.cs` |
| Service / persistence / review | `…/ImplementationGapService.cs`, `ImplementationGapEvaluationRecord`, table `implementation_gap_evaluations`, migration `20261109141450_AddImplementationGapEvaluations` |
| API | `Controllers/ImplementationGapsController.cs` — `api/implementation-gaps` (list, get; re-evaluate and review need configuration-write) |
| Import hook | `Services/ProjectImport/ProjectImportService.cs` — evaluates after the source part settles; never fails an import |
| UI | `Components/ImplementationGapExplorer.razor(+css)` in Implementation Evidence Review (full) and Quality Review (feature readiness only); `Services/ImplementationGapApiService.cs` |
| Navigation | Task Explorer `?task=T119`; explorer host `?document=<path>` selects that exact document (as choosing it would) |

## Tests

Backend `ImplementationGapTests` (42): real Project Import → Source Analysis pipeline on synthetic archives — positive control (Verified only
after recorded passing executions), failed execution, partial, ambiguous, scope absent, unapproved / pending / unknown gates, moved file, renamed
type, partial type, interface, registration-only change, unchecked-but-implemented, deferred task, no snapshot, truncated inventory, review
survival + cross-import isolation, stable explainable findings, extraction rules and the real-archive regressions GAP-DEF-1…12.
Frontend `ImplementationGapExplorerTests` (6) + Task Explorer deep link (1).
