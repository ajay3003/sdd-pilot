namespace BirkNext.Web.Services;

/// <summary>
/// Artifact-repository scope of an imported project: <c>import:{importId}</c>. Each import identity (exact archive) has its own scope, so the
/// documents of two imports — or of an import and the manual workspace — are never listed, selected or reviewed together.
/// </summary>
public static class ProjectImportScope
{
    public const string Prefix = "import:";

    public static string? For(string? importId) => string.IsNullOrWhiteSpace(importId) ? null : Prefix + importId;

    public static bool IsImport(string? scope) => scope is not null && scope.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? ImportIdOf(string? scope) => IsImport(scope) ? scope![Prefix.Length..] : null;

    /// <summary>Whether a persisted lifecycle (SddLifecycleState JSON) has a current imported project. Malformed JSON counts as none.</summary>
    public static bool HasCurrentImport(string? sddLifecycleJson)
    {
        if (string.IsNullOrWhiteSpace(sddLifecycleJson)) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(sddLifecycleJson);
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty(nameof(Models.SddLifecycleState.CurrentProjectImportId), out var id)
                && id.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString());
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
