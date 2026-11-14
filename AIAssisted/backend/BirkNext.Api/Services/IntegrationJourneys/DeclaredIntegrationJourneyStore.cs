using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Integrations;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Integration Journeys a reviewer declared for a project (name + ordered component chain), as one small JSON file per installation (workstation-local
/// QA configuration, like the Test Data registry). Project-scoped: every read names the project. Names only — no URL, value or credential is stored.
/// A declared journey is design metadata: it never runs, and it never confirms anything by itself (Test Coverage only proposes candidates from it).
/// </summary>
public sealed class DeclaredIntegrationJourneyStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<DeclaredIntegrationJourneyStore> _logger;
    private List<DeclaredIntegrationJourney> _journeys;

    public DeclaredIntegrationJourneyStore(IHostEnvironment environment, ILogger<DeclaredIntegrationJourneyStore> logger)
        : this(Path.Combine(AppDataLocation.Root(environment), "integration-journeys"), logger) { }

    public DeclaredIntegrationJourneyStore(string directory, ILogger<DeclaredIntegrationJourneyStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "declared-journeys.json");
        _journeys = Read();
    }

    /// <summary>Every declared journey of every project (the Scope Registry catalog lists them with their project).</summary>
    public IReadOnlyList<DeclaredIntegrationJourney> All() { lock (_gate) return [.. _journeys.OrderBy(j => j.ProjectKey, StringComparer.Ordinal).ThenBy(j => j.DisplayName, StringComparer.OrdinalIgnoreCase)]; }

    /// <summary>The declared journeys of one project; none for a blank key.</summary>
    public IReadOnlyList<DeclaredIntegrationJourney> ForProject(string? projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return [];
        var key = projectKey.Trim();
        lock (_gate) return [.. _journeys.Where(j => string.Equals(j.ProjectKey, key, StringComparison.Ordinal)).OrderBy(j => j.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Declares a journey. Refused when invalid, or when the project already has a journey with the same id (the name's slug): the
    /// existing journey is returned as <c>Existing</c> and nothing is overwritten. False/error when the file could not be written.</summary>
    public (DeclaredIntegrationJourney? Journey, DeclaredIntegrationJourney? Existing, string? Error) Declare(DeclaredIntegrationJourneyRequest request, DateTimeOffset now)
    {
        if (DeclaredIntegrationJourneyRules.Validate(request) is { } invalid) return (null, null, invalid);
        var journey = new DeclaredIntegrationJourney
        {
            ProjectKey = request.ProjectKey.Trim(), JourneyId = DeclaredIntegrationJourneyRules.Slug(request.DisplayName), DisplayName = request.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), Components = DeclaredIntegrationJourneyRules.Chain(request.Components),
            DeclaredBy = request.DeclaredBy.Trim(), DeclaredAt = now,
        };
        lock (_gate)
        {
            if (_journeys.FirstOrDefault(j => j.ProjectKey == journey.ProjectKey && j.JourneyId == journey.JourneyId) is { } existing)
                return (null, existing, $"This project already declares a journey with the id {existing.Id}. Use another name or remove it first.");
            var before = _journeys;
            _journeys = [.. _journeys, journey];
            if (Write()) return (journey, null, null);
            _journeys = before;
            return (null, null, "The declared journey could not be saved.");
        }
    }

    /// <summary>Removes one declared journey of a project. Recorded association decisions and scope members that name it are kept (they then read
    /// as missing / not current) — nothing else is changed.</summary>
    public bool Remove(string projectKey, string journeyId)
    {
        lock (_gate)
        {
            var before = _journeys;
            _journeys = [.. _journeys.Where(j => !(j.ProjectKey == projectKey.Trim() && j.JourneyId == journeyId.Trim()))];
            if (_journeys.Count == before.Count) return false;
            if (Write()) return true;
            _journeys = before;
            return false;
        }
    }

    private List<DeclaredIntegrationJourney> Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<DeclaredIntegrationJourney>>(File.ReadAllText(_path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Declared Integration Journeys could not be read; starting empty.");
            return [];
        }
    }

    private bool Write()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_journeys, Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Declared Integration Journeys could not be written.");
            return false;
        }
    }
}
