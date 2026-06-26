<!--
SYNC IMPACT REPORT
Version change: 1.1.0 → 1.2.0 (MINOR — constitution translated to English; language policy
redefined to require English for all team-produced artifacts except UI text and domain terms)

Modified sections:
  - "Language and code style" (renamed from "Spraak og kodestil"):
      - Code comments: changed from Norwegian (ae/oe/aa) to English.
      - Documentation/specifications: changed from Norwegian to English.
      - UI text: clarified — Norwegian is the required display language (functional requirement
        for Norwegian-speaking end users). This is unchanged in practice but now explicit.
      - Passive Norwegian sources: reading allowed; producing new Norwegian content is not
        (except UI text and domain term identifiers).
  - All other sections: translated from Norwegian to English (no semantic changes).

Template updates:
  - .specify/templates/plan-template.md: Already in English — no changes needed ✅
  - .specify/templates/spec-template.md: Already in English — no changes needed ✅
  - .specify/templates/tasks-template.md: Already in English — no changes needed ✅
  - .specify/templates/commands/: No command files present ✅

Related documents (to be reviewed in a future session):
  - .specify/memory/frontendkonstitusjon.md ⚠️ Norwegian — passive reference only
  - .specify/memory/plattformkonstitusjon.md ⚠️ Norwegian — passive reference only
  - .specify/memory/utviklingsretningslinjer.md ⚠️ Norwegian — passive reference only

Deferred TODOs: None
-->

# M2LB Frontend Constitution

## Core Principles

### I. Headless API Communication

All communication from Blazor components to backend services MUST occur via dedicated,
typed service classes registered in the DI container. No component calls `HttpClient`
directly.

- All service classes MUST return a result object that distinguishes between success,
  validation errors, and technical errors. Silent failure is prohibited — the user MUST
  always receive explicit feedback on the outcome of an action.
- GraphQL client code MUST be generated from the published SDL contract. Hand-written
  GraphQL queries in component code are prohibited.
- All API calls MUST be routed via reverse proxy. Direct backend URLs are prohibited in
  frontend code.
- Error handling MUST follow this pattern: loading state → loading indicator,
  validation errors (400) → inline error message per field, authorization errors (403) →
  explicit message indicating insufficient access rights, technical errors (5xx) → error
  message with retry affordance, network errors → service unavailability message.

**Rationale**: Service classes centralize transport and query logic in one place,
expose domain-specific methods, and make components testable in isolation via DI container
mocking. The transition between proxy implementations (e.g., YARP to APIM) does not
affect frontend code when only relative URLs are used. (FP-06, FP-07, FP-08, GL-01, GL-03)

### II. Authentication and Identity

All authentication MUST occur via MSAL (Microsoft Authentication Library) on the client
side. No server-side authentication state, cookies, or custom token mechanisms are allowed.

- Tokens MUST be acquired, cached, and refreshed by MSAL in the browser context —
  never manually.
- `AuthenticationStateProvider` is the sole source for the user's identity (`BrukerId` =
  EntraID Object ID).
- Tokens MUST NOT be stored in `localStorage`, `sessionStorage`, or cookies.
- User display attributes (name, email) are fetched from Microsoft Graph on demand for
  presentation purposes only and MUST NOT be persisted in application state.
- Token scopes are configured per API endpoint via `AuthorizationMessageHandler`.

**Rationale**: MSAL is the designated browser-side token management mechanism,
supporting acquisition, caching, and silent refresh within the WASM execution context.
The zero-trust model requires explicit, cryptographically verified identity at every
call boundary. (FP-01, FP-02, GL-04, GL-06, PS-01)

### III. Access-Based Navigation and UI Control

The navigation menu and all UI elements MUST be governed by the user's effective access
rights retrieved from the evaluation API. No other source — token claims, locally stored
roles, or custom rules — is permitted as the basis for UI access decisions.

- Access rights MUST be fetched once at page load and cached in an application-scoped
  service for the entire session with periodic refresh (default: 5 minutes).
- The navigation menu MUST only show modules and sections for which the user holds at
  least one operation. Menu items pointing to pages the user cannot access are prohibited.
- If the evaluation API is unavailable at page load, the page MUST display an explicit
  error message and render no functionality (fail-closed).
- UI access control is a presentational safeguard — it does not substitute for backend
  authorization. The backend is always authoritative and rejects unauthorized requests
  regardless of UI state.
- On a 403 response from the backend, the access rights cache MUST be invalidated
  immediately and access rights re-fetched.

**Rationale**: Access-based navigation is a primary functional requirement of this
application. End users MUST only see modules and menu items they are authorized for.
The evaluation API is the sole authoritative source for access decisions (PP-02).
(FP-03, FP-04, FP-05, FP-10, FP-13)

### IV. Component Design and Responsibility Separation

Page components (`Pages/`) and reusable components (`Components/`, `Shared/Components/`)
are strictly separated with clear responsibility boundaries.

- Page components are route endpoints — responsible for data loading, access evaluation,
  and orchestration of child components. They are not reusable.
- Reusable components receive data and callbacks exclusively via parameters. They MUST NOT
  fetch data independently, call APIs directly, or control navigation via
  `NavigationManager`.
- Shared components (tables, search fields, dialogs, error messages, loading indicators)
  MUST be implemented once in `Shared/` and reused across modules without duplication.
- Radzen Blazor is the standard UI component library. Additional UI libraries require
  approval from the solutions architect.
- Component-local state is preferred over global state. Where state MUST survive
  navigation, URL parameters are used. Where state MUST be shared within a page,
  `CascadingValue` or callback parameters are used.
