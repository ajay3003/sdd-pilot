using BirkNext.AiCodeReview;
using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Authorization checks (Task 9 §17–§39, §56–§67): one model (AuthorizationScenario), suggestions from a generic baseline, literal source
/// metadata and Test Packs, explicit identity mapping, and readiness that never runs an unconfirmed, unmapped, unsafe or unpermitted check.
/// </summary>
public sealed class AuthorizationCheckCatalogTests
{
    private const string Host = "api-dev.example.test";

    private static ApiReviewTarget Rest(bool auth = true, params (string Method, string Path)[] ops) => new()
    {
        TargetId = "rest-1", ApiType = ApiReviewTargetType.Rest, Scheme = "https", Host = Host, Port = 443, BasePath = "/api/orders",
        ServiceName = "Orders API", AuthRequired = auth, Selected = true,
        Operations = ops.Select(o => new ApiReviewOperation { Method = o.Method, Path = o.Path, AuthObserved = auth }).ToList(),
    };

    private static ApiReviewTarget GraphQl(bool auth = true) => new()
    {
        TargetId = "gql-1", ApiType = ApiReviewTargetType.GraphQl, Scheme = "https", Host = Host, Port = 443, BasePath = "/api/orders/graphql",
        ServiceName = "GraphQL · orders", AuthRequired = auth, Selected = true,
        Operations = [new ApiReviewOperation { Method = "POST", Path = "/api/orders/graphql", OperationType = GraphQlOperationType.Query, OperationName = "Orders" }],
    };

    private static CodeEndpointAuthorization Source(string verb, string route, IReadOnlyList<string>? policies = null, IReadOnlyList<string>? roles = null,
        bool authorized = true, bool anonymous = false) =>
        new($"HTTP|OrdersController.Act|Http{char.ToUpper(verb[0])}{verb[1..].ToLower()}", "HTTP", $"{verb} OrdersController.Act", "src/Api/OrdersController.cs", 42,
            authorized, anonymous, "[Authorize] on the operation", policies ?? [], roles ?? [], route);

    // ── §56 One model ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThereIsOneAuthorizationStore_TheExistingScenarioList_PlusIdentityMappings()
    {
        typeof(RuntimeSecurityExpectations).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments()[0].Name.Contains("Authorization", StringComparison.Ordinal))
            .Select(p => p.Name).Should().BeEquivalentTo(["AuthorizationScenarios", "AuthorizationIdentities"], "checks reuse AuthorizationScenario; no parallel store");

