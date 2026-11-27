using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// A deterministic, synthetic OpenSpec repository (built-in spec-driven schema, OpenSpec v2 layout) written for these tests — never real project data.
/// Two capabilities share a requirement title («User Authentication»); one requirement has no scenario; one active change adds, modifies,
/// removes and renames; a second active change modifies the same requirement (conflict); one archived change was already merged into the baseline.
/// </summary>
internal static class OpenSpecFixture
{
    public const string Auth = """
        # Auth Specification

        ## Purpose
        Authentication and session management.

        ## Requirements

        ### Requirement: User Authentication
        The system SHALL issue a session token when a user submits valid credentials.

        #### Scenario: Valid credentials
        - **WHEN** a user submits a valid username and password
        - **THEN** a session token is returned

        #### Scenario: Invalid credentials
        - **WHEN** a user submits an invalid password
        - **THEN** an error message is shown
        - **AND** no session token is returned

        ### Requirement: Session Timeout
        The system SHALL end a session after 30 minutes without activity.

        ### Requirement: Password Reset
        The system MUST let a registered user reset a forgotten password.

        #### Scenario: Reset link
        - **GIVEN** a registered user
        - **WHEN** the user requests a password reset
        - **THEN** a reset link is sent to the registered e-mail address
        """;

    public const string Billing = """
        # Billing Specification

        ## Purpose
        Invoices.

        ## Requirements

        ### Requirement: User Authentication
        The billing portal SHALL require the user to be signed in before invoices are shown.

        #### Scenario: Anonymous visitor
        - **WHEN** an anonymous visitor opens the invoice page
        - **THEN** the visitor is sent to the sign-in page

        ### Requirement: Invoice Export
        The system SHALL export an invoice as PDF.

        #### Scenario: Export one invoice
        - **WHEN** the user exports an invoice
        - **THEN** a PDF file is downloaded
        """;

    public const string Add2faDelta = """
        ## ADDED Requirements

        ### Requirement: Two-Factor Authentication
        The system SHALL require a one-time code after the password.

        #### Scenario: OTP required
        - **WHEN** a user submits valid credentials
        - **THEN** an OTP challenge is shown

        ## MODIFIED Requirements

        ### Requirement: Session Timeout
        The system SHALL end a session after 15 minutes without activity.

        #### Scenario: Idle session
        - **WHEN** a session has been idle for 15 minutes
        - **THEN** the user is signed out

        ## REMOVED Requirements

        ### Requirement: Password Reset
        **Reason**: Replaced by the identity provider.
        **Migration**: Use the identity provider's reset page.

        ## RENAMED Requirements

        - FROM: `### Requirement: User Authentication`
        - TO: `### Requirement: User Sign-In`
        """;

    public const string ShorterSessionsDelta = """
        ## MODIFIED Requirements

        ### Requirement: Session Timeout
        The system SHALL end a session after 5 minutes without activity.
        """;

    public const string ArchivedBillingDelta = """
        ## ADDED Requirements

        ### Requirement: Invoice Export
        The system SHALL export an invoice as PDF.

        #### Scenario: Export one invoice
        - **WHEN** the user exports an invoice
        - **THEN** a PDF file is downloaded
        """;

    public static List<SpecificationCatalogService.Document> Documents(string root = "", string? config = "schema: spec-driven\n", string? auth = null) =>
    [
        new($"{root}README.md", "# Demo app\nNot OpenSpec source."),
        new($"{root}openspec/project.md", "# Project context\nTech stack."),
        new($"{root}openspec/specs/auth/spec.md", auth ?? Auth),
        new($"{root}openspec/specs/billing/spec.md", Billing),
        new($"{root}openspec/changes/add-2fa/proposal.md", "# Change: Add two-factor authentication\n\n## Why\nSecurity."),
        new($"{root}openspec/changes/add-2fa/tasks.md", "## 1. Implementation\n- [ ] 1.1 Add OTP"),
        new($"{root}openspec/changes/add-2fa/specs/auth/spec.md", Add2faDelta),
        new($"{root}openspec/changes/shorter-sessions/proposal.md", "# Change: Shorter sessions"),
        new($"{root}openspec/changes/shorter-sessions/specs/auth/spec.md", ShorterSessionsDelta),
        new($"{root}openspec/changes/archive/2026-01-10-add-billing/proposal.md", "# Change: Add billing"),
        new($"{root}openspec/changes/archive/2026-01-10-add-billing/specs/billing/spec.md", ArchivedBillingDelta),
        new($"{root}openspec/schemas/custom/README.md", "# A custom schema description"),
        .. config is null ? Array.Empty<SpecificationCatalogService.Document>() : [new SpecificationCatalogService.Document($"{root}openspec/config.yaml", config)],
    ];

    public static OpenSpecModelBuilder.Built Build(IEnumerable<SpecificationCatalogService.Document>? documents = null, string projectKey = "import:import-openspec",
        CancellationToken ct = default) =>
        OpenSpecModelBuilder.Build(projectKey, "repo:openspec-demo", "import-openspec", "archive-sha", null,
            [.. (documents ?? Documents()).Select(d => new OpenSpecSourceDiscovery.Input(d.Path, d.Content))], DateTimeOffset.Parse("2026-11-27T10:00:00Z"), ct);
}
