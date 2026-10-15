using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// A value held by the backend only. It never serializes or prints itself: <see cref="ToString"/> hides it, and only code that needs the value
/// to execute a test calls <see cref="Reveal"/>, transiently.
/// </summary>
public sealed class TestDataValue(string value)
{
    public string Reveal() => value;
    public bool Is(string? other) => other is not null && string.Equals(value.Trim(), other.Trim(), StringComparison.Ordinal);
    public override string ToString() => "(test data value hidden)";
}

/// <summary>
/// One approved typed data asset as the backend holds it: metadata plus, when available, its value. The value is a scalar's text, a
/// structured or file payload, or the identifier behind an entity reference. Only <see cref="TestDataCatalog"/> turns it into the safe summary.
/// </summary>
public sealed record TestDataAsset
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public TestDataKind Kind { get; init; }
    public TestDataValueType ValueType { get; init; }
    public string? MediaType { get; init; }
    public string? EntityType { get; init; }
    public string? Environment { get; init; }
    public TestDataApproval Approval { get; init; } = TestDataApproval.Unapproved;
    /// <summary>Set by a provider that validated the asset itself (for example a confirmed profile); otherwise the catalog validates by type.</summary>
    public TestDataValidation? Validation { get; init; }
    public string? ValidationDetail { get; init; }
    /// <summary>Why the asset is not approved (a configuration problem), shown instead of the value. Never contains the value.</summary>
    public string? ApprovalDetail { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public TestDataSensitivity Sensitivity { get; init; } = TestDataSensitivity.Sensitive;
    public string Source { get; init; } = "";
    /// <summary>The provider the asset came from; set by <see cref="TestDataCatalog"/>.</summary>
    public string SourceId { get; init; } = "";
    /// <summary>Labelled provenance facts (identifiers only, never values).</summary>
    public List<TestDataFact> Provenance { get; init; } = [];
    public bool IsDefault { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool ExpectsAbsence { get; init; }
    public TestDataValue? Value { get; init; }
    /// <summary>For an XML asset: the XSD it is validated against through the shared XSD capability.</summary>
    public TestDataSchemaReference? Schema { get; init; }
}

/// <summary>A data set: role → asset alias bindings chosen together for one purpose.</summary>
public sealed record TestDataSet
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public string? Environment { get; init; }
    public TestDataApproval Approval { get; init; } = TestDataApproval.Unapproved;
    public List<string> Capabilities { get; init; } = [];
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.Ordinal);
    public string Source { get; init; } = "";
}

/// <summary>
/// A source of test data: what exists. A provider never creates or changes data elsewhere; provisioning, if ever added, is a separate concern.
/// </summary>
public interface ITestDataProvider
{
    string Id { get; }
    string Label { get; }
    string Description { get; }
    IReadOnlyList<TestDataAsset> Assets();
    IReadOnlyList<TestDataSet> Sets() => [];
    /// <summary>Whether anything is configured for this source at all (it may still hold no approved assets).</summary>
    bool Configured => Assets().Count > 0 || Sets().Count > 0;
    /// <summary>Provider-specific explanation (for example which local configuration it reads), shown only as source detail.</summary>
    string? Detail => null;
}
