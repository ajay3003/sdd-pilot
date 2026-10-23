using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Schema Studio fixtures registered for reuse. Schema Studio keeps explicitly saved XML in workspace storage; only a saved document the user
/// then registered as Test Data (alias, intent, approval) becomes an asset here — drafts, previews, mutations and saved-but-unregistered XML
/// never do. Test Data stays the one catalog: this provider reads the registration, it does not copy or own the XML. The XML is treated as
/// sensitive (it may contain entered values), and its schema reference points at the exact Source Analysis snapshot it was generated from,
/// so the shared XSD validator checks it like any other XML asset. Schema Studio never approves: a registration is reviewed and approved in
/// Test Data (registrations made before that rule keep their recorded approval). Schema-valid is never business-valid.
/// </summary>
public sealed partial class SchemaStudioTestDataProvider(Func<IReadOnlyList<string>> savedXmlDocuments) : ITestDataProvider
{
    public const string ProviderId = "schema-studio";
    public const string NegativeTest = "negative-test";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    public string Id => ProviderId;
    public string? StatusLabel(BirkNext.TestData.TestDataSourceStatus status) =>
        status == BirkNext.TestData.TestDataSourceStatus.Available ? "Registered fixtures" : "No registered fixtures";
    public string Label => "Schema Studio";
    public string Description => "Reusable schema-derived XML explicitly registered as Test Data from saved Schema Studio XML.";
    public string? Detail => "Only saved XML that was registered for reuse appears here; drafts, generated previews and mutations stay in Schema Studio. Values are treated as sensitive and never shown.";
    public bool Configured => Registered().Any();

    public IReadOnlyList<TestDataAsset> Assets() => [.. Registered().Select(Asset)];

    private IEnumerable<(SavedXml Xml, string? Scope)> Registered()
    {
        foreach (var content in savedXmlDocuments())
        {
            Document? document;
            try { document = JsonSerializer.Deserialize<Document>(content, Json); }
            catch (JsonException) { continue; } // an unreadable workspace document is not test data
            foreach (var item in document?.Items ?? [])
                if (item.TestData is { Alias.Length: > 0 } && !string.IsNullOrEmpty(item.Xml)) yield return (item, item.ProjectScope);
        }
    }

    private TestDataAsset Asset((SavedXml Xml, string? Scope) entry)
    {
        var (xml, scope) = entry;
        var registration = xml.TestData!;
        var alias = registration.Alias.Trim();
        var negative = string.Equals(registration.Intent, NegativeTest, StringComparison.Ordinal);
        var problem = !AliasPattern().IsMatch(alias) ? "The alias must be lowercase letters, digits and hyphens (at most 64)." : null;
        var provenance = new List<TestDataFact> { new("Saved in", "Schema Studio (saved XML)"), new("Schema file", xml.FileName) };
        if (xml.Provenance?.SourceSnapshotId is { } snapshot) provenance.Add(new("Source snapshot", snapshot.ToString("D")));
        if (xml.Provenance?.ContractFingerprint is { Length: > 0 } fingerprint) provenance.Add(new("Schema fingerprint", fingerprint[..Math.Min(12, fingerprint.Length)]));
        provenance.Add(new("Generation profile", xml.ProfileName));
        if (registration.Facts is { } facts)
        {
            provenance.Add(new("Expected schema result", facts.ExpectedResult));
            provenance.Add(new("Local validation", facts.ValidationStatus));
            if (facts.Mutation is { Length: > 0 } mutation) provenance.Add(new("Mutation", mutation));
            if (facts.TargetRule is { Length: > 0 } rule) provenance.Add(new("Schema rule targeted", rule));
            if (facts.Path is { Length: > 0 } path) provenance.Add(new("Path", path));
            if (facts.RootElement is { Length: > 0 } root) provenance.Add(new("Root element", root));
            if (facts.GeneratorVersion is { Length: > 0 } generator) provenance.Add(new("Generator", generator));
        }
        if (scope is { Length: > 0 }) provenance.Add(new("Project", scope));
        provenance.Add(new("Registered", registration.RegisteredAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'")));
        return new TestDataAsset
        {
            Id = $"{Id}:{alias}", Alias = alias, Label = registration.Label, Kind = TestDataKind.Structured, MediaType = "application/xml",
            Approval = problem is null && registration.Approved ? TestDataApproval.Approved : TestDataApproval.Unapproved,
            ApprovalDetail = problem ?? (registration.Approved ? null : "Registered from Schema Studio; review and approve it in Test Data before it is used."),
            ReviewRequested = !registration.Approved, Environment = registration.Facts?.EnvironmentId,
            Capabilities = negative ? ["schema-derived", NegativeTest] : ["schema-derived"],
            Sensitivity = TestDataSensitivity.Sensitive, Source = Label, Value = new TestDataValue(xml.Xml),
            Schema = new TestDataSchemaReference(xml.Provenance?.SelectedContractPath ?? xml.FileName, xml.Provenance?.SourceSnapshotId,
                xml.Provenance?.ContractFingerprint, null, xml.FileName),
            Provenance = provenance,
        };
    }

    private sealed record Document(int Version, List<SavedXml>? Items);
    private sealed record SavedXml(Guid Id, string FileName, string ProfileName, string Xml, string? ProjectScope, Provenance? Provenance, Registration? TestData);
    private sealed record Provenance(Guid? SourceSnapshotId, string? SelectedContractPath, string? ContractFingerprint);
    private sealed record Registration(string Alias, string? Label, string Intent, bool Approved, DateTimeOffset RegisteredAt, Facts? Facts);
    private sealed record Facts(string ExpectedResult, string ValidationStatus, string? Mutation, string? TargetRule, string? Path, string? ProfileId,
        string? ProfileVersion, string? GeneratorVersion, string? RootElement, string? EnvironmentId);
}
