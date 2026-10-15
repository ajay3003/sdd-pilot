using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Schemas;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>An XSD package found for a <see cref="TestDataSchemaReference"/>: the exact snapshot it came from and its retained XSD files.</summary>
public sealed record TestDataSchemaPackage(Guid SnapshotId, IReadOnlyDictionary<string, string> Files, bool IsComplete);

/// <summary>Where Test Data finds XSD packages. The default reads Source Analysis' retained schema artifacts; nothing is fetched.</summary>
public interface ITestDataSchemaSource
{
    Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct);
}

/// <summary>
/// Resolves a schema reference against Source Analysis snapshots' XSD artifacts: the exact snapshot when an id is given, otherwise the newest
/// snapshot whose artifact contains the schema path (the snapshot used is reported with the result).
/// </summary>
public sealed class SourceAnalysisTestDataSchemaSource(IqrSourceStore store) : ITestDataSchemaSource
{
    private const int MaxSnapshotsSearched = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct)
    {
        if (reference.SourceSnapshotId is { } exact) return Read(await store.FindSchemaArtifactAsync(exact, ct), reference.SchemaPath);
        var (index, _) = await store.ListSchemaSnapshotIndexAsync(ct: ct);
        foreach (var snapshot in index.Where(s => s.HasSchemaArtifact).Take(MaxSnapshotsSearched))
            if (Read(await store.FindSchemaArtifactAsync(snapshot.Id, ct), reference.SchemaPath) is { } package) return package;
        return null;
    }

    private static TestDataSchemaPackage? Read(Models.SourceSchemaArtifactRecord? artifact, string path)
    {
        if (artifact is null) return null;
        List<StoredSchemaFile>? files;
        try { files = JsonSerializer.Deserialize<List<StoredSchemaFile>>(artifact.PackageJson, Json); }
        catch (JsonException) { return null; }
        if (files is null || !files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) map.TryAdd(file.Path, file.Text);
        return new(artifact.SnapshotId, map, artifact.IsComplete);
    }

    // The stored artifact shape written at Source Analysis upload ({ path, text, fingerprint }).
    private sealed record StoredSchemaFile(string Path, string Text);
}

/// <summary>
/// Validates XML Test Data assets with the shared XSD validator. Schema validity and approval are independent facts: an approved negative
/// fixture stays approved when it is (deliberately) invalid, and an invalid asset is never removed or disabled here.
/// </summary>
public static class TestDataSchemaValidator
{
    public static TestDataSchemaCheck? Check(TestDataAsset asset, TestDataSchemaPackage? package)
    {
        if (asset.Schema is not { } reference) return null;
        var label = reference.Label ?? reference.SchemaPath;
        if (!TestDataValidator.MediaType(asset.MediaType).EndsWith("xml", StringComparison.Ordinal))
            return new(TestDataSchemaStatus.NotEvaluated, label, "Schema validation applies to XML assets only.", []);
        if (asset.Value is null)
            return new(TestDataSchemaStatus.NotEvaluated, label, "No value is available to the backend, so it cannot be validated.", []);
        if (package is null)
            return new(TestDataSchemaStatus.SchemaUnavailable, label,
                reference.SourceSnapshotId is { } id
                    ? $"Schema unavailable: snapshot {id:D} has no retained XSD '{reference.SchemaPath}'."
                    : $"Schema unavailable: no Source Analysis snapshot retains the XSD '{reference.SchemaPath}'.", []);

        IReadOnlyDictionary<string, string> closure;
        try { closure = XmlSchemaPackageAnalyzer.DependencyClosure(package.Files, reference.SchemaPath); }
        catch (InvalidDataException e)
        {
            return new(TestDataSchemaStatus.SchemaUnavailable, label, "Schema unavailable: " + XmlSchemaPackageAnalyzer.SafeIssue(e.Message), [], package.SnapshotId);
        }
        var fingerprint = XmlSchemaPackageAnalyzer.Fingerprint(closure.First(f => string.Equals(f.Key, reference.SchemaPath, StringComparison.OrdinalIgnoreCase)).Value);
        if (reference.SchemaFingerprint is { } expected && !string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(TestDataSchemaStatus.SchemaUnavailable, label,
                $"Schema unavailable: the XSD in snapshot {package.SnapshotId:D} has fingerprint {fingerprint[..12]}, not the declared {expected[..Math.Min(12, expected.Length)]}.",
                [], package.SnapshotId, fingerprint);
        string? version = null;
        try { version = XmlSchemaPackageAnalyzer.Analyze(closure).Files.FirstOrDefault(f => string.Equals(f.Name, reference.SchemaPath, StringComparison.OrdinalIgnoreCase))?.Version; }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException) { }
        var result = XmlSchemaPackageAnalyzer.ValidateXml(closure, asset.Value.Reveal(), reference.RootElement);
        var status = result.Status switch
        {
            XmlSchemaValidationStatus.Valid => TestDataSchemaStatus.Validated,
            XmlSchemaValidationStatus.Invalid => TestDataSchemaStatus.Invalid,
            XmlSchemaValidationStatus.DependencyUnresolved => TestDataSchemaStatus.DependencyUnresolved,
            XmlSchemaValidationStatus.SchemaUnavailable => TestDataSchemaStatus.SchemaUnavailable,
            _ => TestDataSchemaStatus.NotEvaluated,
        };
        var detail = status switch
        {
            TestDataSchemaStatus.Validated => "Valid against the schema.",
            TestDataSchemaStatus.Invalid => "Invalid against the schema.",
            TestDataSchemaStatus.DependencyUnresolved => "Not evaluated: a schema dependency is not resolved in the retained package.",
            _ => "Not evaluated: " + result.Message.Replace("Not evaluated: ", "", StringComparison.Ordinal),
        };
        if (!package.IsComplete && status is not (TestDataSchemaStatus.Validated or TestDataSchemaStatus.Invalid))
            detail += " The snapshot retained only part of its XSD files.";
        return new(status, label, detail, [.. result.Issues.Take(10)], package.SnapshotId, fingerprint, version);
    }

