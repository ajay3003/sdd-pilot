# Skolenærvær TT02 — setup for the first unattended run

Test-lead guide for preparing the **first supervised, unattended Skolenærvær run against Altinn TT02** on a local BirkNext.
Everything is done in the BirkNext UI — no terminal, no PowerShell, no environment variables. Following it makes **no** real
ID-porten, Altinn or Storage call. The real run is a separate, explicitly approved step (see [Stop](#stop)).

Never put a real secret, TestID identifier or child identifier in this file, in `appsettings*.json` (including the **tracked**
`appsettings.Local.json`), in frontend configuration or in a committed `.env`.

## Configure once: Target Environments → Altinn

The Altinn environment is configured once per Target Environment and consumed by Skolenærvær Testing (and IQR). Open
**Target Environments → select the environment → Altinn**.

1. **Detect Altinn settings.** A discovery action only: it reads the Target Environment's saved values, the backend's own
   configuration, the Integration Quality Review binding, Source Analysis evidence, stored public Altinn metadata, the test pack's
   suggestions and ID-porten TEST's *public* discovery document. It never authenticates, submits, creates an instance, uses a secret
   or saves anything.
2. **Review and apply.** Each value shows its state and source: *Configured*, *Detected from source*, *Detected · public metadata*,
   *Suggested* (test-pack default), *Observed* (runtime evidence), *Missing* or *Conflict*. Tick the values to keep and select
   **Apply selected**. Disagreeing sources are a *Conflict* — never resolved silently; a saved value is never overwritten by detection.
3. **Automated execution** (only BirkNext's unattended runs need it): the unattended ID-porten TEST **client ID**, **registered
   redirect URI**, **scopes** and **Enabled**. A browser redirect observed for Altinn's own login (e.g. `https://af.tt02.altinn.no/api/cb`)
   is *browser evidence* and is never used as the registered redirect URI.
4. **Add secret for this session.** A password field. The secret is sent once, held in backend memory until the backend restarts or
   the session expires (8 hours), and never stored, logged, exported, returned or kept in the browser. The UI shows only *Available
   for this session* (or *Available to backend* when the backend's own process configuration supplies one).
5. **Runtime observations.** Record what a manual TT02 run showed (TestID login, app opened, form completed, report submitted, receipt).
   These are evidence: they never prove BirkNext's unattended client, its secret, or MU/MM/downstream receipt. Optionally record the
   browser login's observed parameters (issuer, authentication level, redirect URI, scopes, PKCE) — never a code, state, nonce or token.

The page shows **Manual test readiness** (TT02 app, reporter, child, integration, contract) and **Automated BirkNext execution**
(additionally the unattended client, the secret, trust and app approval) separately. Incomplete automated execution never blocks manual
testing.

## Test identities: Test Data → Session test identities

Open **Test Data → Session test identities**:
- **Reporter** — alias (e.g. `reporter-a`), label, and the synthetic TestID identifier (password field; 11 digits, Test-Norge month + 80).
  To mark the reporter's **TT02 profile confirmed**, type the alias again — a deliberate confirmation, never inferred from the
  identifier. *Configured*, *profile confirmed* and *runtime login observed* stay distinct.
- **Child** — alias (e.g. `child-a`), label and identifier. The child rule is domain-correct (11 digits with valid control digits and a
  plausible date part); the reporter's month + 80 rule is **not** applied to a child. Reporter and child must be different persons.

Identifiers are held in backend memory for the session only and are never shown again: everywhere else BirkNext shows the alias.

## Administrator-only backend configuration

These are trust decisions and are never set from the UI or by setup:

| Setting | Key |
|---|---|
| Approved apps (target boundary) | `AltinnTesting:ApprovedApps` (currently `buf/skolenaervaer`) |
| App policy confirmation | `AltinnTesting:ConfirmedAppPolicies` with `PolicySha256` |
| Trusted target + enrolled integrations | `TargetEnvironments:Trusted:<n>:EnvironmentId`, `:EnvironmentType`, `:ExecutionAllowed`, `:IntegrationIds:<m>` (exact ids, never Production) |
| TT02 platform and apps hosts | `AltinnTesting:Endpoints:Test:PlatformBaseUrl`, `:AppsDomain` |

**Compatibility:** the backend still accepts the `AltinnTesting:UnattendedIdPortenTest:*` and `AltinnTesting:SyntheticChildren:*`
settings from its own process configuration. Precedence for the secret: a session secret, then the backend's process configuration.
For the client id, redirect URI and scopes: the Target Environment's saved value, then the backend configuration, then the IQR binding.
Two explicit values that disagree are a conflict that blocks automated execution until one is kept.

## Checklist

1. Restart the backend on the current commit (the readiness row *Backend build* shows it).
2. Target Environments → Altinn → **Detect Altinn settings**, review, **Apply selected**.
3. Enter the unattended **client ID**, **registered redirect URI** and **scopes**, and enable automated execution.
4. **Add secret for this session.**
5. Test Data → **Session test identities**: reporter (confirm its TT02 profile) and child.
6. Ask an administrator for the **trusted target enrolment** of the report-intake integration (exact id).
7. **Read public TT02 metadata** (Skolenærvær → App contract).
8. Run the **dry run** ("Validate run").
9. Confirm every mandatory readiness row is green.
10. **STOP.** Ask for explicit approval before the first real run.

## Restart, then read public TT02 metadata

1. Make sure the backend runs the current commit. Session values (secret, identities) are re-entered after a restart.
2. On `/extensions/skolenaervaer`, check that *Backend build* shows the expected commit.
3. Under *Public TT02 metadata*, select **Read public TT02 metadata**. This makes anonymous GETs only:
   - the application metadata;
   - the form schema;
   - the deployed OpenAPI document;
   - the app's authorization policy (only its SHA-256 is kept).

   *Public TT02 process metadata* then shows **Loaded**, and *Deployed contract* shows **Current**.
4. Expected *Process contract*: **Confirmed for the current operation**.
   - The task is whatever the deployed contract declares. Currently that is `Task_1` · *Utfylling* · `data`, with no actions.
   - There is one `process/next` with an optional body, and its response exposes `currentTask`, `ended` and `endEvent`.
   - The App API paths come from the deployed contract too.
   - `process.bpmn` is **optional**.

## When the deployed app or its policy changes

- **Every read compares against the last confirmed contract.**
  - The first read that confirms the operation becomes the confirmed contract.
  - A compatible change (for example a new app-lib version with the same facts) is accepted automatically.
  - Anything else shows **Deployed Altinn contract changed since last confirmation** with a *Review changes* table
    (Before · After · Impact). Real execution stays blocked until you select *Accept reviewed changes*.
  - *Accept* is only offered when the new contract still fits the bounded operation.
- **Blocking changes cannot be accepted.** These are:
  - a second task;
  - a task action;
  - a non-data task;
  - a missing `process/next`, a required body, or a missing state field;
  - a missing operation.
- **Needs review:** a renamed task, a different data type or task ownership, or a changed path.
- **A changed policy hash** turns *App authorization policy* into **Changed · needs confirmation**, and real execution is
  blocked. Review the new policy and record its new `PolicySha256` in `AltinnTesting:ConfirmedAppPolicies`. A policy that could not
  be re-read is shown as unknown, never as unchanged.
- **Freshness:** a read older than `AltinnTesting:ContractRefreshRecommendedAfterHours` (default 24) shows **Refresh
  recommended**.
- **Imported OpenAPI:** you may import a downloaded OpenAPI document (for example `customOpenapi.json`) for offline comparison. It
  is shown as **Imported OpenAPI · Not live-verified** and is never used to run.
- **Source precedence:**
  1. live deployed metadata;
  2. target configuration;
  3. confirmed policy evidence;
  4. imported OpenAPI;
  5. documented defaults.

  An uploaded app source that disagrees with the live data type is a conflict, not a silent choice.

## Dry run (no external call)

Under **Integration journeys → Rapportmottak**, open **E2E-01**. Use these values:
- reporter `reporter-a`;
- a **different** synthetic child;
- period 2026-09-01 – 2026-09-30;
- planned hours 100, attendance hours 92.

Select **Validate run**. The expected result is **Valid — not executed**: the process contract is confirmed and the XML is
generated (preview masked). No history entry, token, instance or network call is made.

## Meldingshode — first-run risk

BirkNext fills only the verified form fields: the child, the period and the two hour fields. None of the Meldingshode fields
below are filled by BirkNext. Whether the app (or Altinn) fills them is not proven.

| Field | BirkNext supplies | App supplies | Altinn supplies |
|---|---|---|---|
| `Meldingshode.Id` | No | Unknown | Unknown |
| `Meldingshode.MeldingstypeNmsp` | No | Unknown | Unknown |
| `Meldingshode.FagsystemAvsender.Leverandor` | No | Unknown | Unknown |
| `Meldingshode.FagsystemAvsender.Navn` | No | Unknown | Unknown |
| `Meldingshode.FagsystemAvsender.Versjon` | No | Unknown | Unknown |
| `Meldingshode.SendtTidspunkt` | No | Unknown | Unknown |

This stays **First-run risk**, not a blocker. Altinn's validation in the first real run will show it, before any process
transition.

The readiness row needs the app's form schema. Until the public TT02 metadata has been read, the row shows **Not evaluated**
(not ready): with no schema, BirkNext cannot tell whether every required field is filled. It is never shown as Ready then.

## Stop

When every mandatory row is green: **stop**. The first real run needs explicit approval.

When approved, the confirmation states that the run:
- makes real TT02 calls;
- authenticates the selected synthetic reporter;
- creates one instance;
- uploads the form data;
- validates it;
- makes **at most one** process transition;
- reads the receipt/status;
- makes **no automatic retry** and **no second transition**;
- never calls Production.

*Confirm and run* stays disabled until the acknowledgement is checked.

Configured client ≠ authenticated user · synthetic reporter ≠ synthetic child · declared child ≠ verified child ·
authenticated ≠ submitted ≠ MU received ≠ MM processed ≠ report stored ≠ period closed ≠ Bufdata processed ·
one successful `process/next` ≠ permission for another transition.
