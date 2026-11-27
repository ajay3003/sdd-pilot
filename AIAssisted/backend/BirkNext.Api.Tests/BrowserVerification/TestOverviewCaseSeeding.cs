using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.TestPlanning;
using Microsoft.Playwright;
using F = BirkNext.Api.Tests.BrowserVerification.TestOverviewFixture;

namespace BirkNext.Api.Tests.BrowserVerification;

/// <summary>
/// Seeds <see cref="TestOverviewFixture.AuroraCases"/> into the isolated host through the same API calls the pages make — the project's discovery (as
/// Finn og forstå stores it), «Opprett testplan» from chosen scenarios, the reviewer's decision, and the tester's case edits (blocking prerequisite,
/// Test Data decision, verification method). The calls go through the browser context (its local-session cookie, the frontend Origin and the
/// client header), so the API's own write authorization (Local Tester Mode on loopback) applies; nothing bypasses the domain.
/// </summary>
public static partial class TestOverviewCaseSeeding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The seeded cases: fixture case → (plan id, case id) as the API stored them.</summary>
    public sealed record Seeded(IReadOnlyDictionary<F.CaseFixture, (Guid PlanId, string CaseId)> Cases, string ProjectKey);

    public static async Task<Seeded> SeedAsync(IsolatedBrowserHost host, IPage page, F.Application app)
    {
        // The project key the pages use (Testoversikt asks the overview with it).
        var request = await page.RunAndWaitForRequestAsync(
            () => page.GotoAsync($"{host.WebUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 }),
            r => r.Url.Contains("/project/overview?projectKey=", StringComparison.Ordinal), new() { Timeout = 180000 });
        var key = Uri.UnescapeDataString(KeyRe().Match(request.Url).Groups[1].Value);

        // Finn og forstå at project scope computes and stores the project's discovery (what «Opprett testplan» chooses from).
        await page.GotoAsync($"{host.WebUrl}/manual-testing/discover", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        await page.GetByTestId("mtd").WaitForAsync(new() { Timeout = 120000 });
        await Assertions.Expect(page.GetByTestId("mtd-loading")).ToHaveCountAsync(0, new() { Timeout = 240000 });
        ScenarioDiscoveryResult? discovery = null;
        for (var attempt = 0; attempt < 60 && discovery is null; attempt++)
        {
            discovery = await GetAsync<ScenarioDiscoveryResult>(host, page, $"api/test-plan-suggestions/discover/last?projectKey={Uri.EscapeDataString(key)}");
            if (discovery is null) await page.WaitForTimeoutAsync(2000);
        }
        if (discovery is null) throw new InvalidOperationException("No stored discovery for the fixture project after opening Finn og forstå.");

        var byKey = new Dictionary<F.Key, DiscoveredScenario>();
        foreach (var c in discovery.Candidates.Where(c => c.CapabilityId == TestScenarioDiscoveryEngineIds.Specification))
            foreach (var token in TestOverviewBrowserSession.Tokens(c.Title).Distinct())
                if (SpecOf(c) is { } spec) byKey[new F.Key(spec, token)] = c;

        var seeded = new Dictionary<F.CaseFixture, (Guid, string)>();
        foreach (var plan in F.CasesOf(app).GroupBy(c => c.Plan))
        {
            var chosen = plan.Select(c => byKey.TryGetValue(c.Scenario, out var d) ? d
                : throw new InvalidOperationException($"Scenario {c.Scenario} is not in the stored discovery ({byKey.Count} known).")).ToList();
            var draft = await SendAsync<TestPlanDraft>(host, page, "POST", "api/test-plan-suggestions/project-drafts", new CreateProjectTestPlanRequest
            {
                ProjectKey = key, DiscoveryId = discovery.DiscoveryId, CandidateIds = [.. chosen.Select(d => d.CandidateId)], PlanName = plan.Key,
                CreationRequestId = "fixture-" + plan.Key.GetHashCode().ToString("x"),
            });
            // Each scenario's case, found by the scenario it was prepared from (never by position).
            var caseOf = plan.ToDictionary(c => c, c =>
            {
                var candidate = byKey[c.Scenario];
                return draft.Cases.Single(x => x.Source.ScenarioId == candidate.CandidateId || x.Generated.SourceIdentity == candidate.SourceIdentity && x.Generated.SourceIdentity.Length > 0);
            });
            foreach (var (fixture, testCase) in caseOf)
            {
                var state = fixture.State;
                if (state is F.CaseState.Approved or F.CaseState.ApprovedReady or F.CaseState.NeedsClarification)
                    draft = await SendAsync<TestPlanDraft>(host, page, "POST", $"api/test-plan-suggestions/drafts/{draft.Id}/review",
                        new ReviewTestCaseRequest(draft.Revision, testCase.Source.ScenarioId,
                            state == F.CaseState.NeedsClarification ? TestCaseReviewState.NeedsClarification : TestCaseReviewState.Accepted, "Fixture"));
            }
            // The tester's edits, saved once: every case id is listed (a save keeps only the listed cases), only the edited ones carry a part.
            var parts = caseOf.Where(e => e.Key.State is F.CaseState.ApprovedReady or F.CaseState.Blocked).Select(e =>
            {
                var current = draft.Cases.Single(x => x.Source.ScenarioId == e.Value.Source.ScenarioId);
                var edits = e.Key.State == F.CaseState.Blocked
                    ? current.Edits with { BlockedBy = "Testmiljøet mangler tilgang (fixture)" }
                    : current.Edits with { TestDataNeed = TestDataNeed.NotRequired, ObservationMethod = "Les resultatet på skjermen (fixture)" };
                return new TestCaseHumanPart { ScenarioId = current.Source.ScenarioId, SuiteId = current.SuiteId, Edits = edits, Risk = current.Risk };
            }).ToList();
            if (parts.Count > 0)
                draft = await SendAsync<TestPlanDraft>(host, page, "PUT", $"api/test-plan-suggestions/drafts/{draft.Id}", new SaveTestPlanDraftRequest
                {
                    BaseRevision = draft.Revision, PlanName = draft.PlanName, DeliveryLabel = draft.DeliveryLabel,
                    ScenarioIds = [.. draft.Cases.Select(c => c.Source.ScenarioId)], Suites = draft.Suites, Cases = parts,
                });
            foreach (var (fixture, testCase) in caseOf) seeded[fixture] = (draft.Id, testCase.Source.ScenarioId);

            // The seeded states are what the fixture says (checked once here, so a journey failure is never a seeding failure).
            foreach (var (fixture, testCase) in caseOf)
            {
                var saved = draft.Cases.Single(x => x.Source.ScenarioId == testCase.Source.ScenarioId);
                var approved = saved.Review == TestCaseReviewState.Accepted;
                if (approved != F.IsApproved(fixture.State) || saved.Testability.Ready != F.IsReady(fixture.State)
                    || (fixture.State == F.CaseState.NeedsClarification) != (saved.Review == TestCaseReviewState.NeedsClarification))
                    throw new InvalidOperationException($"Seeded case {fixture.Scenario} is {saved.Review}/ready={saved.Testability.Ready}, fixture wants {fixture.State}: " +
                        string.Join("; ", saved.Testability.Blockers));
            }
        }
        return new(seeded, key);
    }

    private static string? SpecOf(DiscoveredScenario c)
    {
        var path = c.Story?.SpecificationPath ?? c.Evidence.FirstOrDefault(e => e.Kind == "specification")?.Reference?.Split('#')[0];
        return path is null ? null : TestOverviewBrowserSession.Normalize(path);
    }

    private static Dictionary<string, string> Headers(IsolatedBrowserHost host) => new()
    {
        ["Origin"] = host.WebUrl, [BirkNext.Api.Services.LocalSecurity.LocalSessionService.ClientHeader] = "isolated-browser-fixture", ["Content-Type"] = "application/json",
    };

    private static async Task<T?> GetAsync<T>(IsolatedBrowserHost host, IPage page, string path)
    {
        var response = await page.Context.APIRequest.GetAsync($"{host.ApiUrl}/{path}", new() { Headers = Headers(host) });
        if (response.Status == 204) return default;
        if (!response.Ok) throw new InvalidOperationException($"GET {path}: {response.Status} {await response.TextAsync()}");
        return JsonSerializer.Deserialize<T>(await response.BodyAsync(), Json);
    }

    private static async Task<T> SendAsync<T>(IsolatedBrowserHost host, IPage page, string method, string path, object body)
    {
        var response = await page.Context.APIRequest.FetchAsync($"{host.ApiUrl}/{path}", new()
            { Method = method, Headers = Headers(host), DataString = JsonSerializer.Serialize(body, body.GetType(), Json) });
        if (!response.Ok) throw new InvalidOperationException($"{method} {path}: {response.Status} {await response.TextAsync()}");
        return JsonSerializer.Deserialize<T>(await response.BodyAsync(), Json)!;
    }

    [GeneratedRegex(@"projectKey=([^&]+)")]
    private static partial Regex KeyRe();
}

/// <summary>The discovery capability id of specification scenarios (the engine's constant, read once here).</summary>
internal static class TestScenarioDiscoveryEngineIds
{
    public const string Specification = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine.Specification;
}
