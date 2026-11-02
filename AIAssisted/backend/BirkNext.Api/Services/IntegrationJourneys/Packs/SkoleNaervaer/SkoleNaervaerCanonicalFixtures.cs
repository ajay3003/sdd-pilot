using BirkNext.Api.Services.TestData;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// The Skolenærvær Test Pack's canonical fixture: its valid report XML, created in Test Data → Create XML fixture from the report-intake template
/// (which records <see cref="ReportCapability"/> on it) under <see cref="ValidReportAlias"/>. Only that fixture is protected — an XML payload with
/// the same alias that was not created for the report capability (or a negative-test variant) is not.
/// </summary>
public sealed class SkoleNaervaerCanonicalFixtures : ITestDataCanonicalFixtures
{
    public const string ReportCapability = "skolenaervaer-report";
    public const string ValidReportAlias = "skolenaervaer-valid-report";

    public IReadOnlyList<TestDataCanonicalFixture> Fixtures { get; } =
    [
        new($"{SkoleNaervaerJourneyPack.Id}:valid-report", ValidReportAlias, ReportCapability,
            "Required baseline data for Skolenærvær: the valid report fixture the report-intake journey submits.", "Skolenærvær Test Pack"),
    ];

    /// <summary>A report fixture created for the pack's report capability is the report XML for Altinn intake / Skolenærvær report.</summary>
    public IReadOnlyList<TestDataFixtureMetadata> Metadata { get; } =
    [
        new(ReportCapability, BirkNext.TestData.TestDataSemanticRoles.ReportXml,
            [BirkNext.TestData.TestDataContexts.AltinnIntake, BirkNext.TestData.TestDataContexts.Skolenaervaer, BirkNext.TestData.TestDataContexts.SkolenaervaerReport]),
    ];
}
