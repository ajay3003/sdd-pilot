# BirkNext status vocabulary

Generated from `frontend/BirkNext.Web/Services/StatusVocabulary.cs` — edit the code, then regenerate (a test fails when this file drifts).

Same term → same fundamental meaning in every feature. Features differ only in their prerequisites (Ready for AQR and Ready for Performance
need different inputs, but Ready always means *all required prerequisites for this activity are satisfied*).

## Before adding a status

1. Check this dictionary. 2. Reuse a term only if its meaning matches exactly. 3. Never reuse a term with a different meaning.
4. Add a new term only for genuinely distinct semantics. 5. Keep status apart from type, provenance, environment, confidence and counts —
those use `MetaBadge` (`BadgeKind`), never `SemanticStatusBadge`. Unknown or legacy values map to *Unknown* (neutral), never to Ready or a warning.

Intents map once to design tokens: Positive → success, Info → info, Warning → warning, Danger → danger, Active → accent, Neutral → muted.
Strong colour is reserved for decision-relevant states; red only for Blocked, Conflict, Failed, Invalid (and their failure variants).

## Evidence

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Detected | Evidence was found. | Something exists in the analysed source or capture. | Implying correct, approved, complete or runtime verified. | Info | ◉ |
| Confirmed | Explicitly established by authoritative evidence or an explicit reviewed association. | An explicit declaration or a person's confirmation exists. | Runtime health or verification (unless the evidence itself is runtime evidence). | Positive | ✓ |
| Inferred | Derived defensibly from available evidence, not declared. | A deterministic but indirect relation. | Ambiguity (use Needs review) or manual approval. | Info | ⇢ |
| Unknown | No defensible state can be established. | Nothing more precise applies (also the safe fallback for unrecognised values). | A more precise state exists. | Neutral | ? |
| Unresolved | A reference exists but its target could not be resolved. | A stated link points at something not found or ambiguous. | Missing (nothing is referenced) or Conflict. | Warning | ? |

## Assessment

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Not assessed | The assessment has not been performed. | Nothing has evaluated this yet. | While loading (use Assessing) or after a failure (use Failed to assess). | Neutral | ○ |
| Warning | A non-blocking concern was found. | The result is usable but something deserves attention. | Blocked (it stops work) or Needs review (a decision is required). | Warning | ⚠ |
| Assessing | The assessment is loading or computing. | Work is in progress. | Showing a previous result that is being refreshed (keep it, add a refreshing hint). | Active | ⟳ |
| Ready | All required prerequisites for this specific activity are satisfied. | The activity can start; prerequisites differ per feature. | Lifecycle, presence, configuration alone, or one sub-dimension while the whole is blocked (name the scope). | Positive | ✓ |
| Ready with limitations | The activity can proceed; known limitations remain that do not block its purpose. | Non-blocking limitations are listed. | A required prerequisite is missing. | Positive | ✓ |
| Needs review | A specific unresolved decision requires human assessment. | A conflict, ambiguity or explicit confirmation exists. | Raw analyzer limitations, unresolved-by-source counts or heuristic parsing that need no decision. | Warning | ! |
| Blocked | A required prerequisite prevents the activity from proceeding. | Something required is missing or refused. | Optional data is missing. | Danger | ⛔ |
| Failed to assess | The assessment itself failed technically or could not complete. | An analyzer or request failed. | The assessed thing is merely blocked or needs review. | Danger | ✕ |

## Configuration

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Available | It exists and can be used. | A source, asset or capability is present and usable. | Ready (all prerequisites met) or verified. | Info | ◇ |
| Missing | No configuration exists at the owning scope. | The value was never configured. | It is required right now (the consumer says Blocked). | Neutral | ○ |
| Configured | A usable configuration value exists at its owning scope. | The setting is present. | Implying approved, runtime verified, execution-compatible or healthy. | Info | ⚙ |
| Invalid | Configuration or content exists but is invalid for its declared purpose. | Validation found it unusable. | It is merely unverified. | Danger | ✕ |
| Disabled | Explicitly disabled by configuration or a person's choice. | Turned off on purpose. | It is unavailable for a technical reason. | Neutral | ⊘ |

