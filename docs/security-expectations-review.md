# Security Expectations and Security Configuration Review

Two separate concepts live under System Settings → Target Environments → **Security Expectations**:

- **Security Expectations are the approved policy** of one Target Environment: authority, tenant, Client/Application IDs, redirect URLs, allowed backend/REST/GraphQL/CDN hosts and expected security headers. They are stored with the Target Environment profile and change only by an explicit person's action (manual edit or approval in the review).
- **Security Configuration Review is a deterministic source-consistency assessment.** It compares what the selected Source Analysis snapshot *declares* with the approved expectations for the selected environment and lists only what needs attention. It is not a runtime check and not a full security assessment.

Hard distinctions: a detected value is not an approved value; an approved value is not runtime-verified; consistent source declarations are not runtime protection; many evidence occurrences are not many issues; a dominant value is never approved automatically; an environment-specific or component-specific difference is not a conflict; a placeholder is not a production breach; a partial Source Analysis is not a failure; no declaration is not a missing value unless applicability says the value should exist.

## Page hierarchy

1. **Context** — reviewing environment (name and type), target URL, source archive and snapshot, Source Analysis state (neutral; "Partial" means absence of evidence is not proof of absence), related sources, approved value count. Actions: Review / Refresh review, Open Source Analysis, Change source.
2. **Assessment** — overall state and summary counts: Consistent, Needs review, Conflicts, Placeholders/defaults, Environment mismatches, Not assessed. Raw evidence counts are never a headline.
3. **Attention queue** — one item per check that needs review or has a conflict; selecting it moves focus to the card. Consistent checks are never listed.
4. **Filters** — Needs attention (default when something needs attention), All, Consistent, Approved, Not assessed.
5. **Review cards** — one per expectation: state badge (icon + text + screen-reader sentence), scope, approved values, number of logical values, source diversity, findings, then the logical value groups (per component scope for Client IDs). Long lists show 8 values and a "Show N more values" control.
6. **Evidence** — per value group, collapsed: occurrences grouped by component and file with config key, environment and applicability; technical details (snapshot, source role, evidence kind, confidence, candidate ids) behind a further disclosure.
7. **Assessment limitations** — only limitations that apply (runtime not verified; Source Analysis partial; environment files that do not map to the selected environment; unresolved component scope; historical snapshots).
8. **Technical evidence** — discovery provenance, decisions and diagnostics, collapsed.

## Candidate, logical value and evidence

- **Evidence occurrence** — one place in source: snapshot, component, file, line, config key, raw value, evidence kind, confidence. Exact duplicate extractions (same provenance) are removed; distinct files or components with the same value are kept.
- **Discovery candidate** — field + source-environment scope + normalized value (backend discovery, unchanged). Approve/ignore decisions are stored per candidate and remapped on refresh by field and normalized value.
- **Logical value group** — what the review shows: field + normalized value + scope, with its occurrences, components and files. One tenant in 18 files is one group with 18 occurrences, never 18 rows. Grouping and assessment are computed by `SecurityConfigurationReviewEngine` (shared contracts, compiled into backend and frontend like the existing `SecurityExpectationValues`); the page does not normalize or deduplicate.

## Expectation metadata (`SecurityExpectationMetadata`)

| Expectation | Value type | Cardinality | Scope |
| --- | --- | --- | --- |
| Authority | Authority URL | Single | Environment |
| Tenant | GUID or tenant domain | Single | Environment |
| Client / Application IDs | GUID or client identifier | Single per component | Environment + component client registration (component · configuration section, e.g. `Proxy.Api · AzureAd:Schemes:person`) |
| Redirect URLs | Redirect URI | Multiple | Environment |
| Backend domains, REST, GraphQL, CDN hosts | Host | Multiple | Environment |
| Security headers | Header name | Multiple | Listed only: header presence is a runtime property (Not assessed) |

Client IDs are approved per component scope (`scopedClientIds`). The legacy project-wide `expectedClientId` is kept unchanged. It is applied only when exactly one component scope applies; with several scopes it is reported as *Ambiguous scope* and never redistributed.

## Environment scope

The selected Target Environment type maps to an ASP.NET environment name: Local, Development, QA, Test, Production. RC and Custom have no mapping, so only shared configuration applies to them.

Each occurrence is classified by its file:

- `appsettings.json` and other non-environment files are **shared configuration**.
- `appsettings.{Name}.json` is that environment. Known names are canonicalized (`Qa` becomes `QA`). Any other suffix keeps its literal name: `appsettings.Dev.json` is the environment "Dev" and is never treated as Development.
- `launchSettings.json` and `.graphqlrc.json` are **design-time tooling**. They never configure a deployed environment.

Values that apply to the selected environment:

- the selected environment's file;
- shared configuration that is not overridden for the same component and key. This is file layering only; environment variables and Key Vault are not resolved.

