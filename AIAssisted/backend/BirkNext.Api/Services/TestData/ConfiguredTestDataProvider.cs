using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>One configured asset (<c>TestData:Assets</c>). <see cref="File"/> is a path relative to the test-data root, never absolute.</summary>
public sealed class ConfiguredTestDataAsset
{
    public string Alias { get; set; } = "";
    public string? Label { get; set; }
    public TestDataKind Kind { get; set; }
    public TestDataValueType ValueType { get; set; }
    public string? MediaType { get; set; }
    public string? EntityType { get; set; }
    public string? Environment { get; set; }
    /// <summary>Unapproved unless configured as Approved: nothing is approved silently.</summary>
    public TestDataApproval Approval { get; set; } = TestDataApproval.Unapproved;
    public List<string> Capabilities { get; set; } = [];
    public TestDataSensitivity Sensitivity { get; set; } = TestDataSensitivity.Sensitive;
    public bool IsDefault { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool ExpectsAbsence { get; set; }
    /// <summary>A scalar or small inline value. A sensitive value belongs in local configuration (an environment variable), never a committed file.</summary>
    public string? Value { get; set; }
    public string? File { get; set; }
    /// <summary>For an XML asset: the XSD (inside a Source Analysis snapshot's schema package) it must be validated against.</summary>
    public ConfiguredTestDataSchema? Schema { get; set; }
}

/// <summary>Configuration shape of <see cref="TestDataSchemaReference"/> (<c>TestData:Assets:n:Schema</c>).</summary>
public sealed class ConfiguredTestDataSchema
{
    public string SchemaPath { get; set; } = "";
    public Guid? SourceSnapshotId { get; set; }
    public string? SchemaFingerprint { get; set; }
    public string? RootElement { get; set; }
    public string? Label { get; set; }

    public TestDataSchemaReference? ToReference() => string.IsNullOrWhiteSpace(SchemaPath) ? null
        : new(SchemaPath.Trim().Replace('\\', '/'), SourceSnapshotId, string.IsNullOrWhiteSpace(SchemaFingerprint) ? null : SchemaFingerprint.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(RootElement) ? null : RootElement.Trim(), Label);
}

public sealed class ConfiguredTestDataSet
{
    public string Alias { get; set; } = "";
    public string? Label { get; set; }
    public string? Environment { get; set; }
    public TestDataApproval Approval { get; set; } = TestDataApproval.Unapproved;
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TestDataOptions
{
    public const string Section = "TestData";
    /// <summary>The folder (relative to the backend's content root) that holds fixture files. Files outside it are refused.</summary>
    public string Root { get; set; } = "TestData";
    public List<ConfiguredTestDataAsset> Assets { get; set; } = [];
    public List<ConfiguredTestDataSet> Sets { get; set; } = [];
}

/// <summary>
/// The configured catalog (<c>TestData</c> section): typed scalars, payloads and files, expected results and data sets declared in
/// configuration. Files are read only from inside the test-data root (no absolute path, no "..", at most 1 MB); nothing is fetched.
/// </summary>
public sealed class ConfiguredTestDataProvider(TestDataOptions options, string contentRoot) : ITestDataProvider
{
    private const long MaxFileBytes = 1024 * 1024;
    public string Id => "configured";
    public string Label => "Configured catalog";
    public string Description => "Typed values, payloads, files, expected results and data sets declared in the TestData configuration section.";
    public string? Detail => $"Read from the TestData configuration section; files only from the {options.Root} folder (at most 1 MB each). Nothing is fetched or generated.";

    public IReadOnlyList<TestDataAsset> Assets() => [.. options.Assets.Where(item => !string.IsNullOrWhiteSpace(item.Alias)).Select(item =>
    {
        var (value, problem) = item.File is { Length: > 0 } file ? ReadFile(file) : (item.Value, null);
        return new TestDataAsset
        {
            Id = $"{Id}:{item.Alias.Trim()}", Alias = item.Alias.Trim(), Label = item.Label, Kind = item.Kind, ValueType = item.Kind == TestDataKind.Scalar ? item.ValueType : TestDataValueType.None,
            MediaType = item.MediaType, EntityType = item.EntityType, Environment = string.IsNullOrWhiteSpace(item.Environment) ? null : item.Environment.Trim(),
            Approval = item.Approval, Capabilities = [.. item.Capabilities.Select(cap => cap.Trim()).Where(cap => cap.Length > 0)], Sensitivity = item.Sensitivity,
            Source = Label, IsDefault = item.IsDefault, ExpiresAt = item.ExpiresAt, ExpectsAbsence = item.ExpectsAbsence,
            Value = value is null ? null : new TestDataValue(value), Schema = item.Schema?.ToReference(),
            Validation = problem is null ? null : TestDataValidation.Unavailable, ValidationDetail = problem,
            // The file name is provenance only when it was read safely from inside the root; a rejected reference is never echoed.
            Provenance = item.File is { Length: > 0 } fileName && problem is null
                ? [new("Configuration", "TestData:Assets"), new("File", fileName)]
                : [new("Configuration", "TestData:Assets")],
        };
    })];

    public IReadOnlyList<TestDataSet> Sets() => [.. options.Sets.Where(item => !string.IsNullOrWhiteSpace(item.Alias)).Select(item => new TestDataSet
    {
        Id = $"{Id}:set:{item.Alias.Trim()}", Alias = item.Alias.Trim(), Label = item.Label, Environment = string.IsNullOrWhiteSpace(item.Environment) ? null : item.Environment.Trim(),
        Approval = item.Approval, Capabilities = item.Capabilities, Bindings = new(item.Bindings, StringComparer.Ordinal), Source = Label,
    })];

    /// <summary>Reads a fixture file strictly inside the root, or says why not (never echoing an absolute path).</summary>
    public (string? Content, string? Problem) ReadFile(string relative)
    {
        if (System.IO.Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is ".." or ""))
            return (null, "The file reference must be a plain path inside the test-data folder.");
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, options.Root));
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return (null, "The file reference leaves the test-data folder.");
        var info = new FileInfo(full);
        if (!info.Exists) return (null, $"The file {relative} does not exist in the test-data folder.");
        if (info.Length > MaxFileBytes) return (null, $"The file {relative} is larger than 1 MB.");
        return (File.ReadAllText(full), null);
    }
}
