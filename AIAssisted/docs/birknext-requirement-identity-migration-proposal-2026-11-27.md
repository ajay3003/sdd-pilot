# BirkNext — Requirement identity: proposal for a source-aware canonical identity (2026-11-27)

**Status: PROPOSAL — REQUIRES DECISION. Nothing in this document is implemented.** No stored identity, plan, case, decision or execution
record has been changed. Decision 4 of 2026-11-27: «no automatic or destructive migration … report the proposal before implementing any persistent
identity migration».

## 1. The problem (evidence)
- A requirement **without** an acceptance scenario becomes a discovery candidate with
  `CanonicalKey = "requirement:{module}|{id}"` (`TestScenarioDiscoveryEngine.cs`, requirement loop). Its `SourceIdentity` is derived from that key
  (`SourceIdentityOf(capability, CanonicalKey)`). **It contains no file.**
- When the module is empty or the same for several documents, every `FR-001` gets **one** source identity.
  - M2LB: **66 source identities occur in more than one document, up to 39 documents** (project-level audit, same date).
- Since 2026-11-26, `ScopeCollidingSpecificationKeys` gives colliding candidates a file-scoped **candidate id**. By design it never changes the **source
  identity**, so existing links keep working.
- Acceptance scenarios are **not** affected: their source identity is already file- and situation-scoped (`situation:{file}|{given}|{when}`).
  Documented tests (`testspec:<document>#TEST-E-01`) are not affected either.

## 2. Impact today (checked, read-only)
| Consumer | Keyed by | Affected? |
|---|---|---|
| Testoversikt module table and coverage | counted per document, summed | **No**: nothing is lost or double-counted. Since 2026-11-27 requirements are a separate column. |
| Drill-down within one specification | one discovery | **No** |
| Preparation and readiness decisions | stored per document | **No** |
| Saved test case → scenario («har testtilfelle», `CoveredKeys`) | source identity | **Possible**: a case for `FR-001` in document A can mark `FR-001` in document B as covered. |
| Plan items, revision diff, discovery content comparison | candidate id / source identity | **Possible**, for requirement-only items only |
| Traceability and ADO export | requirement id / source identity | **To be inventoried** before implementation |

The «possible» rows have not been observed as defects in M2LB. Today they are a latent risk, limited to requirements without an acceptance scenario.

## 3. Proposed canonical identity
1. **Source identity v2 for requirements without a scenario:** `requirement:{normalized specification file}|{normalized requirement id}`. It is
   versioned (`srcid/2`) so a v1 and a v2 value can never be confused.
2. **Relationship to the existing requirement identity review (`RequirementIdentity`, identity/1).** That review maps an unscoped id to a reviewed,
   feature-pack-scoped key. It answers *which feature* a requirement belongs to; v2 answers *which document* it came from. They do not compete:
   - when identity/1 is **Confirmed**, the confirmed scoped key is the canonical requirement;
   - otherwise the file-scoped v2 source identity is canonical;
   - nothing is ever inferred from a name, a text or a module guess. This is the same rule identity/1 already follows.
3. **Unchanged:** acceptance scenarios, documented tests, UI and flow candidates, candidate ids without a collision.

## 4. Backward compatibility
- **Read both, write v2 only for new records.** A record that carries v1 is resolved at read time.
  - If the v1 value maps to **exactly one** v2 value in the record's own document (a case, plan item or decision knows its specification path),
    resolve to it.
  - If it maps to several, or the record has no document, the record shows **«Må vurderes»** with the candidate documents. The system never guesses.
- An alias table (v1 → v2 per document) is **computed**, not stored. It can be rebuilt from the current sources at any time.
- APIs keep returning the v1 field. v2 is an **additional** field, so older clients and exports are unaffected.

## 5. Migration (only after a decision)
1. **Dry run (read-only):** for each project, a report listing:
   - each v1 requirement identity used by a case, plan item, decision or export record;
   - its v2 resolution: one / several / none;
   - the exact records concerned.
   Nothing is written.
2. **Backup:** the existing local backup mechanism, taken before any write and kept verbatim.
3. **Additive write, behind a setting (off by default):**
   - write `SourceIdentityV2` next to the unchanged v1 value, only where the dry run found exactly one resolution;
   - ambiguous records are listed for a person and are not written;
   - the migration is idempotent and can be re-run.
4. **No deletion** of v1 values in this migration. Removing v1 is a separate, later decision.

## 6. Rollback
- Switching the setting off restores v1 behaviour immediately, because v1 values are never changed.
- A full rollback removes the v2 fields, or restores the backup from step 2. Execution history is never touched in either case.

## 7. Tests required before implementation
- **v2 identity:** `FR-001` in two documents of the same module, and in two documents without a module, gives two v2 identities. The same document
  analysed twice gives the same v2 identity.
- **Unchanged:** acceptance scenarios, documented tests and non-colliding candidate ids are byte-identical before and after.
- **Read-time resolution:**
  - one match → resolved;
  - several → «Må vurderes»;
  - no document → «Må vurderes»;
  - never a guessed match.
- **Coverage:** a case for `FR-001` in A no longer marks `FR-001` in B as covered under v2. Under v1 (setting off) the behaviour is unchanged.
- **identity/1:** a confirmed scoped key wins over v2; Candidate, Ambiguous and Rejected never resolve.
- **Migration:**
  - the dry run writes nothing;
  - running it twice gives the same result;
  - the backup is byte-identical to the data before the migration;
  - after rollback, data equals the backup;
  - plans, cases, approvals and execution history counts are unchanged.
- **Real data:** M2LB in an isolated instance. The 66 shared identities resolve as the dry run predicts.

## 8. Decisions needed
1. Approve v2 as defined in §3, file-scoped for requirement-only candidates only.
2. Approve the read-both / additive approach and the dry-run report as the first deliverable.
3. Decide whether coverage under v2 (§7, «Coverage») becomes the default once the migration has run, or stays behind the setting until a later release.
