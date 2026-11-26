using System.Text.Json;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// System Settings → Manuell testing → Testoversikt (2026-11-25): which dashboard parts are shown. One installation-wide setup (like Translation and
/// Azure DevOps – Test Plans), stored in App_Data as JSON; only explicit choices are written, so a widget's default applies until someone changes
/// it. Changing it never touches plans, cases or source data and writes no plan revision.
/// </summary>
public sealed class ManualTestingDashboardSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private string FilePath => System.IO.Path.Combine(directory, "dashboard-settings.json");

    public static string DefaultDirectory(IHostEnvironment environment) => System.IO.Path.Combine(AppDataLocation.Root(environment), "manual-testing");

    public ManualTestingDashboardSettings Read()
    {
        lock (_gate)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ManualTestingDashboardSettings>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    /// <summary>Stores the given switches (merged with the saved ones) or resets to defaults. Unknown ids are refused: nothing is written then.</summary>
    public ManualTestingDashboardSettings Save(SaveManualTestingDashboardSettingsRequest request, string actor, DateTimeOffset now)
    {
        var unknown = (request.Visible?.Keys.AsEnumerable() ?? []).Where(k => !ManualTestingDashboard.IsKnown(k)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Ukjent del av testoversikten: {string.Join(", ", unknown.Take(5))}.");
        if (request.PlanStructure is { } structure && !PlanStructureTemplates.Known(structure)) throw new ArgumentException($"Ukjent testplanstruktur: {structure}.");
        lock (_gate)
        {
            var current = request.ResetToDefaults ? new ManualTestingDashboardSettings() : Read();
            var visible = new Dictionary<string, bool>(current.Visible, StringComparer.Ordinal);
            foreach (var (id, on) in request.Visible ?? []) visible[id] = on;
            var saved = new ManualTestingDashboardSettings
            {
                Visible = request.ResetToDefaults ? new(StringComparer.Ordinal) : visible, UpdatedAt = now, UpdatedBy = actor,
                PlanStructure = request.ResetToDefaults ? null : request.PlanStructure ?? current.PlanStructure,
            };
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, FilePath, overwrite: true);
            return saved;
        }
    }
}
