using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class QaDeltaReviewSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static (string SummaryJson, string DeltaItemsJson) Serialize(SpecComparisonResult result)
    {
        var s = result.Summary;
        var summaryDto = new DeltaSummaryDto(
            s.AddedRequirements, s.ModifiedRequirements, s.RemovedRequirements, s.UnchangedRequirements,
            s.AddedTests, s.RemovedTests, s.PotentiallyImpactedTests,
            s.AddedClarifications, s.RemovedClarifications, s.StillUnresolvedClarifications,
            s.UncoveredRequirements, s.NewClarificationRisks);

        var items = result.RequirementDeltas
            .Select(d => ToDto(d, ScenarioKind.Requirement))
            .Concat(result.TestDeltas.Select(d => ToDto(d, ScenarioKind.Test)))
            .Concat(result.ClarificationDeltas.Select(d => ToDto(d, ScenarioKind.NeedsClarification)))
            .ToArray();

        return (
            JsonSerializer.Serialize(summaryDto, JsonOptions),
            JsonSerializer.Serialize(items, JsonOptions)
        );
    }

    public static SpecComparisonResult Deserialize(string summaryJson, string deltaItemsJson)
    {
        var summaryDto = JsonSerializer.Deserialize<DeltaSummaryDto>(summaryJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize summaryJson");

        var items = JsonSerializer.Deserialize<DeltaItemDto[]>(deltaItemsJson, JsonOptions)
            ?? [];

        var summary = new SpecComparisonSummary(
            summaryDto.AddedRequirements, summaryDto.ModifiedRequirements,
            summaryDto.RemovedRequirements, summaryDto.UnchangedRequirements,
            summaryDto.AddedTests, summaryDto.RemovedTests, summaryDto.PotentiallyImpactedTests,
            summaryDto.AddedClarifications, summaryDto.RemovedClarifications,
            summaryDto.StillUnresolvedClarifications,
            summaryDto.UncoveredRequirements, summaryDto.NewClarificationRisks);

        var reqDeltas = items
            .Where(i => i.Classification == ScenarioKind.Requirement.ToString())
            .Select(ToDomain)
            .ToList();
        var testDeltas = items
            .Where(i => i.Classification == ScenarioKind.Test.ToString())
            .Select(ToDomain)
            .ToList();
        var clrDeltas = items
            .Where(i => i.Classification == ScenarioKind.NeedsClarification.ToString())
            .Select(ToDomain)
            .ToList();

        return new SpecComparisonResult(reqDeltas, testDeltas, clrDeltas, summary);
    }

    private static DeltaItemDto ToDto(SpecDeltaItem delta, ScenarioKind kind) => new(
        delta.Status.ToString(),
        kind.ToString(),
        delta.MatchKey,
        delta.OldCandidate?.Title,
        delta.NewCandidate?.Title,
        delta.NewCandidate?.ContextHeading ?? delta.OldCandidate?.ContextHeading,
        delta.ImpactHints);

    private static SpecDeltaItem ToDomain(DeltaItemDto dto)
    {
        var status = Enum.Parse<SpecDeltaStatus>(dto.Status);
        var classification = Enum.Parse<ScenarioKind>(dto.Classification);

        ExtractionCandidate? oldCandidate = dto.OldTitle is not null
            ? MakeCandidate(dto.OldTitle, classification, dto.ContextHeading)
            : null;

        ExtractionCandidate? newCandidate = dto.NewTitle is not null
            ? MakeCandidate(dto.NewTitle, classification, dto.ContextHeading)
            : null;

        return new SpecDeltaItem(status, classification, oldCandidate, newCandidate, dto.MatchKey, dto.ImpactHints);
    }

    private static ExtractionCandidate MakeCandidate(string title, ScenarioKind kind, string? contextHeading) =>
        new()
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = ClassificationSignal.BddPattern,
            SourceBlockType = BlockType.Heading,
            ContextHeading = contextHeading,
        };
}
