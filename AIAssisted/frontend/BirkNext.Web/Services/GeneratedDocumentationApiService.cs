using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.GeneratedDocumentation;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>Generated Documentation Health diagnostic (project analysis of the imported project). The backend reads stored Source Analysis evidence; the
/// authored artifacts the workspace selected are sent for this run only (never stored).</summary>
public interface IGeneratedDocumentationApiService
{
    Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default);
    /// <summary>Operations/types of a captured OpenAPI or GraphQL artifact (stateless; nothing stored or fetched). Null when unreadable.</summary>
    Task<BirkNext.SourceDomains.SourceContract?> StructureAsync(string path, string content, CancellationToken ct = default) => Task.FromResult<BirkNext.SourceDomains.SourceContract?>(null);
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

    public async Task<BirkNext.SourceDomains.SourceContract?> StructureAsync(string path, string content, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/system-diagnostics/generated-documentation/structure", new { Path = path, Content = content }, Json, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BirkNext.SourceDomains.SourceContract>(Json, ct) : null;
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
