using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SecurityExpectations;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Security Configuration Review: the deterministic assessment of source-declared security configuration against approved expectations for one
/// Target Environment. Fixtures are generic (example.test, synthetic GUIDs); nothing is project-specific.
/// </summary>
public sealed class SecurityConfigurationReviewTests
{
    private static readonly Guid Snap = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111";
    private const string ClientA = "22222222-2222-2222-2222-222222222222";
    private const string ClientB = "33333333-3333-3333-3333-333333333333";
    private const string ClientC = "44444444-4444-4444-4444-444444444444";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One source occurrence, shaped as the analyzer emits it (one candidate per occurrence before discovery grouping).</summary>
    private static SecurityExpectationCandidate O(SecurityExpectationField field, string raw, string component, string file, string key, SecurityCandidateState? state = null, Guid? snapshot = null)
    {
        var normalized = SecurityExpectationValues.Normalize(field, raw);
        var placeholder = normalized is null && SecurityExpectationValues.IsPlaceholder(raw);
        var evidenceState = normalized is null ? ArchitectureEvidenceState.Unresolved : ArchitectureEvidenceState.Confirmed;
        var s = snapshot ?? Snap;
        return new()
        {
            Id = $"{field}|{component}|{file}|{key}|{raw}", FieldType = field, Value = raw.Trim(),
            NormalizedValue = normalized ?? (placeholder ? "placeholder:" : "invalid:") + raw, FormatState = normalized is null ? placeholder ? "Placeholder" : "InvalidFormat" : "Valid",
            EnvironmentScope = "", CandidateState = state ?? SecurityCandidateState.Detected, EvidenceState = evidenceState, SourceSnapshotId = s,
            SourceComponent = component, SourceFile = file, SourceSymbol = key,
            SupportingEvidence = [new(s, "src.zip / " + component, file, 1, key, "Configuration", raw, "Explicit source configuration", evidenceState, "fp")],
        };
    }

    private static SecurityExpectationDiscoveryResult Discovery(params SecurityExpectationCandidate[] observations) =>
        new() { SourceSnapshotId = Snap, IsCurrent = true, Status = ArchitectureStatus.Complete, Candidates = SecurityExpectationValues.Group(observations) };

    private static SecurityConfigurationReview Review(SecurityExpectationDiscoveryResult? discovery, ApprovedSecurityExpectations? approved = null, string environment = "Development", string? status = null) =>
        SecurityConfigurationReviewEngine.Review(discovery, approved ?? new(), new(environment, Snap, status));

    private static SecurityConfigurationCheckResult Check(SecurityConfigurationReview review, SecurityExpectationField field) => review.Checks.Single(c => c.Field == field);

