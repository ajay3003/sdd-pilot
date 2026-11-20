using System.Globalization;
using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The local LibreTranslate server BirkNext runs with Podman for offline translation (System Settings → Translation): download the pinned image
/// (needs internet once), start, stop and report status fact by fact. The server is published on 127.0.0.1 only; its language models live in a
/// named volume so they are downloaded once (only the configured languages are loaded). Nothing starts by itself. Not available with the native
/// runner (Tester Package): there BirkNext cannot run containers, and another LibreTranslate server's address or Azure is used instead.
/// </summary>
public sealed class LibreTranslateServiceManager(IProcessRunner runner, PerformanceTestOptions options, IHttpClientFactory http, ILogger<LibreTranslateServiceManager> logger)
{
    public const string ContainerName = "birknext-libretranslate";
    public const string ModelVolume = "birknext-libretranslate-models";
    public const string NativeRunner = "BirkNext runs without Podman here (Tester Package), so it cannot run the local LibreTranslate server. Give the address of a LibreTranslate server, or use Azure AI Translator.";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    public bool CanManage => !options.UsesNativeRunner;
    private string Cli => string.IsNullOrWhiteSpace(options.Container.CliPath) ? "podman" : options.Container.CliPath!;
    private Task<ProcessOutcome> Podman(TimeSpan timeout, params string[] args) => runner.RunAsync(new ProcessSpec(Cli, args, System.IO.Path.GetTempPath()), timeout, 64 * 1024, CancellationToken.None);

    /// <summary>The exact `podman run` arguments for the managed server (unit-tested): loopback-only port, model volume, only the needed languages.</summary>
    public static List<string> RunArguments(TranslationSettings s)
    {
        var languages = new[] { s.SourceLanguage }.Concat(s.TargetLanguages).Where(l => l != TranslationLanguages.Auto).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (s.SourceLanguage == TranslationLanguages.Auto && !languages.Contains(TranslationLanguages.English)) languages.Insert(0, TranslationLanguages.English);
        return
        [
            "run", "--detach", "--name", ContainerName, "--pull=never", "--label", "birknext.managed=true", "--label", "birknext.component=translation",
            "--publish", $"127.0.0.1:{s.LibreTranslatePort.ToString(CultureInfo.InvariantCulture)}:5000",
            "--volume", $"{ModelVolume}:/home/libretranslate/.local",
            "--env", $"LT_LOAD_ONLY={string.Join(',', languages)}",
            s.LibreTranslateImage,
        ];
    }

    public async Task<LibreTranslateServiceStatus> StatusAsync(TranslationSettings s, CancellationToken ct)
    {
        var url = s.EffectiveLibreTranslateUrl;
        var status = new LibreTranslateServiceStatus { Image = s.LibreTranslateImage, Url = url };
        if (s.LibreTranslateManaged)
        {
            if (!CanManage) return status with { PodmanDetail = NativeRunner, Detail = NativeRunner };
            var version = await Podman(Short, "info", "--format", "{{.Version.Version}}");
            if (version.StartError is not null) return status with { PodmanDetail = "Podman is not installed on this PC.", Detail = "Podman is not installed on this PC, so the local server cannot run." };
            if (version.ExitCode != 0) return status with { PodmanDetail = "Podman is installed but not running (podman machine start).", Detail = "Start Podman (podman machine start), then start the local server." };
            var image = await Podman(Short, "image", "exists", s.LibreTranslateImage);
            var inspect = await Podman(Short, "container", "inspect", "--format", "{{.State.Running}}", ContainerName);
            status = status with
            {
                PodmanAvailable = true, PodmanDetail = $"Podman {version.StandardOutput.Trim()}", ImagePresent = image.ExitCode == 0,
                ContainerExists = inspect.ExitCode == 0, Running = inspect.ExitCode == 0 && inspect.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            };
        }
        var (answering, languages) = await LanguagesAsync(url, ct);
        var pair = answering && PairOffered(languages, s.SourceLanguage, s.DefaultTarget);
        var codes = languages.Select(l => l.Code).ToList();
        string detail = !s.LibreTranslateManaged
            ? answering ? (pair ? "The server answers and offers the language pair." : $"The server answers but does not offer {Pair(s)}.") : $"The server at {url} does not answer."
            : !status.ImagePresent ? "The image is not downloaded yet: choose Download image (needs internet once)."
            : !status.Running ? "The local server is stopped: choose Start."
            : !answering ? "The local server is starting (the first start downloads the language models; this takes a few minutes and needs internet once)."
            : pair ? $"Running on {url} — translates {Pair(s)} offline." : $"Running, but {Pair(s)} is not offered (yet). Check the From and To languages.";
        return status with { Answering = answering, Languages = codes, PairAvailable = pair, Detail = detail };
    }

