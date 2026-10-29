using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>A tester's edit of a protected default: the replacement identifier (null = the seeded value) and label, with a change marker.</summary>
public sealed record ProtectedDefaultValue
{
    public string Role { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Identifier { get; init; }
    public string? Label { get; init; }
    public int ValueVersion { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The edited values of the protected (built-in) Test Data defaults, as one small workstation-local JSON file. Only the built-in Skolenærvær
/// reporter and child are stored here, and only after a tester edits them in Test Data; the identifiers are synthetic test identifiers. The
/// file is deliberately outside the database and the Test Data registry: Maintenance reset deletes local Test Data but preserves the protected
/// defaults with their current values. "Restore default value" is the separate, explicit way back to the seeded value.
/// </summary>
public sealed class ProtectedTestDataDefaultsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<ProtectedTestDataDefaultsStore> _logger;
    private List<ProtectedDefaultValue> _values;

    public ProtectedTestDataDefaultsStore(IHostEnvironment environment, ILogger<ProtectedTestDataDefaultsStore> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "test-data"), logger) { }

    public ProtectedTestDataDefaultsStore(string directory, ILogger<ProtectedTestDataDefaultsStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "protected-defaults.json");
        _values = Read() ?? [];
    }

    public IReadOnlyList<ProtectedDefaultValue> Values() { lock (_gate) return [.. _values]; }

    public void Save(ProtectedDefaultValue value)
    {
        lock (_gate)
        {
            _values = [.. _values.Where(v => !(v.Role == value.Role && v.Alias == value.Alias)), value];
            Write();
        }
    }

    private List<ProtectedDefaultValue>? Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<ProtectedDefaultValue>>(File.ReadAllText(_path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Protected Test Data defaults could not be read; the seeded values apply");
            return null;
        }
    }

    private void Write()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_values, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex) { _logger.LogWarning(ex, "Protected Test Data defaults could not be written"); }
    }

    /// <summary>
    /// Applies the stored edits onto the built-in identities when the options are created (after the seed). Only built-in entries are touched;
    /// an edit that no longer validates (the same rule as on entry) is ignored, so the seeded value applies.
    /// </summary>
    public AltinnTestingOptions Overlay(AltinnTestingOptions options)
    {
        foreach (var value in Values())
        {
            if (value.Role == AltinnTestDataProvider.Reporter && options.UnattendedIdPortenTest.Reporters.FirstOrDefault(r => r.BuiltIn && r.Alias == value.Alias) is { } reporter)
            {
                if (value.Identifier is { Length: > 0 } id && SyntheticPersonIdentifier.IsSynthetic(id)) reporter.TestIdPersonIdentifier = id;
                if (value.Label is { Length: > 0 } label) reporter.Label = label;
                reporter.ValueVersion = value.ValueVersion;
                reporter.ValueUpdatedAt = value.UpdatedAt;
                reporter.ValueModified = reporter.TestIdPersonIdentifier != SkoleNaervaerTestIdentityDefaults.Seed(value.Role, value.Alias);
            }
            else if (value.Role == AltinnTestDataProvider.Child && options.SyntheticChildren.FirstOrDefault(c => c.BuiltIn && c.Alias == value.Alias) is { } child)
            {
                if (value.Identifier is { Length: > 0 } id && SyntheticPersonIdentifier.IsTestPerson(id)) child.TestPersonIdentifier = id;
                if (value.Label is { Length: > 0 } label) child.Label = label;
                child.ValueVersion = value.ValueVersion;
                child.ValueUpdatedAt = value.UpdatedAt;
                child.ValueModified = child.TestPersonIdentifier != SkoleNaervaerTestIdentityDefaults.Seed(value.Role, value.Alias);
            }
        }
        return options;
    }
}
