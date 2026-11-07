# BirkNext Universal Test Data — Implementation — 2026-11-07

Extends the existing Test Data module with **Add data**: typed resources authored in Test Data, offered through the same catalog, approval,
versioning, matching and binding as every other asset. The page was not redesigned. The audit is `docs/audits/birknext-test-data-capability-audit-2026-11-07.md`.

## Delivery classification

| | Status | Evidence |
|---|---|---|
| A. Test data creation and persistence | **VERIFIED** | Nine kinds (all except XML) created in the browser; drafts survive reload; edits add versions; history immutable; concurrency conflict; duplicate |
| B. Schema / contract validation | **PARTIAL** | Exact types, JSON syntax and depth, GraphQL variables against discovered variable definitions and schema input/enum types, REST parameters against OpenAPI operations: VERIFIED. JSON Schema validation: NOT IMPLEMENTED (shown as Not assessed). XSD: the existing capability, not re-verified in this browser pass (TD-10 NOT RUN) |
| C. Consumer binding | **VERIFIED (generic binding path)** | An approved compatible template binds and records its version. A template whose operation contract changed after approval is refused with the reason. A per-operation picker in the API Quality Review page: NOT IMPLEMENTED |
| D. Runtime readiness integration | **PARTIAL** | Per-operation readiness (contract checks + approval) via `POST api/test-data/resources/{alias}/readiness`, used by binding. API Quality Review / Performance Test Review pages do not yet display it |
| E. Runtime execution | **NOT VERIFIED** | Nothing is executed (by design of this task; execution evidence is the next queued task) |

## Data model (`shared/TestDataResourceContracts.cs`)

- **Kinds:** `SingleValue`, `KeyValue`, `Json`, `GraphQlVariables`, `RestRequest`, `Dataset`, `Generated`, `FileReference`. XML is authored in the existing XML fixture editor.
- **Primitives:** `String`, `Int32`, `Int64`, `Decimal`, `Double`, `Boolean`, `Null`, `Date`, `Time`, `DateTime`, `Duration`, `Uuid`, `Enum`, `Json`.
  - Values are kept as invariant text.
  - The stored payload is **typed JSON**: numbers, booleans and null are never strings (`{"age":7,"amount":1234.50,"active":false,"note":null}`).
  - `Null` is an explicit marker; an empty string is a value.
- **Resource:** alias (the stable catalog identity), label, description, scope (environment or Shared), applies-to, content, append-only versions (number, time, fingerprint, change note). Approval, deprecation, ownership (`Authored`) and the immutable asset id come from the existing catalog.
- **Lifecycle:** Draft → Approved (an explicit, per-version catalog decision) → Deprecated; this is separate from readiness for an operation. Validation tiers are separate and never collapsed: Syntax, Type, Contract, References, Sensitive data, Size.
- **Generators:** the definition is stored, and values are materialized per index (UUID, Sequence, DateOffset, Timestamp, IntegerRange, DecimalRange, EnumChoice, FakeName, FakeEmail, FakeAddress, Pattern).
  - Random kinds require a fixed seed.
  - An all-`#` 11-character pattern is refused, so no identity numbers are generated.
- **References:** `{{dataset.x}}`, `{{data.alias}}`, `{{environment.x}}`, `{{scenario.x}}`, `{{step.x}}`.
  - Precedence is step → scenario → dataset → environment.
  - A missing value stays unresolved (never `""`); cycles are reported; sensitive values are masked in the preview.
  - Performance scenarios keep their own `{name}` placeholders, so existing syntax is unchanged.

## Persistence

- `AuthoredTestDataStore` writes `App_Data/test-data/authored.json` next to the existing registry. It is one more provider (`AuthoredTestDataProvider`) of the same catalog, not a second store.
- Writes are atomic (temp file + replace). An edit must carry the version it started from; a mismatch returns 409.
- Versions are never rewritten. The kind never changes under an alias (Duplicate starts a new one). Approval is never copied.
- Delete and Reset Test Data go through the existing lifecycle service via `AuthoredTestDataEditor`; `Authored` is deletable local data.

