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

---

# Research: US3 — Deterministic Rule Engine for Scenario Extraction

**Phase**: Research | **Created**: 2026-05-21

No architecture decisions are made in this section. The goal is to surface tradeoffs, alternatives, risks, constraints, and open questions to inform the planning phase.

---

## R-US3-1. Rule Engine Design Approaches

The current pipeline has eight stages. The hardcoded behaviour at issue is concentrated in two stages: Stage 4 (Structure Filter — which block types are ignored) and Stage 6 (Classification — which signal determines REQUIREMENT / TEST / NEEDS_CLARIFICATION). US3 must replace that hardcoding with a structured, inspectable rule set. Three broad design models apply.

**Option A — Table-driven rules (flat priority list)**  
Rules are expressed as an ordered list of entries, each with a predicate (match condition) and an outcome (classification or filter action). The engine iterates the list in order; the first matching rule wins. The current priority hierarchy — BddPattern before Rfc2119Uppercase before Rfc2119Lowercase before FrPrefix before QuestionTerminator before DeferralMarker before Default — maps naturally to a flat ordered list. Adding a rule means inserting it at the right position.

Characteristics:
- Easy to audit: the full rule set is visible in one scan.
- Conflict resolution is implicit: position determines priority; no separate conflict resolver needed.
- Adding rules is safe: existing rules below the insertion point are unaffected.
- Difficult to express conditional rules: "apply rule B only if rule A also matched" requires extra complexity.
- Difficult to express block-type-scoped rules: "this keyword rule applies only inside list items, not paragraph lines."

**Option B — Typed rule categories with intra-category priority**  
Rules are grouped into named categories (e.g., `FilterRule`, `ClassificationRule`, `ContextRule`). Within each category, rules have an explicit priority weight. The engine evaluates categories in a fixed sequence; within each category, higher-weight rules are evaluated before lower-weight ones.

This mirrors the existing category structure more directly:
- `FilterRule`: suppresses candidate-ineligible blocks (Stage 4 logic).
- `ClassificationRule`: assigns REQUIREMENT / TEST / NEEDS_CLARIFICATION (Stage 6 logic).
- `ContextRule`: attaches contextual metadata without changing classification (Stage 8 context heading logic).

