# BirkNext M2LB Test Data

Reusable markdown test data sets extracted from the uploaded M2LB repository. Each folder is a scenario/domain that can be loaded into BirkNext / QA Review Studio.

## Artifact relationship

```text
constitution.md
  ↓ governs
spec.md
  ↓ defines requirements
data-model.md
  ↓ defines persistent data/entities/tables
plan.md
  ↓ defines implementation approach
tasks.md
  ↓ defines execution work
contracts/*.md
  ↓ define APIs/events/integration contracts
checklists/*.md
  ↓ validate completeness/readiness
```

## Scenarios

| Scenario | Source | Best for |
|---|---|---|
| `person-adapter` | `PersonAdapter/specs/001-birk-person-adapter` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `hendelse-adapter` | `HendelseAdapter/specs/001-birk-hendelse-adapter` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `hendelsestjenesten` | `Hendelse/specs/001-hendelsestjenesten` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `tjeneste` | `Tjeneste/specs/001-tjenestemodul-m01` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `proxy` | `Proxy/specs/001-proxy-initial-setup` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `frontend-admin-panel` | `Frontend/specs/005-access-admin-panel` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `person-module` | `Person/specs/001-person-module` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `autorisasjon` | `Autorisasjon/specs/004-scim-user-sync` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |
| `revisjon` | `Revisjon/specs/001-revisjonstjenesten-m01` | Specification Review, Data Model Explorer, Plan Explorer, Task Explorer, Quality Review |

## Recommended manual test order

1. Open one scenario folder.
2. Load `spec.md` into Specification Review.
3. Load `data-model.md` into Data Model Explorer if present.
4. Load `plan.md` into Plan Explorer.
5. Load `tasks.md` into Task Explorer.
6. Load available artifacts into Quality Review and run selected review packs.
7. Use contracts/checklists for traceability and integration-readiness testing.

## Notes

- Original markdown content is preserved.
- Missing artifacts are not invented.
- Each scenario README lists included and missing common artifacts.
