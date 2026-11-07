using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Authored resources as catalog assets: the same alias identity, approval (per version fingerprint), deprecation, consumer matching and
/// Smart Scenario selection as every other asset — no second catalog. Every saved version is a complete asset; the catalog keeps consumers on
/// the approved version while a newer one is a draft. Authored values passed the credential/identity rules, so they are Internal (visible).
/// </summary>
public sealed class AuthoredTestDataProvider(AuthoredTestDataStore store) : ITestDataProvider
{
    public const string ProviderId = "authored";
    public string Id => ProviderId;
    public string Label => "Added in Test Data";
    public string Description => "Synthetic values, payloads, request templates, datasets and generators added with Add data.";
    public string? StatusLabel(TestDataSourceStatus status) => status == TestDataSourceStatus.Available ? "Added data" : "Nothing added yet";
    public bool Configured => store.Resources().Count > 0;

    public IReadOnlyList<TestDataAsset> Assets() => [.. store.Resources().Select(Asset)];

    /// <summary>Datasets as role → alias bindings, the shape existing data-set consumers resolve.</summary>
    public IReadOnlyList<TestDataSet> Sets() => [.. store.Resources().Where(r => r.Current.Content.Kind == TestDataResourceKind.Dataset).Select(r => new TestDataSet
    {
        Id = $"{ProviderId}:{r.Alias}", Alias = r.Alias, Label = r.Current.Label, Environment = r.Current.EnvironmentId, Source = Label,
        Bindings = r.Current.Content.Members.Where(m => m.Reference is not null).ToDictionary(m => m.Name, m => m.Reference!.Split('@')[0], StringComparer.Ordinal),
    })];

    public static TestDataAsset Asset(AuthoredTestDataResource resource)
    {
        var versions = resource.Versions.Select(v => Version(resource, v)).ToList();
        return versions.Count == 1 ? versions[0] : versions[^1] with { Versions = versions };
    }

    private static TestDataAsset Version(AuthoredTestDataResource resource, AuthoredTestDataVersion v)
    {
        var c = v.Content;
        var (kind, valueType, media) = Shape(c);
        return new TestDataAsset
        {
            Id = $"{ProviderId}:{resource.Alias}", Alias = resource.Alias, Label = v.Label, Kind = kind, ValueType = valueType, MediaType = media,
            Environment = v.EnvironmentId, Approval = TestDataApproval.Unapproved, Sensitivity = TestDataSensitivity.Internal,
            Source = "Added in Test Data", Ownership = TestDataOwnership.Authored, Value = new TestDataValue(TestDataResourceRules.Payload(c)),
            Fields = Fields(c), Tags = ["authored", TestDataResourceRules.Label(c.Kind)], AppliesTo = v.AppliesTo,
            SchemaRef = c.Operation is { } op ? $"{op.ContractId}#{op.Operation}" : null,
            Provenance = [new("Added in Test Data", $"{TestDataResourceRules.Label(c.Kind)} · version {v.Version}"), .. v.ChangeNote is { } note ? [new TestDataFact("Change note", note)] : Array.Empty<TestDataFact>()],
            Version = v.Version, VersionCreatedAt = v.CreatedAt, ValueVersion = v.Version, ValueUpdatedAt = v.CreatedAt,
        };
    }

    /// <summary>Catalog shape: a single value is a scalar of its exact type; structured kinds are JSON payloads; a file reference is a file.</summary>
    private static (TestDataKind Kind, TestDataValueType ValueType, string? Media) Shape(TestDataResourceContent c) => c.Kind switch
    {
        TestDataResourceKind.SingleValue => (TestDataKind.Scalar, c.Value?.Type switch
        {
            TestDataPrimitive.Int32 or TestDataPrimitive.Int64 => TestDataValueType.Integer,
            TestDataPrimitive.Decimal or TestDataPrimitive.Double => TestDataValueType.Decimal,
            TestDataPrimitive.Boolean => TestDataValueType.Boolean,
            TestDataPrimitive.Date => TestDataValueType.Date,
            TestDataPrimitive.DateTime => TestDataValueType.DateTime,
            TestDataPrimitive.Uuid => TestDataValueType.Guid,
            TestDataPrimitive.String or TestDataPrimitive.Enum => TestDataValueType.String,
            _ => TestDataValueType.Other,
        }, null),
        TestDataResourceKind.FileReference => (TestDataKind.File, TestDataValueType.None, c.MediaType ?? "application/octet-stream"),
        TestDataResourceKind.Generated => (TestDataKind.Other, TestDataValueType.None, "application/vnd.birknext.generator+json"),
        TestDataResourceKind.RestRequest => (TestDataKind.Message, TestDataValueType.None, "application/vnd.birknext.rest-request+json"),
        TestDataResourceKind.GraphQlVariables => (TestDataKind.Structured, TestDataValueType.None, "application/graphql-variables+json"),
        _ => (TestDataKind.Structured, TestDataValueType.None, "application/json"),
    };

    /// <summary>Safe structure for matching: names and exact types, never values.</summary>
    private static List<string> Fields(TestDataResourceContent c) => c.Kind switch
    {
        TestDataResourceKind.SingleValue => c.Value is { } v ? [$"{(v.Name.Length > 0 ? v.Name : "value")}: {v.Type}"] : [],
        TestDataResourceKind.KeyValue => [.. c.Entries.Select(e => $"{e.Name}: {e.Type}")],
        TestDataResourceKind.RestRequest => [.. c.Parameters.Select(p => $"{p.In}.{p.Name}: {p.Type}")],
        TestDataResourceKind.Dataset => [.. c.Members.Select(m => m.Inline is { } i ? $"{m.Name}: {i.Type}" : $"{m.Name} → {m.Reference}")],
        TestDataResourceKind.Json or TestDataResourceKind.GraphQlVariables => TestDataResourceRules.JsonError(c.Json).Node is System.Text.Json.Nodes.JsonObject o
            ? [.. o.Select(p => $"{p.Key}: {p.Value?.GetValueKind().ToString() ?? "Null"}")] : [],
        _ => [],
    };
}