A shared value overridden for the selected environment is shown as "Overridden", and other environments' values as "Other environment". None of these is a conflict. Wording stays "source configuration declares …", never "the environment uses …".

## Normalization (unchanged, `SecurityExpectationValues.Normalize`)

- **GUIDs** (tenant, client): trimmed and parsed. Case and braces don't matter. Invalid values remain invalid evidence and are never turned into the empty GUID.
- **Authority**: scheme and host are compared case-insensitively. Default ports are dropped and one final slash is ignored. Path and version are preserved, and http and https stay distinct.
- **Redirect URIs**: host case and default port only. Path case, trailing slash and query are preserved, so `/callback`, `/callback/` and `/callback?x=1` are three values.
- **Hosts**: lowercased, with the terminal dot removed. A non-default port is kept. A wildcard is kept and never merged with its parent domain.

## Placeholders

Only deterministic placeholders are detected:

- the all-zero GUID for tenant and Client IDs;
- `${NAME}`;
- `<name>`;
- `__NAME__`.

A placeholder that applies to the selected environment needs review. One that only appears in another environment's file is informational. Placeholders are never approvable.

## Findings (one per logical issue, never per occurrence)

| Finding | When | Effect |
| --- | --- | --- |
| ConfigurationConflict | A single-value scope has more than one applying value, or source declares a different value than the approved one | Conflict |
| UnapprovedValue | An applying value is not approved yet (multi-value: not in the approved list) | Needs review |
| PlaceholderValue | A deterministic placeholder applies to the selected environment | Needs review (informational when it applies to another environment only) |
| EnvironmentMismatch | An unapproved loopback host or redirect (localhost, 127.x, ::1) is declared for a non-Local environment | Needs review |
| MissingExpectedValue | An identity value is declared for other environments but not for the selected one or shared configuration | Needs review |
| StaleApprovedValue | An approved value is not declared for the selected environment in the selected source; the approval is kept | Needs review |
| AmbiguousScope | A legacy project-wide Client ID applies to several component scopes | Needs review |
| InvalidValue | An applying value has an invalid format | Needs review |

Check state:

- **Conflict** — a conflict finding exists.
- **Needs review** — any other attention finding exists.
- **Consistent** — every applying value is approved.
- **Not assessed** — there is no declaration, only other environments declare the value, or no approval exists to compare with.
- **Not applicable** — Tenant when the declared authority is not Microsoft Entra ID. A non-Entra project gets no tenant findings.

A project without identity configuration in source gets Not assessed, never "missing". No-auth is not inferred from absence.

The overall state is Conflict, then Needs review, then Consistent. A source that could not be assessed is Not assessed.

## Actions

| Action | Applies to | Effect |
| --- | --- | --- |
| Approve this value | Single-value expectations | Sets the approved value |
| Replace approved value | Single-value expectations that already have an approved value | Explicit replacement |
| Approve for this component | Client IDs | Sets the scoped approval for that component |
| Add to approved values | Multi-value expectations | Adds the value to the list; the list is never replaced |
| Ignore candidate | Any value | Removes the logical value from approval consideration. Source evidence is not deleted |
| Copy | Any valid value | Copies the value |

After an action:
- The summary, attention queue and card update in place.
- Focus returns to the card heading without scrolling.
- Nothing is approved automatically, and the bulk "accept all safe candidates" action was removed.

## Persistence and idempotence

- **Approvals** are stored on the Target Environment profile in browser storage, with provenance in `origins`: snapshot, fingerprint, candidate and scope.
- **Ignore decisions** are stored with the discovery record in the backend and remapped on refresh. Contradictory historical decisions show as a conflict.
- **Refreshing the same snapshot** gives the same logical values, evidence, findings and decisions. This is covered five times in tests.
- **Switching snapshot** gives a new review. A stored review for another snapshot is never shown as current.
- **Reset Local Data** clears the discoveries (database) and resets Target Environments to the generic seed, so no approvals remain.

## Integrations and limits

- Quality Review and the Dashboard do not consume Security Expectations; no integration was added.
- Frontend Quality Review still reads the approved hosts as before.
- Runtime effective configuration, deployed environment variables and Key Vault values are not verified.

## Verification

- `SecurityConfigurationReviewTests` (backend) cover:
  - normalization reuse and placeholders;
  - environment layering and custom environment names;
  - component-scoped Client IDs, the legacy approval and conflicts;
  - unapproved, stale and missing values, and environment mismatch;
  - applicability for non-Entra and no-identity projects;
  - partial Source Analysis, no source and another snapshot;
  - ignore, determinism ×5, a large project (500 occurrences), scoped approval;
  - an end-to-end analyzer → discovery → review run with five refreshes.
- `SecurityExpectationsPanelTests` (frontend) cover:
  - the page hierarchy and the empty state;
  - the attention queue and filters;
  - each assessment state;
  - single-value, multi-value and per-component actions;
  - collapsed evidence;
  - redaction, edit mode and pending-change safety;
  - a large project and accessibility semantics.
