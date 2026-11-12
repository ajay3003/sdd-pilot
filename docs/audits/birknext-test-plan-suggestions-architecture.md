# Test Plan Suggestions — architecture (task 20 v2)

Source audit: `birknext-test-plan-suggestions-scenario-source-audit.md`. Implementation: `docs/hardening/birknext-test-plan-suggestions-implementation.md`.

## Responsibilities

| Module | Owns |
|---|---|
| System Understanding | Understanding the system: flow, steps, business rules, and **what to test** (scenario candidates, curated and versioned) |
| **Test Plan Suggestions** (new) | Selecting candidates, preparing Test Plan → Suite → Case **drafts**, review decisions, Test Data links, testability, immutable snapshots, delivery comparison, regression suggestions, a safe export |
| Test Data | Assets and their revisions (linked by id; never copied) |
| Journey packs / Critical E2E | Executable definitions (linked explicitly; never turned into drafts) |
| Azure DevOps Test Plans | Formal Test Plans, Suites, Cases, Runs and Results. BirkNext writes nothing there and claims no Test Case id unless one is verified |

## Scenario reference and provenance

`ScenarioReference`:
- **Kind:** SystemGuide, IntegrationJourney or CriticalE2EFlow.
- **Owner:** guide key, pack/journey, or flow environment.
- **ScenarioId**.
- **Revision:** the guide revision or flow version it was read from.
- **Fingerprint:** SHA-256 of the scenario plus the guide rules that name it and the steps it uses.

No guide content is changed, and none is copied into a second scenario store. A draft keeps the generated content of the revision it was made from.

## Draft model

- A **case** holds the generated content (`TestCaseContent`, server-owned) and reviewer edits (`TestCaseEdits`, human-owned, null = take generated). The effective value is the edit, else the generated value.
- It also holds: review state with an event history (reviewer, rationale, time), risk, Test Data links, correlations, testability and missing information (computed on the server), and a source-changed flag.
- **Expected result basis:**
  - ReviewedGuide (published revision);
  - UnreviewedGuide (curated draft);
  - ReviewerClarified (an edit);
  - Missing;
  - NeedsClarification (the guide says so).

  Implementation behaviour is never a basis.
- **Suites** are proposed from the guide's business-process stages (`GuideStep.Stage` of the scenario's first step), in the guide's order. A scenario without a stage goes to "Not grouped". Reviewers can rename suites, add their own and move cases.
- **Testability** is answered from evidence:
  - input method: a confirmed executable link;
  - Test Data: linked and approved;
  - expected behaviour: from a reviewed guide or a reviewer clarification;
  - observation method: described.

  Environment and permissions are always "not verified here". "Prepared" never means executed.

## Review lifecycle

The states are Generated, UnderReview, NeedsClarification, Accepted, Excluded and Superseded.

- People set UnderReview, NeedsClarification, Excluded and Accepted. BirkNext sets Generated and Superseded.
- Excluded and NeedsClarification need a rationale.
- Accepted is refused when there is no expected result, or when the scenario changed in the guide since generation.
- Accepted means "accepted for preparation/export": never Azure DevOps approval, never execution.

## Change of the source scenario

A guide Draft can be edited in place (same revision number, new fingerprint), so generated content is **never rebuilt on save**:
- On save, the stored generated content is kept. The server takes only human-owned parts from the client.
- New scenarios can be added only while the draft's guide revision is unchanged. Otherwise the save is refused with 409 `guide-changed`.
- On read, review and snapshot, a case whose scenario differs in (or has left) the guide that readers see now is flagged `SourceChanged`.
- **"Create new proposal revision from the current guide"** (refresh) is the only way generated content changes:
  - it regenerates from the current guide revision and records a new basis;
  - an Accepted case whose scenario changed goes back to UnderReview with a BirkNext review event;
  - a scenario that left the guide becomes Superseded.

## Persistence

| Table | Rules |
|---|---|
| `test_plan_drafts` | One row per proposal; `Revision` is an EF concurrency token. A save names its base revision; an older one gets 409 and nothing is overwritten. JSON content is bounded (300 cases, 4 M characters) |
| `test_plan_snapshots` | Written once. Trigger `test_plan_snapshots_no_update` refuses every UPDATE. `UNIQUE(DraftId, Sequence)` and `UNIQUE(DraftId, ContentFingerprint)`: identical content is not stored twice, and concurrent identical saves resolve to one row (the losing insert re-reads the winner). DELETE stays possible (Local Data Reset) |

The **snapshot content fingerprint** is the SHA-256 of the whole proposal except bookkeeping (draft revision, timestamps, who saved). It includes the basis, suites, generated and edited content, review state and history, risk, Test Data revisions, correlations, testability and rule version.

## Delivery comparison

- Cases are matched by scenario id: Added, Removed, Changed or Unchanged.
- Changes are listed per field: Title, ExpectedResult, Requirements, TestData, Preconditions, Steps, Observation, Risk, Testability, Review, Suite and SourceScenario.
- **Context differences** are kept apart:
  - scenario source (guide revision);
  - source (snapshot or baseline per role);
  - suggestion rules (rule version). This alone is "not an application behaviour change".
- **Potential business-rule change** is claimed only when the expected result changed and both sides rest on a reviewed basis (a published guide, or a reviewer's clarification that was accepted). Otherwise the change "needs review".
- Snapshots of different systems are not compared case by case.

## Regression impact

Regression suggestions are derived from the comparison; there is no second impact engine:
- New Test Case;
- Expected result update;
- Existing Test Case review (scenario or rules changed);
- Test Data update;
- Testability recheck;
- Regression inclusion (changed cases; unchanged high-risk cases are kept);
- Previously blocked (accepted but not prepared).

Source change awareness reuses the guide's own `SourceChanges`.

## Azure DevOps boundary and export

- The export is `birknext.test-plan-proposal/v1` JSON plus copyable text, always of one saved snapshot.
- It contains: plan, delivery, suites, cases (preconditions, steps, expected result and its basis, requirements, Test Data ids and revisions, risk, testability), the source baseline, the snapshot id and fingerprint, the unresolved questions, and the boundary statement.
- There is no Azure DevOps call, no write and no "ADO-ready" claim. Import compatibility is explicitly not verified.

## Security

- Reading needs the local session, like every API. Create, save, review, refresh and snapshot need `IntegrationConfigurationWrite`, the same policy as editing the guide.
- Workspace views are navigation only, never permissions.
- Imported text is rendered as text, and steps are never executed.
- Export downloads are `data:` URIs of server JSON.
- BirkNext has no per-project ACL. A proposal belongs to one guide (system), and cross-system data is never combined (gap TPS-G5).

## Performance

- One workspace call returns everything for the page: candidates, stages, drafts, snapshots, executables and Test Data options. There is no call per card.
- Nothing re-runs Source Analysis. Test Data is read once per call.
- The Manual Testing focus panel loads the workspace once, and only in that view.
- Snapshot payloads are bounded with the draft.
