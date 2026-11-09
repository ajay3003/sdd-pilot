# BirkNext — Integration Suite Generator + Kode 6/7: acceptance (2026-11-09)

Branch `008-traceability-first`. Initial HEAD `59299e42`. Commits of this work: `d9426f10` (generator), `0a909fef` (Contract Testing
whole-body negatives), `2f4d73f2` (fixes from acceptance). Reset Data fix `fca0ce04` is reported separately
(`docs/hardening/birknext-reset-data-failure.md`).

**Result: 68/68 PASS** on a clean isolated stack (API 48/48 · after restart 4/4 · browser 16/16), after three acceptance rounds that found and
fixed five product defects (below). Earlier rounds: 12 FAIL → 6 FAIL → 3 FAIL → 1 FAIL → 0 FAIL.

## Setup (scratch only)

- Real BirkNext API (scratch tree = HEAD + this change), port 5098, fresh database `birknext_its_20261109` created for each round, content
  root without `appsettings.Local.json`, outbound proxies pointed at a dead port. Published (Release) frontend on 5198; Edge headless with every
  non-localhost request blocked. The running stack on 5000/5173 was not used or touched.
- Archive **`AIAssisted/testdata/M2LB.zip`**, SHA-256 `22339207cd2ef2f75ea418f66037c2ccacd8df7aad3cd89ddf73bd2a439c28f0`, 2,361 files
  (read-only; imported through the real Project Import endpoints).
- **Independent oracle** `its/oracle.py` — standard-library Python reading the ZIP in memory (no BirkNext code): scope modules present/missing,
  OpenAPI operations per file (indentation reader), GraphQL files, XSD target namespace / root elements / import resolution, Kode 6/7 mentions.
- **Synthetic controls** `its/make_synth.py` — `ward-synthetic.zip` v1 and v2 (same name, one extra operation), an identity-shaped canary
  `01017099999` in an OpenAPI example; expectations declared in the generator.
- **Independent ledger** — a loopback target on 127.0.0.1:5099 recording every request it receives.

## Real M2LB archive (Skolenærvær scope)

| Check | Oracle | BirkNext | Result |
|---|---|---|---|
| Archive identity | `22339207…` | preview + suite provenance `22339207…` | PASS |
| Scope modules present | SkoleService, MeldingsutvekslerReceiver, Person, Tjeneste | same | PASS |
| Scope modules missing | Skole, Meldingsutvekslermottak | "Skole/ is not in this archive", "Meldingsutvekslermottak/ …" | PASS |
| OpenAPI files / operations | 6 files, 44 operations | 6 contracts imported, 44 positive tests (one per operation, none unsupported) | PASS (record level, table below) |
| GraphQL schema files | 9 | 9 `GraphQlSchema` contracts | PASS |
| XSDs | 6 (namespaces, roots, imports resolvable) | 6, namespaces and roots equal, all dependencies resolved | PASS |
| Contracts in scope only | — | 15 of 15 from scope modules | PASS |

| OpenAPI file | Oracle ops | Positive tests | Contract |
|---|---|---|---|
| `MeldingsutvekslerReceiver/autodoc/openapi.yaml` | 4 | 4 | `m2lb-meldingsutvekslerreceiver-operations-endpoints@v1` |
| `Person/autodoc/openapi.yaml` | 10 | 10 | `m2lb-person-api@v1` |
| `SkoleService/autodoc/openapi.yaml` | 17 | 17 | `m2lb-skoleservice@v1` |
| `SkoleService/specs/001-skoleservice/contracts/rapportinntak.openapi.yaml` | 1 | 1 | `skoleservice-report-intake-…@v1` |
| `SkoleService/specs/001-skoleservice/contracts/utdanning-innmating.openapi.yaml` | 8 | 8 | `skoleservice-utdanning-ingestion@v1` |
| `Tjeneste/autodoc/openapi.yaml` | 4 | 4 | `m2lb-tjeneste-api@v1` |

Generated suite (revision 1): **560 test definitions** (90 positive, 470 negative proposals — e.g. 115 null-not-allowed, 109 invalid type,
60 empty value), **15 contracts**, **73 test-data items** (55 synthetic drafts, 8 operations without inputs, 8 unresolved journey inputs,
2 resolved), **7 stages**, **19 security scenarios**, **86 review items**. Status **Needs review**; outcome **Not executed**.

