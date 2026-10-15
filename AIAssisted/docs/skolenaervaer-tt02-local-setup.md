# Skolenærvær TT02 — local setup for the first unattended run

Developer/test-lead guide for preparing the **first supervised, unattended Skolenærvær run against Altinn TT02** on a local BirkNext.
It covers configuration only. Following it makes **no** real ID-porten, Altinn or Storage call. The real run is a separate,
explicitly approved step (see [Stop](#stop)).

Placeholders only appear here (`<…>`). Never put a real secret, TestID identifier or child identifier in this file, in
`appsettings*.json` (including the **tracked** `appsettings.Local.json`), in frontend configuration or in a committed `.env`.

## Automatic resolution on the run page

Skolenærvær run readiness resolves execution inputs automatically from approved existing sources. The run page (Skolenærvær Testing →
Rapportmottak) lists what was **resolved automatically** and where it came from, and **needs attention** shows only what is still
unresolved, each with the owner that resolves it.

| Input | Source (owner) |
|---|---|
| Trusted target, enrolled integrations | Target Environment |
| Report-intake integration | Integration Quality Review + Target Environment |
| App, environment, ID-porten client id, scopes, test reporter reference, synthetic identity set | Integration Quality Review → Altinn test configuration |
| Enabled, redirect URI, client secret (as *Available*/*Missing*), app-policy confirmation | Local execution config |
| Synthetic reporter and its profile confirmation, approved synthetic children | Approved test data (local execution config) |
| Task, data type, process contract, contract fingerprint | Live Altinn metadata (anonymous reads) |

- Automatic resolution does not create configuration or identities. It never generates a child, invents a redirect URI or switches
  *Enabled* on.
- Only unresolved inputs are blockers. A value is preselected on the run form only when the choice is deterministic: exactly one usable
  approved reporter, or exactly one approved child (or exactly one marked default). Otherwise the tester chooses.
- Resolved is not verified. Configured scopes are not a verified client registration, a configured reporter is not an authenticated one,
  and a declared child is not a verified one. No ID-porten or authenticated Altinn call is made to resolve readiness.
- The run page stores nothing. ID-porten values are never configured in Target application.
- **Ready (A)** means ready for a first *supervised* TT02 submission. Nothing is submitted without an explicit confirmation in the run.

## Checklist

1. **Restart the backend on the current commit.** The readiness row *Backend build* shows the running commit.
   A backend started earlier keeps its old code and settings.
2. Set unattended ID-porten TEST **enabled**.
3. Set the **registered redirect URI**.
4. Provide the **client secret** securely (process environment only).
5. Configure the **synthetic reporter** (alias, label, synthetic TestID identifier).
6. Confirm the reporter's **TT02 Altinn profile** — a deliberate, manual step.
7. Configure **IQR → Altinn test configuration**: client id, scopes, app, environment, reporter reference.
8. **Enrol the trusted integration** for the target.
9. Configure the **approved synthetic child**: approved test data via the helper (`-ChildAlias`), or an identity set + approval in IQR.
10. **Read public TT02 metadata.**
11. Run the **dry run** ("Validate run").
12. Confirm every mandatory readiness row is green.
13. **STOP.**
14. Ask for explicit approval before the first real run.

## Where each setting lives

Every value below can change without a code change. The values seen in TT02 today (the app, its task, its data type, the scopes in
use) are **current evidence, not permanent configuration**. The setup page's *Configuration and sources* table shows each value
with its source.

Ownership:
- **You / the environment configure:** the ID-porten TEST client, redirect URI, secret, reporters, profile confirmation, trusted
  target and approved synthetic test data.
- **BirkNext discovers from the deployed app:** process tasks, data type, App API endpoints and contract fingerprint.
- **Central Altinn addresses:** the TT02 platform and apps hosts come from `AltinnTesting:Endpoints:Test` (defaults
  `https://platform.tt02.altinn.no/` and `apps.tt02.altinn.no`). IQR stores only *Environment = Test*. If Altinn moves a TT02 host,
  change these two values (configuration or environment variable); no Skolenærvær code changes. Configured is not verified.
- **Confirmed policy evidence:** the app policy's source, hash, roles, actions and authentication level. It is re-checked on every
  metadata read.

The browser never holds a secret.

| Setting | Exact key | Owner |
|---|---|---|
| Unattended ID-porten TEST enabled | `AltinnTesting:UnattendedIdPortenTest:Enabled` | backend (env var) |
| Registered redirect URI | `AltinnTesting:UnattendedIdPortenTest:RedirectUri` (falls back to `AltinnTesting:IdPortenTest:RedirectUri`) | backend (env var) |
| Client secret | `AltinnTesting:UnattendedIdPortenTest:ClientSecret` (falls back to `AltinnTesting:IdPortenTest:ClientSecret`) | backend (env var only) |
| Client id (optional here) | `AltinnTesting:UnattendedIdPortenTest:ClientId` — used when IQR has none; both set and different is reported as a conflict | backend (env var) |
| Reporter alias / label | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:Alias` / `:Label` (several reporters: `<n>` = 0, 1, …) | backend (env var) |
| Reporter TestID identifier | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:TestIdPersonIdentifier` | backend (env var only) |
| Reporter profile confirmed | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:AltinnProfileConfirmed` (default `false`) | backend (deliberate) |
| Reporter role / security level / environment | `…:Reporters:<n>:AltinnRole` (`PRIV`, the only supported role), `:AcrValue` (`idporten-loa-substantial`), `:AllowedEnvironment` (`TT02`) | backend (defaults) |
| Approved app (target boundary) | `AltinnTesting:ApprovedApps` (currently `buf/skolenaervaer`); another app or organisation needs an explicit change here and in IQR | backend |
| App policy confirmation | `AltinnTesting:ConfirmedAppPolicies` with `PolicySha256` (currently confirmed for `buf/skolenaervaer`, TT02) | backend (confirmed evidence) |
| TT02 platform and apps hosts | `AltinnTesting:Endpoints:Test:PlatformBaseUrl`, `:AppsDomain` (TT02 defaults; override in configuration or `AltinnTesting__Endpoints__Test__…`) | backend (central) |
| Process tasks, data type, endpoints, app-lib version | discovered by **Read public TT02 metadata** from the approved app; never configured | deployed metadata |
| Trusted target + enrolled integrations | `TargetEnvironments:Trusted:<n>:EnvironmentId`, `:DisplayName`, `:EnvironmentType`, `:ExecutionAllowed`, `:TargetUrl`, `:IntegrationIds:<m>` | backend (env var) |
| Client id, scopes, app, environment, reporter reference | IQR → Altinn test configuration: *Client / app reference*, *Configured scopes*, *App identifier*, *Environment*, *Test reporter reference* + *Reporter access approved for this test* | IQR (UI) |
| Approved synthetic child | IQR → *Approved synthetic identity-set reference* + *Test data is approved synthetic data*; the child identifier itself is entered per run and never stored | IQR (UI) + run input |

In environment variables, `:` becomes `__`. For example: `AltinnTesting__UnattendedIdPortenTest__Reporters__0__Alias`.

## Secrets: process environment only

Use the session-only helper, the smallest safe option:

```powershell
# From the repository root, in the window that will run the backend:
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1
# Prompts for: reporter alias, reporter label, reporter synthetic identifier (not shown), profile confirmed?, client id (optional),
# redirect URI and client secret (not shown). Example identifier format: 11 digits — no identifier is built into the script.
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1 -ReporterIndex 1   # a second reporter (reporter-b)
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1 -Status   # what is set; secrets as "Set (value not shown)"
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1 -Clear    # remove them from this session
```

The helper:
- sets **process-scope** variables only, so nothing is written to disk, the registry, or the user/machine environment;
- never echoes the secret or the identifier;
- refuses a non-synthetic identifier (11 digits, month + 80), an `http` redirect that is not loopback, a Production trusted
  environment, and wildcard integration ids;
- makes no network call.

The equivalent by hand, with placeholders:

```powershell
$env:AltinnTesting__UnattendedIdPortenTest__Enabled = 'true'
$env:AltinnTesting__UnattendedIdPortenTest__RedirectUri = '<REGISTERED_IDPORTEN_TEST_REDIRECT_URI>'
$env:AltinnTesting__UnattendedIdPortenTest__ClientSecret = '<SECRET>'                      # type it; do not save it in a script
$env:AltinnTesting__UnattendedIdPortenTest__Reporters__0__Alias = 'reporter-a'
$env:AltinnTesting__UnattendedIdPortenTest__Reporters__0__Label = 'Skolenærvær reporter A'
$env:AltinnTesting__UnattendedIdPortenTest__Reporters__0__TestIdPersonIdentifier = '<SYNTHETIC_TESTID_PERSON>'
$env:AltinnTesting__UnattendedIdPortenTest__Reporters__0__AltinnProfileConfirmed = 'false' # see the next section
```

BirkNext has no `UserSecretsId`. That is deliberate: user secrets would leak into the `WebApplicationFactory` tests. `.env` files
are git-ignored, but the backend does not read them.

## Reporter profile confirmation (deliberate)

Altinn's token exchange fails for a person without an Altinn TT02 profile. BirkNext cannot see that profile, so it never infers it:
not from the identifier existing, not from its format, and not from the configuration parsing.

1. Create or select the synthetic TestID person (Test-Norge).
2. Make sure that person has an Altinn TT02 profile.
3. Mark it confirmed, deliberately: when the helper asks *Profile confirmed?*, type the alias, or set
   `…Reporters__0__AltinnProfileConfirmed = 'true'` yourself.
4. Readiness then shows *Reporter Altinn profile: Confirmed*.

*Configured* is not *confirmed*. A configured reporter shows *Synthetic reporter: Configured* and *Reporter Altinn profile: Not
confirmed* until step 3.

## Integration Quality Review → Altinn test configuration

- **Environment:** `Test` (TT02). Production stays blocked.
- **App identifier:** the approved app (currently `buf/skolenaervaer`).
- **Authentication type:** ID-porten. **Client / app reference:** the ID-porten TEST client id.
- **Configured scopes:** configuration, not a fixed requirement.
  - Currently used: `openid, altinn:instances.read, altinn:instances.write`.
  - They are sent exactly as configured; the current authentication requires only `openid`.
  - Readiness shows them as *N configured · Not checked against the client registration*. Verify them against your ID-porten TEST
    client registration yourself; BirkNext cannot.
- **Test reporter reference** + *Reporter access approved for this test*.
- **Approved synthetic identity-set reference** + *Test data is approved synthetic data* (see the synthetic child section below).

There is no secret field in IQR. The Skolenærvær readiness shows the client secret only as **Available** or **Missing**.

## Trusted target enrolment

Active execution needs a backend trusted environment that enrols the **exact** Altinn report-intake integration id. If the
readiness row *Skolenærvær – Altinn report intake* says **Not enrolled**, its reason names the exact id(s) and the
environment id to use.

```powershell
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1 -RedirectUri <…> `
  -TrustedEnvironmentId <TARGET_ENVIRONMENT_ID> -TrustedDisplayName '<DISPLAY NAME>' -TrustedEnvironmentType Development `
  -TrustedIntegrationIds <EXACT_INTEGRATION_ID>
```

This is equivalent to setting `TargetEnvironments__Trusted__0__EnvironmentId`, `__DisplayName`, `__EnvironmentType`,
`__ExecutionAllowed=true` and `__IntegrationIds__0`.

The rules are enforced by the backend:
- exact ids only (no wildcard);
- the environment id must match the selected target exactly;
- Production is never trusted for execution;
- the browser cannot enrol anything.

No committed configuration defines `TargetEnvironments:Trusted`, so index `0` is free.

## Approved synthetic child

- **Approved test data (preferred).** Run the helper with `-ChildAlias synthetic-child-1` (and `-ChildDefault` if there are several). It
  asks for the synthetic identifier without echoing it and keeps it in this window's process only. Add `-ChildUnknownToPerson` for a
  child declared unknown to Person (used by *Child not found in Person*). The run page then resolves the child by alias and preselects
  it. A child that is the same person as an approved reporter is rejected by the backend.
- **Or** declare the approved synthetic identity set in IQR: the reference, plus *Test data is approved synthetic data*.
  Readiness then shows *Known synthetic child: **Declared***.
- Declared is not verified: whether Person knows the child is not checked by BirkNext.
- The child identifier is entered per run. It must be a synthetic identifier and must differ from the reporter; this is compared
  in the backend, and neither value is shown. It is never stored or written to history, logs or the dry-run preview (masked).

## Restart, then read public TT02 metadata

1. Stop any backend already running on `:5000`. Start it from the configured window: `dotnet run --project
   AIAssisted\backend\BirkNext.Api`, or the helper with `-StartBackend`.
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
