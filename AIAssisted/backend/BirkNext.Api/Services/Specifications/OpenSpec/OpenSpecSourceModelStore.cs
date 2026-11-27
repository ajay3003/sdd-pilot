using System.Text.Json;
using Path = System.IO.Path;
using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// The OpenSpec source model beside the specification catalog it produced (App_Data, one JSON file per project + catalog fingerprint). Written
/// BEFORE the catalog row, so a catalog marked as OpenSpec always has its model unless the file was removed afterwards — which is reported as
/// «kildemodell ikke tilgjengelig» (NOT VERIFIED), never hidden. Also keeps each project's last OpenSpec route problem (e.g. no openspec/ folder).
/// Nothing here is ever applied to plans, cases or executions.
/// </summary>
public sealed class OpenSpecSourceModelStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();

    public static string DefaultDirectory(IHostEnvironment environment) => Path.Combine(AppDataLocation.Root(environment), "specification-sources", "openspec");

    private static string ProjectPart(string projectKey) => OpenSpecIdentity.Sha(projectKey)[..16];

    private string ModelPath(string projectKey, string fingerprint) =>
        Path.Combine(directory, $"{ProjectPart(projectKey)}-{SafeFingerprint(fingerprint)}.json");

    private string ProblemPath(string projectKey) => Path.Combine(directory, $"{ProjectPart(projectKey)}-route-problem.json");

    private static string SafeFingerprint(string fingerprint) =>
        fingerprint.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && fingerprint.Length <= 64 ? fingerprint : throw new ArgumentException("Ugyldig fingeravtrykk.");

    public void Save(string projectKey, string fingerprint, OpenSpecSourceModel model)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = ModelPath(projectKey, fingerprint);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(model, Json));
            File.Move(temp, path, overwrite: true);
            var problem = ProblemPath(projectKey);
            if (File.Exists(problem)) File.Delete(problem);
        }
    }

    public OpenSpecSourceModel? Find(string projectKey, string fingerprint)
    {
        lock (_gate)
        {
            try
            {
                var path = ModelPath(projectKey, fingerprint);
                return File.Exists(path) ? JsonSerializer.Deserialize<OpenSpecSourceModel>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    public sealed record RouteProblemRecord(string Code, string Message, DateTimeOffset At, string? ImportId);

    public void SaveProblem(string projectKey, RouteProblemRecord problem)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(ProblemPath(projectKey), JsonSerializer.Serialize(problem, Json));
        }
    }

    public RouteProblemRecord? Problem(string projectKey)
    {
        lock (_gate)
        {
            try { return File.Exists(ProblemPath(projectKey)) ? JsonSerializer.Deserialize<RouteProblemRecord>(File.ReadAllText(ProblemPath(projectKey)), Json) : null; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }
}
