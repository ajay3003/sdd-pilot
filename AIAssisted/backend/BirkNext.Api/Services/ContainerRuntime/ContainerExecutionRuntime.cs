using Path = System.IO.Path;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.ContainerRuntime;

/// <summary>A host directory mounted into the container. Only BirkNext-created run directories are ever mounted.</summary>
public sealed record ContainerMount(string HostPath, string ContainerPath, bool ReadOnly);

/// <summary>
/// Everything needed to run one ephemeral container, built by trusted backend code only. No field is ever taken verbatim from a user:
/// the name and labels derive from a run id, the image and networks from validated configuration, mounts from BirkNext's own temp
/// directories, environment variables from an allow-list.
/// </summary>
public sealed record ContainerRunSpec
{
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public IReadOnlyList<string> Command { get; init; } = [];
    public IReadOnlyList<ContainerMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>Null = the runtime's default bridge network (ordinary outbound access).</summary>
    public string? Network { get; init; }
    /// <summary>Adds the runtime's host-gateway alias (Podman: host.containers.internal) for targets running on the BirkNext host.</summary>
    public bool HostGateway { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    public int MemoryMegabytes { get; init; } = 1024;
    public double Cpus { get; init; } = 2;
}

public sealed record ContainerRunOutcome(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Cancelled, string? StartError, bool Removed)
{
    /// <summary>Resource limits the runtime could not apply (e.g. rootless Podman without cgroup delegation): reported, never silently assumed.</summary>
    public IReadOnlyList<string> UnappliedLimits { get; init; } = [];
}

public sealed record ContainerImageStatus(string Image, bool Present, string? Digest, string Detail);

/// <summary>
/// A container execution runtime (Podman first; Docker or a Kubernetes Job could implement the same contract). Performance providers depend on
/// this, never on a CLI. A missing runtime or image is a tool limitation, never an application-quality result.
/// </summary>
public interface IContainerExecutionRuntime
{
    /// <summary>Stable id persisted with runs (e.g. <c>container.podman</c>).</summary>
    string RuntimeId { get; }
    string DisplayName { get; }
    /// <summary>The alias a container uses to reach the BirkNext host (Podman: host.containers.internal).</summary>
    string HostGatewayAlias { get; }
    Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default);
    Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default);
    /// <summary>Pulls the image (only ever called on an explicit, policy-permitted action).</summary>
    Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default);
    /// <summary>Runs to completion, timeout or cancellation; the container is always removed afterwards (<see cref="ContainerRunOutcome.Removed"/>).</summary>
    Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct);
    Task<bool> RemoveAsync(string name, CancellationToken ct = default);
    /// <summary>Names of BirkNext-managed containers of one component (label birknext.managed=true + birknext.component), never other containers.</summary>
    Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default);
}

/// <summary>Validation shared by runtimes: values that become CLI arguments must be plain, so nothing can turn into an extra flag.</summary>
public static partial class ContainerArgumentRules
{
    public static bool IsName(string value) => NamePattern().IsMatch(value);
    public static bool IsImage(string value) => ImagePattern().IsMatch(value) && !value.EndsWith(":latest", StringComparison.OrdinalIgnoreCase);
    public static bool IsNetwork(string value) => NamePattern().IsMatch(value);
    public static bool IsLabel(string key, string value) => LabelKeyPattern().IsMatch(key) && LabelValuePattern().IsMatch(value);
    public static bool IsEnvironment(string key, string value) => EnvKeyPattern().IsMatch(key) && !value.Any(char.IsControl) && value.Length <= 2048;
    public static bool IsContainerPath(string value) => ContainerPathPattern().IsMatch(value) && !value.Split('/').Any(s => s is "." or "..");
    /// <summary>A host path for --mount: absolute, no comma/quote/newline (the --mount syntax is comma-separated).</summary>
    public static bool IsHostPath(string value) => Path.IsPathFullyQualified(value) && !value.Any(c => c is ',' or '"' or '\'' || char.IsControl(c));

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")] private static partial Regex NamePattern();
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?(:[0-9]{1,5})?(/[a-z0-9]([a-z0-9._-]*[a-z0-9])?)+(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(@sha256:[a-f0-9]{64})?$")] private static partial Regex ImagePattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,62}$")] private static partial Regex LabelKeyPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}$")] private static partial Regex LabelValuePattern();
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}$")] private static partial Regex EnvKeyPattern();
    [GeneratedRegex(@"^/[a-z0-9/_.-]{1,200}$")] private static partial Regex ContainerPathPattern();
}

