# Document Quality Review and Feature Readiness — evidence semantics and workspace hardening

**Date:** 2026-11-14 · branch `008-traceability-first` · local commits only (no push, no deployment, no remote call).

## Ownership (verified in code)

| Capability | Owner | Engine / store |
|---|---|---|
| Document Quality Review | `Pages/QualityReview.razor` (`/quality-review`, `/quality/document`) | `QualityReviewService`. Each Review Pack is an adapter; the input contracts are in `QualityReviewInputContracts`. The report lives in `QualityReviewSessionService` (memory) and the run record in `SddLifecycle.QualityReviewRuns` (workspace persistence). |
| Supporting documentation | `ArtifactPackInventoryBuilder` (Artifact Explorer context) | Primary, supporting (pack-local or shared), generated and excluded documents, each with its reasons and fingerprint. |
| **Feature Readiness** | **Implementation Review**: `Components/ImplementationGapExplorer.razor` (`/implementation-review`; embedded read-only in Document Quality Review) | Backend `ImplementationGapEngine`, triggered at Import Project when the archive has a `tasks.md`; table `implementation_gap_evaluations` (append-only). It does **not** use Task Alignment or the canonical interpretation. |
| Reviewer decisions | Shared `ManualReviewDecisionStore` (`api/manual-reviews`) | Append-only, latest decision is current, secrets refused. Before this change it accepted only API Quality, Security Classification, Implementation Gaps and Integration Suites. |

## Root causes

1. **The page contradicted the engines.**
   - The pre-run note said "Selected Review Packs read the primary documents of the Artifact Pack only (no supporting documents)".
   - In the code, `PlanDocumentSet.ExtendForReview` adds the supporting plan documents to the parsed Plan that QA Auditor, QA Readiness **and Delivery Readiness** read. Constitution Compliance and the four documentation-standards packs read every authored document.
   - Delivery Readiness's input contract said "Primary documents only", so the provenance list ("read by") left it out.
2. **"Ready" looked like a result.** The readiness chip said *Ready*; it now says *Ready to run* (its inputs are available).
3. **Finding nothing read as a pass.** A pack with no findings showed "✓ No issues".
4. **Supporting documents were one long bullet list** with paths and 12-character fingerprints inline, and there was no way to see which selected pack reads which document. The post-run provenance repeated every reason for every document.
5. **Document findings had no reviewer decision.**
6. **"Not implemented" was too strong.** The engine assigns it when no assessable implementation task has confirmed, partial or candidate source evidence. That means the implementation is *not confirmed*, not that the feature is absent.
7. **Feature cards were large and repeated.** One full card per feature, repeating nine evidence rows.

## What changed

**Document Quality Review**
- **Inputs:** the primary artifacts are one compact table (artifact, document, availability, source, size). The supporting documentation is a searchable catalog:
  - search by name, path or reason;
  - filter by type and by pack-local or shared;
  - filter by "read by a selected pack" or "context only";
  - grouped by document type and collapsible;
  - each document says which selected packs read it (computed by the same `QualityReviewInputContracts.Reads` the run uses);
  - path and fingerprint on demand.
  - A one-line statement separates *available for context* from *read by a Review Pack* and from *reviewed or approved*.
