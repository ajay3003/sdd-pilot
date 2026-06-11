# Bug Fix Tasks: Security and Audit Review

- [ ] T001 Fix Kode 7 search visibility bug — ensure children with Kode 7 are completely invisible in search results for users without `Person:SeGradertBarn` (FR-003, SC-003)

- [ ] T002 Fix authorization failure handling — ensure requests fail closed with HTTP 503 when Authorisation service is unavailable (FR-031)

- [ ] T003 Fix audit event publication — ensure all access grant and revocation actions publish immutable audit events (FR-016, FR-028, SC-004)

- [ ] T004 Fix Service Bus event publishing — ensure domain events are published with SessionId and no personal data in payload (FR-025, FR-026, FR-027)

- [ ] T005 Refactor helper method names in BarnSearchService — no behavior change

- [ ] T006 Add export-to-Excel button for child search results