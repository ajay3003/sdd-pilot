using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Test type (UI smoke, UI functional, Business E2E …) is a classification separate from criticality (Critical / Diagnostic). Approval of a
/// suggestion sets it from the suggestion's category; flows stored before it existed load as Unknown ("not classified") and are never
/// reclassified automatically — nor does their definition fingerprint change, so earlier runs stay tied to them.
/// </summary>
public sealed class CriticalE2ETestTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-e2e-testtype-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(SuggestionCategory.UiSmoke, CriticalE2ETestType.UiSmoke)]
    [InlineData(SuggestionCategory.UiFunctional, CriticalE2ETestType.UiFunctional)]
    [InlineData(SuggestionCategory.BusinessE2E, CriticalE2ETestType.BusinessE2E)]
    [InlineData((SuggestionCategory)99, CriticalE2ETestType.Other)]
    public void ApprovalMapsTheSuggestionCategoryToTheTestType(SuggestionCategory category, CriticalE2ETestType expected) =>
        CriticalE2ETestTypes.FromSuggestion(category).Should().Be(expected);

    // A flows.json written before TestType existed (PascalCase, as the store writes it).
    private const string LegacyFlows = """
        [
          {
            "Id": "legacy-flow",
            "Module": "M02",
            "Name": "Old critical flow",
            "Kind": "Critical",
            "Mode": "CompanionBrowser",
            "ProfileId": "dev",
            "EnvironmentId": "env-1",
            "Enabled": true,
            "RequiredForRelease": true,
            "Steps": [ { "StepId": "s1", "BrowserAction": "AssertVisible", "Selector": { "Kind": "TestId", "Value": "status" }, "IsFinalAssertion": true } ],
            "Version": 3
          }
        ]
        """;

    private CriticalE2EStore LegacyStore()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "flows.json"), LegacyFlows);
        return new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
    }

    [Fact]
    public void ALegacyFlowWithoutTestTypeLoadsAsUnknown_WithItsCriticalityUnchanged()
    {
        var flow = LegacyStore().Flow("legacy-flow")!;

        flow.TestType.Should().Be(CriticalE2ETestType.Unknown, "a flow stored before classification existed is 'not classified', never guessed");
        flow.Kind.Should().Be(CriticalE2EFlowKind.Critical);
        flow.RequiredForRelease.Should().BeTrue();
        flow.Version.Should().Be(3);
    }

    [Fact]
    public void ALegacyFlowKeepsItsFingerprintAndVersion_UntilAPersonClassifiesIt()
    {
        var store = LegacyStore();
        var legacy = store.Flow("legacy-flow")!;
        var fingerprint = CriticalE2EStore.Fingerprint(legacy);

        // Re-saving the unchanged definition is not a new version, and Unknown is not written: the material content is what it was.
        store.Save(legacy).Version.Should().Be(3);
        File.ReadAllText(Path.Combine(_dir, "flows.json")).Should().NotContain("TestType");
        CriticalE2EStore.Fingerprint(store.Flow("legacy-flow")!).Should().Be(fingerprint, "runs recorded against the legacy definition stay current");

        // Classifying is a definition edit: a new version, persisted, criticality untouched.
        var classified = store.Save(legacy with { TestType = CriticalE2ETestType.BusinessE2E });
        classified.Version.Should().Be(4);
        CriticalE2EStore.Fingerprint(classified).Should().NotBe(fingerprint);
        var reloaded = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance).Flow("legacy-flow")!;
        reloaded.TestType.Should().Be(CriticalE2ETestType.BusinessE2E);
        reloaded.Kind.Should().Be(CriticalE2EFlowKind.Critical);
    }

    [Fact]
    public void TheSummaryCarriesTheTestTypeNextToTheKind()
    {
        var flow = LegacyStore().Flow("legacy-flow")! with { TestType = CriticalE2ETestType.UiSmoke };

        var summary = CriticalE2ECoverage.Summarize(flow, [], null);

        summary.TestType.Should().Be(CriticalE2ETestType.UiSmoke);
        summary.Kind.Should().Be(CriticalE2EFlowKind.Critical, "test type never changes criticality");
    }
}
