using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaTestRunHistoryTests
{
    private const string Xsd = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:history\"><xs:element name=\"Root\" type=\"xs:integer\"/></xs:schema>";

    [Fact]
    public void ReopenedHistoryRetainsSchemaEvidenceAndOutcomesButNotGeneratedXmlOrRawDiagnostics()
    {
        var snapshotId = Guid.NewGuid();
        var provenance = new SchemaProvenance("Source Analysis", "PRIVATE_ARCHIVE_NAME.zip", snapshotId,
            new string('b', 64), DateTimeOffset.UtcNow, "schemas/main.xsd", new string('c', 64));
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["schemas/main.xsd"] = Xsd }, "PRIVATE_ARCHIVE_NAME.zip", provenance);
        var results = new[] { new SchemaTestResult("case", "/Root", "wrong datatype", false, "Invalid", "Pass", "value contained in diagnostic", "<Root>synthetic payload</Root>") };
        var history = new SchemaTestRunHistory();
        history.Add(package, "Generic valid", 41, results);
        var json = history.SnapshotJson();

        Assert.DoesNotContain("synthetic payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("value contained in diagnostic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_ARCHIVE_NAME.zip", json, StringComparison.Ordinal);
        var reopened = new SchemaTestRunHistory();
        reopened.RestoreJson(json);
        var run = Assert.Single(reopened.Runs);
        Assert.Equal(package.Fingerprint, run.PackageFingerprint);
        Assert.Equal(snapshotId, run.Provenance!.SourceSnapshotId);
        Assert.Equal(new string('b', 64), run.Provenance.SourceSnapshotFingerprint);
        Assert.Equal("Generic valid", run.ProfileName);
        Assert.Equal(41, run.Seed);
        Assert.Equal("Pass", Assert.Single(run.Results).Outcome);
        Assert.Equal("Validated locally against the recorded XSD fingerprint.", run.Results[0].Diagnostic);
    }

    [Fact]
    public void WorkspaceHistorySnapshotExcludesRunsFromOtherProjects()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["history.xsd"] = Xsd }, "history.xsd");
        var history = new SchemaTestRunHistory();
        var one = new[] { new SchemaTestResult("case", "/Root", "case", false, "Invalid", "Pass", "", "") };
        history.Add(package, "profile-a", 1, one, "import:a");
        history.Add(package, "profile-b", 2, one, "import:b");

        var reopened = new SchemaTestRunHistory();
        reopened.RestoreJson(history.SnapshotJson("import:a"));

        Assert.Equal("import:a", Assert.Single(reopened.Runs).ProjectScope);
    }

    [Fact]
    public void HistoryIsBoundedAndRejectsOversizedRunsWithoutDroppingPriorEvidence()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["history.xsd"] = Xsd }, "history.xsd");
        var history = new SchemaTestRunHistory();
        var one = new[] { new SchemaTestResult("case", "/Root", "case", false, "Invalid", "Pass", "", "") };
        history.Add(package, "profile", 1, one);
        Assert.Throws<InvalidDataException>(() => history.Add(package, "profile", 2,
            Enumerable.Repeat(one[0], SchemaTestRunHistory.MaxResultsPerRun + 1)));
        Assert.Single(history.Runs);

        for (var i = 0; i < SchemaTestRunHistory.MaxRuns + 2; i++) history.Add(package, "profile", i, one);
        Assert.Equal(SchemaTestRunHistory.MaxRuns, history.Runs.Count);
    }

    [Fact]
    public void RestoreRejectsMalformedNullableFieldsInsteadOfThrowingNullReferenceException()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["history.xsd"] = Xsd }, "history.xsd");
        var history = new SchemaTestRunHistory();
        history.Add(package, "profile", 1,
            [new SchemaTestResult("case", "/Root", "case", false, "Invalid", "Pass", "safe", "")]);
        var malformed = history.SnapshotJson().Replace("\"diagnostic\":\"Validated locally against the recorded XSD fingerprint.\"", "\"diagnostic\":null", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => new SchemaTestRunHistory().RestoreJson(malformed));
        Assert.Contains("oversized result metadata", exception.Message, StringComparison.Ordinal);
    }
}