## Verification

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Not verified | No runtime or explicit verification has been performed. | Only source or configuration evidence exists. | Verification failed. | Neutral | ○ |
| Verifying | Verification is running. | A verification is in progress. | Analysis that is not verification. | Active | ⟳ |
| Verified | Runtime or explicit verification succeeded. | A verification was actually performed. | Source declaration, inference, configuration presence or static parsing. | Positive | ✓ |
| Verification failed | Verification ran and did not succeed. | A verification result was negative. | Verification could not run (use Unavailable). | Danger | ✕ |
| Unavailable | The capability or source needed is not available right now. | A tool, backend or runtime is unavailable. | A negative result. | Neutral | ⊘ |

## Lifecycle

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Draft | Editable, not yet validated or approved. | Work in progress. | Readiness. | Neutral | ✎ |
| Validated | Passed its type-specific validation. | A real validation ran. | Mere existence. | Positive | ✓ |
| Approved | Explicit approval has been recorded. | A person or policy approved it. | Configuration presence or verification. | Positive | ✓ |
| Archived | Kept for history, no longer active. | Retired but retained. | Deleted or failed. | Neutral | ▤ |

## Coverage

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Inventory complete | All supported inventory items were discovered; this does not confirm runtime quality or successful execution. | Discovery is complete (tests, dependencies…). | Quality, execution or pass/fail completeness. | Info | ✓ |
| Source model available | A model was built from source. | Architecture/data model extracted. | Runtime verification. | Positive | ✓ |
| Classification incomplete | Everything was read; some items could not be categorised. | A classification gap that may need action. | Defects or human decisions. | Warning | ◐ |
| Partial | A defined coverage dimension is only partly satisfied. | A genuine whole/part relationship. | Source limitations, heuristic parsing, unknown runtime state, review needs or not-applicable evidence. | Warning | ◑ |
| Limited by source | Constrained by what the source, tool or scope can establish. | An analyzer limitation. | A human decision is required. | Neutral | ◌ |
| Heuristic | Read with pattern-based parsing, not a structured parser. | Describing the parse approach. | Indicating a defect or a review need. | Info | ≈ |
| Not applicable | This dimension is not relevant to the selected source or operation. | Out of scope by design. | Not yet assessed, or missing (use Missing). | Neutral | – |

## Discovery

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Not detected | No evidence was detected within the stated scope or snapshot. | Absence in a named scope (say which). | It does not exist globally, is missing, or needs review. | Neutral | ○ |

## Freshness

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Current | The latest version. | Marking the current snapshot/version. | Quality. | Info | ● |
| Stale | Previously valid evidence is outdated. | Inputs changed since it was produced. | Failure. | Warning | ↻ |
| Historical | A past version, kept for history. | Temporal state. | Low confidence. | Neutral | ⟲ |
| Live | Observed now. | Live runtime observation. | Quality pass. | Active | ● |
| Changed | Different from the compared version. | Comparisons. | Quality. | Info | Δ |
| New | Not present in the compared version. | Comparisons. | Quality. | Info | + |
| Removed | No longer present. | Comparisons and removed references. | Failure. | Neutral | − |

## Outcome

| Term | Meaning | Use when | Do not use when | Intent | Icon |
|---|---|---|---|---|---|
| Not run | The operation has not been executed. | A run, test or check that could be started has not been. | Failed (nothing failed) or Not assessed (an assessment, not a run). | Neutral | ○ |
| Completed | The operation ran to completion. | An operation finished (its own result is stated separately). | Quality passed or tests passed. | Positive | ✓ |
| Failed | The operation itself failed. | Prefer a specific label (Import failed, Validation failed). | A negative assessment result. | Danger | ✕ |
| Conflict | Two or more authoritative evidence sources disagree. | Deterministic disagreement. | Missing evidence, unknown or a parser limitation. | Danger | ⚡ |
| Imported | Imported into BirkNext. | An import completed. | Reviewed, approved or tested. | Info | ↓ |
| Cancelled | The operation was stopped before it finished. | A person or the system cancelled a run. | Failed (nothing failed) or Blocked. | Neutral | ⊘ |