    private static string Pair(TranslationSettings s) => $"{TranslationLanguages.Name(s.SourceLanguage)} → {TranslationLanguages.Name(s.DefaultTarget)}";

    public sealed record ServerLanguage(string Code, List<string> Targets);

    /// <summary>GET {url}/languages (the documented list of {code, name, targets}); empty when the server does not answer.</summary>
    public async Task<(bool Answering, List<ServerLanguage> Languages)> LanguagesAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (false, []);
        try
        {
            var client = http.CreateClient(nameof(LibreTranslateServiceManager));
            client.Timeout = TimeSpan.FromSeconds(5);
            using var response = await client.GetAsync(new Uri(uri, "languages"), ct);
            if (!response.IsSuccessStatusCode) return (false, []);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (true, [.. doc.RootElement.EnumerateArray().Select(e => new ServerLanguage(e.GetProperty("code").GetString() ?? "",
                e.TryGetProperty("targets", out var t) && t.ValueKind == JsonValueKind.Array ? [.. t.EnumerateArray().Select(x => x.GetString() ?? "")] : []))]);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException) { return (false, []); }
    }

    public static bool PairOffered(IReadOnlyList<ServerLanguage> languages, string source, string target) =>
        source == TranslationLanguages.Auto
            ? languages.Any(l => string.Equals(l.Code, target, StringComparison.OrdinalIgnoreCase))
            : languages.Any(l => string.Equals(l.Code, source, StringComparison.OrdinalIgnoreCase) && (l.Targets.Count == 0 || l.Targets.Contains(target, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Downloads the pinned image (the only step that needs internet besides the first model download).</summary>
    public async Task<string> PullAsync(TranslationSettings s)
    {
        if (!CanManage) return NativeRunner;
        var pull = await Podman(TimeSpan.FromMinutes(20), "pull", "--quiet", s.LibreTranslateImage);
        if (pull.StartError is not null) return "Podman is not installed on this PC.";
        return pull.ExitCode == 0 ? $"Downloaded {s.LibreTranslateImage}." : $"The image could not be downloaded (no internet, or the image name is wrong): {Clip(pull.StandardError)}";
    }

    /// <summary>Starts the local server with the current settings (an earlier container of BirkNext's is replaced; the model volume is kept).</summary>
    public async Task<string> StartAsync(TranslationSettings s)
    {
        if (!CanManage) return NativeRunner;
        if (!s.LibreTranslateManaged) return "LibreTranslate is set to another server's address; BirkNext does not start it.";
        if ((await Podman(Short, "image", "exists", s.LibreTranslateImage)).ExitCode != 0) return "Download the image first.";
        await Podman(Short, "rm", "--force", "--ignore", ContainerName);
        var run = await Podman(TimeSpan.FromMinutes(2), [.. RunArguments(s)]);
        if (run.ExitCode != 0)
        {
            logger.LogWarning("The local LibreTranslate server could not be started: {Error}", Clip(run.StandardError));
            return $"The local server could not be started: {Clip(run.StandardError)}";
        }
        return $"Started on {s.EffectiveLibreTranslateUrl}. The first start downloads the language models (needs internet once) and takes a few minutes.";
    }

    public async Task<string> StopAsync()
    {
        if (!CanManage) return NativeRunner;
        await Podman(TimeSpan.FromSeconds(60), "stop", "--time", "10", ContainerName);
        await Podman(Short, "rm", "--force", "--ignore", ContainerName);
        return "Stopped. The downloaded language models are kept for the next start.";
    }

    private static string Clip(string text) => text.Trim() is { Length: > 300 } t ? t[..300] + "…" : text.Trim();
}
