using BirkNext.Api.Services.TestData;
using BirkNext.TestData;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// What the Skolenærvær report intake consumes from Test Data, with the requirements its runs resolve: the child the report is about (inside
/// the XML), the reporter who submits it as the Altinn actor (submission context, never inside the XML), and the valid report payload — the
/// canonical XML fixture, optional because a run can also build the report from the child.
/// </summary>
public sealed class SkoleNaervaerTestDataConsumers : ITestDataConsumerDeclarations
{
    public static readonly TestDataConsumer ReportIntake = new()
    {
        ConsumerType = "Test Pack integration", ConsumerId = "skolenaervaer-report-intake", DisplayName = "Skolenærvær – Altinn report intake", Parent = "Skolenærvær Testing",
    };

    public static readonly TestDataRequirement ReportPayload = new()
    {
        Role = "Valid report payload", Kind = TestDataKind.Structured, MediaType = "application/xml", Capabilities = [SkoleNaervaerCanonicalFixtures.ReportCapability], Optional = true,
    };

    public IReadOnlyList<TestDataConsumerDeclaration> Consumers { get; } =
    [
        new(ReportIntake,
        [
            new("child", "Child · XML child field", true, TestDataDataType.SimpleValues, SkoleNaervaerTestData.Child(knownToPerson: true), "The synthetic child the report is about."),
            new("reporter", "Reporter · submitting Altinn actor (not in the XML)", true, TestDataDataType.SimpleValues, SkoleNaervaerTestData.Reporter,
                "The synthetic TestID person who submits the report."),
            new("report-payload", "Valid report payload · XML", false, TestDataDataType.XmlPayload, ReportPayload,
                "The canonical valid report XML (skolenaervaer-valid-report). Optional: a run can build the report from the child."),
        ]),
    ];
}
