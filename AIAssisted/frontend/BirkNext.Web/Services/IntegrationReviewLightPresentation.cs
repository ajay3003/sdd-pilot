using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// IQR light mode: each relevant integration is either resolved from evidence (never "runtime verified") or names the one decision it needs —
/// a consumer mapping to confirm, missing source evidence, several matching snapshots, an unbound source, or an ambiguous contract. Nothing is
/// bound or chosen automatically: this only decides what a tester sees first.
/// </summary>
public static class IntegrationReviewLightPresentation
{
    public sealed record Item(IntegrationReviewSubject Subject, bool Resolved, string Detail, string? Owner);

    public static Item Classify(IntegrationReviewSubject subject)
    {
        var facts = subject.Facts;
        string? Need() =>
            subject.MappingState == ConsumerMappingState.NeedsConfirmation ? "Consumer mapping needs confirmation."
            : subject.MappingState == ConsumerMappingState.Suggested ? "Suggested consumer mapping — confirm or change it."
            : facts?.SourceState == TestPackSourceState.Missing ? "Source evidence missing."
            : facts?.SourceState == TestPackSourceState.ReviewRequired ? "Source evidence needs review."
            : facts is { BoundArchive: null, SourceMatches: > 1 } ? $"{facts.SourceMatches} source snapshots match — choose one (the newest is never chosen automatically)."
            : facts?.SourceState is TestPackSourceState.AvailableNotBound or TestPackSourceState.Available && facts.BoundArchive is null ? "Source evidence available — bind it."
            : facts is { SchemaName: null, SchemaFiles.Count: > 1 } ? $"{facts.SchemaFiles.Count} contract candidates — choose one."
            : null;
        var need = Need();
        return need is null
            ? new(subject, true, facts?.BoundArchive is { } archive ? $"Resolved from evidence · source {archive}. Not runtime verified." : "Resolved from evidence. Not runtime verified.", null)
            : new(subject, false, need, need.StartsWith("Source", StringComparison.Ordinal) || need.Contains("snapshots", StringComparison.Ordinal) ? "Setup & Evidence" : "Integration");
    }

    public static IReadOnlyList<Item> Classify(IntegrationReviewReadiness readiness) => [.. readiness.Subjects.Select(Classify)];

    /// <summary>"5 integrations relevant · 4 resolved from evidence · 1 needs your decision".</summary>
    public static string Summary(IReadOnlyList<Item> items)
    {
        var open = items.Count(item => !item.Resolved);
        return $"{items.Count} integration{(items.Count == 1 ? "" : "s")} relevant · {items.Count - open} resolved from evidence · {open} need{(open == 1 ? "s" : "")} your decision";
    }
}
