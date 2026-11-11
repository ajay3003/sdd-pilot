# BirkNext — System Understanding (Systemforståelse)

**Status:** implemented 2026-11-11 on `008-traceability-first`.

## What it is

System Understanding is a main navigation item (**Workspace → System Understanding**, route `/system-understanding`). It explains the **system under test**:
- what the system does;
- how information moves;
- what happens at each step;
- which data formats and contracts apply;
- which business rules apply;
- what can go wrong;
- what is proven and what is not;
- what to test.

| It is not | That is |
|---|---|
| BirkNext's user documentation | the Help Center |
| A topology engine | Integration Topology (connections according to evidence) |
| A requirements review | Specification Review |
| A test runner | Integration Suites / Test Execution & Evidence |

## Architecture

| Part | Location | Role |
|---|---|---|
| Model | `shared/SystemUnderstandingContracts.cs` | Generic guide content: components (with lanes), edges (kind, transport, format, contract), flows, stages, steps, rules, test scenarios, transfers with per-snapshot version facts, observability, gaps, source roles. `GuideEvidence` is a pointer (source role, reviewed source, file/symbol/lines, contract/requirement/test id, document, safe route), never a copy of source |
| Evidence classes | same | SourceImplemented, Specified, RuntimeVerified, TestVerified, TestDefined, Inferred, NeedsClarification, Conflicting, NotAssessed |
| Store | `Models/SystemGuideRevisionRecord.cs`, table `system_guide_revisions` (migration `AddSystemGuideRevisions`) | One row per revision: typed content JSON + SHA-256 + source basis (snapshot identities only) + author/submitter/approver/timestamps |
| Service | `Services/SystemUnderstanding/SystemGuideService.cs` | Catalog of guides (`SystemGuideCatalogEntry`: key, embedded seed, optional test pack), seeding, lifecycle, change awareness, validation |
| API | `Controllers/SystemUnderstandingController.cs`, `api/system-understanding` | Read for any session; draft/submit/approve/publish need `IntegrationConfigurationWritePolicy` |
| Page | `Pages/SystemUnderstanding.razor`, `Components/SystemUnderstanding/*` | Header, search, seven tabs; SVG flow diagram with a text equivalent |
| Presentation | `Services/SystemUnderstandingPresentation.cs` | Evidence badges (text + glyph), generic lane layout, flow highlighting, accent-insensitive search |

**No duplicated engine:**
- **Source recognition:** reuses `TestPackSources.Recognizes` with the journey pack's source roles, through `IqrSourceStore.ListSourceAnalysisAsync`.
- **Flows:** the guide cites the topology profile, the contract profile, the journey pack and the reviewed cross-repo verification instead of re-deriving them.
- **Diagram:** a dedicated SVG component, because the existing `architectureGraph.js` layered layout has no notion of a main lane with side flows. Interaction semantics match `TopologyMap` (selectable nodes and edges, a detail surface, a text fallback).

## Guide lifecycle

| Rule | Detail |
|---|---|
| States | Draft → InReview → Approved → Published; the previous Published revision becomes Superseded |
| Return | InReview → Draft is allowed |
| Seed | Inserted once as revision 1 **Draft** by "BirkNext curated seed (not reviewed)". Never approved automatically, never re-seeded over existing revisions |
| Immutability | Beyond Draft, a revision's content, fingerprint, source basis and identity are immutable, enforced by the database (trigger `system_guide_revisions_no_rewrite`). A Superseded revision is final; a Published one can only become Superseded |
| Single current revision | Partial unique indexes allow at most one open (Draft/InReview) and one Published revision per guide, so concurrent publishing cannot fork the guide |
| Unchanged content | Identical content creates no revision. A draft save carries the base fingerprint, and a concurrent edit gets 409 |
| Readers | See the Published revision while a new draft is edited |

## Change awareness

- **Source basis:** each revision records the snapshots recognised for its source roles when it was written.
- **On read:** snapshots imported later, or removed, are listed as "may need review", with the number of claims citing that role.
- **Never automatic:** the content is never changed.

## Safety

- **Validation:** every id reference resolves inside the guide (components, stages, steps, scenarios, edges).
- **Links:** must be relative BirkNext routes (`SystemGuideLinks.IsSafeRoute`: no scheme, no `//`, no `\`, no `..`); the same rule is used by the backend and the page.
- **Rendering:** all text is rendered as text, never markup.
- **Authorization:** reading a guide does not grant access to source; source is opened through its own explorer and its own rules.

## Skolenærvær guide (curated draft)

- **Seed:** `Services/SystemUnderstanding/Guides/skolenaervaer.guide.json`, in Norwegian.
- **Contents:**
  - 11 components;
  - 11 connections across six flows: main report, acknowledgment, Person, Utdanning, Tjeneste, period lock & delivery;
  - 20 main-flow steps plus Person, Utdanning, Tjeneste, period-lock and Bufdata steps (31 in total);
  - 14 rules;
  - 21 scenarios;
  - 8 transfers;
  - 8 gaps.
- **Grounding:** the reviewed cross-repository verification (`AIAssisted/docs/skolenaervaer-cross-repo-verification.md`: Skole (2).zip and Meldingsutvekslermottak (3).zip, reviewed 2026-10-14) plus BirkNext's Skolenærvær pack.
- **Evidence classification:**
  - **SourceImplemented:** MM and Skoletjenesten claims verified from source.
  - **TestDefined:** tests that exist in source but have not been executed.
  - **NeedsClarification:** MU internals, SkoleAdapter, Tjeneste, the v2.0.1 / `/reports` generation.
  - **Specified:** Bufdata Parquet delivery (ADR-028; not implemented in the reviewed snapshot).
  - Nothing is runtime- or test-verified.
