using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Implements <see cref="IArtifactParserService"/> by delegating to the four
/// parsers: two injected services and two static utilities.
///
/// <list type="bullet">
///   <item><see cref="IConstitutionAnalysisService"/> — parses constitution markdown</item>
///   <item><see cref="SpecExplorerService"/> (static) — parses specification markdown</item>
///   <item><see cref="IPlanAnalysisService"/> — parses plan markdown</item>
///   <item><see cref="TaskExplorerService"/> (static) — parses task markdown</item>
/// </list>
///
/// All four parsers are deterministic and have no side effects.
/// Parsing only happens when the corresponding text is non-empty.
/// </summary>
public sealed class ArtifactParserService : IArtifactParserService
{
    private readonly IConstitutionAnalysisService _constitutionParser;
    private readonly IPlanAnalysisService         _planParser;

    public ArtifactParserService(
        IConstitutionAnalysisService constitutionParser,
        IPlanAnalysisService         planParser)
    {
        _constitutionParser = constitutionParser;
        _planParser         = planParser;
    }

    public ParsedArtifactSet Parse(
        string? constitutionText,
        string? specText,
        string? planText,
        string? taskText)
    {
        return new ParsedArtifactSet
        {
            Constitution = !string.IsNullOrWhiteSpace(constitutionText)
                ? _constitutionParser.Parse(constitutionText)
                : null,

            Spec = !string.IsNullOrWhiteSpace(specText)
                ? SpecExplorerService.Parse(specText)
                : null,

            Plan = !string.IsNullOrWhiteSpace(planText)
                ? _planParser.Parse(planText)
                : null,

            Tasks = !string.IsNullOrWhiteSpace(taskText)
                ? TaskExplorerService.Parse(taskText)
                : null,
        };
    }
}