    [Fact]
    public void Authority_SameValueAcrossComponentsAndFiles_IsOneConsistentValueGroup()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.json", "AzureAd:Authority"),
            O(SecurityExpectationField.Authority, Authority + "/", "Api", "Api/appsettings.json", "AzureAd:Authority"),
            O(SecurityExpectationField.Authority, "HTTPS://LOGIN.microsoftonline.com/11111111-1111-1111-1111-111111111111", "Proxy", "Proxy/appsettings.Development.json", "AzureAd:Authority"));

        var check = Check(Review(discovery, new() { ExpectedAuthority = Authority }), SecurityExpectationField.Authority);

        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);
        check.ValueGroups.Should().ContainSingle().Which.Should().Match<SecurityValueGroup>(g => g.Occurrences == 3 && g.Components == 3 && g.Files == 3 && g.IsApproved);
        check.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Tenant_ZeroGuid_IsAPlaceholder_WithoutMultiplyingCandidates()
    {
        var observations = Enumerable.Range(0, 18).Select(i => O(SecurityExpectationField.TenantId, i % 2 == 0 ? Tenant : "{" + Tenant.ToUpperInvariant() + "}", $"C{i % 7}", $"C{i % 7}/appsettings{(i < 11 ? "" : ".Development")}.json", $"AzureAd:TenantId{i}"))
            .Append(O(SecurityExpectationField.TenantId, "00000000-0000-0000-0000-000000000000", "Tjeneste", "Tjeneste/appsettings.Local.json", "AzureAd:TenantId")).ToArray();

        var dev = Check(Review(Discovery(observations), new() { ExpectedTenant = Tenant }), SecurityExpectationField.TenantId);
        dev.ValueGroups.Should().HaveCount(2, "formatting variants normalize to one tenant; the zero GUID is the second logical value");
        dev.ValueGroups.Single(g => g.Value == Tenant).Occurrences.Should().Be(18);
        dev.ValueGroups.Single(g => g.IsPlaceholder).Should().Match<SecurityValueGroup>(g => !g.AppliesToSelectedEnvironment && g.ApprovalCandidateId == null);
        dev.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.PlaceholderValue && !f.RequiresAttention, "Local configuration does not apply to Development");
        dev.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);

        var local = Check(Review(Discovery(observations), new() { ExpectedTenant = Tenant }, environment: "Local"), SecurityExpectationField.TenantId);
        local.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);
        local.Findings.Should().Contain(f => f.Type == SecurityConfigurationFindingType.PlaceholderValue && f.RequiresAttention);
    }

    [Theory]
    [InlineData("${TENANT_ID}")]
    [InlineData("<tenant-id>")]
    [InlineData("__TENANT__")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void PlaceholderDetection_IsDeterministic(string value) =>
        SecurityConfigurationReviewEngine.IsPlaceholder(SecurityExpectationField.TenantId, value).Should().BeTrue();

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("example-tenant.onmicrosoft.com")]
    [InlineData("tenant")]
    public void PlaceholderDetection_DoesNotGuess(string value) =>
        SecurityConfigurationReviewEngine.IsPlaceholder(SecurityExpectationField.TenantId, value).Should().BeFalse();

    [Fact]
    public void ClientIds_DifferingByComponent_AreSeparateScopes_NotAGlobalConflict()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.ClientId, ClientB, "Api", "Api/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.ClientId, ClientC, "Proxy", "Proxy/appsettings.json", "AzureAd:Schemes:person:ClientId"));

        var unapproved = Check(Review(discovery), SecurityExpectationField.ClientId);
        unapproved.Scopes.Should().Equal("Api · AzureAd", "Proxy · AzureAd:Schemes:person", "Web · AzureAd");
        unapproved.Findings.Should().NotContain(f => f.Type == SecurityConfigurationFindingType.ConfigurationConflict);
        unapproved.Findings.Where(f => f.Type == SecurityConfigurationFindingType.UnapprovedValue).Select(f => f.Scope).Should().HaveCount(3);
        unapproved.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);

        var approved = new ApprovedSecurityExpectations { ScopedClientIds = [new("Web · AzureAd", ClientA), new("Api · AzureAd", ClientB.ToUpperInvariant()), new("Proxy · AzureAd:Schemes:person", ClientC)] };
        Check(Review(discovery, approved), SecurityExpectationField.ClientId).Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);
    }

    [Fact]
    public void ClientId_TwoValuesInTheSameScope_IsAConflict()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.ClientId, ClientB, "Web", "Web/config/appsettings.json", "AzureAd:ClientId"));
        var check = Check(Review(discovery), SecurityExpectationField.ClientId);
        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.Conflict);
        check.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.ConfigurationConflict && f.Scope == "Web · AzureAd");
    }

    [Fact]
    public void LegacyProjectWideClientId_IsNeverRedistributed_AcrossSeveralScopes()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.ClientId, ClientB, "Api", "Api/appsettings.json", "AzureAd:ClientId"));
        var check = Check(Review(discovery, new() { ExpectedClientId = ClientA }), SecurityExpectationField.ClientId);
        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);
        check.Findings.Should().Contain(f => f.Type == SecurityConfigurationFindingType.AmbiguousScope && f.Scope == "");
        check.ValueGroups.Should().NotContain(g => g.IsApproved, "a project-wide approval is not silently assigned to a component");
        check.ApprovedValues.Should().Contain(ClientA, "the legacy approval is preserved and shown");

        var single = Discovery(O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.json", "AzureAd:ClientId"));
        Check(Review(single, new() { ExpectedClientId = ClientA }), SecurityExpectationField.ClientId).Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent,
            "with exactly one scope the legacy approval is unambiguous");
    }

    [Fact]
    public void BaseAndEnvironmentOverride_AreFileLayering_NotAConflict()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.Authority, "https://identity.example.test/base", "Web", "Web/appsettings.json", "Auth:Authority"),
            O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.Development.json", "Auth:Authority"));

        var dev = Check(Review(discovery, new() { ExpectedAuthority = Authority }), SecurityExpectationField.Authority);
        dev.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);
        dev.ValueGroups.Single(g => g.Value.EndsWith("/base", StringComparison.Ordinal)).Status.Should().StartWith("Overridden");

        var qa = Check(Review(discovery, new() { ExpectedAuthority = "https://identity.example.test/base" }, environment: "QA"), SecurityExpectationField.Authority);
        qa.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent, "QA has no override file; the shared value applies and the Development value is another environment's");
        qa.ValueGroups.Single(g => g.Value == Authority).Status.Should().Be("Other environment");
    }

    [Fact]
    public void LocalhostRedirect_IsAssessedRelativeToTheEnvironment()
    {
        var discovery = Discovery(O(SecurityExpectationField.RedirectUrl, "http://localhost:7284/authentication/login-callback", "Web", "Web/appsettings.Development.json", "AzureAd:RedirectUri"),
            O(SecurityExpectationField.RedirectUrl, "http://localhost:7284/authentication/login-callback", "Web", "Web/appsettings.Local.json", "AzureAd:RedirectUri"));

        var dev = Check(Review(discovery), SecurityExpectationField.RedirectUrl);
        dev.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.EnvironmentMismatch);
        var local = Check(Review(discovery, environment: "Local"), SecurityExpectationField.RedirectUrl);
        local.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.UnapprovedValue, "localhost is ordinary for Local; it is only not yet approved");
        Check(Review(discovery, new() { AllowedRedirectUrls = ["http://localhost:7284/authentication/login-callback"] }), SecurityExpectationField.RedirectUrl)
            .Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent, "an explicitly approved loopback value is the tester's decision");
    }

    [Fact]
    public void RedirectNormalization_DoesNotCollapseDistinctCallbacks()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.RedirectUrl, "https://app.example.test/callback", "Web", "Web/appsettings.json", "Auth:RedirectUri"),
            O(SecurityExpectationField.RedirectUrl, "https://APP.example.test:443/callback/", "Api", "Api/appsettings.json", "Auth:RedirectUri"),
            O(SecurityExpectationField.RedirectUrl, "https://app.example.test/callback?x=1", "Proxy", "Proxy/appsettings.json", "Auth:RedirectUri"));
        Check(Review(discovery), SecurityExpectationField.RedirectUrl).ValueGroups.Select(g => g.Value).Should()
            .BeEquivalentTo(["https://app.example.test/callback", "https://app.example.test/callback/", "https://app.example.test/callback?x=1"]);
    }

    [Fact]
    public void BackendHosts_ShowOnlyExceptions_UnapprovedMismatchAndStale()
    {
        var discovery = Discovery(
            O(SecurityExpectationField.BackendDomain, "https://api.qa.example.test/api", "Web", "Web/appsettings.json", "ApiBaseUrl"),
            O(SecurityExpectationField.BackendDomain, "https://person.qa.example.test", "Proxy", "Proxy/appsettings.json", "ReverseProxy:Clusters:person:Destinations:d:Address"),
            O(SecurityExpectationField.BackendDomain, "https://new.qa.example.test", "Proxy", "Proxy/appsettings.json", "ReverseProxy:Clusters:new:Destinations:d:Address"),
            O(SecurityExpectationField.BackendDomain, "https://localhost:5001", "Web", "Web/appsettings.json", "AuditUrl"));
        var approved = new ApprovedSecurityExpectations { AllowedBackendDomains = ["api.qa.example.test", "person.qa.example.test", "retired.qa.example.test"] };

        var check = Check(Review(discovery, approved, environment: "QA"), SecurityExpectationField.BackendDomain);

        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);
        check.Findings.Select(f => f.Type).Should().BeEquivalentTo([SecurityConfigurationFindingType.UnapprovedValue, SecurityConfigurationFindingType.EnvironmentMismatch, SecurityConfigurationFindingType.StaleApprovedValue]);
        check.Findings.Single(f => f.Type == SecurityConfigurationFindingType.UnapprovedValue).Message.Should().Contain("new.qa.example.test");
        check.ApprovedValues.Should().Contain("retired.qa.example.test", "a stale approval is kept, never removed");
    }

    [Fact]
    public void SingleValue_ApprovedDiffersFromSource_IsAConflict_ButMultiValueAdditionIsUnapproved()
    {
        var authority = Discovery(O(SecurityExpectationField.Authority, "https://identity.example.test/other", "Web", "Web/appsettings.json", "Auth:Authority"));
        Check(Review(authority, new() { ExpectedAuthority = Authority }), SecurityExpectationField.Authority).Assessment.Should().Be(SecurityConfigurationAssessmentState.Conflict);

        var hosts = Discovery(O(SecurityExpectationField.RestHost, "https://a.example.test", "Web", "Web/appsettings.json", "ApiBaseUrl"),
            O(SecurityExpectationField.RestHost, "https://b.example.test", "Api", "Api/appsettings.json", "ApiBaseUrl"));
        var check = Check(Review(hosts, new() { AllowedRestHosts = ["a.example.test"] }), SecurityExpectationField.RestHost);
        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);
        check.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.UnapprovedValue);
    }

    [Fact]
    public void ApprovedValueWithoutCurrentEvidence_IsStale_AndKept()
    {
        var check = Check(Review(Discovery(O(SecurityExpectationField.RestHost, "https://a.example.test", "Web", "Web/appsettings.json", "ApiBaseUrl")), new() { ExpectedAuthority = Authority }), SecurityExpectationField.Authority);
        check.Assessment.Should().Be(SecurityConfigurationAssessmentState.NeedsReview);
        check.Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.StaleApprovedValue);
        check.ApprovedValues.Should().Equal(Authority);
    }

    [Fact]
    public void ValueDeclaredOnlyForOtherEnvironments_IsMissingForTheSelectedOne()
    {
        var discovery = Discovery(O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.Local.json", "AzureAd:ClientId"));
        Check(Review(discovery, environment: "QA"), SecurityExpectationField.ClientId).Findings.Should().ContainSingle(f => f.Type == SecurityConfigurationFindingType.MissingExpectedValue);
    }

    [Fact]
    public void CustomEnvironmentFileNames_AreNotGuessed()
    {
        var discovery = Discovery(O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.Dev.json", "AzureAd:Authority"));
        SecurityConfigurationReviewEngine.FileEnvironment("Web/appsettings.Dev.json").Should().Be("Dev");
        SecurityConfigurationReviewEngine.FileEnvironment("Web/appsettings.qa.json").Should().Be("QA");
        var review = Review(discovery, new() { ExpectedAuthority = Authority });
        Check(review, SecurityExpectationField.Authority).ValueGroups.Single().AppliesToSelectedEnvironment.Should().BeFalse("\"Dev\" is not \"Development\"");
        review.Limitations.Should().Contain(l => l.Contains("\"Dev\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DesignTimeToolingFiles_NeverConfigureADeployedEnvironment()
    {
        var discovery = Discovery(O(SecurityExpectationField.GraphQlHost, "https://localhost:7029/graphql", "Web", "Web/.graphqlrc.json", "extensions:strawberryShake:url"),
            O(SecurityExpectationField.RestHost, "https://localhost:7001", "Api", "Api/Properties/launchSettings.json", "profiles:https:applicationUrl"));
        var review = Review(discovery);
        Check(review, SecurityExpectationField.GraphQlHost).Should().Match<SecurityConfigurationCheckResult>(c => c.Findings.Count == 0 && c.ValueGroups.Single().Status == "Design-time tooling");
        Check(review, SecurityExpectationField.RestHost).Findings.Should().BeEmpty();
        review.Limitations.Should().NotContain(l => l.Contains("Tooling", StringComparison.Ordinal));
    }

    [Fact]
    public void NonEntraAuthority_MakesTenantNotApplicable_WithoutFalseFindings()
    {
        var discovery = Discovery(O(SecurityExpectationField.Authority, "https://keycloak.example.test/realms/shop", "Web", "Web/appsettings.json", "Oidc:Authority"),
            O(SecurityExpectationField.ClientId, "shop-web", "Web", "Web/appsettings.json", "Oidc:ClientId"));
        var review = Review(discovery);
        Check(review, SecurityExpectationField.TenantId).Should().Match<SecurityConfigurationCheckResult>(c => c.Assessment == SecurityConfigurationAssessmentState.NotApplicable && c.Findings.Count == 0);
        review.Attention.Should().NotContain(a => a.CheckId == nameof(SecurityExpectationField.TenantId));
    }

    [Fact]
    public void NoIdentityDeclarations_AreNotAssessed_NotMissing()
    {
        var review = Review(Discovery(O(SecurityExpectationField.RestHost, "https://a.example.test", "Web", "Web/appsettings.json", "ApiBaseUrl")), new() { AllowedRestHosts = ["a.example.test"] });
        foreach (var field in new[] { SecurityExpectationField.Authority, SecurityExpectationField.TenantId, SecurityExpectationField.ClientId, SecurityExpectationField.RedirectUrl })
            Check(review, field).Should().Match<SecurityConfigurationCheckResult>(c => c.Assessment == SecurityConfigurationAssessmentState.NotAssessed && c.Findings.Count == 0);
        review.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);
        review.Attention.Should().BeEmpty();
    }

    [Fact]
    public void NoSource_OrAnotherSnapshot_IsNotAssessed()
    {
        Review(null).Should().Match<SecurityConfigurationReview>(r => !r.HasSource && r.Assessment == SecurityConfigurationAssessmentState.NotAssessed && r.Attention.Count == 0);
        var other = SecurityConfigurationReviewEngine.Review(Discovery(O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.json", "Auth:Authority")), new(), new("Development", Guid.NewGuid()));
        other.HasSource.Should().BeFalse("an assessment of another snapshot is never presented as current");
    }

    [Fact]
    public void PartialSourceAnalysis_IsALimitation_NotAFailure()
    {
        var review = Review(Discovery(O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.json", "Auth:Authority")), new() { ExpectedAuthority = Authority }, status: "Partial");
        review.Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);
        review.Limitations.Should().Contain(l => l.Contains("absence of evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoredValue_ProducesNoFinding()
    {
        var discovery = Discovery(O(SecurityExpectationField.RestHost, "https://a.example.test", "Web", "Web/appsettings.json", "ApiBaseUrl", SecurityCandidateState.Rejected));
        var check = Check(Review(discovery), SecurityExpectationField.RestHost);
        check.ValueGroups.Single().Status.Should().Be("Ignored");
        check.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Review_IsDeterministic_AcrossFiveRecalculations()
    {
        var observations = new[]
        {
            O(SecurityExpectationField.Authority, Authority, "Web", "Web/appsettings.json", "AzureAd:Authority"),
            O(SecurityExpectationField.TenantId, Tenant, "Web", "Web/appsettings.json", "AzureAd:TenantId"),
            O(SecurityExpectationField.ClientId, ClientA, "Web", "Web/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.ClientId, ClientB, "Api", "Api/appsettings.json", "AzureAd:ClientId"),
            O(SecurityExpectationField.RestHost, "https://localhost:5001", "Web", "Web/appsettings.Development.json", "ApiBaseUrl"),
        };
        var approved = new ApprovedSecurityExpectations { ExpectedAuthority = Authority };
        var first = JsonSerializer.Serialize(Review(Discovery(observations), approved), Json);
        for (var i = 0; i < 5; i++)
        {
            // Regrouping already-grouped candidates (a refresh) must not grow candidates, evidence or findings.
            var regrouped = Discovery(observations) with { Candidates = SecurityExpectationValues.Group(Discovery(observations).Candidates) };
            JsonSerializer.Serialize(Review(regrouped, approved), Json).Should().Be(first);
        }
    }

    [Fact]
    public void LargeProject_StaysCompact_OccurrencesAreNotIssues()
    {
        var observations = Enumerable.Range(0, 50).SelectMany(c => Enumerable.Range(0, 10).Select(f =>
            O(SecurityExpectationField.Authority, Authority, $"Component{c:00}", $"Component{c:00}/cfg{f}/appsettings.json", "AzureAd:Authority"))).ToArray();
        var check = Check(Review(Discovery(observations), new() { ExpectedAuthority = Authority }), SecurityExpectationField.Authority);
        check.Occurrences.Should().Be(500);
        check.Components.Should().Be(50);
        check.ValueGroups.Should().ContainSingle();
        check.Findings.Should().BeEmpty();
    }

    [Fact]
    public void ScopedApproval_AddsOneComponent_AndKeepsTheLegacyValue()
    {
        var candidate = SecurityExpectationValues.Group([O(SecurityExpectationField.ClientId, ClientB, "Api", "Api/appsettings.json", "AzureAd:ClientId")]).Single();
        var approved = SecurityExpectationValues.AcceptScoped(new() { ExpectedClientId = ClientA, ScopedClientIds = [new("Web · AzureAd", ClientA)] }, candidate, "Api · AzureAd", "fp", DateTimeOffset.UnixEpoch);
        approved.ExpectedClientId.Should().Be(ClientA);
        approved.ScopedClientIds.Should().BeEquivalentTo([new ScopedSecurityValue("Web · AzureAd", ClientA), new ScopedSecurityValue("Api · AzureAd", ClientB)]);
        approved.Origins.Should().ContainSingle(p => p.Scope == "Api · AzureAd" && p.Origin == SecurityExpectationOrigin.AcceptedFromSource);

        var placeholder = SecurityExpectationValues.Group([O(SecurityExpectationField.ClientId, "00000000-0000-0000-0000-000000000000", "Api", "Api/appsettings.json", "AzureAd:ClientId")]).Single();
        FluentActions.Invoking(() => SecurityExpectationValues.AcceptScoped(new(), placeholder, "Api · AzureAd", "fp", DateTimeOffset.UnixEpoch)).Should().Throw<InvalidOperationException>();
    }

    // ── End to end: analyzer → discovery → review, generic fixture ──

    private static IqrSourceSnapshot Analyze(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
        var (workspace, error) = IqrSourceArchiveReader.Read("generic.zip", memory.ToArray());
        error.Should().BeNull();
        var snapshot = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = "source-analysis", AnalyzedAt = DateTimeOffset.UtcNow, Status = SourceAnalysisStatus.Ready, Archive = new("generic.zip", new string('a', 64), files.Length) };
        snapshot = snapshot with { Architecture = SourceArchitectureAnalyzer.Analyze(snapshot.Id, workspace!, snapshot.AnalyzedAt) };
        return snapshot with { SecurityExpectationsEvidence = SecurityExpectationSourceAnalyzer.Analyze(snapshot, workspace!) };
    }

    private static readonly (string Path, string Content)[] Shop =
    [
        ("Shop.Ui/Shop.Ui.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><ItemGroup><PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.0.0"/></ItemGroup></Project>"""),
        ("Shop.Ui/Program.cs", """builder.Services.AddMsalAuthentication(options => builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication));"""),
        ("Shop.Ui/appsettings.json", """{"AzureAd":{"Authority":"https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111","ClientId":"22222222-2222-2222-2222-222222222222"}}"""),
        ("Shop.Ui/appsettings.Local.json", """{"AzureAd":{"ClientId":"00000000-0000-0000-0000-000000000000"}}"""),
        ("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Microsoft.Identity.Web" Version="1.0.0"/></ItemGroup></Project>"""),
        ("Shop.Api/Program.cs", """builder.Services.AddAuthentication().AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));"""),
        ("Shop.Api/appsettings.json", """{"AzureAd":{"ClientId":"33333333-3333-3333-3333-333333333333","TenantId":"11111111-1111-1111-1111-111111111111"}}"""),
    ];

    [Fact]
    public async Task EndToEnd_GenericProject_ScopesClientIds_AndScopedApprovalMakesTheReviewConsistent()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var snapshot = Analyze(Shop);
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = "qa", IntegrationId = "source-analysis", AnalyzedAt = snapshot.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        await db.SaveChangesAsync();
        var service = new SecurityExpectationDiscoveryService(db);
        var approved = new ApprovedSecurityExpectations { ExpectedAuthority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111", ExpectedTenant = Tenant };

        var discovery = await service.DiscoverAsync("qa", new(snapshot.Id, approved));
        var review = SecurityConfigurationReviewEngine.Review(discovery, approved, new("QA", snapshot.Id));
        var clients = review.Checks.Single(c => c.Field == SecurityExpectationField.ClientId);
        clients.Scopes.Should().HaveCount(2).And.OnlyContain(s => s.EndsWith(" · AzureAd", StringComparison.Ordinal), "one client registration per component");
        clients.Findings.Should().NotContain(f => f.Type == SecurityConfigurationFindingType.ConfigurationConflict);
        clients.Findings.Should().Contain(f => f.Type == SecurityConfigurationFindingType.PlaceholderValue && !f.RequiresAttention, "the zero GUID is Local-only");
        review.Checks.Single(c => c.Field == SecurityExpectationField.Authority).Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);

        // Approve each component's value through the service (scoped approval), as the UI does.
        foreach (var group in clients.ValueGroups.Where(g => g.AppliesToSelectedEnvironment && g.ApprovalCandidateId is not null))
        {
            var response = await service.ReviewAsync("qa", new(discovery.Id, group.ApprovalCandidateId!, discovery.Revision, approved, Scope: group.Scope), true);
            approved = response.Approved;
            discovery = response.Discovery;
        }
        approved.ExpectedClientId.Should().BeNull("scoped approvals never write the legacy project-wide value");
        var after = SecurityConfigurationReviewEngine.Review(discovery, approved, new("QA", snapshot.Id));
        after.Checks.Single(c => c.Field == SecurityExpectationField.ClientId).Assessment.Should().Be(SecurityConfigurationAssessmentState.Consistent);

        // Refresh five times: same values, evidence, findings and approvals.
        var baseline = JsonSerializer.Serialize(after with { }, Json);
        for (var i = 0; i < 5; i++)
        {
            var refreshed = await service.DiscoverAsync("qa", new(snapshot.Id, approved));
            JsonSerializer.Serialize(SecurityConfigurationReviewEngine.Review(refreshed, approved, new("QA", snapshot.Id)), Json).Should().Be(baseline);
        }
    }
}
