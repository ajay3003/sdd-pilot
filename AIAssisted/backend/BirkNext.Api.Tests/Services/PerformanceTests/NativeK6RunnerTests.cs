using System.Security.Cryptography;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Path = System.IO.Path;
using P = BirkNext.Api.Tests.Services.PerformanceTests.PerformanceTestReviewTests;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// The Tester Package's native k6 runner. The provider stays one implementation (script, safety, parsing, evidence); a native runtime only changes
/// where k6 runs: host paths instead of mounts, the host network instead of a container alias, and limits/graceful stop/CA bundles reported as
/// unavailable rather than assumed. No test here executes a k6 binary: the executable checks use an inert file named k6.exe.
/// </summary>
public sealed class NativeK6RunnerTests
{
    /// <summary>The fake runtime with the native runtime's sharing semantics: k6 sees host paths and the host network.</summary>
    private sealed class NativeFake(P.FakeRuntime inner) : IContainerExecutionRuntime
    {
        public List<ContainerRunSpec> Runs { get; } = [];
        public string? Script { get; private set; }
        public string RuntimeId => PerformanceProviderIds.NativeRuntime;
        public string DisplayName => "Bundled k6 (native Windows process)";
        public string HostGatewayAlias => "localhost";
        public bool SharesHostNetwork => true;
        public bool SharesHostFilesystem => true;
        public Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default) => inner.StatusAsync(ct);
        public Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default) => inner.ImageAsync(image, ct);
        public Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default) => inner.PullAsync(image, ct);
        public Task<bool> RemoveAsync(string name, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<ContainerStatsSnapshot> StatsAsync(string name, CancellationToken ct = default) => Task.FromResult(new ContainerStatsSnapshot(null, null, null, null, "n/a"));
        public Task<ContainerInstanceInfo?> InspectAsync(string name, CancellationToken ct = default) => Task.FromResult<ContainerInstanceInfo?>(null);

        /// <summary>Like k6 on the host: reads the script from the host path in the command and writes the summary where the script says.</summary>
        public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
        {
            Runs.Add(spec);
            if (spec.Command[0] == "version") return new ContainerRunOutcome(0, "k6 v1.0.0 (go1.24.1, windows/amd64)", "", false, false, null, true);
            Script = await File.ReadAllTextAsync(spec.Command[^1], ct);
            var summaryPath = System.Text.RegularExpressions.Regex.Match(Script, "const SUMMARY_PATH = \"([^\"]+)\"").Groups[1].Value;
            await File.WriteAllTextAsync(summaryPath, P.Summary(), ct);
            return new ContainerRunOutcome(0, "", "", false, false, null, true) { UnappliedLimits = ["memory", "cpu", "pids"] };
        }
    }

    private static K6PerformanceTestProvider Provider(IContainerExecutionRuntime runtime) => new(new PerformanceTestOptions(), runtime, NullLogger<K6PerformanceTestProvider>.Instance);

    [Fact]
    public async Task A_native_run_uses_host_paths_the_host_network_and_says_what_a_container_would_have_added()
    {
        var runtime = new NativeFake(new P.FakeRuntime());
        var dir = Path.Combine(Path.GetTempPath(), "birknext-native-k6-test", Guid.NewGuid().ToString("N"));
        var definition = P.Definition(d => d with { TargetOrigin = "http://127.0.0.1:5097" });
        var result = await Provider(runtime).ExecuteAsync(new PerformanceProviderInput(Guid.NewGuid(), definition, P.Data(), dir, TimeSpan.FromMinutes(2)), null, CancellationToken.None);

        result.State.Should().Be(PerformanceRunState.Completed, result.Reason);
        result.ResultArtifact.Should().Be(PerformanceResultArtifactState.Parsed, "the same summary parser reads the native run's handleSummary JSON");
        result.Metrics!.Latency.P95Ms.Should().Be(420);
        var spec = runtime.Runs.Single(s => s.Command[0] == "run");
        spec.Command.Should().NotContain(a => a.Contains("/birknext/"), "a native process has no mounts: every path is the run's own host directory");
        spec.Command[^1].Should().StartWith(dir.Replace('\\', '/'));
        spec.Network.Should().BeNull("a native process has no container network to choose");
        spec.HostGateway.Should().BeFalse();
        runtime.Script.Should().Contain("http://127.0.0.1:5097", "a loopback target stays loopback on the host (no host.containers.internal alias)")
            .And.NotContain("host.containers.internal");
        result.RuntimeId.Should().Be(PerformanceProviderIds.NativeRuntime);
        result.ContainerImage.Should().BeNull("no container image ran");
        result.MetricsSource.Should().Contain("bundled native executable");
        result.Limitations.Should().Contain(l => l.Contains("native process without memory/CPU/process limits"));
        Directory.Exists(dir).Should().BeFalse("the run directory is removed after the results are read");
    }

    [Fact]
    public async Task Native_status_names_the_bundled_executable_and_never_suggests_a_container_pull()
    {
        var status = await Provider(new NativeFake(new P.FakeRuntime())).StatusAsync();
        status.Availability.Should().Be(ProviderAvailability.Available);
        status.Version.Should().Be("1.0.0");
        status.Image.Should().Be("bundled k6.exe");
        status.Detail.Should().Contain("bundled executable").And.NotContain("podman");
        var missing = await Provider(new NativeFake(new P.FakeRuntime { ImagePresent = false })).StatusAsync();
        missing.Availability.Should().Be(ProviderAvailability.Unavailable);
        missing.Detail.Should().NotContain("podman pull", "a tester is never told to install or use Podman");
        (await Provider(new NativeFake(new P.FakeRuntime())).PrepareAsync()).Should().Contain("nothing is downloaded");
        (await Provider(new NativeFake(new P.FakeRuntime())).DefaultCaStorePemAsync()).Should().BeNull("there is no image CA store to read");
    }

    [Fact]
    public void The_target_is_addressed_exactly_as_configured_on_the_host()
    {
        var provider = Provider(new NativeFake(new P.FakeRuntime()));
        provider.ResolveTarget("http://localhost:5097/x").Should().Match<ContainerTarget>(t => t.ExecutionOrigin == "http://localhost:5097" && t.Network == null && !t.HostGateway);
        provider.ResolveTarget("https://app-qa.example.test").ExecutionOrigin.Should().Be("https://app-qa.example.test");
        Provider(new P.FakeRuntime()).ResolveTarget("http://localhost:5097").ExecutionOrigin.Should().Be("http://host.containers.internal:5097", "the container runner is unchanged");
    }

    // ── The native runtime itself (no process is started) ─────────────────────────────────────────────────────────

    private static (NativeProcessExecutionRuntime Runtime, string Exe, string Sha) Runtime(Action<string>? tamper = null, string fileName = "k6.exe", string? pinned = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "birknext-native-k6-exe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, fileName);
        File.WriteAllBytes(exe, "inert test bytes, never executed"u8.ToArray());
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant();
        tamper?.Invoke(exe);
        var options = new PerformanceTestOptions { Runner = PerformanceTestOptions.Runners.Native, Native = new PerformanceNativeOptions { K6Path = exe, K6Sha256 = pinned ?? sha } };
        return (new NativeProcessExecutionRuntime(options, NullLogger<NativeProcessExecutionRuntime>.Instance), exe, sha);
    }

    [Fact]
    public void The_executable_is_accepted_only_when_configured_present_and_matching_its_pinned_hash()
    {
        Runtime().Runtime.Executable().Should().Match<(string? Path, string? Sha256, string? Problem, ProviderAvailability Availability)>(e => e.Problem == null && e.Availability == ProviderAvailability.Available);
        var tampered = Runtime(exe => File.AppendAllText(exe, "changed")).Runtime.Executable();
        tampered.Availability.Should().Be(ProviderAvailability.Misconfigured);
        tampered.Path.Should().BeNull("a changed executable is never used");
        tampered.Problem.Should().Contain("does not match its pinned SHA-256");
        var missing = Runtime(File.Delete).Runtime.Executable();
        missing.Availability.Should().Be(ProviderAvailability.Unavailable);
        missing.Problem.Should().Contain("bundled k6 executable is missing").And.NotContainEquivalentOf("podman");
        Runtime(fileName: "evil.exe").Runtime.Executable().Availability.Should().Be(ProviderAvailability.Misconfigured, "only k6.exe is ever started");
        Runtime(pinned: "").Runtime.Executable().Problem.Should().Contain("no pinned SHA-256");
        new NativeProcessExecutionRuntime(new PerformanceTestOptions(), NullLogger<NativeProcessExecutionRuntime>.Instance).Executable().Availability.Should().Be(ProviderAvailability.Unavailable);
        new NativeProcessExecutionRuntime(new PerformanceTestOptions { Native = new() { K6Path = "runtime\\k6\\k6.exe", K6Sha256 = new string('a', 64) } }, NullLogger<NativeProcessExecutionRuntime>.Instance)
            .Executable().Availability.Should().Be(ProviderAvailability.Misconfigured, "a relative path could resolve to anything");
    }

    [Fact]
    public async Task A_tampered_executable_is_refused_before_anything_starts()
    {
        var (runtime, _, _) = Runtime(exe => File.AppendAllText(exe, "changed"));
        var outcome = await runtime.RunAsync(new ContainerRunSpec { Name = "birknext-k6-version-x", Command = ["version"] }, CancellationToken.None);
        outcome.ExitCode.Should().BeNull();
        outcome.StartError.Should().Contain("pinned SHA-256");
        (await runtime.ImageAsync(PerformanceContainerOptions.DefaultImage)).Present.Should().BeFalse();
        (await runtime.PullAsync(PerformanceContainerOptions.DefaultImage)).Detail.Should().Contain("nothing is downloaded");
    }

    [Fact]
    public void What_a_process_cannot_honour_is_refused_not_ignored()
    {
        var run = Path.GetTempPath();
        var ok = new ContainerRunSpec { Name = "birknext-k6-run-1", Command = ["run", Path.Combine(run, "test.js")], Mounts = [new(run, "/birknext/in", true)] };
        NativeProcessExecutionRuntime.Refusal(ok).Should().BeNull();
        NativeProcessExecutionRuntime.Refusal(ok with { Environment = new Dictionary<string, string> { ["SSL_CERT_FILE"] = "C:\\ca.pem" } })
            .Should().Contain("Windows certificate store", "a CA bundle k6 would silently ignore must not look applied");
        NativeProcessExecutionRuntime.Refusal(ok with { Entrypoint = "/bin/cat" }).Should().Contain("only the bundled k6");
        NativeProcessExecutionRuntime.Refusal(ok with { Command = ["run", "/birknext/in/test.js"] }).Should().Contain("container paths");
        NativeProcessExecutionRuntime.Refusal(ok with { Name = "bad name; rm" }).Should().NotBeNull();
        NativeProcessExecutionRuntime.Refusal(ok with { Environment = new Dictionary<string, string> { ["lower"] = "x" } }).Should().NotBeNull();
        NativeProcessExecutionRuntime.Refusal(ok with { Mounts = [new("relative\\dir", "/birknext/in", true)] }).Should().NotBeNull();
    }

    [Fact]
    public void The_process_environment_is_an_allow_list_and_never_the_backends_own()
    {
        var marker = "BIRKNEXT_TEST_BACKEND_ONLY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(marker, "connection-string-secret");
        try
        {
            var spec = new ContainerRunSpec
            {
                Name = "birknext-k6-run-2", Environment = new Dictionary<string, string> { ["K6_NO_USAGE_REPORT"] = "true", ["HTTPS_PROXY"] = "http://proxy.local:8080" },
                SecretEnvironment = new Dictionary<string, string> { [K6PerformanceTestProvider.ApiAuthorizationVariable] = "Bearer abc" },
            };
            var env = NativeProcessExecutionRuntime.ProcessEnvironment(spec, "C:\\run-home");
            env.Should().NotContainKey(marker).And.NotContainKey("ConnectionStrings__Default").And.NotContainKey("PATH");
            env.Should().Contain("K6_NO_USAGE_REPORT", "true").And.Contain("HTTPS_PROXY", "http://proxy.local:8080").And.Contain(K6PerformanceTestProvider.ApiAuthorizationVariable, "Bearer abc");
            env["USERPROFILE"].Should().Be("C:\\run-home", "k6 reads no user profile configuration");
            env["APPDATA"].Should().Be("C:\\run-home");
        }
        finally { Environment.SetEnvironmentVariable(marker, null); }
    }

    [Theory]
    [InlineData("Native", true)]
    [InlineData("native", true)]
    [InlineData("Container", false)]
    [InlineData(null, false)]
    public void The_runner_is_explicit_configuration_and_defaults_to_the_container(string? runner, bool native)
    {
        var settings = new Dictionary<string, string?> { ["PerformanceTests:Native:K6Path"] = "C:\\BirkNext\\runtime\\k6\\k6.exe", ["PerformanceTests:Native:K6Sha256"] = new string('b', 64) };
        if (runner is not null) settings["PerformanceTests:Runner"] = runner;
        var options = PerformanceTestOptions.From(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        options.UsesNativeRunner.Should().Be(native);
        options.Native.K6Path.Should().Be("C:\\BirkNext\\runtime\\k6\\k6.exe");
    }

    [Theory]
    [InlineData("TesterNative", null, "Native")]
    [InlineData("TesterNative", "Native", "Native")]
    [InlineData("DeveloperContainer", null, "Container")]
    [InlineData(null, null, "Container")]
    [InlineData("DeveloperContainer", "Native", "Native")]
    public void The_k6_runner_follows_the_runtime_profile(string? profile, string? runner, string expected)
    {
        var settings = new Dictionary<string, string?>();
        if (profile is not null) settings[BirkNext.BuildInfo.RuntimeProfiles.ConfigurationKey] = profile;
        if (runner is not null) settings["PerformanceTests:Runner"] = runner;
        PerformanceTestOptions.From(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).Runner.Should().Be(expected);
    }

    [Fact]
    public void Ambiguous_or_contradictory_runner_configuration_stops_instead_of_falling_back()
    {
        FluentActions.Invoking(() => PerformanceTestOptions.ResolveRunner(BirkNext.BuildInfo.RuntimeProfile.TesterNative, "Container"))
            .Should().Throw<InvalidOperationException>().WithMessage("*never runs k6 in a container*");
        FluentActions.Invoking(() => PerformanceTestOptions.ResolveRunner(null, "Podman")).Should().Throw<InvalidOperationException>().WithMessage("*Unknown PerformanceTests:Runner*");
        BirkNext.BuildInfo.RuntimeProfiles.Parse("testernative").Profile.Should().Be(BirkNext.BuildInfo.RuntimeProfile.TesterNative);
        BirkNext.BuildInfo.RuntimeProfiles.Parse("").Should().Be(((BirkNext.BuildInfo.RuntimeProfile?)null, (string?)null), "unset is not an error: the caller applies its default");
        BirkNext.BuildInfo.RuntimeProfiles.Parse("Tester").Error.Should().Contain("Unknown runtime profile");
        FluentActions.Invoking(() => BirkNext.Api.Services.SystemInfo.RuntimeEnvironmentService.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BirkNext.BuildInfo.RuntimeProfiles.ConfigurationKey] = "Hybrid" }).Build()))
            .Should().Throw<InvalidOperationException>();
        BirkNext.Api.Services.SystemInfo.RuntimeEnvironmentService.Resolve(new ConfigurationBuilder().Build())
            .Should().Be((BirkNext.BuildInfo.RuntimeProfile.DeveloperContainer, false), "a source checkout keeps its developer behaviour");
    }

    [Fact]
    public async Task The_native_runtime_observes_only_its_own_processes_and_reports_no_limits()
    {
        var (runtime, _, _) = Runtime();
        (await runtime.ListManagedAsync(K6PerformanceTestProvider.Component)).Should().BeEmpty();
        (await runtime.InspectAsync("birknext-k6-run-unknown")).Should().BeNull();
        (await runtime.ControllersAsync()).Should().BeEmpty("no cgroup controllers: memory/CPU/process limits are not applied natively");
        (await runtime.StopAsync("birknext-k6-run-unknown", 15)).Should().BeFalse("no graceful stop for a Windows console child");
        (await runtime.RemoveAsync("birknext-k6-run-unknown")).Should().BeTrue("nothing of ours to remove; other processes are never touched");
        runtime.SharesHostFilesystem.Should().BeTrue();
        runtime.SharesHostNetwork.Should().BeTrue();
    }
}
