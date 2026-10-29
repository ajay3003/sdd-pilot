using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One canonical source snapshot + contract resolution for XML fixtures: explicit saved choice → confirmed contract on an IQR-bound snapshot →
/// Target Environment → Project binding → IQR binding → exact form-data-type match in the recognized source → only candidate → Needs decision.
/// Each level picks one candidate or asks; the analysis date never decides.
/// </summary>
public sealed class XmlContractResolutionTests
{
    private const string Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";
    private static readonly Guid Mu1 = Guid.Parse("148f9899-0000-0000-0000-000000000001"), Mu2 = Guid.Parse("2a2a2a2a-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Old = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), New = new(2026, 10, 29, 7, 15, 0, TimeSpan.Zero);

    private static IntegrationTemplateSchemaCandidate Xsd(Guid snapshot, string archive, DateTimeOffset at, bool bound = false, bool recognized = true, bool confirmed = false,
        string file = "xsd/" + Contract + ".xsd") => new(snapshot, archive, Contract, file)
    { AnalyzedAt = at, Bound = bound, BoundIntegration = bound ? "Altinn → Meldingsutveksleren (MU)" : null, Recognized = recognized, ContractConfirmed = confirmed };

    private static IntegrationTemplateView Context(params IntegrationTemplateSchemaCandidate[] candidates) =>
        new("skolenaervaer-report-intake", "Report intake", "", IntegrationKind.HttpApi, "skolenaervaer", "Skolenærvær Testing")
        { SchemaName = Contract, SchemaCandidates = candidates, Flow = "Altinn → Meldingsutveksleren (MU)" };

    [Fact]
    public void UniqueRecognizedSource_IsPreselected_WithItsReason()
    {
        var selection = XmlContractResolution.Resolve(Context(Xsd(Mu1, "Tiltak 2 - Sikkert digitalt økosystem.zip", New)));
        selection.SnapshotState.Should().Be(XmlSelectionState.Preselected);
        selection.SnapshotReason.Should().Be("Preselected from Altinn → Meldingsutveksleren (MU) integration evidence: the form data type matches the consumer schema");
        selection.Contract!.SnapshotId.Should().Be(Mu1);
        selection.ContractReason.Should().Be($"Exact form data type ↔ schema match ({Contract})");
        selection.Snapshot!.Label.Should().Be("Tiltak 2 - Sikkert digitalt økosystem.zip · Analyzed 2026-10-29 07:15 UTC · snapshot 148f9899 · recognized source");
    }

    [Fact]
    public void TwoEquallyValidSnapshots_NeedDecision_TheNewestIsNeverChosen()
    {
        var selection = XmlContractResolution.Resolve(Context(Xsd(Mu1, "mu-a.zip", Old), Xsd(Mu2, "mu-b.zip", New)));
        selection.SnapshotState.Should().Be(XmlSelectionState.NeedsDecision);
        selection.Snapshot.Should().BeNull();
        selection.Contract.Should().BeNull();
        selection.SnapshotReason.Should().Be("2 compatible source snapshots found — review required");
        selection.Snapshots.Select(s => s.ArchiveFileName).Should().Equal(new[] { "mu-a.zip", "mu-b.zip" }, "ordered by evidence then name, never by date");
    }

    [Fact]
    public void ConfirmedContract_ThenProjectBinding_ThenIqrBinding_OutrankRecognitionAndDate()
    {
        XmlContractResolution.Resolve(Context(Xsd(Mu1, "a.zip", Old, bound: true, confirmed: true), Xsd(Mu2, "b.zip", New)))
            .Should().Match<XmlContractSelection>(s => s.Snapshot!.SnapshotId == Mu1 && s.SnapshotState == XmlSelectionState.Bound
                && s.SnapshotReason == "Bound from confirmed integration contract (Altinn → Meldingsutveksleren (MU))" && s.ContractReason!.StartsWith("Confirmed consumer contract"));

        var project = XmlContractResolution.Resolve(Context(Xsd(Mu1, "a.zip", New), Xsd(Mu2, "b.zip", Old)), projectBound: [Mu2]);
        project.Snapshot!.SnapshotId.Should().Be(Mu2);
        project.SnapshotReason.Should().Be("Bound from Project setup (Target Environment → Project)");

        var iqr = XmlContractResolution.Resolve(Context(Xsd(Mu1, "a.zip", New), Xsd(Mu2, "b.zip", Old, bound: true)));
        iqr.Snapshot!.SnapshotId.Should().Be(Mu2);
        iqr.SnapshotReason.Should().Be("Bound from Altinn → Meldingsutveksleren (MU) integration evidence (IQR)");
    }

    [Fact]
    public void ExplicitSelection_Wins_OverEveryDerivedSuggestion()
    {
        var context = Context(Xsd(Mu1, "a.zip", Old, bound: true, confirmed: true), Xsd(Mu2, "b.zip", New));
        var selection = XmlContractResolution.Resolve(context, new XmlExplicitSelection(Mu2, null), projectBound: [Mu1]);
        selection.Snapshot!.SnapshotId.Should().Be(Mu2);
        selection.SnapshotState.Should().Be(XmlSelectionState.Explicit);
        selection.Contract!.SnapshotId.Should().Be(Mu2);
    }

    [Fact]
    public void TwoCompatibleContractsInOneSnapshot_NeedDecision_UntilOneIsChosen()
    {
        var context = Context(Xsd(Mu1, "a.zip", New), Xsd(Mu1, "a.zip", New, file: "other/" + Contract + ".xsd"));
        var open = XmlContractResolution.Resolve(context);
        open.Snapshot!.SnapshotId.Should().Be(Mu1);
        open.ContractState.Should().Be(XmlSelectionState.NeedsDecision);
        open.ContractReason.Should().Be("2 compatible contracts found — review required");
        open.Resolved.Should().BeFalse();
        var chosen = XmlContractResolution.Resolve(context, new XmlExplicitSelection(Mu1, "other/" + Contract + ".xsd"));
        chosen.Contract!.File.Should().Be("other/" + Contract + ".xsd");
        chosen.ContractState.Should().Be(XmlSelectionState.Explicit);
    }

    [Fact]
    public void ShowAll_OffersOtherSchemasOfTheSelectedSnapshot_OnlyAsAnExplicitChoice()
    {
        var context = Context(Xsd(Mu1, "a.zip", New)) with
        { OtherSchemaCandidates = [new(Mu1, "a.zip", "kontrakt_annen", "xsd/kontrakt_annen.xsd") { MatchesContract = false }] };
        var selection = XmlContractResolution.Resolve(context);
        selection.Contract!.File.Should().EndWith(Contract + ".xsd", "a schema not named as the contract is never preselected");
        selection.OtherContracts.Should().ContainSingle().Which.File.Should().Be("xsd/kontrakt_annen.xsd");
        XmlContractResolution.Resolve(context, new XmlExplicitSelection(Mu1, "xsd/kontrakt_annen.xsd")).Contract!.File.Should().Be("xsd/kontrakt_annen.xsd");
    }

    [Fact]
    public void LegacyPreselected_DelegatesToTheCanonicalResolver()
    {
        XmlFixturePresentation.Preselected(Context(Xsd(Mu1, "a.zip", New))).Should().NotBeNull();
        XmlFixturePresentation.Preselected(Context(Xsd(Mu1, "a.zip", Old), Xsd(Mu2, "b.zip", New))).Should().BeNull();
    }
}