/// <summary>
/// Podman CLI runtime (`podman run` per test, never a long-running service). Hardened by default: no --privileged, all capabilities dropped,
/// no-new-privileges, read-only root filesystem with a small /tmp, pid/memory/cpu limits, no host network/PID, no runtime socket and no mount
/// other than the run's own directories. Every argument goes through <see cref="IProcessRunner"/> as a structured list — no shell.
/// </summary>
public sealed partial class PodmanContainerExecutionRuntime(IProcessRunner runner, PerformanceTestOptions options, ILogger<PodmanContainerExecutionRuntime> logger) : IContainerExecutionRuntime
{
    public static readonly Version MinimumVersion = new(4, 0, 0);
    public string RuntimeId => PerformanceProviderIds.PodmanRuntime;
    public string DisplayName => "Podman";
    public string HostGatewayAlias => "host.containers.internal";
    private string Cli => string.IsNullOrWhiteSpace(options.Container.CliPath) ? "podman" : options.Container.CliPath!;
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    public async Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = new PerformanceRuntimeStatus { RuntimeId = RuntimeId, DisplayName = DisplayName };
        if (!string.IsNullOrWhiteSpace(options.Container.CliPath) && !File.Exists(options.Container.CliPath))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "The configured Podman CLI path (PerformanceTests:Container:CliPath) does not exist." };
        var client = await runner.RunAsync(new ProcessSpec(Cli, ["version", "--format", "{{.Client.Version}}"], Path.GetTempPath()), Short, 4096, ct);
        if (client.StartError is not null)
            return status with { Availability = ProviderAvailability.Unavailable, Detail = "Podman is not installed on this BirkNext host (the podman CLI could not be started)." };
        var server = await runner.RunAsync(new ProcessSpec(Cli, ["info", "--format", "{{.Version.Version}}"], Path.GetTempPath()), Short, 4096, ct);
        if (server.ExitCode != 0)
            return status with { Availability = ProviderAvailability.RuntimeUnavailable, Version = client.StandardOutput.Trim(),
                Detail = "The Podman CLI is installed but its machine/service is not reachable (start it with `podman machine start`)." };
        var text = server.StandardOutput.Trim();
        if (!Version.TryParse(VersionPattern().Match(text).Value, out var version))
            return status with { Availability = ProviderAvailability.Misconfigured, Detail = "Podman did not report a recognisable version." };
        if (version < MinimumVersion)
            return status with { Availability = ProviderAvailability.VersionUnsupported, Version = version.ToString(), Detail = $"Podman {version} is older than the supported minimum {MinimumVersion}." };
        return status with { Availability = ProviderAvailability.Available, Version = version.ToString(), Detail = $"Podman {version} is available." };
    }

    public async Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default)
    {
        if (!ContainerArgumentRules.IsImage(image)) return new(image, false, null, "The configured image reference is not a pinned, fully-qualified image (no ':latest').");
        var inspect = await runner.RunAsync(new ProcessSpec(Cli, ["image", "inspect", image, "--format", "{{.Digest}}"], Path.GetTempPath()), Short, 4096, ct);
        return inspect.ExitCode == 0
            ? new(image, true, inspect.StandardOutput.Trim() is { Length: > 0 } d ? d : null, $"{image} is available locally.")
            : new(image, false, null, $"{image} is not available locally; no download is performed at run time.");
    }

    public async Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default)
    {
        if (!ContainerArgumentRules.IsImage(image)) return new(image, false, null, "The configured image reference is not a pinned, fully-qualified image.");
        var pull = await runner.RunAsync(new ProcessSpec(Cli, ["pull", "--quiet", image], Path.GetTempPath()), TimeSpan.FromMinutes(10), 16 * 1024, ct);
        if (pull.ExitCode != 0) return new(image, false, null, "The image could not be pulled.");
        return await ImageAsync(image, ct);
    }

    /// <summary>The exact `podman run` argument list for a spec (unit-tested; nothing user-supplied can become a flag).</summary>
    public static List<string> RunArguments(ContainerRunSpec spec, IReadOnlySet<string>? controllers = null)
    {
        if (!ContainerArgumentRules.IsName(spec.Name)) throw new ArgumentException("Invalid container name.", nameof(spec));
        if (!ContainerArgumentRules.IsImage(spec.Image)) throw new ArgumentException("Invalid image reference.", nameof(spec));
        if (spec.Network is { } n && !ContainerArgumentRules.IsNetwork(n)) throw new ArgumentException("Invalid network name.", nameof(spec));
        var args = new List<string> { "run", "--name", spec.Name, "--rm", "--pull=never", "--read-only", "--tmpfs", "/tmp:rw,size=64m", "--cap-drop=ALL",
            "--security-opt=no-new-privileges" };
        // Resource limits need the matching cgroup controller (rootless Podman often has none delegated); without it the limit flag fails the run.
        if (controllers is null || controllers.Contains("pids")) args.Add("--pids-limit=512");
        if (controllers is null || controllers.Contains("memory")) args.Add($"--memory={Math.Clamp(spec.MemoryMegabytes, 128, 8192)}m");
        if (controllers is null || controllers.Contains("cpu")) args.Add($"--cpus={Math.Clamp(spec.Cpus, 0.25, 16).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
        foreach (var (key, value) in spec.Labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            if (!ContainerArgumentRules.IsLabel(key, value)) throw new ArgumentException($"Invalid label {key}.", nameof(spec));
            args.Add("--label"); args.Add($"{key}={value}");
        }
        if (spec.Network is { } network) { args.Add("--network"); args.Add(network); }
        if (spec.HostGateway) { args.Add("--add-host"); args.Add("host.containers.internal:host-gateway"); }
        foreach (var m in spec.Mounts)
        {
            if (!ContainerArgumentRules.IsHostPath(m.HostPath) || !ContainerArgumentRules.IsContainerPath(m.ContainerPath)) throw new ArgumentException("Invalid mount.", nameof(spec));
            args.Add("--mount"); args.Add($"type=bind,source={m.HostPath},target={m.ContainerPath}{(m.ReadOnly ? ",readonly" : "")}");
        }
        foreach (var (key, value) in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!ContainerArgumentRules.IsEnvironment(key, value)) throw new ArgumentException($"Invalid environment variable {key}.", nameof(spec));
            args.Add("--env"); args.Add($"{key}={value}");
        }
        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }

    private IReadOnlySet<string>? _controllers;

    /// <summary>cgroup controllers available to containers (podman info), cached. Empty when none are delegated.</summary>
    public async Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default)
    {
        if (_controllers is not null) return _controllers;
        var info = await runner.RunAsync(new ProcessSpec(Cli, ["info", "--format", "{{.Host.CgroupControllers}}"], Path.GetTempPath()), Short, 4096, ct);
        var set = info.ExitCode != 0 ? new HashSet<string>(StringComparer.Ordinal)
            : info.StandardOutput.Trim().Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return _controllers = set;
    }

    public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
    {
        var controllers = await ControllersAsync(ct);
        var args = RunArguments(spec, controllers);
        var unapplied = new[] { "memory", "cpu", "pids" }.Where(c => !controllers.Contains(c)).ToList();
        ProcessOutcome outcome;
        var removed = false;
        try
        {
            outcome = await runner.RunAsync(new ProcessSpec(Cli, args, Path.GetTempPath()), spec.Timeout, spec.MaxOutputBytes, ct);
        }
        finally
        {
            // Killing the podman client does not stop the container: always force-remove it (idempotent), then verify it is gone.
            removed = await RemoveAsync(spec.Name, CancellationToken.None);
        }
        return new ContainerRunOutcome(outcome.ExitCode, outcome.StandardOutput, outcome.StandardError, outcome.TimedOut, outcome.Cancelled, outcome.StartError, removed) { UnappliedLimits = unapplied };
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        if (!ContainerArgumentRules.IsName(name)) return false;
        await runner.RunAsync(new ProcessSpec(Cli, ["rm", "--force", "--ignore", "--time", "5", name], Path.GetTempPath()), Short, 4096, ct);
        var exists = await runner.RunAsync(new ProcessSpec(Cli, ["container", "exists", name], Path.GetTempPath()), Short, 1024, ct);
        if (exists.ExitCode == 0) logger.LogWarning("BirkNext-managed container {Name} could not be removed.", name);
        return exists.ExitCode != 0;
    }

    public async Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default)
    {
        if (!ContainerArgumentRules.IsLabel("birknext.component", component)) return [];
        var list = await runner.RunAsync(new ProcessSpec(Cli, ["ps", "--all", "--filter", "label=birknext.managed=true", "--filter", $"label=birknext.component={component}",
            "--format", "{{.Names}}"], Path.GetTempPath()), Short, 64 * 1024, ct);
        return list.ExitCode != 0 ? [] : list.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(ContainerArgumentRules.IsName).ToList();
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")] private static partial Regex VersionPattern();
}
