using BirkNext.Integrations;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Pages;

/// <summary>An in-memory stand-in for the declared-journey backend: project-scoped, append-only revisions, removal retires (history kept).</summary>
internal sealed class DeclaredJourneyApiFake : IDeclaredIntegrationJourneyApiService
{
    public List<DeclaredIntegrationJourney> Stored { get; } = [];
    public Dictionary<string, List<DeclaredIntegrationJourneyRevision>> Revisions { get; } = new(StringComparer.Ordinal);
    public List<DeclaredIntegrationJourneyRequest> Sent { get; } = [];
    public List<(string ProjectKey, string JourneyId, DeclaredIntegrationJourneyRequest Request)> Revised { get; } = [];
    public List<string> ListedFor { get; } = [];
    public List<string> HistoryRequests { get; } = [];
    public DeclaredIntegrationJourneyMigrationReport? Migration { get; set; }

    /// <summary>Seeds a journey with its revision 1 (and further revisions when given).</summary>
    public DeclaredIntegrationJourney Seed(string projectKey, string name, string by, params string[][] revisions)
    {
        var slug = DeclaredIntegrationJourneyRules.Slug(name);
        var history = Revisions[$"{projectKey}|{slug}"] = [];
        foreach (var (components, i) in revisions.Select((c, i) => (c, i)))
            history.Add(new() { Revision = i + 1, DisplayName = name, Components = [.. components], DeclaredBy = by, DeclaredAt = DateTimeOffset.UtcNow.AddDays(i - 10),
                Origin = i == 0 ? DeclaredIntegrationJourneyRules.OriginDeclared : DeclaredIntegrationJourneyRules.OriginEdited });
        var journey = Current(projectKey, slug, history);
        Stored.Add(journey);
        return journey;
    }

    private static DeclaredIntegrationJourney Current(string projectKey, string slug, List<DeclaredIntegrationJourneyRevision> history)
    {
        var last = history[^1];
        return new()
        {
            ProjectKey = projectKey, JourneyId = slug, DisplayName = last.DisplayName, Description = last.Description, Components = last.Components,
            DeclaredBy = last.DeclaredBy, DeclaredAt = last.DeclaredAt, Revision = last.Revision, CreatedBy = history[0].DeclaredBy, CreatedAt = history[0].DeclaredAt,
        };
    }

    public Task<(IReadOnlyList<DeclaredIntegrationJourney>? Journeys, string? Error)> ListAsync(string projectKey, CancellationToken ct = default)
    {
        ListedFor.Add(projectKey);
        return Task.FromResult<(IReadOnlyList<DeclaredIntegrationJourney>?, string?)>(([.. Stored.Where(j => j.ProjectKey == projectKey && j.RetiredAt is null)], null));
    }

    public Task<(DeclaredIntegrationJourney? Journey, string? Error)> DeclareAsync(DeclaredIntegrationJourneyRequest request, CancellationToken ct = default)
    {
        Sent.Add(request);
        var slug = DeclaredIntegrationJourneyRules.Slug(request.DisplayName);
        if (Stored.Any(j => j.ProjectKey == request.ProjectKey && j.JourneyId == slug))
            return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((null, "This project already declares a journey with that id."));
        var history = Revisions[$"{request.ProjectKey}|{slug}"] = [Revision(1, request, DeclaredIntegrationJourneyRules.OriginDeclared)];
        var journey = Current(request.ProjectKey, slug, history);
        Stored.Add(journey);
        return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((journey, null));
    }

    public Task<(DeclaredIntegrationJourney? Journey, string? Error)> ReviseAsync(string projectKey, string journeyId, DeclaredIntegrationJourneyRequest request, CancellationToken ct = default)
    {
        Revised.Add((projectKey, journeyId, request));
        if (!Revisions.TryGetValue($"{projectKey}|{journeyId}", out var history))
            return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((null, "This project declares no journey with that id."));
        if (request.ExpectedRevision is { } expected && expected != history[^1].Revision)
            return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((null, "The journey changed since you opened it."));
        history.Add(Revision(history[^1].Revision + 1, request, DeclaredIntegrationJourneyRules.OriginEdited));
        var journey = Current(projectKey, journeyId, history);
        Stored.RemoveAll(j => j.ProjectKey == projectKey && j.JourneyId == journeyId);
        Stored.Add(journey);
        return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((journey, null));
    }

    public Task<(DeclaredIntegrationJourneyHistory? History, string? Error)> HistoryAsync(string projectKey, string journeyId, CancellationToken ct = default)
    {
        HistoryRequests.Add($"{projectKey}|{journeyId}");
        var journey = Stored.FirstOrDefault(j => j.ProjectKey == projectKey && j.JourneyId == journeyId);
        if (journey is null || !Revisions.TryGetValue($"{projectKey}|{journeyId}", out var history))
            return Task.FromResult<(DeclaredIntegrationJourneyHistory?, string?)>((null, "This project declares no journey with that id."));
        return Task.FromResult<(DeclaredIntegrationJourneyHistory?, string?)>((new DeclaredIntegrationJourneyHistory
        {
            Journey = journey, Revisions = [.. history.AsEnumerable().Reverse()],
        }, null));
    }

    public Task<string?> RemoveAsync(string projectKey, string journeyId, string? retiredBy, CancellationToken ct = default)
    {
        var index = Stored.FindIndex(j => j.ProjectKey == projectKey && j.JourneyId == journeyId && j.RetiredAt is null);
        if (index < 0) return Task.FromResult<string?>("Not found.");
        Stored[index] = Stored[index] with { RetiredAt = DateTimeOffset.UtcNow };
        return Task.FromResult<string?>(null);
    }

    public Task<DeclaredIntegrationJourneyMigrationReport?> MigrationReportAsync(string projectKey, CancellationToken ct = default) =>
        Task.FromResult(Migration is null ? null : Migration with { Items = [.. Migration.Items.Where(i => i.ProjectKey == projectKey)] });

    private static DeclaredIntegrationJourneyRevision Revision(int number, DeclaredIntegrationJourneyRequest request, string origin) => new()
    {
        Revision = number, DisplayName = request.DisplayName.Trim(), Description = request.Description, Components = DeclaredIntegrationJourneyRules.Chain(request.Components),
        DeclaredBy = request.DeclaredBy.Trim(), DeclaredAt = DateTimeOffset.UtcNow, Origin = origin,
    };
}
