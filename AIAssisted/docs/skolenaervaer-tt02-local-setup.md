# Skolenærvær TT02 — local setup for the first unattended run

Developer/test-lead guide for preparing the **first supervised, unattended Skolenærvær run against Altinn TT02** on a local BirkNext.
It covers configuration only. Following it makes **no** real ID-porten, Altinn or Storage call. The real run is a separate,
explicitly approved step (see [Stop](#stop)).

Placeholders only appear here (`<…>`). Never put a real secret, TestID identifier or child identifier in this file, in
`appsettings*.json` (including the **tracked** `appsettings.Local.json`), in frontend configuration or in a committed `.env`.

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
9. Configure the **approved synthetic child** (identity set + approval in IQR).
10. **Read public TT02 metadata.**
11. Run the **dry run** ("Validate run").
12. Confirm every mandatory readiness row is green.
13. **STOP.**
14. Ask for explicit approval before the first real run.

## Where each setting lives

The backend owns trust and secrets. Integration Quality Review owns the app, client and scopes. The browser never holds a
secret.

| Setting | Exact key | Owner |
|---|---|---|
| Unattended ID-porten TEST enabled | `AltinnTesting:UnattendedIdPortenTest:Enabled` | backend (env var) |
| Registered redirect URI | `AltinnTesting:UnattendedIdPortenTest:RedirectUri` (falls back to `AltinnTesting:IdPortenTest:RedirectUri`) | backend (env var) |
| Client secret | `AltinnTesting:UnattendedIdPortenTest:ClientSecret` (falls back to `AltinnTesting:IdPortenTest:ClientSecret`) | backend (env var only) |
| Reporter alias / label | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:Alias` / `:Label` | backend (env var) |
| Reporter TestID identifier | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:TestIdPersonIdentifier` | backend (env var only) |
| Reporter profile confirmed | `AltinnTesting:UnattendedIdPortenTest:Reporters:<n>:AltinnProfileConfirmed` (default `false`) | backend (deliberate) |
| Reporter role / security level / environment | `…:Reporters:<n>:AltinnRole` (`PRIV`, the only supported role), `:AcrValue` (`idporten-loa-substantial`), `:AllowedEnvironment` (`TT02`) | backend (defaults) |
| App policy confirmation | `AltinnTesting:ConfirmedAppPolicies` (committed for `buf/skolenaervaer`, TT02) | backend (done) |
| Trusted target + enrolled integrations | `TargetEnvironments:Trusted:<n>:EnvironmentId`, `:DisplayName`, `:EnvironmentType`, `:ExecutionAllowed`, `:TargetUrl`, `:IntegrationIds:<m>` | backend (env var) |
| Client id, scopes, app, environment, reporter reference | IQR → Altinn test configuration: *Client / app reference*, *Configured scopes*, *App identifier*, *Environment*, *Test reporter reference* + *Reporter access approved for this test* | IQR (UI) |
| Approved synthetic child | IQR → *Approved synthetic identity-set reference* + *Test data is approved synthetic data*; the child identifier itself is entered per run and never stored | IQR (UI) + run input |

In environment variables, `:` becomes `__`. For example: `AltinnTesting__UnattendedIdPortenTest__Reporters__0__Alias`.

## Secrets: process environment only

Use the session-only helper, the smallest safe option:

```powershell
# From the repository root, in the window that will run the backend:
.\AIAssisted\scripts\configure-skolenaervaer-tt02-local.ps1 -RedirectUri <REGISTERED_IDPORTEN_TEST_REDIRECT_URI>
# Prompts for the client secret and the reporter's synthetic TestID identifier (input is not shown).
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
3. Mark it confirmed, deliberately. Rerun the helper with `-ReporterProfileConfirmed` and type the alias when asked, or set
   `…Reporters__0__AltinnProfileConfirmed = 'true'` yourself.
4. Readiness then shows *Reporter Altinn profile: Confirmed*.

*Configured* is not *confirmed*. A configured reporter shows *Synthetic reporter: Configured* and *Reporter Altinn profile: Not
confirmed* until step 3.

## Integration Quality Review → Altinn test configuration

- **Environment:** `Test` (TT02). Production stays blocked.
- **App identifier:** `buf/skolenaervaer`.
- **Authentication type:** ID-porten. **Client / app reference:** the ID-porten TEST client id.
- **Configured scopes:** `openid, altinn:instances.read, altinn:instances.write`. These are sent exactly as configured, and BirkNext
  requires `openid`. Check them against your ID-porten TEST client registration yourself; BirkNext cannot.
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

- Declare the approved synthetic identity set in IQR: the reference, plus *Test data is approved synthetic data*.
  Readiness then shows *Known synthetic child: **Declared***.
- Declared is not verified: whether Person knows the child is not checked by BirkNext.
- The child identifier is entered per run. It must be a synthetic identifier and must differ from the reporter; this is compared
  in the backend, and neither value is shown. It is never stored or written to history, logs or the dry-run preview (masked).

## Restart, then read public TT02 metadata

1. Stop any backend already running on `:5000`. Start it from the configured window: `dotnet run --project
   AIAssisted\backend\BirkNext.Api`, or the helper with `-StartBackend`.
2. On `/extensions/skolenaervaer`, check that *Backend build* shows the expected commit.
3. Under *Public TT02 metadata*, select **Read public TT02 metadata**. This makes three anonymous GETs: application metadata,
   the form schema and the deployed OpenAPI document. *Public TT02 process metadata* then shows **Loaded**.
4. Expected *Process contract*: **Confirmed for the current operation**. That is `Task_1` · *Utfylling* · `data`, no actions,
   one `process/next` with an optional body, and a response exposing `currentTask`, `ended` and `endEvent`.
   `process.bpmn` is **optional**.

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