        // Legacy scenarios (no origin/confirmation in JSON) stay manual and confirmed — nothing existing is silently disabled.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AuthorizationScenario>("""{"scenarioId":"x","url":"https://h/x","identities":[{"alias":"anonymous"}]}""")!;
        legacy.Origin.Should().Be(AuthorizationCheckOrigin.Manual);
        legacy.Confirmation.Should().Be(AuthorizationCheckConfirmation.Confirmed);
    }

    // ── §57 Predefined baseline ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AProtectedApi_GetsTheUnauthenticatedBaseline_ExpectedDenied_AndAPublicApiDoesNot()
    {
        var rest = AuthorizationCheckCatalog.Baseline(Rest(true, ("GET", "/api/orders/list"), ("POST", "/api/orders/list")))!;
        rest.Origin.Should().Be(AuthorizationCheckOrigin.PredefinedBaseline);
        rest.Confirmation.Should().Be(AuthorizationCheckConfirmation.Confirmed);
        rest.Method.Should().Be("GET");
        rest.Url.Should().Be($"https://{Host}/api/orders/list");
        rest.Identities.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Alias = "anonymous", Expected = AuthorizationExpectation.Deny });

        var gql = AuthorizationCheckCatalog.Baseline(GraphQl())!;
        gql.GraphQlQuery.Should().Be("{ __typename }");

        AuthorizationCheckCatalog.Suggestions([Rest(auth: false, ("GET", "/api/orders/list"))]).Should().BeEmpty("a public API has no authentication to verify");
        AuthorizationCheckCatalog.Baseline(Rest(true, ("POST", "/api/orders/list"))).Should().BeNull("no safe request exists, so none is invented");
        AuthorizationCheckCatalog.Baseline(Rest(true, ("GET", "/api/orders/{id}"))).Should().BeNull("a path template is not a concrete request");
    }

    // ── §58 Source-derived ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANamedPolicy_SuggestsAllowForTheHolderAndDenyWithoutIt_BothNeedingConfirmationAndIdentities()
    {
        var target = Rest(true, ("GET", "/api/orders/admin/users"));
        var suggestions = AuthorizationCheckCatalog.FromSource(Source("GET", "/api/orders/admin/users", policies: ["AdminOnly"]), [target]);

        var scenario = suggestions.Should().ContainSingle().Subject;
        scenario.Origin.Should().Be(AuthorizationCheckOrigin.SourceDerived);
        scenario.Confirmation.Should().Be(AuthorizationCheckConfirmation.NeedsConfirmation);
        scenario.Evidence.Should().Contain("policy \"AdminOnly\"").And.Contain("OrdersController.cs:42");
        scenario.Identities.Select(i => (i.Role, i.Alias, i.Expected)).Should().Equal(
            ("AdminOnly", "", AuthorizationExpectation.Allow), ("Without AdminOnly", "", AuthorizationExpectation.Deny));

        var summary = AuthorizationCheckCatalog.Build(new RuntimeSecurityExpectations(), [target], [Source("GET", "/api/orders/admin/users", policies: ["AdminOnly"])], executionPermitted: true);
        summary.Items.Where(i => i.Origin == AuthorizationCheckOrigin.SourceDerived).Should().OnlyContain(i => i.Readiness == AuthorizationCheckReadiness.NeedsConfirmation);
        AuthorizationScenarioRules.Validate(scenario).Should().Contain("needs confirmation", "the shared rule (and so the backend) refuses it");
    }

    [Fact]
    public void RoleTemplatesMatchConcreteOperations_AndUnmatchedSourceEndpointsSuggestNothing()
    {
        var target = Rest(true, ("GET", "/api/orders/42"));
        AuthorizationCheckCatalog.FromSource(Source("GET", "/api/orders/{id}", roles: ["Caseworker"]), [target])
            .Should().ContainSingle().Which.Url.Should().Be($"https://{Host}/api/orders/42");
        AuthorizationCheckCatalog.FromSource(Source("GET", "/api/elsewhere", roles: ["Caseworker"]), [target]).Should().BeEmpty();
        AuthorizationCheckCatalog.FromSource(Source("GET", "/api/orders/42", roles: ["Caseworker"]) with { Route = null }, [target])
            .Should().BeEmpty("without a literal route nothing can be matched to an operation");
    }

    // ── §59 No role guessing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEndpointThatIsOnlyAuthenticated_GetsOnlyTheBaseline_NeverInventedRoles()
    {
        var target = Rest(true, ("GET", "/api/orders/list"));
        AuthorizationCheckCatalog.FromSource(Source("GET", "/api/orders/list"), [target]).Should().BeEmpty();

        var all = AuthorizationCheckCatalog.Suggestions([target], [Source("GET", "/api/orders/list")]);
        all.Should().ContainSingle().Which.Origin.Should().Be(AuthorizationCheckOrigin.PredefinedBaseline);
        all.SelectMany(s => s.Identities).Should().OnlyContain(i => i.Alias == "anonymous" && i.Expected == AuthorizationExpectation.Deny,
            "no Admin allowed / User denied rule exists without evidence");
    }

    // ── §60 Test Pack ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestPackExpectations_AreGenericSelectors_ConfirmedAsTheyDeclare()
    {
        var target = Rest(true, ("GET", "/api/orders/list"));
        var confirmed = AuthorizationCheckCatalog.FromTestPack(new TestPackAuthorizationExpectation("orders", "Caseworker", AuthorizationExpectation.Allow) { Path = "/api/orders/list" }, [target])!;
        confirmed.Origin.Should().Be(AuthorizationCheckOrigin.TestPack);
        confirmed.Confirmation.Should().Be(AuthorizationCheckConfirmation.Confirmed);
        confirmed.Identities.Single().Role.Should().Be("Caseworker");

        AuthorizationCheckCatalog.FromTestPack(new TestPackAuthorizationExpectation("orders", "", AuthorizationExpectation.Deny) { Path = "/api/orders/list", Confirmed = false }, [target])!
            .Should().BeEquivalentTo(new { Confirmation = AuthorizationCheckConfirmation.NeedsConfirmation }, o => o.ExcludingMissingMembers());
        AuthorizationCheckCatalog.FromTestPack(new TestPackAuthorizationExpectation("payments", "Caseworker", AuthorizationExpectation.Allow) { Path = "/x" }, [target])
            .Should().BeNull("a selector that matches no selected API yields nothing");
    }

    // ── §61 Identity mapping ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARoleCheck_NeedsIdentityMapping_UntilTheTargetEnvironmentMapsIt_ThenIsReady()
    {
        var target = Rest(true, ("GET", "/api/orders/list"));
        var pack = new TestPackAuthorizationExpectation("orders", "Admin", AuthorizationExpectation.Allow) { Path = "/api/orders/list" };
        var settings = new RuntimeSecurityExpectations();

        var unmapped = AuthorizationCheckCatalog.Build(settings, [target], packExpectations: [pack], executionPermitted: true);
        unmapped.Items.Single(i => i.Origin == AuthorizationCheckOrigin.TestPack).Readiness.Should().Be(AuthorizationCheckReadiness.NeedsIdentity);
        AuthorizationCheckCatalog.UnmappedRoles(unmapped).Should().Equal("Admin");
        AuthorizationCheckCatalog.Runnable(unmapped, settings.AuthorizationIdentities).Should().OnlyContain(s => s.Origin == AuthorizationCheckOrigin.PredefinedBaseline);

        settings.AuthorizationIdentities.Add(new AuthorizationIdentityMapping { Role = "admin", Alias = "test-admin" });
        var mapped = AuthorizationCheckCatalog.Build(settings, [target], packExpectations: [pack], executionPermitted: true);
        mapped.Items.Single(i => i.Origin == AuthorizationCheckOrigin.TestPack).Readiness.Should().Be(AuthorizationCheckReadiness.Ready);
        var sent = AuthorizationCheckCatalog.Runnable(mapped, settings.AuthorizationIdentities).Single(s => s.Origin == AuthorizationCheckOrigin.TestPack);
        sent.Identities.Single().Alias.Should().Be("test-admin", "the mapped alias is resolved at send time; the stored check keeps the role");
        AuthorizationScenarioRules.Validate(sent).Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(settings).Should().NotContainAny("password", "token", "secret");
    }

    // ── §38, §66 Execution boundary and safety ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChecksAreReadyOnlyWhenExecutionIsPermitted_AndUnsafeOperationsAreNeverRunnable()
    {
        var target = Rest(true, ("GET", "/api/orders/list"), ("DELETE", "/api/orders/list"));
        var settings = new RuntimeSecurityExpectations();
        settings.AuthorizationScenarios.Add(new AuthorizationScenario
        {
            ScenarioId = "delete", Url = $"https://{Host}/api/orders/list", Method = "DELETE", TargetId = target.TargetId,
            Identities = [new() { Alias = "anonymous", Expected = AuthorizationExpectation.Deny }],
        });

        var notPermitted = AuthorizationCheckCatalog.Build(settings, [target], executionPermitted: false, executionReason: "Not registered.");
        notPermitted.Items.Where(i => i.Origin == AuthorizationCheckOrigin.PredefinedBaseline).Should().OnlyContain(i => i.Readiness == AuthorizationCheckReadiness.BlockedByExecutionPermission && i.Reason == "Not registered.");
        notPermitted.Ready.Should().Be(0);
        AuthorizationCheckCatalog.Build(settings, [target], executionPermitted: null).Ready.Should().Be(0, "unknown permission is not permission");

        var permitted = AuthorizationCheckCatalog.Build(settings, [target], executionPermitted: true);
        permitted.Items.Single(i => i.Configured).Readiness.Should().Be(AuthorizationCheckReadiness.UnsafeOperationApprovalRequired);
        AuthorizationCheckCatalog.Runnable(permitted, []).Should().NotContain(s => s.Method == "DELETE");
        permitted.Headline.Should().Be("2 available · 1 ready · 1 blocked");
    }

    // ── §67 Allow/deny comparison (the shared rule the executor uses) ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthorizationExpectation.Deny, 403, AuthorizationOutcome.VerifiedDeny)]
    [InlineData(AuthorizationExpectation.Deny, 200, AuthorizationOutcome.UnexpectedAllow)]
    [InlineData(AuthorizationExpectation.Allow, 403, AuthorizationOutcome.UnexpectedDeny)]
    [InlineData(AuthorizationExpectation.Allow, 200, AuthorizationOutcome.VerifiedAllow)]
    public void ExpectedOutcomeIsComparedWithTheActualStatus(AuthorizationExpectation expected, int status, AuthorizationOutcome outcome)
    {
        var scenario = new AuthorizationScenario { ScenarioId = "s", Url = $"https://{Host}/x", Identities = [new() { Alias = "anonymous", Expected = expected }] };
        AuthorizationScenarioRules.Classify(scenario, scenario.Identities[0], executed: true, status, null, null, null).Outcome.Should().Be(outcome);
    }

    [Fact]
    public void ConfiguredSuccessCodesOverrideTheDefault_AndAntiDisclosure404IsExplicit()
    {
        var scenario = new AuthorizationScenario { ScenarioId = "s", Url = $"https://{Host}/x", SuccessStatusCodes = [204], Identities = [new() { Alias = "a", Expected = AuthorizationExpectation.Allow }] };
        AuthorizationScenarioRules.Classify(scenario, scenario.Identities[0], true, 200, null, null, null).Outcome.Should().Be(AuthorizationOutcome.NotVerified);
        AuthorizationScenarioRules.Classify(scenario, scenario.Identities[0], true, 204, null, null, null).Outcome.Should().Be(AuthorizationOutcome.VerifiedAllow);
        var deny = new AuthorizationScenarioIdentity { Alias = "anonymous", Expected = AuthorizationExpectation.Deny, NotFoundMeansDeny = true };
        AuthorizationScenarioRules.Classify(scenario, deny, true, 404, null, null, null).Outcome.Should().Be(AuthorizationOutcome.VerifiedDeny);
    }

    // ── Deep link safety ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api-quality-review?targets=a,b", true)]
    [InlineData("/api-quality-review", true)]
    [InlineData("https://evil.example/api-quality-review", false)]
    [InlineData("//evil.example/api-quality-review", false)]
    [InlineData("/admin/system-settings", false)]
    [InlineData("/api-quality-reviewer", false)]
    public void TheReturnLinkAcceptsOnlyTheApiReviewRoute(string href, bool allowed) =>
        FrontendAnalysisSettingsReturn.IsAllowed(href).Should().Be(allowed);
}