    /// <summary>
    /// Combines the format check with the schema check: Invalid when either fails, Validated only when the schema says valid, and
    /// NeedsValidation when the XML is well formed but the schema could not be evaluated. Approval is never changed.
    /// </summary>
    public static TestDataAssetSummary Apply(TestDataAssetSummary summary, TestDataSchemaCheck? check)
    {
        if (check is null) return summary;
        var validation = summary.Validation == TestDataValidation.Invalid ? TestDataValidation.Invalid : check.Status switch
        {
            TestDataSchemaStatus.Validated => TestDataValidation.Validated,
            TestDataSchemaStatus.Invalid => TestDataValidation.Invalid,
            _ => TestDataValidation.NeedsValidation,
        };
        var detail = summary.Validation == TestDataValidation.Invalid ? summary.ValidationDetail : $"{check.Schema}: {check.Detail}";
        return summary with { Validation = validation, ValidationDetail = detail, SchemaCheck = check };
    }
}

/// <summary>The Test Data catalog with XML assets checked against their declared schemas (read on request; nothing is cached or stored).</summary>
public sealed class TestDataSchemaValidationService(TestDataCatalog catalog, ITestDataSchemaSource schemas)
{
    public async Task<TestDataCatalogView> ViewAsync(CancellationToken ct)
    {
        var view = catalog.View();
        var assets = catalog.Assets().ToDictionary(a => a.Id, StringComparer.Ordinal);
        var checkedAssets = new List<TestDataAssetSummary>(view.Assets.Count);
        foreach (var summary in view.Assets)
            checkedAssets.Add(assets.TryGetValue(summary.Id, out var asset) ? await CheckAsync(summary, asset, ct) : summary);
        return view with { Assets = checkedAssets };
    }

    public async Task<TestDataAssetDetail?> DetailAsync(string id, CancellationToken ct)
    {
        if (catalog.Detail(id) is not { } detail) return null;
        var asset = catalog.Assets().First(a => a.Id == id);
        return detail with { Asset = await CheckAsync(detail.Asset, asset, ct) };
    }

    private async Task<TestDataAssetSummary> CheckAsync(TestDataAssetSummary summary, TestDataAsset asset, CancellationToken ct)
    {
        if (asset.Schema is null) return summary;
        var package = await schemas.FindAsync(asset.Schema, ct);
        return TestDataSchemaValidator.Apply(summary, TestDataSchemaValidator.Check(asset, package));
    }
}
