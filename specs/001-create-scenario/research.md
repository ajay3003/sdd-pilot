# Research: Scenario Management

**Phase**: 0 | **Branch**: `001-create-scenario` | **Date**: 2026-04-30

---

## 1. GraphQL Server — HotChocolate 14

**Decision**: HotChocolate 14 (code-first schema)  
**Rationale**: First-class ASP.NET Core integration via `AddGraphQLServer()`. Code-first approach keeps schema in sync with C# domain models without a separate SDL file. Native EF Core DataLoader support. Built-in Banana Cake Pop IDE at `/graphql` for development. Schema snapshot testing supported via `HotChocolate.Testing`.  
**Alternatives considered**:
- *graphql-dotnet*: Older API, less ergonomic EF Core integration.
- *SDL-first (schema-first)*: Adds a synchronisation burden between the SDL file and C# resolvers; code-first is strictly less work for a small team.

---

## 2. GraphQL Client — Strawberry Shake 14

**Decision**: Strawberry Shake 14 (HotChocolate's typed client generator for .NET)  
**Rationale**: Generates strongly typed C# classes from `.graphql` operation documents and the server schema at build time. Integrates with Blazor WASM's DI container via `AddScenariosClient()`. Eliminates manual JSON serialisation and keeps client operations in sync with the schema automatically. Same HotChocolate ecosystem as the server — single version to manage.  
**Alternatives considered**:
- *Raw `HttpClient` + `System.Text.Json`*: No type safety, verbose, error-prone schema drift.
- *ZeroQL*: Lighter, but smaller community and fewer Blazor examples.

---

## 3. Database — PostgreSQL 16 with EF Core 8

**Decision**: PostgreSQL 16 via `Npgsql.EntityFrameworkCore.PostgreSQL` (single provider, both dev and production)  
**Rationale**: PostgreSQL is the constitution-selected production target. Using a single provider eliminates the SQLite-to-Postgres divergence risk (e.g. case-sensitivity, enum handling). EF Core migrations are run via `dotnet ef migrations`. Local development uses a Docker Compose Postgres instance.  
**Alternatives considered**:
- *SQLite (dev) + Postgres (prod)*: Reduces local setup but introduces provider differences that can mask bugs; rejected on Test-First grounds.
- *SQL Server*: Heavier licensing and resource footprint; not warranted.

---

## 4. Structured Logging — Serilog

**Decision**: Serilog with `WriteTo.Console(new CompactJsonFormatter())`  
**Rationale**: Produces structured JSON meeting the constitution requirement (level, timestamp, trace-id, correlation-id). Console sink is container-friendly; downstream aggregators (Seq, ELK, Azure Monitor) ingest from stdout with no code change.  
**Key events** (from spec §Observability):

| Event | Fields |
|-------|--------|
| `ScenarioCreated` | scenarioId, projectId, type, durationMs |
| `ScenarioValidationFailed` | fields[], projectId, correlationId |
| `ScenarioCreationFailed` | exception, projectId, correlationId |

---

## 5. Correlation / Trace IDs

**Decision**: Custom `CorrelationIdMiddleware` that reads `X-Correlation-Id` from the request header (or generates a new GUID) and pushes it into Serilog's `LogContext` for every request.  
**Rationale**: HotChocolate receives all operations at `POST /graphql`, so per-operation tracing must be carried by a correlation ID on every HTTP request. OpenTelemetry full instrumentation can be layered on in a future observability story without altering this middleware.  
**Alternatives considered**: OpenTelemetry full setup — deferred; middleware is sufficient for v1.

---

## 6. Project Scoping (FR-010)

**Decision**: `projectId` is a required argument on both the `scenarios` query and the `createScenario` mutation.  
**Rationale**: Makes the scope explicit in the schema, avoids hidden JWT-claim magic, and keeps resolvers independently testable without a token present.  
**Alternatives considered**: Derive `projectId` from the JWT claim server-side — cleaner UX but harder to test in isolation and introduces hidden coupling between auth middleware and resolvers.

---

## 7. Double-Submit Prevention

**Decision**: Disable the submit button in Blazor component state while the `createScenario` mutation is in flight (bind to a `_isSubmitting` bool field).  
**Rationale**: Zero infrastructure cost; covers the common user case (slow connection, accidental double-click). The server-side domain model does not enforce deduplication in v1.  
**Alternatives considered**: Idempotency key header on the mutation — adds backend complexity not warranted at v1 scale.

---

## 8. Input Validation

**Decision**: Validate in two layers — Blazor component (DataAnnotations on the form model) and HotChocolate input type (custom `InputValidator` / `IInputFormatter` on the server).  
**Rationale**: Client-side validation provides immediate feedback (SC-003, US3); server-side validation is the authoritative gate (security principle — never trust client input). Both layers use the same rules: title required, max 500 chars; type must be a valid enum value.  
**Alternatives considered**: Server-only validation — compliant but poorer UX (round-trip per error).

---

## 9. Testing Strategy

### Backend

| Layer | Tool | Scope |
|-------|------|-------|
| Unit | xUnit + Moq + FluentAssertions | `ScenarioService` business logic, validation rules |
| Integration | xUnit + `WebApplicationFactory` + Testcontainers (PostgreSQL) | Full GraphQL request → real DB round trip |
| Contract | HotChocolate schema snapshot tests | Schema shape does not regress between commits |

### Frontend (Blazor)

| Layer | Tool | Scope |
|-------|------|-------|
| Component (unit) | bUnit + Moq | `ScenarioForm` renders, validation messages, disabled state; `ScenarioList` empty state and data rows |
| Page (integration) | bUnit + mocked Strawberry Shake client | `Scenarios.razor` full interaction — form submit triggers mutation, list refreshes |

**TDD order** (mandated by constitution):
1. Write failing test
2. Run → confirm red
3. Implement minimum code to pass
4. Refactor

---

## 10. CORS

**Decision**: Allow the Blazor WASM origin (configurable via `FRONTEND_ORIGIN` env var, default `http://localhost:5173`) for `POST /graphql`. All other origins blocked.  
**Rationale**: Single GraphQL endpoint; fine-grained per-operation CORS is unnecessary. Environment variable keeps the origin out of source control.

---

## 11. Local Development Setup

**Decision**: Docker Compose file at the repo root providing a single `postgres` service. Backend connects via `ConnectionStrings__Default` environment variable (override in `appsettings.Development.json` pointing to `localhost:5432`).  
**Rationale**: Zero-install PostgreSQL for contributors; reproducible across machines. `dotnet ef database update` runs migrations on first launch.

---

# Research: US2 — Deterministic Scenario Extraction

**Phase**: Research | **Created**: 2026-05-13

No architecture decisions are made in this section. The goal is to surface tradeoffs, alternatives, risks, constraints, and open questions to inform the planning phase.

---

## R-US2-1. Extraction Approach Options

The core challenge is identifying which lines or blocks in a pasted specification document should be surfaced as candidate scenarios. Three broad approaches are worth evaluating.

**Option A — Line-by-line scanning**  
Each line is evaluated independently against a fixed set of pattern rules. Simple to reason about and easy to audit. Loses multi-line context: a bullet that wraps across two lines may be split into two candidates or the continuation line may be dropped entirely.

**Option B — Token-based block scanning**  
The raw input is first split into structural blocks (headings, list items, paragraphs, code blocks) before classification rules are applied. Handles multi-line bullets correctly. Requires a reliable block-boundary detection step that must itself be deterministic and tolerant of malformed input.

**Option C — Markdown AST traversal**  
The pasted text is parsed into a full markdown abstract syntax tree, and rules are applied to typed nodes (ListItem, Paragraph, etc.). Most structurally accurate. Carries real risk when pasted text is not well-formed markdown — mixed prose, copy-pasted fragments from browsers, or plain text documents will produce unreliable parse trees. Introduces a dependency on a markdown parser library.

**Tradeoffs**:

| | A: Line-by-line | B: Token/block | C: AST |
|---|---|---|---|
| Auditability | High | Medium | Medium |
| Multi-line bullet handling | Poor | Good | Good |
| Tolerates malformed input | Yes | Partial | No |
| Implementation complexity | Low | Medium | High |
| Future extensibility | Limited | Moderate | High |

**Key risk**: The spec states that users paste spec.md text, but does not guarantee that the pasted content will always be valid markdown. A parsing approach that requires well-formed input will silently produce poor results when the input is not clean.

**Open questions**:
- Is the pasted input expected to always be well-formed markdown, or should the extractor tolerate plain prose and mixed formats?
- How should the system handle nested bullets — are sub-bullets independent candidates or children of their parent?

---

## R-US2-2. Safe Parsing Strategies for Pasted Input

Pasted text is untrusted. Before any rule application, the raw input must be treated as hostile content.

**Input normalization (non-optional):**
- Normalize line endings (`\r\n` → `\n`) before any line-splitting occurs.
- Trim leading and trailing whitespace from each extracted candidate before display.
- Strip or neutralize HTML tags in pasted content before display. Users frequently paste from browsers, which may include inline HTML.

**HTML / XSS risk:**  
If extracted candidate text is rendered as markup rather than plain text in the review UI, a pasted bullet containing `<script>` or `<img onerror=...>` becomes an XSS vector. All candidate text must be rendered as plain text. This is a rendering constraint on the UI, not a parsing constraint on the extractor, but it must be considered as part of any extraction design.

**ReDoS risk (Regular Expression Denial of Service):**  
Regex patterns with nested quantifiers applied to long lines can exhibit catastrophic backtracking. Mitigation: keep all extraction patterns simple and anchored; avoid nested quantifiers; apply a line length cap before any complex pattern is attempted.

**Input length cap alternatives:**

| Cap | Tradeoff |
|---|---|
| 10,000 chars | Matches spec performance target; may frustrate users with large specifications |
| 50,000 chars | Covers most real-world specifications; increases worst-case processing time |
| None | Highest usability; unacceptable ReDoS and memory risk |

**Client-side vs. server-side parsing:**  
Deterministic rule-based extraction does not inherently require a server round-trip. Processing on the client keeps untrusted text out of the server entirely and eliminates network latency from the extraction path. Processing on the server centralizes auditable logic but introduces a new surface for untrusted input. This is an architecture decision deferred to planning, but it is worth noting that client-side processing is the more natural fit for stateless text transformation.

**Assumption**: The input cap and normalization steps occur before any extraction rule is applied. A blank or too-large input never reaches the extraction logic.

---

## R-US2-3. Classification Heuristics

Each extracted candidate must be assigned to exactly one of: REQUIREMENT, TEST, or NEEDS_CLARIFICATION. The heuristics must be deterministic, auditable, and ordered to resolve conflicts.

**REQUIREMENT signals:**

| Signal type | Examples |
|---|---|
| RFC 2119 modal verbs (uppercase) | MUST, SHALL, SHOULD, MAY, MUST NOT, SHALL NOT |
| RFC 2119 modal verbs (lowercase) | must, shall, required, is required to |
| Functional requirement prefix | `FR-001:`, `FR-US2-003:` |
| Declarative system statement | "The system...", "System MUST...", "System SHALL..." |
| Capability language | "Users can...", "The application allows...", "Supports..." |

**TEST signals:**

| Signal type | Examples |
|---|---|
| BDD keyword triple (same line) | "Given...When...Then..." |
| BDD section opener | Line starts with "Given ", "When ", "Then " |
| Acceptance / success criterion prefix | `AC-001:`, `SC-003:` |
| Verification verbs | verify, validate, assert, confirm, check |
| Expected outcome language | "should display", "should return", "should show" |

**NEEDS_CLARIFICATION signals:**

| Signal type | Examples |
|---|---|
| Question terminator | Line ends with `?` |
| Deferral markers | TBD, TODO, TBC, open question, to be defined, to be decided |
| Uncertainty language | "may", "might", "could", "possibly", "unclear", "approximately" |
| Question opener | "What happens when", "How does", "Whether", "Is it..." |
| Insufficient text | Line is below a minimum length threshold (no signal possible) |

**Priority order when multiple signals match:**
1. TEST — the BDD triple (Given/When/Then on one line) is a near-zero false-positive signal.
2. REQUIREMENT — RFC 2119 uppercase keywords are strong and unambiguous.
3. NEEDS_CLARIFICATION — questions and deferral markers.
4. Default fallback: NEEDS_CLARIFICATION — safer than defaulting to REQUIREMENT; prompts the user to decide rather than asserting a classification the system cannot justify.

**Risks:**
- Lowercase modal verbs produce false positives. "You should consider this" does not express a system requirement, but "should" will match the REQUIREMENT heuristic.
- A line starting with "Given that the system supports..." may be classified as TEST (BDD opener) when it is a conditional requirement.
- Mixed signals on the same line are unresolvable without context: "The system MUST handle TBD format" triggers both REQUIREMENT and NEEDS_CLARIFICATION.
- Non-English specifications defeat all keyword matching entirely and silently.
- Very short candidates (one or two words) carry no classification signal; the default fallback applies.

**Open questions:**
- Should the system surface a confidence level alongside the classification, or is a single label sufficient for v1?
- Is there a minimum line length below which extraction is suppressed entirely?
- How should compound lines with conflicting signals be handled — priority rules, or automatic NEEDS_CLARIFICATION?

---

## R-US2-4. Structures to Extract vs. Ignore

**Extract:**
- Unordered list items (`-`, `*`, `+` prefixes at any indentation level)
- Ordered list items (`1.`, `2.`, etc.)
- Lines containing an RFC 2119 modal verb anywhere in the line
- Lines matching a functional requirement prefix pattern (FR-NNN)
- Lines matching a BDD pattern (Given / When / Then opener)
- Lines ending with a question mark
- Lines containing TBD, TODO, TBC, or equivalent deferral markers
- Table body row cells that match any of the above signals (pipe characters stripped)

**Ignore:**
- Markdown headings (`#`, `##`, `###`, etc.) — structural labels, not specification statements
- Horizontal rules (`---`, `***`, `___`)
- Fenced code blocks (` ``` ` ... ` ``` `) — technical content, not specification language
- Inline code spans (`` `code` ``) — strip backtick markup; retain surrounding text if it otherwise qualifies
- Blockquotes (`>`) — typically commentary, references, or notes
- HTML comments (`<!-- ... -->`) — metadata, not specification content
- YAML / TOML front matter blocks
- Image syntax (`![alt](url)`) — decorative, non-textual
- Standalone hyperlink lines (`[text](url)`) — navigational, not specification statements
- Empty lines
- Table header rows and separator rows (`|---|---|`)
- Lines containing only whitespace or punctuation

**Tradeoffs:**
- Extracting from headings would capture correctly formed requirements written as headings ("## System MUST authenticate users"), at the cost of also extracting many false positives (topic headings like "## Authentication"). The precision loss is likely not worth the recall gain for v1.
- Ignoring nested bullets below depth 1 simplifies the extractor but misses sub-requirements expressed as indented bullets. Including nested bullets increases recall but complicates how the candidate is displayed without its parent context.
- Ignoring table body rows is the safest v1 choice; tables require a dedicated parsing step and the benefit is marginal given that most spec tables either duplicate prose requirements or organize metadata.

**Open questions:**
- Should sub-bullets be extracted as independent candidates, or should the parent bullet's text be prepended as context?
- Should table body rows be in scope for v1?

---

## R-US2-5. Edge Cases and Ambiguity Risks

**Input edge cases:**

| Case | Risk | Mitigation candidate |
|---|---|---|
| No newlines in 10,000-char input | Single block; no bullets to extract | Treat as a paragraph; still apply RFC 2119 and question-mark rules |
| 500+ bullet points | Review UX degrades severely | Display total count before rendering; warn before extracting |
| Unicode / emoji in bullet text | Character boundary edge cases in line splitting | Normalize to UTF-8 before processing |
| Windows line endings (`\r\n`) | Line-split logic may produce `\r` artifacts | Normalize before any parsing step |
| Mixed markdown and plain prose | Heuristics misfire on paragraph text | Tolerate gracefully; scope extraction rules conservatively |
| Blank bullet (`- ` with no text after) | Empty candidate extracted | Skip lines with no content after the list marker |
| Bullet with only inline code | Candidate has no readable classification signal | Strip code markers; skip if remaining text is empty |
| Duplicate lines | Same candidate appears twice | Deduplicate extracted candidates before display |
| Non-UTF-8 pasted content | Character decoding failures | Reject or sanitize before processing |

**Classification edge cases:**

| Case | Risk |
|---|---|
| "The system should not do X" | "should" triggers REQUIREMENT; negation may confuse the user about what they're saving |
| "Should this be a requirement?" | "should" (REQUIREMENT) + "?" (NEEDS_CLARIFICATION) — conflicting signals |
| "MUST (TBD)" | REQUIREMENT and NEEDS_CLARIFICATION signals on the same line |
| "Given this context, the system MUST..." | "Given" (TEST signal) + "MUST" (REQUIREMENT signal) — most common conflict pattern |
| Single-word bullet ("Performance") | No classification signal; falls to NEEDS_CLARIFICATION by default |
| Non-English specification | All keyword heuristics fail silently; all candidates default to NEEDS_CLARIFICATION |

**User review edge cases:**

| Case | Risk |
|---|---|
| User pastes source code | Code comments and string literals produce many false extractions |
| User pastes a changelog | Historical past-tense statements extracted as requirements |
| User navigates away mid-review | All extracted candidates are lost; no draft persistence |
| User re-triggers extraction without saving | Previous batch is implicitly abandoned — should the system warn? |
| User re-pastes the same text after a partial save | Duplicates in the scenario list from US1 |

---

## R-US2-6. UX Considerations for Candidate Review

Four workflow patterns are worth evaluating against the spec's "no automatic persistence" constraint.

**Option A — Opt-in selection (nothing pre-selected)**  
All candidates are displayed with unchecked checkboxes. The user must explicitly check each candidate to include it in the save. Nothing is saved unless the user actively selects it.  
Strongest alignment with FR-US2-006. Best for extractions with a high expected false-positive rate. Risk: users may miss good candidates by not scrolling.

**Option B — Opt-out selection (all pre-selected)**  
All candidates are displayed pre-checked. The user unchecks candidates to exclude them.  
More efficient when extraction quality is high and most candidates are valid. Risk: if the user saves without reviewing, all candidates — including incorrect ones — are persisted. This is a partial violation of the spirit of FR-US2-006 even if not the letter.

**Option C — Card-by-card review**  
One candidate is shown at a time; the user accepts or rejects before advancing.  
Highest review thoroughness. Impractical for more than ~20 candidates. Does not scale to a realistic spec document.

**Option D — Grouped by classification**  
Candidates are displayed in three groups: REQUIREMENT, TEST, NEEDS_CLARIFICATION. Each group can be bulk-accepted or reviewed individually.  
Reduces cognitive load by grouping similar candidates. Compatible with either opt-in or opt-out selection within each group.

**Comparison:**

| | Opt-in (A) | Opt-out (B) | Card-by-card (C) | Grouped (D) |
|---|---|---|---|---|
| Matches "no auto-persist" | ✓ | Partial | ✓ | ✓ |
| Scales to 100+ candidates | ✓ | ✓ | ✗ | ✓ |
| Efficient for clean extractions | ✗ | ✓ | ✗ | ✓ |
| Review thoroughness | Low | Low | High | Medium |

**Additional UX risks:**
- **Context loss**: Extracted candidates are displayed without their surrounding document context. A user may not recognize a bullet when it appears in isolation. Displaying the nearest section heading above each candidate as context would help.
- **Reclassification unavailability**: The spec marks reclassification as a v1 non-goal. Users will encounter mis-classified candidates immediately. The absence of reclassification should be communicated clearly in the UI (e.g., an informational note in the review view).
- **Count summary**: A summary before the review list ("Extracted N candidates — X REQUIREMENT, Y TEST, Z NEEDS_CLARIFICATION") orients the user before they scroll and sets expectations.
- **Large candidate sets**: A document producing 300 candidates is realistic for a mature spec. Reviewing 300 items in a flat list is impractical. This is an open UX problem for v1.

---

## R-US2-7. Observability Considerations

**Candidate event schema:**

| Event | Key fields | Constraint |
|---|---|---|
| `ExtractionTriggered` | inputLengthChars, inputLineCount, sessionId | Do NOT log raw input text |
| `ExtractionCompleted` | candidateCount, requirementCount, testCount, needsClarificationCount, durationMs | Aggregate metadata only |
| `ExtractionEmpty` | inputLengthChars, reason (`no_candidates_found` / `empty_input`) | No content |
| `CandidateReviewSaved` | selectedCount, totalExtracted, durationMs | Count of what user kept |
| `CandidateReviewAbandoned` | totalExtracted | User left without saving |

**Metrics worth instrumenting:**
- Extraction duration distribution (p50 / p95 / p99) segmented by input size bracket.
- Classification distribution per session (ratio of REQUIREMENT / TEST / NEEDS_CLARIFICATION across all extractions) — a skewed distribution signals heuristic problems.
- Acceptance rate proxy: `candidatesSelected / candidatesExtracted` — a consistently low rate signals high false-positive extraction.
- Abandonment rate: extractions triggered with no subsequent save event.

**Constraints:**
- Raw pasted content MUST NOT appear in any log under any circumstances. Only metadata (lengths, counts, durations) is safe.
- Candidate titles that users choose to save inherit the same privacy consideration as scenario titles in US1.
- High-cardinality per-candidate logging (one log line per candidate) is too noisy; aggregate per extraction event.

---

## R-US2-8. Security Considerations

**Threat model:**

| Threat | Vector | Mitigation direction |
|---|---|---|
| XSS via pasted content | Extracted candidate rendered as HTML in review UI | Render all candidate text as plain text; never use raw markup |
| ReDoS via crafted input | Pathological regex input triggers catastrophic backtracking | Cap input length; use simple anchored patterns only; avoid nested quantifiers |
| Memory exhaustion | Extremely large paste clogs browser memory | Hard character cap before processing begins |
| Content injection into US1 list | Saved candidate displayed in scenario list without re-sanitization | US1 list must sanitize at render time, not only at write time |
| Privacy leak via logging | Raw pasted content written to logs | Log metadata only (lengths, counts); never log text content |

**Rendering constraint:**  
The review UI must render candidate title and description text as plain text in all cases. Using a Blazor `@((MarkupString)candidate.Title)` pattern without sanitization would directly expose the XSS vector. This is not an extraction concern — it is a UI constraint that must be carried into planning.

**Input length cap — risk-benefit:**

| Cap | Benefit | Risk |
|---|---|---|
| 10,000 chars | Matches spec performance target; bounds ReDoS worst case | Frustrates users with larger specifications |
| 50,000 chars | Covers most real-world specifications | Increases worst-case processing time; requires validated regex patterns |
| None | Maximum usability | Unacceptable DoS and memory risk |

**Assumption**: The extraction logic runs entirely within the authenticated user's session context. No elevated privileges are involved. The feature introduces no new server-side endpoints that accept raw user text in v1 (subject to the client-vs-server architecture decision deferred to planning).

---

## R-US2-9. Performance Expectations

**Baseline target from spec**: Extraction completes and results are displayed within 2 seconds for pasted text up to 10,000 characters.

**Cost analysis by processing step:**

| Step | Complexity | Risk level |
|---|---|---|
| Input normalization (line endings, trim) | O(N chars) | Negligible |
| Line splitting | O(N chars) | Low |
| Pattern matching per line | O(N lines × M patterns) | Moderate if M grows large |
| Candidate deduplication | O(K log K) where K = candidates | Low |
| UI rendering of all candidates | O(K × render cost per item) | **High when K > 100** |

**Key insight**: The bottleneck is not text processing — it is rendering a large number of candidates in the review UI. A document producing 300 candidates will render slowly regardless of how fast the extraction rules run. This is a UI performance concern, not an extraction algorithm concern.

**Mitigation options (for evaluation in planning):**

| Option | Benefit | Cost |
|---|---|---|
| Progressive rendering | Candidates appear incrementally rather than all at once | Increases complexity |
| Virtual scrolling | Only DOM-visible candidates are rendered | Increases complexity |
| Input size warning | Alert user before processing input above a threshold | Low cost; reduces surprise |
| Hard input cap | Bounds worst-case rendering and processing | May frustrate users |
| Candidate count cap (top N only) | Bounds rendering cost | Silently loses candidates — high risk |

**Assumption**: For v1 at the 10,000 character performance target, synchronous extraction with full candidate rendering is likely acceptable. Progressive rendering and virtual scrolling are deferred enhancements unless performance testing in planning reveals otherwise.

---

## R-US2-10. Open Questions for Planning Phase

The following questions are unresolved and must be addressed before planning begins. They represent the boundary between research and architecture decision.

1. **Client vs. server extraction**: Should extraction run in the browser (client-side, no round-trip) or on the server (centralised, auditable)? This is the single most consequential architectural question for US2.
2. **Input length cap**: What is the definitive hard cap on accepted input length?
3. **Sub-bullet handling**: Should nested list items be extracted as independent candidates, or should the parent bullet's text be prepended as context?
4. **Table body rows**: Are table body rows in scope for v1?
5. **Review workflow**: Which review pattern (opt-in A, opt-out B, grouped D) best aligns with the team's expectations for "explicit user action before save"?
6. **Minimum line length**: Is there a minimum character count below which a line is not extracted?
7. **Re-extraction behaviour**: What happens when a user re-triggers extraction without saving the previous batch — silent discard, or a warning prompt?
8. **Confidence signalling**: Should classification labels be presented with any indication of confidence, or is a single label sufficient for v1?
9. **Non-English input**: Is non-English specification text in scope? If so, keyword heuristics need a different strategy.
