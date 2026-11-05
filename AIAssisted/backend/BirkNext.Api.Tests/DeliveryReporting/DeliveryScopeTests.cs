using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Source + Functional scope for Delivery: an M2LB-language-shaped fixture (translation features under one decision record, a translation feature
/// that only cites it, one that shares only its wording, unrelated features, an English-worded feature that is not a translation). Assertions are
/// semantic (which features are in or out, and why), never archive totals.
/// </summary>
public sealed class DeliveryScopeTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Oslo");
    private const string Adr = "capability:decision:adr-029";
    private const string AutorisasjonKey = "autorisasjonservice::005-translate-adr-029", FrontendKey = "frontend::007-translate-adr-029",
        SourceCodeKey = "autorisasjonservice::001-translate-source-english", TjenesteKey = "tjenesteservice::005-translate-source-english",
        RenameKey = "platform::001-rename-contracts-adr-029", PlacementsKey = "tjenesteservice::004-child-placements-period", PolishKey = "frontend::003-designer-polish", LabelsKey = "frontend::008-english-labels";

    private static (string, string)[] Feature(string module, string folder, string title, int total, int done, string extra = "") =>
    [
        ($"{module}/specs/{folder}/spec.md", Spec(title) + extra),
        ($"{module}/specs/{folder}/tasks.md", Tasks(total, done)),
    ];

    private static readonly (string, string)[] Unrelated =
    [
        .. Feature("TjenesteService", "004-child-placements-period", "Children in Placements per Period", 9, 5),
        .. Feature("Frontend", "003-designer-polish", "Designer Feedback Polish", 3, 3),
    ];

    private static readonly (string, string)[] Language =
    [
        .. Unrelated,
        .. Feature("AutorisasjonService", "005-translate-adr-029", "Translate Autorisasjon to English (ADR-029)", 10, 4, "\n\nPer ADR-029 the source code is English.\n"),
        .. Feature("Frontend", "007-translate-adr-029", "Translate Frontend to English (ADR-029)", 2, 2),
        // Declared under the same decision but worded differently: it belongs to the change and must not erase the intent's name.
        .. Feature("Platform", "001-rename-contracts-adr-029", "Rename the platform-owned contract names (ADR-029, platform level)", 1, 1),
        .. Feature("AutorisasjonService", "001-translate-source-english", "Translate Source Code to English", 3, 1, "\n\nSee ADR-029 (source code language).\n"),
        .. Feature("TjenesteService", "005-translate-source-english", "Translate the Tjeneste module to English", 4, 0, "\n\nNaming rule: docs/glossary.md.\n"),
        .. Feature("Frontend", "008-english-labels", "English labels for the dashboard", 2, 1),
        .. Feature("Proxy", "002-adr026-health-checks", "ADR-026 Differentiated Health Checks", 2, 2),
    ];

    private static IReadOnlyList<WorkItemIntentInput> Intents(ParsedDeliverySnapshot parsed, Guid snapshotId) =>
        [.. parsed.Features.Select(f => new WorkItemIntentInput(f.Module, f.Key, f.Title, [.. f.Stories.Select(s => s.Title)], f.SpecPath, $"delivery:{snapshotId}", f.DecisionReferences))];

    [Fact]
    public void Parser_RecordsTheDecisionRecordsASpecCites_Normalised()
    {
        var parsed = Parse(Language, "M2LB-language.zip");
        parsed.Features.Single(f => f.Key == SourceCodeKey).DecisionReferences.Should().Equal("ADR-029");
        parsed.Features.Single(f => f.Key == TjenesteKey).DecisionReferences.Should().BeEmpty("a glossary reference is not a decision record");
        DecisionReferences.InText("adr 29, ADR-0029 and ADR_029; not MADR-1 or ADR-0").Should().Equal("ADR-029");
        DecisionReferences.Declared("Translate Frontend to English", "frontend::007-translate-adr-029").Should().Equal("ADR-029");
    }

    [Fact]
    public void DecisionRule_GroupsDeclaringFeatures_ProposesCitingAndSameWordingFeatures_AndKeepsIdentitiesSeparate()
    {
        var parsed = Parse(Language, "M2LB-language.zip");
        var result = CorrelationRules.Correlate(CorrelationInput.Empty("proj") with { WorkItems = Intents(parsed, Guid.NewGuid()) });

        var capability = result.Entities.Single(e => e.Type == CanonicalEntityType.Capability && e.IdentityKey == "decision:adr-029");
        capability.DisplayName.Should().Be("ADR-029: Translate English");
        var into = result.Relations.Where(r => r.TargetKey == capability.Key && r.RuleId == CorrelationRules.DecisionRule).ToList();
        string Feature(string key) => CanonicalKeys.EntityKey(CanonicalEntityType.Feature, CorrelationRules.FeatureIdentity(parsed.Features.Single(f => f.Key == key).Module, key));

        into.Where(r => r.Resolution == RelationResolution.Derived).Select(r => r.SourceKey).Should().BeEquivalentTo([Feature(AutorisasjonKey), Feature(FrontendKey), Feature(RenameKey)]);
        into.Single(r => r.SourceKey == Feature(SourceCodeKey)).Should().Match<CanonicalRelationFact>(r => r.Resolution == RelationResolution.Candidate && r.Confidence == CorrelationConfidence.Medium);
        into.Single(r => r.SourceKey == Feature(TjenesteKey)).Should().Match<CanonicalRelationFact>(r => r.Resolution == RelationResolution.Candidate && r.Confidence == CorrelationConfidence.Low);
        into.Select(r => r.SourceKey).Should().NotContain([Feature(PlacementsKey), Feature(PolishKey), Feature(LabelsKey)], "English wording alone and unrelated work never join the change");
        // One declaring feature is no shared intent; and grouping never merges features.
        result.Entities.Should().NotContain(e => e.IdentityKey == "decision:adr-026");
        result.Entities.Count(e => e.Type == CanonicalEntityType.Feature).Should().Be(parsed.Features.Count);
    }

    // ── Scope service over a frozen report ──────────────────────────────────────────────────────────────────────────

    private readonly string _db = $"scope-{Guid.NewGuid():N}";
    private CanonicalInterpretationDbContext Db() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_db).Options);

    private sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private static WeeklyDeliveryReport Report(string currentFile = "M2LB-language.zip")
    {
        var baseline = Content(Parse(Unrelated, "M2LB -main.zip"), Monday, "main");
        var current = Content(Parse(Language, currentFile), Friday, "language");
        return WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", Friday, baseline, current, DeliverySnapshotComparison.Compare(baseline, current), Oslo);
    }

    private async Task<(DeliveryScopeService Service, CanonicalInterpretationDbContext Db, CorrelationResult Result)> ScopedAsync(WeeklyDeliveryReport report)
    {
        var db = Db();
        var canonical = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
        var result = CorrelationRules.Correlate(CorrelationInput.Empty("proj") with { WorkItems = Intents(Parse(Language, "M2LB-language.zip"), report.Current.SnapshotId) });
        await canonical.PersistAsync("proj", result, [new(BirkNext.Snapshots.SnapshotType.Delivery, report.Current.SnapshotId.ToString(), "Delivery snapshot")], [], "test", default);
        return (new DeliveryScopeService(canonical), db, result);
    }

    [Fact]
    public async Task FunctionalScope_ScopesEveryNumber_ToEstablishedMembers_AndListsCandidatesApart()
    {
        var report = Report();
        var (service, _, _) = await ScopedAsync(report);

        var all = await service.ForReportAsync(report.ProjectId, report, new(), [], default);
        all.Scope.Should().BeNull("no filter = the frozen report unchanged");
        all.Report.Should().BeSameAs(report);
        var option = all.Scopes.Single(s => s.Id == Adr);
        option.Should().Match<DeliveryFunctionalScopeOption>(o => o.Kind == FunctionalScopeKind.ChangeIntent && o.CurrentFeatures == 3 && o.BaselineFeatures == 0 && o.PendingCandidates == 2);
        all.Sources.Select(s => s.Id).Should().Equal("package:m2lb-language", "package:m2lb -main");

        var view = await service.ForReportAsync(report.ProjectId, report, new(FunctionalScopeId: Adr), [], default);
        view.Report!.Stories.Select(s => s.FeatureKey).Should().BeEquivalentTo([AutorisasjonKey, FrontendKey, RenameKey]);
        view.Members.Should().OnlyContain(m => m.Why.Contains("declared in the feature"));
        view.Candidates.Select(c => c.FeatureKey).Should().BeEquivalentTo([SourceCodeKey, TjenesteKey], "candidates are shown for review, never counted");
        Metric(view.Report, "inventory-features").Should().Be(3);
        Metric(view.Report, "inventory-tasks").Should().Be(13);
        Metric(view.Report, "inventory-checked").Should().Be(7);
        view.Report.Attention.Where(a => a.FeatureKey is not null).Select(a => a.FeatureKey).Should().OnlyContain(k => k == AutorisasjonKey || k == FrontendKey || k == RenameKey);
        view.Scope!.ScopeInBaseline.Should().BeFalse();
        view.Notices.Should().ContainSingle(n => n.StartsWith("Functional scope introduced in the current snapshot"));
    }

    [Fact]
    public async Task ConfirmedCandidate_JoinsTheScope_RejectedOneNever()
    {
        var report = Report();
        var (service, db, result) = await ScopedAsync(report);
        var canonical = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
        string Relation(string key) => result.Relations.Single(r => r.RuleId == CorrelationRules.DecisionRule && r.SourceKey.EndsWith(key, StringComparison.Ordinal)).Key;
        await canonical.DecideAsync("proj", new(ReviewTargetKind.Relation, Relation(TjenesteKey), ReviewDecisionOutcome.Confirmed, "tester"), null, default);
        await canonical.DecideAsync("proj", new(ReviewTargetKind.Relation, Relation(SourceCodeKey), ReviewDecisionOutcome.Rejected, "tester"), null, default);

        var view = await service.ForReportAsync(report.ProjectId, report, new(FunctionalScopeId: Adr), [], default);
        view.Report!.Stories.Select(s => s.FeatureKey).Should().BeEquivalentTo([AutorisasjonKey, FrontendKey, RenameKey, TjenesteKey]);
        view.Members.Single(m => m.FeatureKey == TjenesteKey).Why.Should().Contain("confirmed by a person");
        view.Candidates.Should().BeEmpty("the rejected one is not proposed again");
    }

    [Fact]
    public async Task Source_IsTheImportedSource_ByStableId_AndAbsentFromTheBaselineIsStated()
    {
        var report = Report("M2LB-language (2).zip");
        var (service, _, _) = await ScopedAsync(report);

        var language = await service.ForReportAsync(report.ProjectId, report, new("package:m2lb-language"), [], default);
        language.InvalidFilters.Should().BeEmpty("a re-downloaded archive keeps its source id");
        language.Report!.Stories.Should().NotContain(s => s.RemovedFeature).And.HaveCount(9);
        language.Scope!.SourceInBaseline.Should().BeFalse();
        language.Notices.Should().ContainSingle(n => n.StartsWith("Source not present in baseline"));

        var both = await service.ForReportAsync(report.ProjectId, report, new("package:m2lb-language", Adr), [], default);
        both.Report!.Stories.Select(s => s.FeatureKey).Should().BeEquivalentTo([AutorisasjonKey, FrontendKey, RenameKey], "Source ∩ Functional scope");

        var main = await service.ForReportAsync(report.ProjectId, report, new("package:m2lb -main"), [], default);
        main.Report!.Stories.Should().BeEmpty("the baseline's source has no current features");
        main.Sources.Single(s => s.Id == "package:m2lb -main").Should().Match<DeliveryScopeSourceOption>(s => s.CurrentFeatures == 0 && s.BaselineFeatures == 2);
    }

    [Fact]
    public async Task UnknownIds_AreReportedExplicitly_NeverSubstituted()
    {
        var report = Report();
        var (service, _, _) = await ScopedAsync(report);

        var view = await service.ForReportAsync(report.ProjectId, report, new("package:other", "capability:decision:adr-999"), [], default);
        view.InvalidFilters.Should().Equal(DeliveryScopeService.ScopeUnavailable, DeliveryScopeService.SourceUnavailable);
        view.Report!.Stories.Should().BeEmpty();
        view.Report.Metrics.Single(m => m.Key == "inventory-features").Value.Should().Be(0);
    }

    [Fact]
    public async Task CapabilityRename_KeepsTheScopeId_AndAFrozenScopeKeepsItsLabel()
    {
        var report = Report();
        var (service, db, _) = await ScopedAsync(report);
        var frozen = (await service.ForReportAsync(report.ProjectId, report, new(FunctionalScopeId: Adr), [], default)).Scope!;

        var entity = await db.Entities.SingleAsync(e => e.IdentityKey == "decision:adr-029");
        entity.DisplayName = "English translation";
        await db.SaveChangesAsync();

        var after = await service.ForReportAsync(report.ProjectId, report, new(FunctionalScopeId: Adr), [], default);
        after.Scopes.Single(s => s.Id == Adr).Name.Should().Be("English translation");
        after.Report!.Stories.Should().HaveCount(3);
        frozen.FunctionalScopeName.Should().Be("ADR-029: Translate English", "a frozen report keeps the label it was generated with");
        frozen.FunctionalScopeId.Should().Be(Adr);
    }

    [Fact]
    public async Task WithoutInterpretation_NoCapabilityIsInvented_AndTheReasonIsShown()
    {
        var report = Report();
        var service = new DeliveryScopeService(new CanonicalInterpretationService(Db(), new NoSources(), TimeProvider.System));

        var view = await service.ForReportAsync(report.ProjectId, report, new(), [], default);
        view.Scopes.Should().BeEmpty();
        view.InterpretationNote.Should().Contain("No canonical interpretation covers this Delivery project");
    }

    [Fact]
    public void Projection_OverEveryFeature_ReproducesEveryFrozenMetric()
    {
        var report = Report();
        var projected = DeliveryReportScoping.Project(report,
            report.Stories.Where(s => !s.RemovedFeature).Select(s => s.FeatureKey).ToHashSet(StringComparer.Ordinal),
            report.Stories.Where(s => s.RemovedFeature).Select(s => s.FeatureKey).ToHashSet(StringComparer.Ordinal), new DeliveryReportScope());

        projected.Metrics.Select(m => (m.Key, m.Value)).Should().Equal(report.Metrics.Select(m => (m.Key, m.Value)),
            "the scoped projection uses the report builder's rule for every metric, so an unscoped projection changes nothing");
    }

    // ---- Work type: shared feature classification + canonical technical change intents ----

    [Fact]
    public void FeatureClassification_IsShared_TechnicalWorkFirst()
    {
        FunctionalQualityClassifier.ClassifyFeature("Translate Frontend to English (ADR-029)").Category.Should().Be(QualityCategory.Technical);
        FunctionalQualityClassifier.ClassifyFeature("Developer Mode Settings Panel").Category.Should().Be(QualityCategory.Technical, "developer tooling");
        FunctionalQualityClassifier.ClassifyFeature("Children in Placements per Period").Category.Should().Be(QualityCategory.Functional);
        FunctionalQualityClassifier.ClassifyFeature("Access Administration Panel (Tilgangsadministrasjon)").Category.Should().Be(QualityCategory.Security);
        FunctionalQualityClassifier.ClassifyFeature("English labels for the dashboard").Category.Should().Be(QualityCategory.Functional, "English wording is not translation work");
        FunctionalQualityClassifier.ClassifyFeature("Case overview", ["Retry delivery when the queue is down", "Messages are not lost when the service restarts"]).Should()
            .Be((QualityCategory.Reliability, "fq.feature.stories-majority"));
    }

    [Fact]
    public async Task NonTechnical_HidesTechnicalWork_IncludingAConfirmedChangeIntentMember_AndEveryNumberFollows()
    {
        var report = Report();
        var (service, db, result) = await ScopedAsync(report);
        var canonical = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
        // "Translate the Tjeneste module to English" has no technical wording rule of its own: it becomes technical by joining ADR-029 (reviewed).
        FunctionalQualityClassifier.ClassifyFeature("Translate the Tjeneste module to English").Category.Should().Be(QualityCategory.Functional);
        var tjeneste = result.Relations.Single(r => r.RuleId == CorrelationRules.DecisionRule && r.SourceKey.EndsWith(TjenesteKey, StringComparison.Ordinal)).Key;
        await canonical.DecideAsync("proj", new(ReviewTargetKind.Relation, tjeneste, ReviewDecisionOutcome.Confirmed, "tester"), null, default);

        var view = await service.ForReportAsync(report.ProjectId, report, new(), [], default, new(DeliveryWorkType.NonTechnical));
        view.Classifications.Single(c => c.FeatureKey == TjenesteKey).Should().Match<DeliveryFeatureWork>(c => c.Category == QualityCategory.Technical && c.Rule == DeliveryScopeService.TechnicalIntentRule);
        view.Report!.Stories.Select(s => s.FeatureKey).Should().BeEquivalentTo([PlacementsKey, PolishKey, LabelsKey], "translation and decision-record work is technical");
        Metric(view.Report, "inventory-features").Should().Be(3);
        view.WorkSummary!.Should().Match<DeliveryWorkSummary>(w => w.Total == 9 && w.Technical == 6 && w.NonTechnical == 3 && w.Shown == 3);
        view.Scope!.WorkType.Should().Be(DeliveryWorkType.NonTechnical);
        view.Scope.Label.Should().Be("Non-technical");

        var technical = await service.ForReportAsync(report.ProjectId, report, new(), [], default, new(DeliveryWorkType.Technical));
        technical.Report!.Stories.Should().HaveCount(6);
        var functional = await service.ForReportAsync(report.ProjectId, report, new(), [], default, new(DeliveryWorkType.NonTechnical, QualityCategory.Functional));
        functional.Report!.Stories.Select(s => s.FeatureKey).Should().BeEquivalentTo([PlacementsKey, PolishKey, LabelsKey]);
        var all = await service.ForReportAsync(report.ProjectId, report, new(), [], default);
        all.Scope.Should().BeNull("All work, no scope = the frozen report unchanged");
    }

    [Fact]
    public async Task A_report_frozen_before_the_integration_category_is_viewed_with_its_own_rules_and_a_new_report_with_the_new_ones()
    {
        var report = Report();
        report.QualityRuleSet.Should().Be(FunctionalQualityClassifier.RuleSetVersion, "a new report records the quality rule set it was frozen with");
        var first = report.Stories.First(s => !s.RemovedFeature && !s.NewFeature || !s.RemovedFeature);
        var integration = report with { Stories = [.. report.Stories.Select(s => s.FeatureKey == first.FeatureKey ? s with { Title = "Altinn report arrives and is delivered to SkoleService" } : s)] };
        var service = new DeliveryScopeService();

        var current = await service.ForReportAsync(integration.ProjectId, integration, new(), [], default, new(DeliveryWorkType.NonTechnical, QualityCategory.Integration));
        current.Classifications.Single(c => c.FeatureKey == first.FeatureKey).Category.Should().Be(QualityCategory.Integration);
        current.Report!.Stories.Select(s => s.FeatureKey).Should().Contain(first.FeatureKey);

        var legacy = integration with { QualityRuleSet = null };
        var old = await service.ForReportAsync(legacy.ProjectId, legacy, new(), [], default, new(DeliveryWorkType.NonTechnical, QualityCategory.Integration));
        old.Classifications.Single(c => c.FeatureKey == first.FeatureKey).Category.Should().NotBe(QualityCategory.Integration,
            "a report frozen before functional-quality/3 is never silently reclassified");
        old.Report!.Stories.Should().BeEmpty();
    }

    private static int Metric(WeeklyDeliveryReport report, string key) => report.Metrics.Single(m => m.Key == key).Value;
}
