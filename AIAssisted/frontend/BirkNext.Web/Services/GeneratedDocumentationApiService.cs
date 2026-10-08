using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.GeneratedDocumentation;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>Generated Documentation Health diagnostic (System Settings → Developer). The backend reads stored Source Analysis evidence; the
/// authored artifacts the workspace selected are sent for this run only (never stored).</summary>
public interface IGeneratedDocumentationApiService
{
    Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default);
}

public sealed class GeneratedDocumentationApiService(HttpClient http) : IGeneratedDocumentationApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/system-diagnostics/generated-documentation/run", request, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GeneratedDocumentationDiagnosticRun>(Json, ct);
    }

    /// <summary>The request for the current workspace: its imported-project identity (when it is one) and the selected Constitution,
    /// Specification, Plan and Tasks — resolved through the same role authority as Requirements Traceability. Nothing is picked for the user.</summary>
    public static GeneratedDocumentationDiagnosticRequest Request(TraceabilityInputs? inputs) => new()
    {
        ProjectImportId = inputs is null ? null : ProjectImportScope.ImportIdOf(inputs.Scope),
        AuthoredArtifacts = inputs?.Roles.Where(r => r.IsLoaded && r.State.Selected is not null && r.Fingerprint is not null)
            .Select(r => new AuthoredArtifactInput(r.Role.ToString(), r.State.Selected!.Id, r.State.Selected.DisplayName, r.State.Selected.SourcePath, r.Fingerprint!, r.Content))
            .ToList() ?? [],
    };
}