- Business logic — including domain validation and access rule evaluation — MUST NOT be
  implemented in the presentation layer. Frontend validates only user input format
  (empty fields, invalid date formats) — never business rules.

**Rationale**: A strict boundary between orchestration and presentation makes components
independently testable and reusable. Business logic placed in the presentation layer can
be circumvented by API calls made outside the UI. (FP-09, FP-11, FP-12, FP-18, GL-05, GL-17)

### V. Testing is Mandatory

All Blazor components with business logic or access control MUST have bUnit tests.
Testing is a compliance requirement — not a recommendation.

- Service classes MUST be mocked via interfaces in the DI container. No bUnit test may
  make actual HTTP calls.
- The following test categories are mandatory per screen: correct component visibility per
  operation set, loading state, error handling per HTTP status code, confirmation dialogs
  for destructive operations, form validation, and edge cases.
- A specification change without a corresponding test change is incomplete.
- The full test suite MUST pass before merging to the main branch.

**Rationale**: In a system where access control carries legal and ethical obligations,
tests function as executable security specifications. A test that verifies a Code 7 child
is not exposed to unauthorized users is a security specification with legal weight — not
merely a technical assertion. (FP-14, FP-15, PP-09, GL-24)

### VI. Security in the Presentation Layer

Sensitive personal data MUST never be exposed in URLs, page titles, browser history,
or browser cache.

- URL parameters MUST use M2LB UUIDs — never names, national identity numbers, or other
  identifying personal information.
- Components that display data about Code 6/7 children MUST NOT expose the child's
  identity in URLs, page titles, or browser history. Specific requirements are documented
  per screen specification.
- Business rule validation (e.g., which operations may be assigned to which roles) is
  performed exclusively by the backend and returned as structured error messages in the
  API response.

**Rationale**: M2LB processes some of the most sensitive personal data in Norwegian
public administration — records of vulnerable children and families, including individuals
with active security classifications. Presentation-layer security failures in this domain
carry direct legal, ethical, and reputational consequences.
(FP-16, FP-17, FP-18, PP-04)

## Technical Foundation and Project Structure

| Component | Technology | Version |
| --- | --- | --- |
| Frontend framework | Blazor WebAssembly standalone | .NET 10 (LTS) |
| UI component library | Radzen Blazor | Latest stable |
| Authentication | MSAL (Microsoft Authentication Library) | Latest stable |
| API communication | HttpClient via typed service classes | — |
| GraphQL client | Strawberry Shake or equivalent .NET GraphQL client | Latest stable |
| Test framework | bUnit + xUnit | Latest stable |

The application is a single deployable unit organized in modules. A frontend module is a
logically bounded unit that realizes one or more user journeys and consumes the backend
services those journeys require. Module boundaries are logical, not deployable; there is
no runtime isolation between modules.

```text
M2LB.Web/
├── Layout/                      # App shell and navigation
│   ├── MainLayout.razor
│   ├── NavMenu.razor
│   └── AuthorizedLayout.razor
├── Modules/                     # One folder per frontend module
│   └── [ModuleName]/
│       ├── Pages/               # Page components (route endpoints)
│       ├── Components/          # Reusable components within the module
│       └── Services/            # Typed service classes for relevant backend services
├── Shared/                      # Cross-cutting components and services
│   ├── Components/              # Shared UI components (search, tables, dialogs, etc.)
│   ├── Services/                # Shared services (error handling, access evaluation)
│   └── Models/                  # Shared view models
├── Auth/                        # MSAL configuration and authentication setup
└── wwwroot/                     # Static resources
```

## Language and Code Style

- **UI text (user interface)**: Norwegian is the required display language for all text
  shown to end users. Text MUST use Norwegian special characters directly: `æ`, `ø`, `å`.
  This applies to all user-visible text in markup. This is a functional requirement — the
  application serves Norwegian-speaking users.
- **Documentation** (`.md` files, specifications, constitution, plans, tasks): MUST be
  written in English.
- **Code**: MUST be written in English. All identifiers, class and method names, file
  names, and API field names MUST be in English.
- **Comments in code**: MUST be written in English.
- **Commit messages**: MUST be written in English.
- **Domain term identifiers**: Norwegian-origin domain terms (e.g., `BarnId`,
  `BarnRelasjon`, `KlassifiserOperasjon`) MAY be used as identifiers when they are defined
  in the M2LB domain specification. The domain specification is the authoritative source
  for such terms; usage MUST be consistent with it.
- **Passive Norwegian sources**: External source material consumed by the system
  (Norwegian regulations, BiRK exports, legacy documents) is exempt from the English
  requirement. New content produced by the team MUST be in English, with the exceptions
  noted above.

## Governance

This constitution is the authoritative reference for all frontend development — human
or AI-assisted. It inherits from and is subordinate to the M2LB Platform Constitution
(v4.0). Where this constitution is silent, the platform constitution applies. All
elaborations remain within the constraints established by the platform constitution.

**Amendment process**: This constitution may only be amended through an explicit
architecture review with written approval from the solutions architect. Each amendment
MUST be documented with rationale, date, and updated version number.

**Versioning**: Follows semantic versioning — MAJOR for backward-incompatible removal or
redefinition of principles, MINOR for additions of principles or sections or material
redefinitions, PATCH for clarifications, corrections, and wording improvements.

**Compliance review**: All pull requests MUST demonstrate compliance with the principles
in this constitution. Deviations require written justification and approval from the
solutions architect before merging.

**AI agents and developers**: This document is non-negotiable during implementation
unless a formal amendment has been issued. A request to deviate from the constitution is
a signal that an amendment process is required — not a signal that the constitution may
be disregarded.

**Version**: 1.2.0 | **Ratified**: 2026-03-10 | **Last Amended**: 2026-03-19
