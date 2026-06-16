# Contract: CandidateReviewStatus GraphQL Enum

**Contract type**: GraphQL enum (additive change)
**Version**: v1 → v1.1 (backward compatible)

## Change

Adding `AutoAccepted` to the `CandidateReviewStatus` enum.

## Before

```graphql
enum CandidateReviewStatus {
  NEW
  ACCEPTED
  REJECTED
  NEEDS_REVIEW
}
```

## After

```graphql
enum CandidateReviewStatus {
  NEW
  AUTO_ACCEPTED
  ACCEPTED
  REJECTED
  NEEDS_REVIEW
}
```

## Compatibility

- Additive change only. Existing clients that do not know `AUTO_ACCEPTED` will receive an unknown enum value if they query a record with that status — they must handle unknown enum values gracefully (treat as `NEW`/Unreviewed).
- The backend (`BirkNext.Api.Models.CandidateReviewStatus`) and frontend (`BirkNext.Web.GraphQL.CandidateReviewStatus`) must be updated in the same PR.
- `SaveReviewedCandidatesInput` already accepts `CandidateReviewStatus` — `AutoAccepted` becomes a valid input value for auto-persist calls.

## Migration

No migration needed. Existing rows in `reviewed_candidates` use string storage; `AutoAccepted` is a new value for new rows only.
