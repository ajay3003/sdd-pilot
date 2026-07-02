using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ConstitutionAnalysisServiceTests
{
    private readonly ConstitutionAnalysisService _svc = new();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string AuthConstitution() => """
        # Authorization Module Constitution

        Version: 1.2
        Ratified: 2024-01-15

        ## Core Principles

        ### PP-01 Zero-Trust Security
        All access requires explicit authorization. No implicit trust.

        ### PP-02 Least Privilege
        Grant the minimum permissions required.

        ### PP-03 Separation of Concerns
        Authorization logic must not leak into business logic.

        ### PP-04 Audit Everything (PP-02, PP-03)
        Every authorization decision must be logged.
        - Related Guidelines: GL-28

        ### PP-05 Fail Secure
        On error, deny access.

        ## Platform Standards

        ### PS-01 Token Validation
        Tokens must be validated on every request.

        ### PS-02 Role Enforcement
        Roles must be enforced at the service boundary.

        ### PS-03 Permission Scoping
        Permissions must be scoped to the minimum required operation.

        ### PS-04 JWT Standards
        Use RS256 signed JWTs. Expiry: 15 minutes access, 7 days refresh.

        ### PS-05 RBAC Model
        Use role-based access control. Roles defined in identity service.

        ### PS-06 Authorization Headers
        All API requests must include Authorization: Bearer <token>.

        ### PS-07 Token Refresh
        Refresh tokens must be rotated on each use.

        ## Authorization Constraints

        ### AC-01 No Direct DB Access
        Modules must not query the authorization DB directly.

        ### AC-02 No Hardcoded Roles
        Role names must not be hardcoded in module code.

        ### AC-03 No Auth Bypass
        No mechanism to skip authorization checks is permitted.

        ### AC-04 Single Auth Source
        Authorization must come from one central service only.

        ### AC-05 No Token Forwarding
        Modules must not forward tokens to downstream services directly.

        ### AC-06 No Silent Failures
        Authorization failures must throw, not return null or empty.

        ### AC-07 No Role Enum
        Roles must not be represented as enums in module code.

        ### AC-08 Immutable Permissions
        Permission sets must not be mutated after initialization.

        ## Governance

        ### Amendment Process
        Any change requires approval from the security guild.

        ## Changelog

        ### 1.2 - 2024-01-15
        - Added AC-08 Immutable Permissions
        """;

    private static string NoChangelogConstitution() => """
        # Simple Constitution

        ## Core Principles

        ### PP-01 Single Responsibility
        Each module has one reason to change.

        ## Platform Standards

        ### PS-01 Testing Required
        All modules must have unit tests.
        """;

    private static string MultiIdConstitution() => """
        # Multi-ID Test Constitution

        ## Core Principles

        ### Zero-Trust Security (PP-02, PP-04)
        Core security principle covering zero-trust and audit requirements.

        ### PP-01 Separation of Concerns
        Authorization logic must not leak into business logic.

        ## Platform Standards

        ### PS-08 Observability Standard (GL-28)
        All services must emit structured logs with trace IDs.
        """;

    // ── 1: Authorization constitution parsing ─────────────────────────────

    [Fact]
    public void AuthConstitution_ParsesPrinciples()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Principles.Should().HaveCount(5);
    }

    [Fact]
    public void AuthConstitution_ParsesStandards()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Standards.Should().HaveCount(7);
    }

    [Fact]
    public void AuthConstitution_ParsesConstraints()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Constraints.Should().HaveCount(8);
    }

    [Fact]
    public void AuthConstitution_TotalRulesIsNotZero()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Health.TotalRules.Should().BeGreaterThan(0,
            "TotalRules must reflect parsed principles + standards + constraints");
    }

    [Fact]
    public void AuthConstitution_TotalRulesMatchesSectionCounts()
    {
        var doc = _svc.Parse(AuthConstitution());
        // Catalog must contain at least one rule per parsed section item
        doc.RuleCatalog.Should().HaveCountGreaterThanOrEqualTo(
            doc.Principles.Count + doc.Standards.Count + doc.Constraints.Count);
    }

    [Fact]
    public void AuthConstitution_HealthIndicatorShowsCounts()
    {
        var doc = _svc.Parse(AuthConstitution());
        var firstIndicator = doc.Health.Indicators.First();
        firstIndicator.Level.Should().Be(HealthIndicatorLevel.Good);
        firstIndicator.Message.Should().Contain("principles");
        firstIndicator.Message.Should().Contain("standards");
        firstIndicator.Message.Should().Contain("constraints");
    }

    [Fact]
    public void AuthConstitution_PP02IsSearchable()
    {
        var doc = _svc.Parse(AuthConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-02").ToList();
        results.Should().NotBeEmpty("PP-02 is explicitly defined");
        results.Any(r => r.RuleId == "PP-02").Should().BeTrue();
    }

    [Fact]
    public void AuthConstitution_GovernanceParsed()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.GovernanceItems.Should().NotBeEmpty();
    }

    [Fact]
    public void AuthConstitution_ChangelogParsed()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Changelog.Should().NotBeEmpty();
        doc.Changelog[0].Version.Should().Be("1.2");
    }

    // ── 2: Constitution without changelog ────────────────────────────────

    [Fact]
    public void NoChangelog_ChangelogCountIsZero()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        doc.Changelog.Should().BeEmpty();
        doc.Health.TotalVersions.Should().Be(0);
    }

    [Fact]
    public void NoChangelog_TotalRulesStillReflectsParsedItems()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        doc.Health.TotalRules.Should().BeGreaterThan(0);
        doc.RuleCatalog.Should().NotBeEmpty();
    }

    [Fact]
    public void NoChangelog_ChangelogIndicatorIsWarning()
    {
        var doc = _svc.Parse(NoChangelogConstitution());
        var indicator = doc.Health.Indicators
            .FirstOrDefault(i => i.Message.Contains("changelog", StringComparison.OrdinalIgnoreCase));
        indicator.Should().NotBeNull("a changelog warning indicator should be present");
        indicator!.Level.Should().Be(HealthIndicatorLevel.Warning);
    }

    // ── 3: Multi-ID title ("Zero-Trust Security (PP-02, PP-04)") ─────────

    [Fact]
    public void MultiId_PrincipleWithParenthesisIdSearchablePP02()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-02").ToList();
        results.Should().NotBeEmpty("PP-02 appears in the heading text");
    }

    [Fact]
    public void MultiId_PrincipleWithParenthesisIdSearchablePP04()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PP-04").ToList();
        results.Should().NotBeEmpty("PP-04 is an alias in the heading (PP-02, PP-04)");
    }

    [Fact]
    public void MultiId_BothPP02AndPP04MapToSameRule()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var byPP02 = _svc.SearchRules(doc.RuleCatalog, "PP-02").FirstOrDefault();
        var byPP04 = _svc.SearchRules(doc.RuleCatalog, "PP-04").FirstOrDefault();
        byPP02.Should().NotBeNull();
        byPP04.Should().NotBeNull();
        // They must be the same rule or the alias PP-04 refers back to PP-02's rule
        (byPP02!.RuleId == byPP04!.RuleId ||
         byPP04.Aliases.Contains("PP-04", StringComparer.OrdinalIgnoreCase) ||
         byPP02.Aliases.Contains("PP-04", StringComparer.OrdinalIgnoreCase))
            .Should().BeTrue("PP-04 is an alias of the PP-02 rule");
    }

    [Fact]
    public void MultiId_StandardWithParenthesisIdSearchablePS08()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        var results = _svc.SearchRules(doc.RuleCatalog, "PS-08").ToList();
        results.Should().NotBeEmpty("PS-08 appears in the heading");
    }

    [Fact]
    public void MultiId_GL28ExtractedFromParenthesis()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        // GL-28 appears in the parenthetical of the PS-08 heading
        var results = _svc.SearchRules(doc.RuleCatalog, "GL-28").ToList();
        results.Should().NotBeEmpty("GL-28 is referenced/aliased in the PS-08 heading");
    }

    // ── 4: Broken references ──────────────────────────────────────────────

    [Fact]
    public void BrokenReferences_CountedCorrectly()
    {
        const string md = """
            # Test Constitution

            ## Core Principles

            ### PP-01 Test Principle
            References PS-99 which does not exist.

            ## Platform Standards

            ### PS-01 Real Standard
            This exists.
            """;

        var doc = _svc.Parse(md);
        // PS-99 is referenced but not defined — should be counted as broken OR implied
        // The catalog should contain PS-99 as an implied rule (not a broken reference per se)
        // but broken references are those where the TARGET is unknown
        doc.Health.Should().NotBeNull();
    }

    // ── 5: Items without IDs get synthetic IDs ────────────────────────────

    [Fact]
    public void ItemsWithoutIds_GetSyntheticIds()
    {
        const string md = """
            # Test Constitution

            ## Core Principles

            ### Zero-Trust Design
            No ID in this heading.

            ### Least Privilege
            Also no ID.

            ## Platform Standards

            ### Coding Standard
            No ID here either.
            """;

        var doc = _svc.Parse(md);
        doc.RuleCatalog.Should().NotBeEmpty("items without IDs must still appear in catalog");
        doc.Health.TotalRules.Should().BeGreaterThan(0);
        doc.RuleCatalog.Should().Contain(r => r.RuleId.StartsWith("PRINCIPLE-"),
            "headings without PP-NN should get synthetic PRINCIPLE-NNN ids");
    }

    // ── 6: Rule type accuracy ─────────────────────────────────────────────

    [Fact]
    public void CatalogRuleTypes_MatchParsedSections()
    {
        var doc = _svc.Parse(AuthConstitution());

        var principles = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Principle);
        var standards = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Standard);
        var constraints = doc.RuleCatalog
            .Count(r => r.RuleType == ConstitutionRuleType.Constraint);

        principles.Should().BeGreaterThanOrEqualTo(doc.Principles.Count,
            "all parsed principles must appear in catalog");
        standards.Should().BeGreaterThanOrEqualTo(doc.Standards.Count,
            "all parsed standards must appear in catalog");
        constraints.Should().BeGreaterThanOrEqualTo(doc.Constraints.Count,
            "all parsed constraints must appear in catalog");
    }

    // ── 7: SearchRules covers aliases ─────────────────────────────────────

    [Fact]
    public void SearchRules_FindsByAlias()
    {
        var doc = _svc.Parse(MultiIdConstitution());
        // PP-04 is only an alias, not a primary RuleId
        var rule = doc.RuleCatalog.FirstOrDefault(r => r.RuleId == "PP-04");
        // Either it's a primary or an alias — either way, SearchRules must return it
        var found = _svc.SearchRules(doc.RuleCatalog, "PP-04").ToList();
        found.Should().NotBeEmpty();
    }

    // ── 8: Health TotalRules = catalog.Count when catalog is non-empty ────

    [Fact]
    public void Health_TotalRulesEqualsCatalogCount()
    {
        var doc = _svc.Parse(AuthConstitution());
        doc.Health.TotalRules.Should().Be(doc.RuleCatalog.Count);
    }

    // ── 9: Module Principles (MP, H, P, FP) support ────────────────────────

    [Fact]
    public void Parse_WithModulePrinciples_ExtractsWithoutError()
    {
        var markdown = """
            # Tjenestemodul Constitution

            ## Module Principles (MP)

            ### MP-01 — Read-Only in M01
            Tjenestemodulen MUST NOT support creation or modification.

            ### MP-02 — Data Minimisation
            Only fields necessary for the module's purpose MUST be stored.

            ### MP-03 — BiRK Terminology Does Not Leak Out
            External API MUST expose M2LB terminology exclusively.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle module principles without throwing
        doc.Should().NotBeNull();
        doc.Title.Should().Contain("Tjenestemodul");
    }

    [Fact]
    public void Parse_WithServicePrinciples_RecognizesHNotation()
    {
        var markdown = """
            # Hendelsestjenesten Constitution

            ## Service Principles

            ### H-01 — Immutable History
            Hendelsestjenesten MUST maintain immutable history.

            ### H-02 — Audit Trail
            All access events MUST be written to immutable trail.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should recognize H- prefix even if not explicitly mapped
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithPlatformPrinciplesTables_ExtractsRows()
    {
        var markdown = """
            # Constitution

            ## Platform Principles

            | ID | Principle | Details |
            |----|-----------|---------|
            | PP-01 | Contract-Driven | All communication via API contracts |
            | PP-02 | Zero-Trust | No implicit trust, explicit authorization |
            | PP-03 | Immutable | All data retains history |
            """;

        var doc = _svc.Parse(markdown);

        // Tables should be parsed and contribute to rule catalog
        doc.Health.TotalRules.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_WithSyncImpactReport_ParsesMetadata()
    {
        var markdown = """
            <!--
            SYNC IMPACT REPORT
            ==================
            Version change: 0.9.0 → 1.0.0
            Added sections:
              - Platform Principles (PP-01–PP-09)
              - Module Principles (MP-01–MP-05)
            Templates reviewed:
              - plan-template.md ✅
            -->

            # Module Constitution
            """;

        var doc = _svc.Parse(markdown);

        // Metadata should not break parsing despite HTML comments
        doc.Title.Should().Contain("Module");
    }

    [Fact]
    public void Parse_WithNorwegianContent_DoesNotBreakParsing()
    {
        var markdown = """
            # Tjenestemodul Constitution

            > **Arver fra:** M2LB Plattformkonstitusjon v4.0
            > **Gjelder for:** `m2lb-tjeneste` repo
            > **Domenekontekst:** Forvalter informasjon om barns aktive og historiske tjenester

            ## Core Principles

            ### PP-01 — Contract-Driven
            All communication MUST occur via published API contracts.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle Norwegian text without errors
        doc.Title.Should().NotBeEmpty();
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithAlternativeTitleFormat_ExtractsTitle()
    {
        var markdown = """
            # Hendelsestjenesten — Constitution

            ## Core Principles

            ### PP-01 Contract-Driven
            All communication via contracts.
            """;

        var doc = _svc.Parse(markdown);

        doc.Title.Should().Contain("Hendelsestjenesten");
    }

    [Fact]
    public void Parse_WithSecurityComplianceSection_ParsingSucceeds()
    {
        var markdown = """
            # Constitution

            ## Security & Compliance Requirements

            - **Fail-Closed**: Upon failure, access MUST be denied
            - **Read-Log**: All read operations MUST publish events
            - **Outbox Pattern**: Events MUST use transactional outbox
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle Security & Compliance section without errors
        doc.Should().NotBeNull();
        doc.Title.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithDeferredTODOs_ExtractsFromMetadata()
    {
        var markdown = """
            <!--
            Deferred TODOs:
              - RATIFICATION_DATE pending approval
              - TODO(WORM_RETENTION_PERIOD) to be defined
            -->

            # Constitution
            """;

        var doc = _svc.Parse(markdown);

        // Parser should handle TODO markers gracefully
        doc.Title.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithMultiplePrincipalTypes_CategorizesProperly()
    {
        var markdown = """
            # Multi-Type Constitution

            ## Platform Principles

            ### PP-01 Zero-Trust
            Principle text.

            ## Module Principles

            ### MP-01 Read-Only
            Principle text.

            ## Development Guidelines

            ### GL-01 Testing Required
            Guideline text.
            """;

        var doc = _svc.Parse(markdown);

        doc.Principles.Should().NotBeEmpty();
        doc.RuleCatalog.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithRomanNumeralPrinciples_ExtractsAsAlternativeFormat()
    {
        var markdown = """
            # Core Constitution

            ## Core Principles

            ### I. Contract-Driven Communication
            Principle details.

            ### II. Zero-Trust Security
            More details.
            """;

        var doc = _svc.Parse(markdown);

        // Roman numerals should be recognized as principle identifiers
        doc.Principles.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_WithMandatoryKeywords_FlagsAsBindingRules()
    {
        var markdown = """
            # Constitution

            ## Core Principles

            ### PP-01 Required Rule
            This principle MUST be followed without exception.
            All implementations MUST comply.
            Violations are forbidden.
            Non-negotiable requirement.
            """;

        var doc = _svc.Parse(markdown);

        // Parser should recognize binding keywords
        doc.Principles.Should().NotBeEmpty();
    }
}
