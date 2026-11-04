# Governance & quality gates — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. This is the first governance slice: a compact shared model over the canonical store (Phases 1–3), with cross-cutting canonical policies migrated first. Domain readiness stays with each domain.

## GOVERNANCE & QUALITY GATE AUDIT MAP

**Column key**

| Short name | Meaning |
|---|---|
| Block? | Whether the rule can block the action |
| Dup | Duplicated elsewhere? |
| Can. | Built on canonical evidence? |

| Rule / gate | Owner | Input → trigger | Block? | Scope | Status vocabulary | Dup | Can. | Decision |
|---|---|---|---|---|---|---|---|---|
| Review queue classification (conflict / stale / ambiguity / story ref / suggestion) | Canonical (Phase 3) | relation + review state | no (queue only) | project | Status Vocabulary | — | yes | **Reused** as the finding source: one fact |
| Trace counts (explicit / candidate / none; removed / unresolved story refs) | Canonical trace | VERIFIES + task relationship | no | project / feature | counts | Delivery attention items (same Phase 1 engine) | yes | **Reused** for coverage and story policies |
| Delivery attention items (RemovedStoryReference, UnresolvedReference, BrokenTraceability) | Delivery | Phase 1 work-item interpretation | no | snapshot | Delivery categories | canonical task attributes (same engine) | via Phase 1 | Keep; governance reads the canonical copy, so both use the one engine |
| AQR readiness (contract / auth / target) | AQR | AQR inputs | per check | target | AQR states | — | partly | **Keep** in AQR; read-only vs active split exists. Migrating auth semantics is debt. |
| IQR readiness (enabled integration, bindings, Known / Saved / Verified) | IQR | integration config + evidence | Run gate | integration | IQR states | — | partly | **Keep**; review ≠ execution already split |
| FQR readiness (scope, DOM, ZAP, browser session) | FQR | target + companion | Run gate | target | FQR states | — | no | **Keep** |
| Performance readiness (auth, CA trust, probes, target-scoped gates) | Performance | scenario steps + target | Run gate | target / scenario | Perf states | auth rules overlap AQR | no | **Keep**; shared auth policy is debt |
| Critical E2E readiness / release evidence | Critical E2E | run + page context | Run gate | flow | E2E states | — | no | **Keep** |
| Test Data approval / schema validation / staleness | Test Data | XSD + fixture + server validation | approval | asset version | Test Data states | — | stable ids | **Keep** (already enforces server validation); governance surfacing is debt |
| Source Analysis five status dimensions + limitations | Source Analysis | analyzers | no | snapshot | qualified statuses | — | snapshot | Keep; canonical trace limitations surface as **Informational** findings |
| Documentation Drift confidence / coverage | Doc Drift | snapshots | no | pair | drift states | — | no | Keep |
| Generated Documentation health | Gen Docs | outputs | no | project | health states | — | no | Keep |
| Schema Studio validation | Schema Studio | XSD | no | package | validation states | Test Data (same engine) | — | Keep (one XSD engine) |
| Glossary authority / conflict | Source Analysis glossary | Markdown | no | snapshot | Authoritative / Proposed / Conflict / Unknown | — | snapshot | Keep; glossary policies are debt |
| Pipeline Review | Pipeline Review | CI files | no | project | review states | — | no | Keep; project-policy gates are debt |
| Package / import validation (`validate-package.ps1`) | Tester package | package | CI exit code | package | OK / ERROR | — | — | Keep |
| Snapshot Registry pointers / retention | Registry | snapshots | pointer rules | module | registry states | — | — | Keep; release reports register as frozen evidence (#25) |
| Release gates | — | — | — | — | — | — | — | **New**: `GateAction.ReleaseReadiness` (used by #25) |
| Waivers / exceptions | — (none before) | — | — | — | — | — | — | **New**, minimal in the evaluator (persistence is debt) |

## What was built

### Model (`shared/Governance.cs`, policy set `governance/1`)

| Concept | What it is |
|---|---|
| **Policy** | Stable id + version, title, purpose, owner, resolution, actionable flag, default level per action |
| **Finding** | Policy id + version, count, named evidence examples, owner, resolution, affected consumers, module, rule ids |
| **Gate** | `GovernanceGate.Evaluate(action, findings, …)` gives a `GateResult` |

**Actions:** Review · Execution · Approval · Delivery report · Release readiness.

**Levels:** Blocking · Non-blocking warning · Informational · Not applicable · Not assessed. "Partial" is never a level.

**Decisions:** Ready · Ready with limitations · **Passed with reviewed exception** · Blocked · Not assessed, always with a reason. There is no score.

**Waiver:** policy + action + optional module + reason + optional expiry.
- It changes only that gate decision; the finding and its evidence stay.
- An expired waiver, or one for another action or module, does not apply.

**Override:** policy + action + level + source. The effective level is shown with where it comes from ("Policy default (id vN)" or "Project override").

**CI:** `ExitCode` is non-zero only for an unwaived blocking finding; warnings never fail CI. The evaluation is deterministic and needs no network or AI.

### Policies (`governance/1`)

| Id | Default levels | Source |
|---|---|---|
| `gov.identity.conflict` | Blocking for Approval / Release; warning otherwise | Conflict review state or conflicting declared aliases |
| `gov.review.stale-association` | Warning | Revalidation required |
| `gov.traceability.ambiguous-reference` | Warning (review, delivery, release) | Ambiguous relations, one per question (the queue's grouping); FR-001 across packs is never merged |
| `gov.traceability.removed-story-reference` | Warning (delivery, release) | Task relationship from Phase 1; counted once (not also as its unresolved relation) |
| `gov.traceability.unresolved-story-reference` | Warning | Task relationship; shared / foundation / group work never counts |
| `gov.coverage.requirement-without-test` | Warning (release) | No VERIFIES at all |
| `gov.coverage.candidate-only` | Warning (release) | Only suggested VERIFIES: never satisfies confirmed coverage |
| `gov.impact.unknown` | Warning (release) | Unresolved relations other than story references |
| `gov.analysis.not-interpreted` | Not assessed | No interpretation |
| `gov.analysis.limitation` | Informational, not actionable | Analyzer limitations (never review work) |

**Default safety:** new policies start as warnings. Only a contradicted identity blocks, and only approval and release.

### Scope

- Findings carry their module, taken from the evidence's `module` attribute.
- A System / Service / Package / Library **named exactly like a declared module** belongs to it.
- A module-scoped gate keeps that module's findings plus global ones.
- Real case: "Utdanning ↔ SkoleAdapter" (conflicting declared aliases) blocks a SkoleAdapter release and **no longer blocks PersonService**.

### Endpoint and UI

- `GET api/canonical/{scope}/governance?action=…&module=…`: server-side and scoped; the browser never downloads the graph.
- **Governance panel on Correlation Review:**
  - an action selector;
  - the decision with icon + label + reason (never colour alone);
  - counts;
  - finding cards: why it matters, "Found in", resolve + owner, waiver, and technical details (policy id/version, level source, rule ids, affected consumers, interpretation).
  - It is re-evaluated after every review decision or interpretation, so findings recompute.

### Bugs found and fixed on the way

- **Correlation Review → Explore never loaded.** `Scope="_scope"` on a string parameter passed the text "_scope" (`api/canonical/_scope/… 400`). There is now a regression test.
- **Same mistake** (`DeletingId="_deletingId"`) in Spec Changes and Compare Reviews: the row being deleted was never recognised.

## Real data (throwaway `birknext_ci2`, scope `ci-live`; localhost only)

**Release readiness gate: Blocked** — 1 blocking (identity conflict, SkoleAdapter) and 28 warnings. Response 0.28–0.86 s, 31 KB.
- Candidate-only coverage per module: SkoleService 33, MeldingsutvekslerReceiver 15, SkoleAdapter 7, …
- Requirements without tests per module.
- Removed-story refs: 5 in TjenesteService (T047/T045 → US3).
- Undefined-story refs: 6 in Frontend (T023/T024 → US6).
- 2 ambiguous test links.
- 1 informational limitation (no task names a source type yet).

**Other actions and scopes**
- Review gate: Ready with limitations (4 warnings).
- `module=PersonService`: Ready with limitations (exit 0).
- `module=SkoleAdapter`: Blocked (exit 1).

**Browser** (1440/390 × light/dark): no overflow, no console errors, **0 external requests**.

## Tests

- **Backend** `GovernanceGateTests` (8). Real cases:
  - A, shared/foundation is no finding;
  - B, removed story counted once;
  - C, undefined story;
  - D, conflict blocks release / approval but warns review;
  - E, derived contract occurrence is no finding;
  - J, candidate-only is not coverage;
  - module scope;
  - system named like a module;
  - not interpreted gives Not assessed.
- **Backend, pure gate:** waiver action / module / expiry, and "Passed with reviewed exception"; override level + source, Not applicable; limitations Informational; unique ids; default safety.
- **Frontend:**
  - `GovernanceGatePanelTests` (2): decision, cards, technical details, action switch, re-evaluate on version, error state;
  - Correlation Review: governance evaluated for the project, Explore sends the active project.
- **Full suites after this commit:** frontend **8017 / 8017**; backend: see the #25 report (run together).

## Commits

| Commit | What |
|---|---|
| 0ba0ea52 | Governance model, policies, endpoint, panel, Explore fix |
| 75e56155 | DeletingId binding fix |

## Remaining governance debt

- **Persistence:** waivers and project/environment overrides (the evaluator supports both; there is no store or UI yet). Governance results are persisted only inside frozen release reports (#25).
- **Not yet migrated to the shared model:** auth semantics (configured ≠ verified; attended ≠ unattended; Bearer + None blocks execution); Test Data schema approval / staleness / reference policies; endpoint / browser discovery levels; glossary policies (new term, Norwegian identifier); pipeline policies; import / encoding; snapshot pointer policies.
- **Domain consumption:** AQR, IQR, Performance and Critical E2E gates do not read shared findings yet.
- **Dashboard** governance summary tile; Delivery governance badge.
- **Generated policy documentation** from the definitions (the definitions already carry purpose, levels and resolution).
- **Change-impact gate** ("high-impact change + no confirmed test") once baseline/current impact exists (#23 debt).

## Confirmations

- Governance is built on existing canonical entities, relations, review states and the review queue's classification; no parallel model.
- Policies, findings and gates are distinct; blocking, warning and informational are not conflated.
- Non-actionable limitations are Informational and never review work.
- Shared / foundation / group work is not a broken story; removed-story references remain findings; undefined references need review.
- FR-001 in two packs is not merged (ambiguity stays a question).
- A derived contract occurrence across v1/v2 is no failure.
- Suggested test links never satisfy confirmed coverage.
- A waiver changes a gate decision, never the evidence, and shows "Passed with reviewed exception".
- Gates respect module scope; an unrelated module's finding does not block.
- The CI result is deterministic, network-free and needs no AI.
- No external policy engine, DSL, graph database or new service.
- No real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