- **Contract fix:** Delivery Readiness declares the supporting plan documents its engine receives, so its description and provenance match what it read.
- **Review Packs:** each shows a *Reads:* line (its input contract) and *Ready to run*, Partial or Blocked. The summary counts say "ready to run". The corrected note says what each group of packs reads and that "a pack that finds nothing has not proven the documentation correct".
- **Results:** a pack with no findings shows *No indicator found*, with an explanation, never "No issues". The post-run "What did this review read?" is grouped and collapsed: counts per category with "read by a selected pack: n", the reason on request, and the fingerprint kept on the row.
- **Reviewer decisions:** *Findings needing human attention* records a decision per finding (subject = the finding's logical id; scope = `artifact-pack:{module}/{pack}`) in the shared store under the new module `document-quality`. The choices are:
  - confirmed observation;
  - false positive or resolved with evidence;
  - accepted documentation risk;
  - needs clarification;
  - not applicable.

  Decisions keep their history and never change a rule result. A later run that no longer reports a finding does not resolve it.

**Feature Readiness** (in its owner, Implementation Review)
- The label *Not implemented* becomes **Implementation not confirmed**. The engine's explanation adds "That is not proof the feature is absent." The enum and the precedence are unchanged: no snapshot gives Not assessed; an unapproved gate gives **Blocked** (always first); then the evidence decides.
- A **compact overview** table with one row per feature: status, implementation evidence ("n of m confirmed"), blocking gates (non-approved only, with their status), test evidence (defined and executed) and runtime. A dimension with no evidence base reads "Not assessed", never a bare 0.
- Filters: status, *No executed test*, *No runtime evidence*, and search. **Detail on demand:** opening a row shows the existing evidence (basis, contract, tasks, gate links, dependency, source, tests, runtime, requirements, the task table and the evidence dimensions).

**User Guide:** Document Quality Review covers inputs, *Ready to run* and decisions; Implementation Review covers Feature Readiness.

## Verification

- **Backend focused** (Implementation Gaps, Manual Review, Quality Review): 112/112. This includes the decision module and the "not proof the feature is absent" explanation.
- **Frontend focused:** the latest run passed 646/646. It includes new tests for:
  - the supporting catalog (search, read by, semantics);
  - the Delivery Readiness contract;
  - the *Ready to run* label;
  - the feature overview (filters, detail on demand, no percentage);
  - the *Implementation not confirmed* label.
  - Updated tests: readiness label, readiness note, the "No indicator found" pack card, the collapsed provenance.
- **Backend regression** (sequential memory-guarded partitions, this task's build):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 852 | 0 | 0 |
  | 4 | 652 | 1 | 0 |
  | 5 | 521 | 3 | 0 |
  | 6 | **not run** | | |

  Partition 6 was stopped by Claude Code because the machine was low on memory, so its result is unknown. In the previous task it passed 2470, with 2 load-sensitive tests that passed when re-run alone.
- **Full frontend regression:** 8828/8828 (run before the final provenance-grouping edit; that edit is covered by the focused run above).
- **Browser acceptance (Edge, isolated, real M2LB archive imported read-only):** 36/36.
  - After choosing the **SkoleService** module in an Explorer, the five primary artifacts are listed: Constitution (771 lines), Specification (1059), Plan (318), Tasks (952), Data Model (439).
  - **19** supporting documents; searching "contract" gives 7 of 19.
  - Each document states its readers, e.g. "Read by QA Auditor, Constitution Compliance, WCAG 2.2 — Documentation, OWASP Security Documentation Coverage".
  - No pack chip says a bare "Ready".
  - The review ran (5.7 s).
  - **Feature Readiness:** 9 features with statuses Partially implemented, Blocked, Implementation not confirmed and Not assessed.
    - The one **Blocked** feature is gated by "DPO sign-off of the EXC-05 blind-index amendment (constitution v2.4.0; Solution Architect approved 2026-10-12)", status *Unknown*. Its explanation names the gate, and the architect's approval of another item does not approve it.
    - Runtime: "Not assessed — no runtime execution was observed or recorded".
  - No overflow at 1440, 1024 or 768 px on either page; no page errors, no server errors, no external calls.

## Limitations (honest)

- **Reviewer decisions** were not exercised in the browser: the M2LB SkoleService run produced no top-level findings. They are covered by the shared-store validation test and the existing store tests, not by a browser test.
- **The review report itself** is still held in memory per session; the persisted run record stores the inputs and graph findings, not every pack's finding list. Nothing about this was changed.
- **The results area** keeps its existing per-pack sections. Severity, document and pack filters across all findings were not added.
- **Feature Readiness** still does not use Task Alignment or the canonical interpretation, and imported TRX executions reach it only on re-evaluate. Scope (Systems & Scopes) filtering of features was not added, because features have no scope member kind.
- **Cross-module links** stay the existing ones (task links, gate anchors, Implementation Review). No new links were invented from display text.

## Update 2026-11-14

Further gaps closed and verified on the final build (partitions 1–6, full frontend, browser 52/52): see `birknext-next-cross-module-gap-closure.md`.