## Validation architecture

The same shared rules run in the browser (inline errors) and on the server, where they also get the context: catalog aliases, dataset references, and the selected operation from the newest or selected Source Analysis snapshot.
- **Sensitive data:**
  - Credential-like names (password, token, api-key, authorization, cookie, secret, …) with literal values are refused.
  - Token, JWT, private-key and connection-string patterns are refused.
  - 11-digit numbers are refused as possible national identity numbers.
  - REST templates never store authentication headers or cookies; they use the target's authentication.
- **Safety:** payloads are limited to 256 KB and JSON depth to 32. XML DTDs are prohibited and the resolver is null. File references must be relative paths inside the Test Data folder (no `..`, drive or absolute paths).
- **GraphQL:**
  - Required and non-null variables are checked, along with lists, built-in scalars, enums and nested input objects.
  - Custom scalars and unknown types are not asserted.
  - Without an operation the result is "Not assessed"; a selected operation missing from the snapshot gives "Contract missing" (Failed).
- **REST:**
  - Checks method, required parameters per location, and the exact type expected from the OpenAPI type (`integer`, `integer:int64`, `string:uuid`, …).
  - Also checks enum values, a required body, and the body content type.
  - The body is not validated against the JSON schema; that is reported as Not assessed.

## UI changes (Test Data page)

- An **Add data** button above the assets table opens a compact guided panel: Kind → Configure → Validate and save.
- **Type-aware editors:**
  - typed value input: toggle for booleans, date/time pickers, an explicit null marker, Generate for UUIDs, allowed values for enums;
  - key-value grid (add / duplicate / reorder / delete);
  - JSON editor with Format, line/column errors and a typed structure tree;
  - GraphQL operation picker with a synthetic template from declared types;
  - REST operation picker ("Use its method, path and parameters"), parameter grid and body;
  - dataset members (existing data or inline typed values);
  - generator settings with a preview that is not stored;
  - file reference.
- Errors for empty required fields appear after the first Validate. Format errors appear while typing.
- After saving, the panel returns to the catalog, clears filters and highlights the row. Added rows offer **Edit** (a new version) and **Duplicate**; approval and deprecation are the existing detail actions.
- Below 640 px, grids stack with visible cell labels.

## Consumer integration

- **Binding** (`POST api/test-data/usages`, the existing path): approval and scope were already required. Added REST templates and GraphQL variables are now also checked against their operation's current contract; a changed contract (e.g. a new required parameter) refuses the binding with the reason.
- **Performance / Smart Scenario Engine:** authored JSON payloads and typed scalars are ordinary catalog assets, so they take part in its existing matching. This was not exercised end-to-end in this pass.
- **Not done:** an operation-level picker inside API Quality Review, Performance Test Review and Skolenærvær pages (see gaps).

## Tests

| Suite | Result |
|---|---|
| New backend `TestDataResourceTests` | 40 cases (19 facts plus a 21-case theory), covering: <br>• primitive parsing (the 21-case theory) and explicit null versus empty <br>• enums and typed payloads with decimal precision <br>• duplicate keys <br>• JSON line, depth, nesting and contract Not assessed <br>• XML DTD and entity refusal <br>• GraphQL required, nested input, list, enum and non-null checks, Not assessed versus Contract missing <br>• REST credentials, path parameters, required parameters and type mismatch <br>• credentials, JWTs and identity numbers <br>• dataset unresolved reference, cycle and self-reference <br>• deterministic generators and the identity-pattern refusal <br>• binding precedence, missing value, cycle and masking <br>• draft, reload, versions, immutability, conflict and kind change <br>• approved version stays current with a draft, approval not copied, alias unique across the catalog <br>• oversized and sensitive content never written <br>• readiness: approval and contract missing <br>• readiness against a changed contract, with an approved v1 kept behind a v2 draft (the SourceId fix) <br>• delete via the editor |
| New bUnit `TestDataResourceEditorTests` | 8: <br>• kinds offered, and XML routes to the existing editor <br>• integer typed inline error and typed save, with no errors on an untouched form <br>• boolean toggle and null marker <br>• key-value rows and duplicate keys <br>• JSON line error and typed tree <br>• GraphQL operation, template and missing required variable <br>• REST Authorization header refused <br>• edit keeps the name and sends the expected version |
| Test Data and consumer backend areas (TestData, Schema Studio, Performance, Critical E2E, Skolenærvær, Connected Systems) | 976 passed / 0 failed |
| Frontend Test Data UI tests | 100 / 100 |
| Full regression on final code | **PARTIAL**: backend partitions 1–5 passed (4,055 / 0 failed). Partition 6 (root services, controllers, Delivery Reporting, …) and the full frontend suite were **NOT RUN**: Claude Code stopped the run for low system memory. They need a rerun before this counts as a full regression |

