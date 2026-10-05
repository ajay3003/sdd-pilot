# Security Expectations review

Security Expectations compares approved Target Environment settings with explicit Source Analysis evidence. A detected source value is a candidate only; it is not approved until a person accepts it. Confidence describes evidence quality and does not grant approval.

## Candidates and evidence

A logical candidate is keyed by its expectation field, normalized semantic value, and recognized source-environment scope inside one discovery scope. The discovery scope is one selected Target Environment, one primary Source Analysis snapshot, and any explicitly included related source snapshots. The same value in another file or selected related source is supporting evidence for that candidate when it has the same source-environment scope. Evidence occurrences remain separate when their snapshot, source, environment, file, line, config key, evidence kind, or raw value differs. An exact repeated extraction at the same provenance is collapsed.

The candidate display uses its normalized value. Evidence retains each original raw value and location. Candidate IDs depend on the field and normalized value, not the number or ordering of supporting files, so refreshes can retain review decisions. On refresh, decisions are matched from the prior discovery to the current candidate by field and normalized value. If equivalent historical candidate records contain contradictory decisions, the candidate is shown as a conflict and requires an explicit review action.

## Normalization

Normalization is field-specific. Tenant IDs and GUID-shaped client IDs use canonical GUID formatting. Authority URLs compare scheme and host without case, omit default ports, preserve path case and path segments, and ignore a final slash. Redirect URLs preserve path case, trailing slash, and query text; fragments are rejected. Host expectations compare DNS names case-insensitively, remove a DNS terminal dot, and retain non-default ports. A leading `*.` remains part of a wildcard host candidate and never merges with its base domain. Safe malformed values are retained as `InvalidFormat` review-only candidates; recognized placeholders are marked `Placeholder`. Credential-shaped, empty, control-character, and oversized values are omitted. Quoted scalar values are parsed before comparison; raw evidence remains unchanged. Invalid and placeholder candidates cannot be approved until the source is corrected and analyzed again.

Source Analysis does not infer which appsettings file wins runtime configuration precedence. `appsettings.Development.json`, `appsettings.Local.json`, `appsettings.Test.json`, `appsettings.QA.json`, `appsettings.Staging.json`, and `appsettings.Production.json` set an explicit source-environment scope. Values from different recognized scopes remain separate candidates even when normalized values match; the UI labels that scope and preserves each file as provenance. Unknown custom suffixes remain unclassified and are not guessed from file names. The selected Target Environment and included source snapshots scope the discovery independently.

## Review workflow and provenance

Review proceeds from expectation to normalized candidate to collapsed supporting evidence. Evidence starts collapsed and can be expanded for raw values and locations. Snapshot IDs and fingerprints are available in discovery details instead of being repeated in every row. Approving a candidate changes only the selected Target Environment's approved security expectations and records source provenance. Ignoring a candidate changes its review state, not the approved value.

The security expectation source analyzer version advances when extraction and normalization behavior changes, so re-analysis records the new projection. Refresh is safe to repeat without increasing the candidate or evidence counts. It preserves a prior approval/ignore decision when its logical candidate remains in the same primary and related snapshot scope. A changed snapshot is a distinct review scope; historical evidence remains attached to its own discovery record.