Characteristics:
- Category boundaries match existing pipeline stage boundaries — migration from current code is straightforward.
- Intra-category priority is explicit and inspectable; adding a rule means assigning a numeric weight, not locating an insertion point.
- Cross-category rule interaction (a filter rule depending on a classification rule's result) is not expressible — but this interaction is not required by the current pipeline.
- The category model makes it easier to restrict rule scope to block types: a `ClassificationRule` can carry a `BlockType[]` applicability list.

**Option C — Predicate-tree rules (composable conditions)**  
Rules are defined as composable predicates — AND, OR, NOT combinations of primitive conditions (pattern match, block type check, length check, signal check). This is the most expressive model but also the most complex to implement, audit, and safeguard from regex abuse.

Characteristics:
- Maximum expressibility: complex conditional rules (if MUST appears AND the line is a list item AND it has no `?` terminator, then REQUIREMENT) are directly representable.
- Hardest to audit: a complex predicate tree requires more cognitive effort to verify.
- Highest implementation cost.
- Risk of rule interactions that are difficult to reason about statically.
- The current pipeline does not contain rules of this complexity; the expressibility gain is not justified by current requirements.

**Comparison:**

| | A: Flat list | B: Typed categories | C: Predicate tree |
|---|---|---|---|
| Auditability | High | High | Medium |
| Maps to existing pipeline stages | Partial | Good | Poor |
| Intra-stage priority expression | Implicit (position) | Explicit (weight) | Implicit (structure) |
| Block-type scoping | Awkward | Natural | Natural |
| Migration complexity | Low | Low-Medium | High |
| Expressibility | Low | Medium | High |
| Security surface (regex abuse) | Low | Low | High |

**Recommended direction**: Option B (typed categories). The category boundaries align with the existing pipeline stage responsibilities. Explicit priority weights are more maintainable than positional ordering in a flat list. The expressibility of Option C is not needed for the current rule vocabulary, and the predicate-tree model introduces security surface for regex composition that is unnecessary at this stage.

**Open question**: Should the rule set be defined entirely in code (as structured C# data), in a data file (JSON/YAML/TOML loaded at startup), or in a hybrid approach (a code-defined rule builder with data-driven configuration)? This is an architecture decision deferred to planning but shapes the configurability story significantly.

---

## R-US3-2. Rule Definition Strategies

Given a typed-category rule engine, rules themselves can be expressed in several ways. The key question is: what form does a single rule take?

**Option A — Code-defined rule objects (C# records or classes)**  
Each rule is a C# record with typed fields: name, priority, match condition (compiled regex or keyword set), applicable block types, and outcome. The rule set is assembled in code — e.g., a static factory or a builder.

Advantages:
- Rules are type-checked at compile time.
- Rule correctness is verifiable with standard unit tests.
- No file loading, no parsing, no deserialization surface.
- Regex patterns are compiled once at startup, not repeatedly during evaluation.
- Rule changes require a code change and a redeploy — this provides an audit trail via version control.

Disadvantages:
- Changing a rule requires rebuilding and redeploying.
- Non-developer maintainers cannot adjust rules without editing source code.

**Option B — Data-file rules (JSON or YAML, loaded at startup)**  
Rules are expressed in a JSON or YAML file. The engine deserializes the file and compiles the rules (including regex compilation) at startup.

Advantages:
- Rules can be adjusted without touching compiled code.
- Potentially allows rule versioning by shipping different rule files.

Disadvantages:
- Regex patterns are expressed as raw strings in the file — no compile-time validation.
- Malformed regex in a data file causes a runtime startup failure; compile-time safety is lost.
- Deserialization introduces an additional failure mode.
- A data-file approach with regex strings is a significant regex injection surface if the file ever becomes user-editable (it must not be).
- Adds a test gap: rule file correctness cannot be verified by the type system; it requires an additional validation step.

**Option C — Code-defined rule builder (structured DSL)**  
Rules are defined using a fluent builder pattern in code. The rule set is expressed as a sequence of builder calls, each producing a typed rule object. This is closer to Option A than Option B — rules live in code, but the syntax is more readable.

Example form (not a code proposal; illustrative only):
```
rules.Add("RFC2119-Uppercase", priority: 20)
     .AppliesTo(BlockType.UnorderedListItem, BlockType.ParagraphLine)
     .WhenMatches(Rfc2119UppercasePattern)
     .Classify(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase);
```

Advantages:
- Type-checked, compile-time safe.
- More readable than raw record construction.
- The builder pattern makes applicability constraints (block type scoping) easy to express.
- Patterns are compiled at rule construction time — no runtime regex compilation.

Disadvantages:
- More implementation effort than a flat list of records.
- The DSL must be designed carefully to remain readable as the rule count grows.

**Recommended direction**: Option A (code-defined rule objects) for the initial US3 implementation. This is the lowest-risk starting point: rules are type-checked, compile-time validated, testable, and carry no deserialization surface. A builder (Option C) can be layered on in a later iteration as a readability improvement. Data-file rules (Option B) should be deferred until there is a concrete requirement for rule adjustment without a redeploy.

---

## R-US3-3. Rule Ordering and Priority Strategies

The existing classification priority order is explicit and well-understood:

> BddPattern → Rfc2119Uppercase → Rfc2119Lowercase → FrPrefix → QuestionTerminator → DeferralMarker → Default

This ordering must be preserved exactly in the rule engine. The question is how to encode it.

**Option A — Enum-based priority (current implicit model)**  
Priority is determined by the `ClassificationSignal` enum value. The engine evaluates signals in a fixed iteration order over the enum. This is the current implicit model: the order is embedded in a `switch` or `if-else` chain.

Risk: the priority is embedded in code control flow. Adding a new signal requires inserting code at the right place and is easy to do incorrectly.

**Option B — Integer priority weight**  
Each rule carries an explicit integer priority weight. Higher weight = higher priority. The engine sorts rules by weight before evaluation, or evaluates them in weight order from the outset.

Characteristics:
- Priority is explicit and inspectable at the rule definition site.
- Adding a rule at a new priority level does not require knowing where in a list to insert it.
- Gaps in the weight space allow new rules to be inserted between existing rules without renumbering.
- Priority conflicts (two rules with the same weight, both matching) must have a defined tie-breaking rule — recommended: the first rule defined in the set wins.

**Option C — Named tiers**  
Priorities are expressed as named tiers (e.g., `ExactMatch`, `StrongSignal`, `WeakSignal`, `Default`). Rules within a tier are unordered among themselves; tiers are evaluated in a defined sequence.

Characteristics:
- Readable: rule intent is clearer from a named tier than from a bare integer.
- Less fine-grained: rules within the same tier have no relative order.
- Appropriate if the current priority requirements map cleanly to a small number of tiers.

**Analysis of current signals against tiers:**

| Signal | Proposed tier |
|---|---|
| `BddPattern` | `ExactMatch` — near-zero false positive; always wins |
| `Rfc2119Uppercase` | `StrongSignal` — unambiguous vocabulary |
| `Rfc2119Lowercase` | `WeakSignal` — matches "should" in non-normative text |
| `FrPrefix` | `StrongSignal` — structured identifier pattern |
| `QuestionTerminator` | `WeakSignal` — structural marker, lower confidence |
| `DeferralMarker` | `WeakSignal` — keyword set |
| `Default` | `Default` — fallback, no signal |

The current ordering does not map cleanly to exactly four tiers: `Rfc2119Uppercase` and `FrPrefix` would be in the same `StrongSignal` tier, but their relative order matters (RFC-2119 uppercase should currently win over FR-prefix on the same line). This means named tiers alone are insufficient — within-tier ordering is still needed.

**Recommended direction**: Option B (integer priority weight) with a recommended gap convention (weights spaced by 10 or 100 so new rules can be inserted without renumbering). This is the most maintainable approach as the rule set grows. Named tier annotations can be added as a documentation layer over the numeric weights without replacing them.

---

## R-US3-4. Conflict Resolution When Multiple Rules Match

When two or more rules match the same candidate, a single classification must be produced. The current resolution is: highest-priority signal wins. US3 must formalize this.

**The three conflict scenarios:**

1. **Same-tier conflict**: Two rules at the same priority weight both match. Example: a future rule for `AcPrefix` (`AC-001:`) added at the same weight as `Rfc2119Uppercase`. Without a tie-breaker, the outcome is undefined.

2. **Cross-tier conflict**: A higher-priority rule (`BddPattern`) and a lower-priority rule (`Rfc2119Uppercase`) both match. The higher-priority rule wins. This is the most common case and is already handled correctly in the current pipeline.

3. **Filter-vs-classification conflict**: A filter rule marks a block as non-extractable, but a classification rule also matches it. Filter rules should always take precedence — a filtered block is not a candidate regardless of what classification rules would say.

**Conflict resolution strategy options:**

**Option A — First-match wins (current implicit model)**  
Rules are evaluated in priority order; the first matching rule terminates evaluation for that candidate. No explicit conflict resolver needed. Deterministic as long as rule order is stable.

Risk: the "first match" depends on evaluation order, which must be guaranteed stable across runs.

**Option B — Highest-weight wins, tie-break by definition order**  
All rules are evaluated against the candidate. The highest-weight matching rule is selected. On a tie (same weight), the first-defined rule in the rule set wins.

Characteristic: requires evaluating all rules even after a high-priority match, which is slightly less efficient but allows a future "explain mode" (listing all rules that matched, not just the winner). The efficiency impact is negligible for a rule set of fewer than 50 rules.

**Option C — Explicit conflict resolution policy per rule**  
Each rule carries a conflict resolution annotation: `StopOnMatch` (first match wins within this rule's tier) or `Continue` (evaluate lower-priority rules too). Useful for meta-rules.

Complexity: high. Not warranted for the current rule vocabulary.

**Recommended direction**: Option B (highest-weight wins, tie-break by definition order). This provides deterministic conflict resolution, makes all matching rules observable (useful for future diagnostic tooling), and is simple to implement and test. The tie-break by definition order must be documented as part of the rule engine contract so that rule authors can reason about it.

**Open question**: Should the rule engine expose a "which rules matched" diagnostic alongside the winning classification? This would be useful for observability (logging which rule fired is already required by FR-US3-005 and the observability requirements in spec.md §US3) but adds complexity to the result model. The `ClassificationSignal` field on `ExtractionCandidate` is already the right carrier for "which rule won" — the question is whether the runner-up signals should also be recorded.

---

## R-US3-5. Classification Strategies for Each Outcome

**REQUIREMENT rules (preserving US2 behaviour):**

The current US2 signals that map to REQUIREMENT are:
- `Rfc2119Uppercase`: MUST, SHALL, SHOULD, MAY, MUST NOT, SHALL NOT (uppercase, whole-word match)
- `Rfc2119Lowercase`: must, shall, required, is required to (lowercase)
- `FrPrefix`: lines matching `FR-[0-9]+` pattern

Each of these is a candidate classification rule in US3. The priority ordering must be preserved.

Risks and edge cases for classification rules targeting REQUIREMENT:
- Whole-word matching is essential: "MUST" should match but "MUSTARD" should not. Simple `Contains` checks fail this; word-boundary anchoring is required.
- Uppercase modal verbs in quoted text ("the specification says MUST") produce false positives. The rule engine has no quotation context; this risk exists in the current pipeline and is not made worse by US3.
- Negations ("MUST NOT") are a separate RFC-2119 signal but still produce REQUIREMENT classification in the current model. Whether "MUST NOT do X" should be classified as REQUIREMENT or flagged differently is an open question deferred from US2. US3 should preserve the current behaviour.

**TEST rules (preserving US2 behaviour):**

The current US2 signal for TEST is `BddPattern`:
- BDD triple detected on one line: Given ... When ... Then
- Line starts with a BDD opener: Given, When, Then, And, But, Scenario

Risks:
- "Given this context, the system MUST..." is the canonical false-positive: the BDD opener fires even though the line is a conditional requirement statement. This is a known limitation from US2 research and is not worsened by US3.
- The BDD-opener rule should require the keyword as a standalone word at the line start, not as a prefix of another word ("Whenever" should not trigger).

**NEEDS_CLARIFICATION rules (preserving US2 behaviour):**

Current signals:
- `QuestionTerminator`: line ends with `?`
- `DeferralMarker`: contains TBD, TODO, TBC, open question, to be defined, to be decided
- `Default`: fallback when no other signal fires

All three are candidates for explicit `ClassificationRule` entries in the rule engine. `Default` is a special-case rule — it has no match condition and always fires at the lowest priority.

**Classification gap: no rule for Rfc2119Lowercase today?**  
In the current implementation, `Rfc2119Lowercase` is defined as a `ClassificationSignal` value but its effective priority vs. `QuestionTerminator` and `DeferralMarker` should be confirmed against actual code before formalizing it in the rule engine. If a line contains both "must" (lowercase) and "?" (question terminator), the current pipeline's resolution must be verified and preserved.

**Open question**: Should the rule engine support classification rules scoped to specific `BlockType` values? For example, a rule that only fires on `UnorderedListItem` blocks but not `ParagraphLine` blocks. The current pipeline applies classification rules uniformly across all candidate-eligible block types. Adding block-type scoping to classification rules would increase expressibility but adds complexity. This is an extensibility decision for planning.

---

## R-US3-6. Markdown-Aware Rule Matching

The current Stage 3 (Block Partitioning) already produces typed `TextBlock` instances with `BlockType` annotations. The rule engine can receive these typed blocks rather than raw text lines, enabling block-type-aware rule matching without an additional parsing step.

**Block-type applicability as a rule property:**

Each rule can carry an optional `ApplicableBlockTypes` constraint. If the constraint is absent, the rule applies to all candidate-eligible block types. If present, the rule applies only to blocks of the listed types.

Example applicability constraints that may be useful:
- A future `TableBodyCell` classification rule might apply only to `TableBodyRow` blocks (where pipe syntax must be stripped before matching).
- A `FrPrefix` rule might apply only to `OrderedListItem` blocks, since FR-numbered items in prose paragraphs are unusual.
- A heading-extraction rule (if ever desired) would target `Heading` blocks — currently filtered in Stage 4, so this would also require a Stage 4 filter rule change.

Risk: if block-type scoping is introduced in US3, the rule set must be validated against the full set of `BlockType` values to ensure no candidate block type is accidentally left without any applicable rules. A candidate block with no matching classification rule would bypass the `Default` fallback rule if the Default rule is also scoped — this must be prevented by making the `Default` rule unconditionally applicable to all block types.

**Line length cap as a rule parameter:**

The current Stage 6 applies a per-line sub-cap (2,000 characters) before running any pattern with quantifiers. This cap should become a first-class parameter of the rule engine — either a global engine configuration value or a per-rule configuration parameter. The 2,000-character default from US2 planning is a calibration choice, not a hardcoded constant; the rule engine should expose it as a configurable value.

---

## R-US3-7. Section-Aware Extraction

The current Stage 8 attaches `TextBlock.PrecedingHeading` as `ExtractionCandidate.ContextHeading`. This context is carried from Stage 3 Block Partitioning through Stage 5 Content Extraction to Stage 8 Result Assembly without any classification influence — it is purely display metadata.

In the rule engine model, section awareness can be expressed as a `ContextRule`: a rule that fires when a `TextBlock.BlockType == Heading` and records the heading text into a "current context" accumulator. The accumulator value is attached to all subsequent candidates until the next heading rule fires.

This is a behaviorally identical representation to the current implementation — the context-tracking logic is extracted from Stage 3 into an explicit named rule rather than being implicit in the block partitioning loop.

**Potential extension: section-scoped classification rules**  
A more powerful use of section awareness would be a classification rule that fires differently depending on which section heading was most recently seen. Example: "MUST" in a section headed "Out of Scope" might be classified as NEEDS_CLARIFICATION rather than REQUIREMENT because the context negates the normative reading.

This is significantly more complex and should be treated as a future extensibility item, not a US3 requirement. US3 must preserve the current context-neutral behaviour.

**Risk**: if `ContextRule` is implemented as a first-class rule type in US3, it must be clearly distinguished from `FilterRule` and `ClassificationRule` in the rule set. A `ContextRule` has no classification outcome — it only updates the context accumulator. Mixing it with classification rules would require the engine to handle rules with no outcome, which complicates the result model.

---

## R-US3-8. Filter and Ignore Rules

Stage 4 (Structure Filter) currently hardcodes which `BlockType` values are discarded. In the rule engine model, these become explicit `FilterRule` entries.

**Current filtered block types (from data-model.md §BlockType):**
- `Heading` — structural label, not a specification statement
- `FencedCodeBlock` — technical content
- `Blockquote` — commentary
- `HorizontalRule` — separator
- `HtmlComment` — metadata
- `YamlFrontMatter` — document front matter
- `Empty` — blank lines
- `TableHeaderRow` — column labels
- `TableSeparatorRow` — table structural separator

Each of these maps to a `FilterRule` with a single `BlockType` match condition and a `Discard` outcome.

**Content-based filter rules (Stage 5 minimum-length check):**  
The Stage 5 minimum content length check is currently an inline condition in the extraction loop. In the rule engine model, this can be a `FilterRule` with a content-based condition: "if the stripped text length is below threshold N, discard." This makes the threshold configurable as a named rule parameter rather than a hardcoded constant.

**Filter rule ordering relative to classification rules:**  
Filter rules must always be evaluated before classification rules. A candidate that is filtered never reaches the classification stage. This is a rule engine invariant, not a per-rule property. In the typed-category model (R-US3-1, Option B), `FilterRule` and `ClassificationRule` are separate categories; the engine evaluates all `FilterRule` entries for a block before evaluating any `ClassificationRule`. This ordering is deterministic and explicit.

**Risk: filter rule completeness**  
If a new `BlockType` enum value is added in a future version and no corresponding `FilterRule` entry is present, the block will be evaluated by classification rules by default — it will be classified rather than discarded. This may or may not be the desired behavior. A defensive default ("if a block type has no filter rule and no classification rule matches, discard rather than default to NEEDS_CLARIFICATION") is worth evaluating in planning.

---

## R-US3-9. Observability Needs for Rule Execution

US3 introduces two new observability requirements beyond the US2 baseline:

1. **Which rule fired** — `ExtractionCandidate.ClassificationSignal` already records this for the winning classification. In the rule engine model, `ClassificationSignal` maps 1:1 to the rule that produced the classification. No new model fields are needed; the rule engine must populate `ClassificationSignal` from the matched rule's signal type.

2. **Rule evaluation counts** — The number of rules evaluated per extraction event is a new metric. This matters when the rule set grows: a rule set with 50+ rules and 500 candidates produces 25,000+ evaluations per extraction. This is measurable without storing per-evaluation detail — a summary count at the end of the pipeline is sufficient.

**New candidate diagnostic fields (for evaluation):**  
A `MatchedRuleNames` diagnostic field on `ExtractionCandidate` would record all rules that matched (not just the winner). This is useful for rule authoring and debugging but adds model size and is not required by the US2 production code path. Options:
- Include in all builds (adds overhead for every candidate, every extraction).
- Include only in debug/diagnostic mode (adds conditional complexity).
- Defer entirely (document as a future observability enhancement).

**Structured log event additions for US3:**

| Event | New fields | Constraint |
|---|---|---|
| `ExtractionCompleted` | `rulesEvaluatedCount` (total across all candidates) | Count only; no text |
| `ExtractionRuleViolation` | `ruleName`, `blockType`, `candidateIndex` | Fired if a rule engine invariant is violated (e.g., same rule matches twice for the same candidate) | No text content |

The existing `ExtractionTriggered`, `ExtractionCompleted`, `ExtractionEmpty`, `CandidateReviewSaved`, and `CandidateReviewAbandoned` events from US2 remain unchanged. New fields are additive and do not break existing log consumers.

---

## R-US3-10. Security Risks and Mitigations

**Regex abuse (ReDoS in rule definitions):**  
US3 moves regex patterns from hardcoded code into a rule definition structure. This is both a maintainability gain and a potential security surface: a developer-authored rule with a poorly constructed pattern could introduce catastrophic backtracking.

Mitigations:
- All rule regex patterns must be reviewed as part of code review. The existing policy from US2 (anchored patterns, no nested quantifiers) must be documented as a rule authoring constraint and enforced in the rule definition structure where possible.
- The 2,000-character per-line sub-cap from US2 bounds the input length any single pattern sees, limiting worst-case backtracking time even for imperfect patterns.
- Regex patterns defined in code (Option A / Option C from R-US3-2) are subject to standard code review; the risk is lower than data-file patterns which bypass type checking.
- Static analysis for ReDoS patterns exists but is not yet part of the CI pipeline. This is a gap that US3 should document as a recommendation for the quality gate.

**Rule definitions as developer-only content:**  
Rule definitions must not be user-editable at runtime. If rules are defined in code (recommended), this is enforced by the build system. If rules are ever moved to a data file, the file must not be writable by application runtime users; it must be treated as read-only configuration deployed with the application.

**No new server surface:**  
US3 does not introduce any new server-side API surface. The extraction pipeline remains entirely client-side. The server boundary and its security properties (no raw text, typed inputs only) are unchanged from US2.

**Input handling — unchanged from US2:**  
The input validation gate (Stage 1), line-ending normalization (Stage 2), and minimum content length check (Stage 5) all remain in place. The rule engine does not change what inputs are accepted — it only changes how accepted inputs are evaluated. Security properties of input handling are inherited from US2 unchanged.

**Open question**: If rule configuration is eventually moved to a data file, should a startup-time rule validation step be added to reject rules with potentially unsafe regex patterns? This would require integrating a static regex analysis library. It is worth noting as a future security gate, but it is not required for US3 if rules remain code-defined.

---

## R-US3-11. Performance Constraints

**US2 measured baseline (T093 results):**  
Extraction of 10,000-character input with the current hardcoded pipeline: `durationMs = 0` (sub-millisecond); 87 candidates produced. The bottleneck is UI rendering, not extraction processing.

**Expected US3 overhead:**  
Replacing hardcoded `if-else` evaluation with a rule engine introduces per-candidate iteration overhead. For a rule set of 10–15 rules and 87 candidates, this is approximately 870–1,305 rule evaluations per extraction. Each evaluation is a pattern match (typically a compiled regex or a keyword lookup) against a string of at most 2,000 characters. Total additional overhead is expected to be negligible (microseconds, not milliseconds) for this scale.

**Rule set growth scenarios:**

| Rule count | Candidates | Evaluations | Risk |
|---|---|---|---|
| 15 rules (current signal set) | 87 | ~1,300 | Negligible |
| 50 rules (expected growth limit) | 87 | ~4,350 | Negligible |
| 50 rules | 500 (dense document) | ~25,000 | Likely still < 10 ms |
| 200 rules (hypothetical large set) | 500 | ~100,000 | Measurable; profile before shipping |

**200 ms ceiling (spec §US3 Performance Expectations):**  
The US2-established 200 ms ceiling must be preserved. Based on the analysis above, a rule set of fewer than 50 rules will not threaten this ceiling for typical inputs. A rule count above 100 should trigger a performance validation step before shipping.

**Optimization options (for evaluation in planning):**

| Option | Benefit | Cost |
|---|---|---|
| Short-circuit on first highest-priority match | Avoids evaluating lower-priority rules after a BddPattern match | Small; compatible with tie-break model only if explicitly designed in |
| Compiled regex patterns at rule construction time (not at evaluation time) | Eliminates per-evaluation regex compilation overhead | Low — standard practice; already assumed in R-US3-2 Option A |
| Filter rules evaluated before classification rules | Avoids classification evaluation on filtered blocks | Low; natural consequence of typed-category model (R-US3-1 Option B) |
| Rule applicability pre-filtering by block type | Avoids evaluating rules that cannot apply to the current block type | Low-medium; valid if block-type scoping is implemented |

The most important optimization is early short-circuit: once a `FilterRule` matches a block, no `ClassificationRule` need be evaluated. This is the typed-category model's natural behaviour.

---

## R-US3-12. Preserving All US2 Behaviour

US3 must be a non-breaking refactor of the extraction pipeline. The existing 8-stage structure and all US2 acceptance criteria must remain satisfied. This section maps current hardcoded behaviour to the equivalent rule engine representations to verify that no behaviour is lost.

**Stage 4 filter rules (current → rule engine equivalents):**

| Current hardcoded filter | Equivalent FilterRule |
|---|---|
| Discard `BlockType.Heading` | FilterRule: match `BlockType.Heading`, outcome `Discard` |
| Discard `BlockType.FencedCodeBlock` | FilterRule: match `BlockType.FencedCodeBlock`, outcome `Discard` |
| Discard `BlockType.Blockquote` | FilterRule: match `BlockType.Blockquote`, outcome `Discard` |
| Discard `BlockType.HorizontalRule` | FilterRule: match `BlockType.HorizontalRule`, outcome `Discard` |
| Discard `BlockType.HtmlComment` | FilterRule: match `BlockType.HtmlComment`, outcome `Discard` |
| Discard `BlockType.YamlFrontMatter` | FilterRule: match `BlockType.YamlFrontMatter`, outcome `Discard` |
| Discard `BlockType.Empty` | FilterRule: match `BlockType.Empty`, outcome `Discard` |
| Discard `BlockType.TableHeaderRow` | FilterRule: match `BlockType.TableHeaderRow`, outcome `Discard` |
| Discard `BlockType.TableSeparatorRow` | FilterRule: match `BlockType.TableSeparatorRow`, outcome `Discard` |

**Stage 6 classification rules (current → rule engine equivalents):**

| Current hardcoded classification | Equivalent ClassificationRule |
|---|---|
| BDD pattern → TEST, signal BddPattern, priority 1 (highest) | ClassificationRule: BDD match, weight 70, outcome TEST, signal BddPattern |
| RFC 2119 uppercase → REQUIREMENT, signal Rfc2119Uppercase, priority 2 | ClassificationRule: RFC2119-UC match, weight 60, outcome REQUIREMENT, signal Rfc2119Uppercase |
| RFC 2119 lowercase → REQUIREMENT, signal Rfc2119Lowercase, priority 3 | ClassificationRule: RFC2119-LC match, weight 50, outcome REQUIREMENT, signal Rfc2119Lowercase |
| FR prefix → REQUIREMENT, signal FrPrefix, priority 4 | ClassificationRule: FR-NNN match, weight 40, outcome REQUIREMENT, signal FrPrefix |
| Question terminator → NEEDS_CLARIFICATION, signal QuestionTerminator, priority 5 | ClassificationRule: ends-with-`?` match, weight 30, outcome NEEDS_CLARIFICATION, signal QuestionTerminator |
| Deferral marker → NEEDS_CLARIFICATION, signal DeferralMarker, priority 6 | ClassificationRule: TBD/TODO/TBC keyword match, weight 20, outcome NEEDS_CLARIFICATION, signal DeferralMarker |
| Default fallback → NEEDS_CLARIFICATION, signal Default | ClassificationRule: unconditional, weight 0, outcome NEEDS_CLARIFICATION, signal Default |

**Verification strategy:**  
The US2 unit tests (`ScenarioExtractionServiceTests.cs`) and the US2 acceptance criteria tests (`ExtractionAcceptanceCriteriaTests.cs`) must all continue to pass without modification after the rule engine is introduced. These tests are the regression safety net for the US3 migration. If any test requires modification due to US3, that modification must be explicitly justified as an intentional behaviour change, not a regression.

---

## R-US3-13. Open Questions for Planning Phase

The following questions are unresolved and must be addressed before planning begins.

1. **Rule definition location**: Should the rule set be code-defined (records/builder), data-file-defined (JSON/YAML), or hybrid? The recommended direction from R-US3-2 is code-defined for US3; this is the planning team's decision to confirm.

2. **Block-type scoping for classification rules**: Should classification rules carry an optional `ApplicableBlockTypes` constraint? Including this in the initial design adds expressibility; deferring it keeps US3 simpler. The current US2 pipeline applies classification uniformly across block types, so no behaviour is lost by deferring.

3. **Diagnostic "all matched rules" field**: Should `ExtractionCandidate` expose a list of all rules that matched (in addition to the winning rule's `ClassificationSignal`)? This aids rule authoring and debugging but adds model size. Recommend deferring; `ClassificationSignal` already captures the winning rule.

4. **Default rule scope**: Should the `Default` fallback rule apply only to blocks that matched at least one `FilterRule` applicability check (i.e., are candidate-eligible blocks), or to all blocks? The current behaviour is that the Default rule fires only for blocks that survived Stage 4 filtering. The rule engine must preserve this.

5. **Rfc2119Lowercase vs. QuestionTerminator priority**: For a line containing both a lowercase RFC-2119 keyword and a question terminator (e.g., "must this be required?"), what is the expected classification? The current pipeline's resolution of this specific conflict should be confirmed against the implementation before formalizing the rule engine weights. This is a precision question, not an open design question.

6. **ContextRule as a first-class rule type**: Should section-aware context tracking be expressed as an explicit `ContextRule` category in the rule engine, or should it remain an implicit accumulator in the pipeline? Including it as a named rule type makes the pipeline fully rule-driven but adds a new rule category. Deferring preserves simplicity for US3.

7. **Startup validation of the rule set**: Should the rule engine validate the rule set at startup (no duplicate signal types, no weight conflicts, no empty rule set) and fail fast if the rule set is incoherent? This is a quality-of-life feature for rule authors. Recommended: yes, at a low implementation cost.

8. **Migration strategy**: Should US3 introduce the rule engine alongside the existing hardcoded pipeline (switchable via configuration) or replace it directly? A parallel-running comparison mode would allow the team to verify identical outputs before the hardcoded path is removed. This is a migration risk question for planning.

9. **Rule naming convention**: What naming scheme should rule names follow for log output and observability traces? A consistent naming convention (e.g., `Filter:Heading`, `Classify:Rfc2119Uppercase`, `Context:SectionHeading`) aids log readability.

10. **Performance regression guard**: Should a performance test that measures rule engine overhead specifically (separate from the end-to-end extraction test) be included in the test suite? This would guard against rule set growth causing a performance regression without a visible test failure.

---

# Research: US4 — Level 1 Configurable Extraction Rules

**Phase**: Research | **Created**: 2026-05-21

No architecture decisions are made in this section. The goal is to surface tradeoffs, alternatives, risks, constraints, and open questions to inform the planning phase.

---

## R-US4-1. Approaches to Safe Rule Configurability

The central design challenge for US4 is how to allow teams to extend extraction rule behavior without exposing the regex, scripting, or code surfaces that would introduce security and determinism risks. A configurability spectrum helps frame the options:

**Level 0 — Fully internal (current US3 state)**  
All rules are developer-authored code objects in `ExtractionRuleSet.Default()`. Changing any keyword or threshold requires a code change and a redeploy. No configuration path exists. This is the US3 baseline that US4 must extend.

**Level 1 — Keyword and prefix additions (US4 target)**  
Users supply plain strings — keywords to extend existing rule groups, and prefix strings that map to a classification outcome. The rule engine escapes all configuration values and wraps them with word-boundary assertions before incorporating them into patterns. No regex metacharacters are accepted from configuration. No code execution. This is the US4 target.

**Level 2 — Validated regex (future)**  
Users supply regex patterns that are statically analyzed for catastrophic backtracking risk before acceptance. Substantially more powerful but introduces a significant security analysis burden. Requires integration of a static ReDoS detection library (e.g., a RE2-compatible validator or RXXR2). Not appropriate for the MVP.

**Level 3 — Custom rule types (future)**  
Users define rules using a DSL or sandboxed execution environment. Full expressibility; high implementation and security burden. Not in scope.

**Within Level 1, the configurable surface is additive along four dimensions:**

| Dimension | Description | Safety property |
|---|---|---|
| Keyword additions to existing groups | Plain strings added to BDD, RFC-2119, or deferral keyword sets | Engine escapes and wraps; user never touches regex |
| Prefix-based classification rules | (prefix string, ScenarioKind) pairs evaluated as `StartsWith` | No regex involved; literal string comparison only |
| Ignore prefixes | Strings whose presence at the start of stripped text causes the candidate to be filtered | Same safety as prefix rules |
| Rule group toggles and priority overrides | Enable/disable named rules; adjust integer priorities within a bounded range | No new regex surface; operates on existing rule objects |

Each dimension is independently safe and can be implemented without enabling the others. The US4 implementation can therefore be staged if necessary.

**Key risk**: accepting even seemingly safe plain strings requires careful escaping discipline. If a single path through the codebase incorporates a configuration string into a regex pattern without calling `Regex.Escape` first, the safety property of Level 1 is broken. The escaping responsibility must be centralized in one place and verified at code review.

---

## R-US4-2. Configuration Storage Strategies

Where the configuration lives determines who can change it, how quickly changes take effect, and how complex the infrastructure is.

**Option A — .NET configuration system (appsettings.json / environment variables)**  
The rule configuration is expressed as a named section in `appsettings.json` (or `appsettings.{Environment}.json`) and bound to a typed configuration model via `IOptions<ExtractionRuleConfiguration>`. Loading happens at startup; no extraction-time reads. This is how `IExtractionConfiguration` (MaxInputLengthChars, MaxLineLengthForPatternMatching) is already managed in the project.

Characteristics:
- No new infrastructure; integrates with the existing .NET configuration pipeline.
- Configuration is version-controlled alongside code; changes require a redeploy.
- Works uniformly across development, staging, and production via environment variable overrides.
- Application-wide only — no per-project customization without multiple deployment targets.

**Option B — Database storage (application-wide)**  
A single configuration row in a dedicated table stores the rule configuration as JSON. The application reads this row at startup and compiles the rule set. Hot-reload (without restart) is possible if the configuration is re-read on a schedule or on a signal.

Characteristics:
- Configuration can be updated by an authorized user without a redeploy (if a write path is provided).
- Adds a startup dependency on the database; if the DB is unavailable, startup cannot proceed unless a fallback is built in.
- Requires a new table migration, a new read path, and admin-level write access control.
- Appropriate for a future version with a configuration UI, but over-engineered for MVP.

**Option C — Database storage (per-project)**  
Each project has its own rule configuration row, loaded at extraction time based on the active project context.

Characteristics:
- Enables project-level vocabulary customization — different teams can tune rules for their terminology.
- Requires a new DB table, new GraphQL mutations and queries, project-scoped authorization, and a settings UI.
- Introduces a runtime DB read per extraction session (or per session startup), which must be accounted for in performance budgets.
- The determinism property becomes scoped: `(projectRuleSet(projectId), inputText) → candidateList`. Still deterministic within a stable project configuration; a configuration change between two runs produces different outputs, but two runs with the same configuration produce identical outputs.
- Significant scope beyond the US4 MVP; appropriate for a future evolution story.

**Option D — Hybrid: appsettings for MVP, abstracted for future evolution**  
Start with Option A, but define an `IExtractionRuleConfiguration` interface (or equivalent) as the consumer-facing abstraction. The concrete implementation reads from `IOptions<ExtractionRuleConfiguration>`. A future implementation can read from the DB (Option B or C) without changing any code that consumes the interface.

**Recommended direction**: Option D. Starting with appsettings avoids infrastructure complexity for MVP while preserving the seam for project-level configuration. This mirrors the project's existing pattern for `IExtractionConfiguration`. The interface should be introduced at the same time as the first concrete implementation, so the seam is available when the DB-backed implementation is needed.

**Risk — appsettings in WASM**: Blazor WebAssembly loads configuration from `wwwroot/appsettings.json`, which is a publicly served static file. Configuration content must not include any sensitive information. Rule keywords and prefixes are not sensitive, but this public exposure is worth documenting. If configuration ever includes authorization data or internal vocabulary that should not be client-visible, a different delivery mechanism (e.g., served by an API endpoint behind auth) would be needed.

---

## R-US4-3. Validation Strategies for Keywords and Prefixes

Configuration values come from a file or environment that may be edited by a human without IDE type-checking. Validation must reject values that would produce unsafe or ill-formed behavior, without being so strict that legitimate values are rejected.

**Validation surface — string values (keywords and prefixes):**

| Check | Rationale |
|---|---|
| Non-empty string | A zero-length keyword would match an empty string at every word boundary — a pathological match |
| Maximum length (proposed: 200 characters) | A keyword longer than 200 chars is almost certainly a configuration error; caps worst-case pattern complexity |
| Printable ASCII characters only | Unicode keywords are a future consideration; ASCII-only keeps escaping behavior predictable for MVP |
| No regex metacharacters (`\ ^ $ . | ? * + ( ) [ ] { }`) | These characters change meaning after `Regex.Escape` in a way that may not match the config author's intent; rejecting them at validation time prevents unexpected pattern behavior and eliminates the metacharacter escaping surface entirely |
| Post-escaping pattern compiles to a valid Regex | Belt-and-suspenders: even after metacharacter prohibition, the assembled pattern (wrapping in `\b(?:...) \b`) must compile successfully |
| Maximum count per group (proposed: 50) | Prevents bloated rule sets; bounds pattern alternation complexity |

**Validation surface — rule names (for enable/disable):**

| Check | Rationale |
|---|---|
| Name must exist in `ExtractionRuleSet.Default()` | Unknown names are silent no-ops (probably typos); rejecting them forces the config author to notice the error |
| `Classify:Default` must not appear in a disable list | Disabling the Default fallback rule would leave some candidates without a classification — a hard invariant violation |

**Validation surface — priority overrides:**

| Check | Rationale |
|---|---|
| Integer value strictly greater than 0 | Priority 0 is reserved exclusively for the `Classify:Default` unconditional fallback rule |
| Integer value strictly less than 100 | Bounds the override range; leaves headroom for future high-priority system rules (priority 100+) if needed |

**When to validate**: All validation must run at application startup before any extraction session begins. This is consistent with the `ExtractionRuleEngine` constructor validation established in US3. Validation at startup catches all configuration errors before any user interaction occurs.

**Failure response**: Log a structured Warning identifying the violated check and the configuration field name (not the field value). Fall back to `ExtractionRuleSet.Default()` for all subsequent sessions. See R-US4-6 for the full fallback discussion.

**What NOT to reject during validation:**
- Keyword additions that duplicate existing default keywords: the resulting alternation pattern redundantly matches a word it already matched, but causes no incorrect behavior. A Warning log is appropriate; rejection is not.
- Priority overrides that create ties with existing rules: the existing first-registered tie-break from US3 handles this deterministically. Not an error.

**Alternative — validate at configuration authoring time**: A CLI tool or schema validator (e.g., a JSON Schema document for the configuration section) could catch errors before the application starts. This is a better developer experience but requires additional tooling. Not required for MVP; worth noting as a future quality-of-life improvement.

---

## R-US4-4. Rule Enable/Disable Mechanisms

Allowing named default rules to be toggled off gives teams the ability to suppress rules that produce too many false positives for their vocabulary.

**Option A — Disabled names list (opt-out model)**  
The configuration carries a `string[]` of rule names to exclude. All rules from `ExtractionRuleSet.Default()` that are not in the list are included. New default rules added in future code versions are automatically included.

Characteristics:
- Simple: the common case (disable one or two rules) is a short list.
- Future-proof: a new default rule added to `ExtractionRuleSet.Default()` is automatically active for all configurations that do not explicitly disable it.
- The invariant that `Classify:Default` cannot be disabled is enforced at validation time as a special-case check.

**Option B — Enabled names list (opt-in model)**  
The configuration carries a `string[]` of rule names to include; all others are excluded.

Characteristics:
- Maximally explicit: only rules the administrator has reviewed are active.
- Fragile against future default rule additions: adding a new rule to `ExtractionRuleSet.Default()` silently suppresses it for all configurations that don't explicitly opt in. This is a maintenance hazard and would require every configuration to be updated when the default rule set grows.
- Appropriate for highly locked-down deployments but not for a general-purpose configuration layer.

**Option C — Per-rule explicit flag (enable: true/false per named rule)**  
The configuration maps rule names to boolean flags. Rules with no entry default to enabled.

Characteristics:
- Verbose; readable.
- Same "future-proof" property as Option A (unlisted = enabled by default).
- More configuration surface for the same outcome as Option A.

**Recommended direction**: Option A (disabled names list). It produces the most concise configuration for the most common use case (disable one or two noisy rules) while remaining future-proof against default rule set growth.

**Interaction with priority overrides**: If a rule name appears in both the disabled list and in the priority overrides section, the disabled list takes precedence. A disabled rule is excluded from the compiled rule set regardless of any priority configuration for that name.

**Interaction with keyword additions**: If a rule group has keyword additions configured but the rule is disabled, the additions are irrelevant — the rule is excluded entirely. This is not an error; the keyword additions are simply unused for that session.

---

## R-US4-5. Safe Priority Tuning Boundaries

The default rule set uses priorities 0, 20, 30, 40, 50, 60, 70, spaced by 10. Configured priority overrides shift named rules within this space.

**Why allow priority tuning?**  
A team may find that RFC-2119 lowercase keywords in their domain are reliably normative ("must" in their specs always means a requirement, never appears in hedging language). Raising `Classify:Rfc2119Lowercase` priority above `Classify:Rfc2119Uppercase` (60) would make it the preferred classifier for lines containing both case forms.

**Priority boundary risks:**

| Scenario | Risk | Mitigation |
|---|---|---|
| Override to priority 0 | Would conflict with `Classify:Default` (reserved at 0) | Reject values of 0 at validation; bound range to > 0 |
| Override creates a tie with an existing rule | Two rules at the same priority — which wins? | Acceptable: the existing first-registered tie-break from US3 handles this deterministically; document as expected behavior |
| Override elevates a rule above all default rules (e.g., priority 99) | The overridden rule always wins; may suppress more specific rules | Allowed within the bounded range; the administrator accepts this consequence |
| Override lowers the Default fallback (currently at 0) | Default rule must stay at 0 to guarantee every candidate receives a classification | `Classify:Default` is excluded from the priority override map at validation time |

**Boundary options:**

| Range | Effect |
|---|---|
| 1–99 | Full flexibility within the configured space; any default rule can be outranked or demoted |
| 1–79 | Prevents overrides from outranking `Classify:BddPattern` (currently at 70); preserves BDD as the highest-priority default |
| 1–69 | Preserves all relative ordering among default rules; overrides can only shift configured rules up to (but not above) the BDD rule |

The US4 spec proposes 1–99 (greater than 0, less than 100). This is the most flexible choice. If future default rules are defined at priorities 80–99, they would be immune to configured overrides; if defined at 1–79, they would not. The bound of < 100 leaves headroom for this convention.

**Priority of new prefix-based classification rules**: Prefix rules are new rules not present in `ExtractionRuleSet.Default()`. They need an assigned priority. Options:
- Fixed default of 10 (below all default classification rules except `Classify:Default`): prefix rules fire when no other rule matched. Low specificity; appropriate for supplementary vocabulary.
- Configurable per prefix rule (within the 1–99 bound): gives the administrator full control over where prefix rules rank relative to default rules. A "REQ-" prefix for REQUIREMENT could be given priority 45 (between FrPrefix at 40 and Rfc2119Lowercase at 50) to rank it higher than the FR-prefix pattern.

Having a configurable per-rule priority for prefix rules is more useful than a fixed default, but adds complexity to the configuration model. A default of 10 (low priority, can be overridden per rule) is a good starting point.

---

## R-US4-6. Fallback and Default Configuration Behavior

When configuration is present but invalid, three response strategies exist.

**Option A — Fail fast (application cannot start)**  
If any configured value fails validation, startup fails with a descriptive error message. The application does not serve any requests until the configuration is corrected.

Pros: no silent misbehavior; forced fix before users interact with the system; consistent with strict "configuration as code" discipline.  
Cons: a single invalid keyword in configuration takes down extraction for all users; in production this is a high blast radius for a potentially minor configuration error.

**Option B — Warn and fall back to Default**  
If configuration validation fails, log a structured Warning identifying the failure reason and the field name (not the value). Fall back to `ExtractionRuleSet.Default()`. Application starts and serves requests with default extraction behavior.

Pros: application remains operational; extraction works correctly with default rules; the Warning log provides the feedback loop for the administrator to fix the configuration.  
Cons: the intent of the configuration is not applied, and users may not immediately notice (they see extraction results, not configuration errors).

**Option C — Partial application (skip invalid entries, apply valid ones)**  
Apply all valid configuration entries and skip invalid ones. Log a Warning for each skipped entry.

Pros: maximizes the benefit of valid configuration even when some entries are invalid.  
Cons: partial application produces a rule set that is neither the intended configured set nor the default set; the resulting behavior is difficult to reason about without examining logs. An administrator might see "mostly configured" behavior and not notice that some rules were silently dropped.

**Recommended direction**: Option B (warn and fall back to Default). The US4 spec (FR-US4-013) aligns with this approach. The rationale: a complete, known-good fallback is more predictable than a partial application. The Warning log is the mandatory feedback mechanism; without it, the fallback would be silent and harder to diagnose.

**Fallback invariant**: "Fall back to Default" means `ExtractionRuleSet.Default()` exactly — the complete US3 baseline with no configured modifications. It is never a partial state.

**Future consideration**: A deploy-time validation mode (e.g., `--validate-config` flag in a startup health check or CI/CD step) would catch invalid configuration before it reaches production. This converts the runtime fallback into a build-time failure for teams with automated deployments. Worth noting as a future quality gate.

---

## R-US4-7. Observability for Configurable Rule Evaluation

**Startup logging:**

| Event | Fields | Constraint |
|---|---|---|
| Configuration loaded | keywordAdditionCount (per group), prefixRuleCount, disabledRuleNames (list of names, not values) | No keyword content; only counts and developer-assigned names |
| Configuration validation failure | fieldName, violationType (e.g., `regex_metacharacter`, `unknown_rule_name`, `priority_out_of_range`) | No field value content in log |
| Fallback to Default | reason (`validation_failure`, `no_configuration`) | Emitted whenever Default is used instead of a configured set |

**Extraction-time logging (no new events needed):**

The existing `ExtractionCompleted` log event already carries `rulesEvaluatedCount`, which counts all rule evaluations including any added by configuration. No new fields are needed for configured rules — the existing machinery is sufficient.

The existing `WinningRuleName` field in `RuleEvaluationResult` identifies which rule fired. For configured prefix rules, this field carries the rule's developer-assigned name (e.g., `Configure:Prefix:Req`). For keyword extensions to existing rules (e.g., `Classify:Rfc2119Uppercase` extended with configured keywords), the winning rule name remains `Classify:Rfc2119Uppercase` — the configured extension fires via the existing rule, not as a separate rule.

**ClassificationSignal for prefix rules — two options:**

Prefix-based classification rules produce a `ClassificationSignal` value. The choice here affects downstream consumers of `ExtractionCandidate.ClassificationSignal`.

*Option A — Reuse the ScenarioKind-matching existing signal*: A prefix rule targeting REQUIREMENT uses `ClassificationSignal.FrPrefix`. A prefix rule targeting TEST uses `ClassificationSignal.BddPattern`. Semantically imprecise — the signal no longer accurately describes the mechanism that fired.

*Option B — New `ClassificationSignal.ConfiguredPrefix` value*: One new enum value, additive and non-breaking. Unambiguously identifies that this classification was produced by a configured prefix rule rather than by any default pattern rule. Consumers that switch on `ClassificationSignal` must handle the new value (or their catch-all handles it).

Option B is cleaner and avoids misleading signal semantics. It requires one additive change to the `ClassificationSignal` enum. All existing switch statements over this enum should be audited at implementation time to ensure the new value is handled.

Keyword additions to existing rule groups do not require any new signal value — they fire via the existing rule and carry the existing rule's signal.

**Privacy constraint (unchanged from US2/US3)**:  
No keyword value, prefix value, or any extraction-derived text may appear in any log field. All log fields are counts, durations, developer-assigned names, or opaque identifiers. This constraint applies equally to configuration-related log events: if the admin configures the keyword "REQUIRED", the string "REQUIRED" must not appear in any log output.

---

## R-US4-8. Security Risks from Malformed Configuration

**ReDoS via configured keyword patterns:**

When a user adds keyword "VERIFY", the engine builds a pattern incorporating `\bVERIFY\b`. `Regex.Escape("VERIFY")` returns `VERIFY` (no special chars; a no-op). The assembled pattern is safe.

The metacharacter prohibition (from R-US4-3) means `Regex.Escape` is always a no-op for any accepted keyword — all characters that would be transformed by `Regex.Escape` are rejected at validation time. This makes the escaping step a safety backstop, not the primary defense.

Even if a malicious or buggy keyword somehow bypassed validation and contained unescaped metacharacters, the 2,000-character per-line sub-cap (`MaxLineLengthForPatternMatching`, inherited from US3) bounds the worst-case input length any pattern sees. This limits catastrophic backtracking time even for imperfect patterns.

**Defense-in-depth layers for ReDoS:**

1. Metacharacter prohibition at validation (primary defense — prevents malformed patterns from being accepted).
2. `Regex.Escape` in the pattern assembler (belt-and-suspenders — escapes any character that slipped through validation).
3. Per-line sub-cap from US3 (bounds worst-case input size regardless of pattern quality).
4. All patterns compiled at startup (no runtime regex compilation — a failed compile at startup is caught before any extraction session).

**Configuration injection risk:**

In Blazor WASM, `wwwroot/appsettings.json` is a publicly served static file. A malicious client could read this file. The configuration must not include any information that would be sensitive if publicly visible — rule keywords and prefixes are not sensitive in themselves. If the vocabulary represents a confidential internal domain language, a server-side configuration delivery mechanism should be considered.

A malicious client cannot modify the configuration file. The application serves configuration read-only at startup; the file must be read-only to the application runtime process from an OS permissions standpoint.

**Startup-only compilation eliminates runtime attack surface:**

The rule configuration is compiled at application startup into an immutable `ExtractionRuleSet`. At extraction time, the `IExtractionRuleEngine.Evaluate()` method is a pure function operating only on its arguments and the compiled rule set. No configuration reads, no file reads, and no network calls occur during evaluation. This design means the configuration attack surface is bounded to startup time, not active for the lifetime of every extraction session.

**Word-boundary injection attempt (rejected by metacharacter check):**

A configuration author might attempt to supply a value like `"MUST\b"` (embedding a word-boundary assertion). The `\` character is a prohibited regex metacharacter; this value is rejected at validation time. No special handling is needed at the pattern assembly level.

**Empty group risk:**

If all keywords in a group are removed via the disabled rule toggle (the entire `Classify:Rfc2119Uppercase` rule is disabled), there is no pattern to evaluate — no regex risk, no empty-alternation problem. The rule is simply absent from the compiled rule set.

If keyword additions result in a pattern with a very large alternation (e.g., 50 added keywords + 6 default keywords = 56-branch alternation), the pattern complexity increases but the sub-cap ensures the worst-case evaluation time remains bounded. Empirically, a 56-branch alternation over a 2,000-character string using a compiled regex with word-boundary anchors evaluates in microseconds on modern hardware.

**Access control:**

SEC-US4-006 (spec) requires that configuration changes are authorized at the application or project-administrator level. For appsettings-based configuration (MVP), this is an operational concern: the deployment pipeline must ensure that only authorized persons can deploy configuration changes. This is not a code concern but must be documented as an operational constraint.

---

## R-US4-9. Performance Impact of Configurable Rules

**Keyword additions:**

Adding N keywords to an existing rule group extends the pattern's alternation from K branches to K + N branches. A compiled `Regex` with a longer alternation incurs slightly more startup compilation time and a marginally higher per-evaluation cost. For the proposed maximum of 50 additions distributed across 4 groups (~12 additions per group), the evaluation cost increase per candidate is measured in nanoseconds. Not a measurable overhead.

**Prefix-based classification rules:**

Each prefix rule is evaluated as a `string.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)` check against each candidate's stripped text. This is a simple O(prefix.Length) operation.

| Prefix rules | Candidates (typical) | Total comparisons | Expected overhead |
|---|---|---|---|
| 10 | 87 | 870 | Negligible (< 0.1 ms) |
| 50 (maximum) | 87 | 4,350 | Negligible (< 1 ms) |
| 50 (maximum) | 500 (dense document) | 25,000 | Measurable but < 5 ms |

**Enable/disable and priority overrides:**

Rules excluded by the disabled list are absent from the compiled rule set — zero runtime evaluation cost. Priority overrides reorder the rule list at compile time; the ordered list is precomputed at startup. Both mechanisms have zero evaluation-time overhead.

**Combined overhead analysis:**

Starting from the US3 baseline (16 rules × 87 candidates ≈ 1,300 evaluations, sub-millisecond):

- Maximum keyword additions: 4 extended patterns; 0 additional evaluations.
- Maximum prefix rules: 50 × 87 = 4,350 additional comparisons.
- Total evaluations for a maximally configured extraction: ~5,650.
- Expected extraction pipeline overhead: well below 1 ms.

The 200 ms extraction performance ceiling established in US2 and confirmed in US3 is not threatened by US4 configuration. The UI rendering bottleneck identified in R-US2-9 remains the dominant cost; extraction processing time remains negligible by comparison.

**Startup compilation time:**

Compiling the configured rule set at startup involves: deserializing the configuration, running validation, extending patterns with configured keywords, compiling the assembled regex patterns. For the proposed maximum configuration (50 keywords + 50 prefix rules + a handful of toggles), this adds approximately one additional regex compilation per rule group (4 compilations). Startup overhead is immeasurable in practice — this is well below even the browser console's measurement resolution.

---

## R-US4-10. Migration Strategy from Fully Internal Rules to Configurable Rules

US3 delivers `ExtractionRuleSet.Default()` as the only rule set factory. US4 needs a path from this baseline to a configured rule set without modifying the existing factory or the existing engine.

**Option A — Factory method extension on ExtractionRuleSet**

Add a static method `ExtractionRuleSet.Configured(ExtractionRuleConfiguration config)` alongside the existing `ExtractionRuleSet.Default()`. This method calls `Default()` internally and applies configuration modifications to produce a derived `ExtractionRuleSet`. The `IExtractionRuleEngine` and `ExtractionRuleEngine` are unchanged.

Pros: minimal new types; `ExtractionRuleSet.Default()` remains the canonical US3 baseline; the pattern is familiar.  
Cons: `ExtractionRuleSet` takes on responsibility for configuration application logic, which may grow large as the configuration surface expands.

**Option B — Separate configuration compiler class**

A new `ExtractionRuleSetCompiler` (or `ExtractionRuleSetFactory`) class accepts a base `ExtractionRuleSet` and an `ExtractionRuleConfiguration` and produces a configured `ExtractionRuleSet`. The compiler is the locus of all configuration application logic, validation, escaping, and pattern assembly.

Pros: clean separation of concerns — `ExtractionRuleSet` remains a pure data object; the compiler is independently testable; validation, escaping, and assembly are co-located.  
Cons: one additional type in the hierarchy.

**Option C — Configure ExtractionRuleEngine directly**

The `ExtractionRuleEngine` constructor accepts an optional `IExtractionRuleConfiguration` and applies configuration modifications internally.

Pros: fewer types; the engine owns the full pipeline.  
Cons: the engine is no longer a pure evaluator — it becomes responsible for configuration application, which mixes two distinct concerns; testing the engine becomes entangled with testing configuration compilation; this is the least testable option.

**Recommended direction**: Option B (separate compiler). The compilation step (default rules → configured rules + validation + escaping + pattern assembly) is sufficiently complex to deserve its own testable unit. The `ExtractionRuleEngine` should remain a pure evaluation machine with no knowledge of how its rule set was assembled.

**Backwards compatibility requirement**: `ExtractionRuleSet.Default()` must continue to work unchanged. All US3 tests that construct `ExtractionRuleEngine` with `ExtractionRuleSet.Default()` must pass without modification after US4 is introduced. This is the regression safety net for the US4 migration.

**DI composition root evolution (illustrative; not a code proposal):**

US3 current registration pattern:
```text
Services.AddSingleton<IExtractionRuleEngine>(sp =>
    new ExtractionRuleEngine(ExtractionRuleSet.Default(), config, logger));
```

US4 pattern (compiler intermediary):
```text
Services.AddSingleton<IExtractionRuleEngine>(sp => {
    var ruleConfig = sp.GetService<IOptions<ExtractionRuleConfiguration>>()?.Value;
    var ruleSet = ruleConfig is null
        ? ExtractionRuleSet.Default()
        : new ExtractionRuleSetCompiler(logger).Compile(ExtractionRuleSet.Default(), ruleConfig);
    // Compile() validates, falls back to Default() on failure, logs Warning on fallback
    return new ExtractionRuleEngine(ruleSet, extractionConfig, logger);
});
```

When no `ExtractionRuleConfiguration` section exists in configuration, `IOptions<ExtractionRuleConfiguration>` returns null or a default-valued object. The compiler treats an empty/null configuration as "no overrides" and returns `ExtractionRuleSet.Default()` directly — behavior is identical to US3.

**No parallel-running comparison mode is planned.** The rationale from US3 applies equally to US4: the full existing test suite (153 tests after US3 completion) is the regression safety net. If the configured rule set with no configuration applied passes all 153 tests, the migration is correct by construction.

---

## R-US4-11. Preserving Deterministic Guarantees Under Configuration

US3 establishes five determinism properties: no randomness, no external state during evaluation, stable candidate ordering, idempotency, and rule isolation. US4 must extend each of these.

**No randomness:**  
Configured keyword additions are compiled to regex alternation patterns at startup. Compiled `.NET Regex` pattern matching is deterministic. `string.StartsWith` is deterministic. Priority ordering is determined at startup from integer values. No randomness is introduced by any configurable element.

**No external state during evaluation:**  
The critical invariant: `IExtractionRuleEngine.Evaluate(block, strippedText)` may not read from any source other than its arguments and the compiled rule set. Configuration is consumed at startup by the `ExtractionRuleSetCompiler` to produce an immutable `ExtractionRuleSet`. The resulting rule set is compiled once and referenced thereafter. At evaluation time, there are no `IOptions<>` reads, no file reads, no network calls, and no shared mutable state. Configuration is baked into the rule set at compile time, not consulted at evaluation time.

**Stable candidate ordering:**  
Candidate ordering is determined by source position in the input text (established in Stage 3 Block Partitioning) and is independent of which rules fired. Configuration changes affect classification outcomes, not candidate ordering. The stable-ordering guarantee is preserved.

**Idempotency:**  
Running the same input through the same configured rule set twice produces identical output. The rule set is immutable after compilation. Configuration changes between two runs can produce different outputs — this is expected and desired — but two runs with the same configuration always produce identical outputs.

**Rule isolation:**  
Configured prefix rules evaluate a single `string.StartsWith` call and return a result. Configured keyword extensions operate on the same compiled regex as the base rule. Neither reads nor writes any shared mutable state. Rule isolation is preserved.

**Additional invariant specific to US4 — compiled rule set validity:**  
The `ExtractionRuleSetCompiler` must produce a rule set that satisfies all `ExtractionRuleEngine` startup validation checks: exactly one unconditional Default rule, no duplicate names, all patterns compile. If the compiler cannot produce a valid configured rule set, it must fall back to `ExtractionRuleSet.Default()` and log a Warning. The engine's startup validation must not be bypassed or weakened by the introduction of a compiler intermediary.

**Determinism with future project-level configuration:**  
If project-level configuration is introduced in a future version, the determinism property extends to: `(ruleSet(projectId, configVersion), inputText) → candidateList`. The output depends on the configuration at the time of the extraction. Two extraction sessions that use the same project configuration produce identical output. This is a weaker claim than application-wide determinism but is still meaningful and predictable. The key invariant — no randomness and no external state during a single evaluation — is preserved.

---

## R-US4-12. Open Questions for Planning Phase

The following questions are unresolved and must be addressed before planning begins. They represent the boundary between research and architecture decisions.

1. **Compiler vs. factory method**: Should the configuration application logic be a separate `ExtractionRuleSetCompiler` class (R-US4-10, Option B) or a factory method on `ExtractionRuleSet` (Option A)? The recommended direction is Option B, but the planning team should confirm this.

2. **ClassificationSignal for prefix rules**: Should prefix-based classification rules produce a new `ClassificationSignal.ConfiguredPrefix` value (R-US4-7, Option B) or inherit an existing signal? Choosing a new value requires auditing all `ClassificationSignal` switch statements in the codebase for exhaustiveness. Choosing an existing value (e.g., `FrPrefix` for REQUIREMENT targets) is faster but semantically imprecise.

3. **Prefix matching semantics**: Should prefix rules apply to stripped text via `string.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`, or should case-sensitivity be configurable per prefix rule? Case-insensitive is more forgiving; case-sensitive is more precise. The spec says "literal prefix" — planning should nail down the case behavior.

4. **Keyword additions vs. companion rule**: When keywords are added to an existing rule group, should the engine extend the existing rule's pattern (one rule matches both default and configured keywords), or add a companion rule at the same priority? The single-rule approach is simpler; the companion-rule approach makes it possible to independently disable the configured extension without affecting the default keywords. This is a design question with real implications for how `WinningRuleName` is reported.

5. **Configuration model shape**: Should `ExtractionRuleConfiguration` expose user-oriented properties (`BddKeywords: string[]`, `RequirementKeywords: string[]`, etc.) or rule-engine-oriented properties (`ClassificationRuleExtensions: [{ruleName, keywords}]`)? User-oriented is more intuitive for non-developers; rule-engine-oriented is more general. The choice affects how much mapping logic the compiler must perform.

6. **Configuration section naming**: What `appsettings.json` section name should hold the extraction rule configuration? Candidates: `ExtractionRules`, `BirkNext:ExtractionRules`, `ExtractionRuleConfiguration`. The name affects discoverability and the `IOptions<>` binding key.

7. **Prefix rule naming**: Configured prefix rules need a `Name` for `WinningRuleName` diagnostics and enable/disable support. Should names be auto-generated from the prefix (e.g., `Configure:Prefix:REQ-`) or must the configuration author supply an explicit name? Auto-generated names are convenient but may conflict with other auto-generated names for similar prefixes.

8. **Maximum priority for configured prefix rules**: Should new prefix-based classification rules default to priority 10 (below all default classification rules, serving as a last-resort before Default) or should the default be higher (e.g., 35, between FrPrefix at 40 and QuestionTerminator at 30)? The choice of default priority determines where prefix rules rank relative to default rules when no explicit priority is configured.

9. **Empty configuration section behavior**: If the `ExtractionRules` section exists in `appsettings.json` but contains no entries (all arrays empty, no overrides), should the compiler treat this identically to "no configuration section present" (use `ExtractionRuleSet.Default()` directly) or should it compile a configured set that is functionally equivalent to Default? The outcome is the same for users, but the code path differs and the startup log message would differ.

10. **WASM appsettings visibility**: Rule keywords and prefixes configured in `wwwroot/appsettings.json` are readable by any client. If the configured vocabulary is considered sensitive (confidential internal terminology), the application-level configuration delivery mechanism must be reconsidered — a server-side configuration endpoint behind authentication would be needed. Is the configured vocabulary for the MVP considered public information, or does it require protection?