## Browser evidence (isolated stack)

- **Environment:** API :5099 with content root `api-td` and dedicated database `birknext_td_20261107`; web :5199. A synthetic source archive (OpenAPI 3.0.3 with 2 operations, GraphQL SDL with a query and a mutation, and an XSD) was imported locally. No external host was contacted.
- **Result:** 26 PASS, 1 NOT RUN (TD-10).

| Id | Check | Result |
|---|---|---|
| TD-01 | Add data opens with the keyboard; kinds are focusable | PASS |
| TD-03…06 | String; integer (`4.2` refused inline, 7 stored as Int32); boolean toggle; decimal (comma refused, 1234.50 kept) | PASS |
| TD-07/08 | JSON object (typed tree: String/Number/True/Null); nested arrays; malformed shows line/column | PASS |
| TD-09 | XML opens the existing XML fixture editor | PASS |
| TD-10 | XML validated against a synthetic XSD | **NOT RUN**: that editor offers only its Test Pack context XSD; synthetic-XSD XML is created in Schema Studio |
| TD-11 | GraphQL variables: template from the schema; missing `$input`, Int and enum mismatches shown; valid saved with Contract Passed | PASS |
| TD-12 | REST template from GET /children/{id}; an empty required value blocks save; filled template saved | PASS |
| TD-13 | Dataset with two references and a typed inline UUID | PASS |
| TD-14/15 | Nine drafts listed (none approved) after reload | PASS |
| TD-16 | Edit → version 2, version 1 kept | PASS |
| TD-17/18 | Readiness Blocked while unapproved, Ready after approval in the UI | PASS |
| TD-19/20 | Type mismatch and missing required parameter block readiness | PASS |
| TD-18c / 18b | Compatible template binds; after a v2 contract adds a required parameter, readiness is Blocked and a new binding is refused with the reason | PASS |
| TD-21 | 1280 / 768 / 390 px without horizontal overflow | PASS |
| TD-22 | axe: no serious or critical violations | PASS |
| TD-23 | No page or console errors, no API 5xx, no external request | PASS |

- **Screenshots** (synthetic data only) are in the scratch folder `tdval/shots`: kinds, integer inline error, JSON editor, XML fixture editor, GraphQL variable errors, REST template, dataset, catalog after reload, and key-value at 1280/768/390.
- **Persisted file check:** `authored.json` contains 0 credential-like strings.

## Remaining gaps and risks

| Gap | Effect |
|---|---|
| JSON Schema validation of JSON payloads and REST bodies | Shown as Not assessed, never Valid |
| A template saved without an operation is not contract-checked at binding | The binding gate needs the operation the resource declares |
| An operation-level test data picker in API Quality Review, Performance and Skolenærvær pages | Readiness and binding exist through Test Data and its API; those pages do not yet show them |
| GraphQL operation documents carry variable names only | Variable types are asserted only for schema operations |
| The XML fixture editor is context-bound | Generic XSD XML via Schema Studio; not re-verified in this pass |
| Prior-step outputs as references | `{{step.x}}` is resolved from values a runner supplies; no runner supplies them yet |
| Per-virtual-user data isolation, row selection | Existing Performance selection policies; not extended |

## Local commits

See `git log`. The implementation is committed separately from these documents; nothing was pushed.