Kode 6/7: 7 existing M01 requirements reused (`classification-propagation`, `unauthorized-access`, `authorized-graded-access`,
`identity-references`, `test-context`, `protected-downstream-handling`, `bufdata-exclusion`) — all *waiting for a prerequisite* (no M01 review
run in this database); K67-01/02/04/05/10/11 **need a domain decision** (intake rule; aggregate Bufdata figures; corrections;
reclassification); K67-03/06/07/09/12 **not supported** (no executor/observer); K67-08 **can run now** (BirkNext's own redaction only).
K67-01/02 link E2E-05, K67-05 links DATA-03.

Flow: Altinn *executable* (blocked: no Target Environment); MU, MM, Skole intake, Person/Tjeneste, period lock, Bufdata *unsupported* in this
installation (no registered adapter/observer) — the Bufdata stage is never reported as executable and observable.

| Ids | Result | Checks |
|---|---|---|
| IA-01…09 | PASS | identity, snapshot, scope, draft revision 1, nothing sent on generation, modules, contracts, XSDs |
| IA-R-* (6), IA-X-* (6) | PASS | per OpenAPI file and per XSD reconciliation with the oracle |
| IA-10…15 | PASS | K67-01…12 with triage; M01 reused not duplicated; waiting vs decision kept apart; prerequisites and questions named; aggregate figures = domain decision; no K67 Ready without an executed review (except K67-08) |
| IA-16…21 | PASS | seven stages in order; Bufdata not executable+observable; not Ready to execute / Not executed / no runs; 12 readiness dimensions; no identity-shaped generated value; every test triaged with gaps |
| IA-22…24 | PASS | regeneration reuses (same id, same tests); review decision stored; execution without authorisation refused, nothing sent |

## Synthetic controls (independent ledger)

| Ids | Result | Checks |
|---|---|---|
| IS-01 | PASS | one positive test per declared operation (`GET /children`, `GET /health`) |
| IS-02/03 | PASS | the identity canary never appears in the suite or generated data; `fodselsnummer` is not synthesised |
| IS-04…06 | PASS | authorised run of a ready test → **exactly `GET /health` on the ledger**; run linked; state "1 notassessed of 1" (source-backed contract cannot assess every schema keyword) → outcome *Executed*, **never Verified** |
| IS-07/08 | PASS | a test outside the suite is refused; a test waiting for a prerequisite is not sent |
| IS-09…11 | PASS | v2 snapshot of the same project = revision 2 of the same suite; drift "contract v1 → v2 … stay on v1" + "Source snapshot changed"; new operation is a new test; existing tests pinned to v1 |
| IS-12 | PASS | nothing but the one authorised request reached the target during the whole phase |

## After restart

| Ids | Result | Checks |
|---|---|---|
| IR-01…04 | PASS | review decision kept; regeneration reuses (no duplicates); run link and its evidence state kept; exactly three revisions listed |

## Browser (Edge headless; 1440, 820 and 390 px)

| Ids | Result | Checks |
|---|---|---|
| IB-01/02 | PASS | snapshot + scope pickers; Generate disabled until a snapshot is chosen; archive and SHA-256 prefix shown |
| IB-03…06 | PASS | regenerate from the UI reuses the suite; status/outcome; three outcome totals; missing scope modules |
| IB-07…10 | PASS | Kode 6/7 grouped by triage with prerequisites and the aggregate question; 6 XSDs with Schema Studio links; only ready tests selectable; the API-recorded review decision shown |
| IB-11 | PASS | execution needs a target, a selected ready test and explicit authorisation (a fresh browser has the seeded Target Environment active; the run stays disabled) |
| IB-12…14 | PASS | no page-level horizontal scroll on any tab at 1440, 820 and 390 px |
| IB-15, IB-Z1 | PASS | tabs keyboard reachable; no external request, no page error, no console error |

## Defects found by acceptance (fixed, with tests)

| Id | Found by | Defect | Fix / commit |
|---|---|---|---|
| ATG-DEF-5 | IA-04 (HTTP 500) | Contract Testing negative proposals threw `JsonReaderException` on a whole-body placeholder (`{{dataset.body}}`) — also broke *Propose negatives* in Contract Testing | variant replaces the whole body; regression test red first — `0a909fef` |
| ATG-DEF-6 | IA-22 | regeneration made new revisions: unstable test order; "already exists" recorded as unsupported; review decisions in the fingerprint | stable order, filter, decisions as live overlay — `2f4d73f2` |
| ATG-DEF-7 | IR-02 | Kode 6/7 review keys were a per-process string hash (changed on restart; a decision never answered its question) | stable scenario review key; unit test — `2f4d73f2` |
| ATG-DEF-8 | IA-11/12 | M01 requirements were not read without a Target Environment | always read the consumer (environment-independent state) — `2f4d73f2` |
| ATG-DEF-9 | IR-03 | a running call already counted as *Executed* | unfinished calls ignored in the outcome — `2f4d73f2` |
| UI-ATG-1/2 | IB-14 | 390 px page scroll (selects; long badges/paths on the XML tab) | wrapping/width rules — `2f4d73f2` |

Harness corrections (scratch only): v2 synthetic archive must keep the project's file name; wait for a terminal run state; a fresh browser has
the seeded Target Environment active.

## Security and evidence

- No real personal data: generated values are `synthetic-*`, seeded numbers/uuids/dates; identity-shaped fields refused (IA-20, IS-02/03).
- No secrets in definitions: authentication headers are never synthesised; credentials are resolved only by Contract Testing at run time.
- No unauthorised execution (IA-24, IS-07/08, IB-11); nothing sent on generation (IA-05, IS-12); no external traffic (proxies dead, IB-Z1).
- No duplicates (IA-22, IR-02, IB-03); provenance on every suite (snapshot, SHA-256, contract `key@vN`); no false PASS (NotAssessed shown as
  such); no false VERIFIED (IS-06, IA-17); uncertain Kode 6/7 aggregation kept as a domain decision (IA-14); recorded review decisions survive
  regeneration and restart (IR-01, unit test).

## Regression

See the gap register for the final counts of the full backend (six partitions) and frontend regression and the Release build.
