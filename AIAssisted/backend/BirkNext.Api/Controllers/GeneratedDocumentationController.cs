using BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;
using BirkNext.GeneratedDocumentation;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Generated Documentation Health diagnostic. Reads stored Source Analysis evidence only; authored artifact content in the request is
/// used for this run and never stored.</summary>
[ApiController]
[Route("api/system-diagnostics/generated-documentation")]
public sealed class GeneratedDocumentationController(GeneratedDocumentationDiagnosticService diagnostics) : ControllerBase
{
    private const int MaxAuthoredArtifacts = 16;
    private const int MaxAuthoredCharacters = 2 * 1024 * 1024;

    [HttpPost("run")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<GeneratedDocumentationDiagnosticRun>> Run([FromBody] GeneratedDocumentationDiagnosticRequest? request, CancellationToken cancellationToken)
    {
        request ??= new GeneratedDocumentationDiagnosticRequest();
        if (request.AuthoredArtifacts.Count > MaxAuthoredArtifacts || request.AuthoredArtifacts.Any(a => (a.Content?.Length ?? 0) > MaxAuthoredCharacters))
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too many or too large authored artifacts",
                detail: $"At most {MaxAuthoredArtifacts} authored artifacts of up to {MaxAuthoredCharacters / (1024 * 1024)} MB each are compared.");
        return Ok(await diagnostics.RunAsync(request, cancellationToken));
    }

    /// <summary>
    /// The structure of one captured generated contract artifact (OpenAPI/Swagger YAML or JSON, GraphQL SDL) for the read-only Generated
    /// Documentation Explorer: the same deterministic readers Source Analysis uses. Stateless; nothing is stored, executed, fetched or resolved
    /// remotely. 422 when the text is not a readable document of that type.
    /// </summary>
    [HttpPost("structure")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public ActionResult<BirkNext.SourceDomains.SourceContract> Structure([FromBody] GeneratedArtifactStructureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || request.Content is null) return BadRequest();
        var ext = System.IO.Path.GetExtension(request.Path).ToLowerInvariant();
        try
        {
            var contract = ext switch
            {
                ".graphql" or ".graphqls" or ".gql" => BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.ReadGraphQl(request.Path, request.Content),
                ".yaml" or ".yml" or ".json" => BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.ReadOpenApi(request.Path, request.Content),
                _ => null,
            };
            return contract is null ? UnprocessableEntity() : Ok(contract);
        }
        catch (System.Text.Json.JsonException) { return UnprocessableEntity(); }
    }
}

/// <summary>A captured generated artifact's path (for its type) and text.</summary>
public sealed record GeneratedArtifactStructureRequest(string Path, string? Content);
