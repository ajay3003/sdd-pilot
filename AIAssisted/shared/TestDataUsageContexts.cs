namespace BirkNext.TestData;

// The known usage contexts of this installation. Kept apart from the generic Test Data contract (which knows no domain): Test Data only
// stores and matches context ids; which systems exist is configuration of the products that use Test Data.

/// <summary>Canonical usage contexts ("Applies to"). Matching uses these ids, never display text.</summary>
public static class TestDataContexts
{
    public const string AltinnIntake = "altinn-intake", Skolenaervaer = "skolenaervaer", SkolenaervaerReport = "skolenaervaer-report",
        Meldingsutveksleren = "meldingsutveksleren", PersonApi = "person-api", PerformanceTest = "performance-test", BrowserTesting = "browser-testing";
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (AltinnIntake, "Altinn intake"), (Skolenaervaer, "Skolenærvær"), (SkolenaervaerReport, "Skolenærvær report payload"),
        (Meldingsutveksleren, "Meldingsutveksleren"), (PersonApi, "Person API"), (PerformanceTest, "Performance Test"), (BrowserTesting, "Browser Testing"),
    ];

    public static string Label(string id) => All.FirstOrDefault(c => c.Id == id).Label ?? id;

    /// <summary>"Altinn intake +1" for a compact table cell; "Not specified" when none.</summary>
    public static string Summary(IReadOnlyList<string>? ids) => ids is not { Count: > 0 } ? "Not specified" : ids.Count == 1 ? Label(ids[0]) : $"{Label(ids[0])} +{ids.Count - 1}";

    public static bool Valid(string id) => All.Any(c => c.Id == id);
}
