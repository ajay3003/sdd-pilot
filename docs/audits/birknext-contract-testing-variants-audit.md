# Contract Testing — source variants and logical API grouping, part 1 (2026-11-13)

## Verified implementation

- **Discovery.** `SourceContractCandidate` (`shared/ContractTestingContracts.cs`) lists each REST/GraphQL file that a Source Analysis
  snapshot found, with its module, state (New / UpToDate / Changed / Archived / Moved / NeedsReview), registry continuation (`ContractKey`,
  version), identity classification, and relocation `Candidates` with evidence.
- **Identity.** The **existing registry identity rules** decide continuation. NeedsReview cases go through the existing identity review.
  No second registry was added.
- **Grouping.** The import view already grouped files by module.

## Implemented (presentation over the existing discovery)

- Within each module, files are grouped into **candidate logical APIs** by protocol ("SkoleService · GraphQL API — candidate logical
  API"). This is a proposal, not a confirmed identity.
- Each file shows the **role its location suggests**: generated documentation, specification, test snapshot, client schema, schema
  extension, or source. It is explicitly labelled "(by location)", because a path alone is not proof of role.
- A **GraphQL file with no root operations** is marked as a fragment or extension of a parent schema. It is not called invalid, and it is
  not presented as standalone-testable.
- **One summary over the listed files:** files, candidate groups, REST and GraphQL, already imported, new or changed, needing identity
  review, and fragments. Each file is counted once.
- User Guide updated.

**Not claimed:** exact-duplicate or structural-equivalence detection. The candidate model carries no content or structural fingerprint for
the listed files, so equivalence is not shown.

## Tests

`ContractSourceVariantsTests` uses the real M2LB-shaped paths from the brief. It covers the role per location, fragment detection (REST
is never a fragment), and the summary counts. The contract-related focused suites passed 199/199.

## Not done (rest of the brief)

- Content or structural fingerprints and equivalence.
- Import preview with three import options.
- The four-view navigation: Contracts / Generated Tests / Readiness / Execution & Results.
- Revision drift classification.
- Readiness model breakdown.
- Browser acceptance with a local mock server.
