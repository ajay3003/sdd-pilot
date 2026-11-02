using System.Security.Cryptography.X509Certificates;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// TLS trust in Performance readiness: failure-specific remediation (server chain first, never a CA bundle for an expired or wrong-host certificate),
/// runtime execution blocked only for the failing target, a successful local repair that unblocks the run with a non-blocking server warning,
/// per-host managed bundles used identically by the network check and the run, and no TLS bypass anywhere.
/// </summary>
public sealed class PerformanceTlsReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-tls-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PerformanceTestDefinition Definition(string origin = "https://m2lbdev.example.test") => new() { TargetOrigin = origin, EnvironmentName = "Development" };

    private static PerformanceReachability Failed(string failureClass, TlsRepairOutcome? repair = null) => new()
    {
        TargetOrigin = "https://m2lbdev.example.test", State = PerformanceProbeStates.TlsTrustFailure, ErrorCode = 1310, CheckedAt = DateTimeOffset.UtcNow, TlsRepair = repair,
        Chain = new TlsChainDiagnostics
        {
            Reached = true, FailureClass = failureClass, FailedLayer = TlsTrustLayers.ServerChain,
            Remediation = failureClass == TlsFailureClasses.IncompleteServerChain
                ? "Recommended fix: configure the server, reverse proxy, ingress, gateway or load balancer for m2lbdev.example.test to serve the complete certificate chain."
                : failureClass == TlsFailureClasses.ExpiredCertificate ? "Renew or replace the certificate of m2lbdev.example.test. A CA bundle cannot fix this."
                : "Configure the approved corporate/private CA bundle for the k6 container.",
        },
    };

    [Fact]
    public void IncompleteServerChain_BlocksRuntimeExecution_WithTheServerFixFirst()
    {
        var item = PerformanceTestReadinessService.CaTrustItem(PerformanceCaBundleInspector.Inspect(null), Failed(TlsFailureClasses.IncompleteServerChain), Definition());
        item.Blocking.Should().BeTrue();
        item.Status.Should().Be("Blocked");
        item.Headline.Should().Be("Execution blocked — target certificate chain is incomplete");
        item.Detail.Should().Contain("Recommended fix: configure the server").And.NotContain("Configure the corporate CA bundle");
        item.Label.Should().Be("Container TLS trust");
    }

    [Fact]
    public void ExpiredOrWrongHost_NeverRecommendACaBundle_WhileAPrivateCaDoes()
    {
        PerformanceTestReadinessService.CaTrustItem(PerformanceCaBundleInspector.Inspect(null), Failed(TlsFailureClasses.ExpiredCertificate), Definition())
            .Detail.Should().Contain("Renew or replace").And.Contain("A CA bundle cannot fix this");
        PerformanceTestReadinessService.CaTrustItem(PerformanceCaBundleInspector.Inspect(null), Failed(TlsFailureClasses.UntrustedPrivateOrCorporateCA), Definition())
            .Detail.Should().Contain("corporate CA bundle");
    }

    [Fact]
    public void ASuccessfulLocalRepair_LetsTheRunProceed_ButKeepsTheServerWarning()
    {
        var repaired = new PerformanceReachability
        {
            TargetOrigin = "https://m2lbdev.example.test", State = PerformanceProbeStates.AuthChallenge, Reachable = true, HttpStatus = 401, CheckedAt = DateTimeOffset.UtcNow,
            TlsRepair = new TlsRepairOutcome { State = TlsRepairStates.Applied, Host = "m2lbdev.example.test", IntermediateSubject = "CN=Issuing CA", BundleSha256 = "abc", ValidatedAt = DateTimeOffset.UtcNow },
        };
        var item = PerformanceTestReadinessService.CaTrustItem(PerformanceCaBundleInspector.Inspect(null), repaired, Definition());
        item.Blocking.Should().BeFalse("a validated local repair does not keep Run blocked");
        item.State.Should().Be(PerformanceReadinessState.Ready);
        item.Status.Should().Be("Repaired locally");
        item.Detail.Should().Contain("Server TLS configuration should still be corrected");
        // A 401 after TLS is network/TLS reachability, never a TLS failure.
        PerformanceTestReadinessService.NetworkItem(true, repaired, fresh: true, sameConfiguration: true).State.Should().Be(PerformanceReadinessState.Ready);
    }

    [Fact]
    public void ManagedRepairedBundles_AreScopedToOneHostAndPort_AndUsedByBothTheCheckAndTheRun()
    {
        var options = new PerformanceTestOptions();
        var store = new PerformanceLocalExecutionStore(options, directory: _dir);
        using var root = TlsIntermediateRepairTests.Authority($"CN=Store Root {Guid.NewGuid():N}", null);
        var pem = root.ExportCertificatePem();
        var outcome = new TlsRepairOutcome { State = TlsRepairStates.Applied, Host = "m2lbdev.example.test", Port = 443, IntermediateNotAfter = DateTimeOffset.UtcNow.AddYears(1) };
        var path = store.SaveRepair(outcome, pem);

        options.Container.CaBundlePathFor("https://m2lbdev.example.test").Should().Be(path);
        options.Container.CaBundlePathFor("https://m2lbdev.example.test:8443").Should().BeNull("another port is another target");
        options.Container.CaBundlePathFor("https://qa.example.test").Should().BeNull("one host's repair never applies to another host");
        File.ReadAllText(path).Should().NotContain("PRIVATE KEY");

        var k6 = new K6PerformanceTestProvider(options, Mock.Of<IContainerExecutionRuntime>(), NullLogger<K6PerformanceTestProvider>.Instance);
        var target = options.Container.CaBundlePathFor("https://m2lbdev.example.test");
        k6.Environment(target)["SSL_CERT_FILE"].Should().Be("/birknext/ca/ca.pem");
        k6.Mounts("C:/in", "C:/out", target).Should().Contain(m => m.HostPath == path && m.ContainerPath == "/birknext/ca/ca.pem" && m.ReadOnly);

        store.SetRepairMode(TlsRepairModes.ManualOnly).Should().BeTrue();
        options.Container.CaBundlePathFor("https://m2lbdev.example.test").Should().BeNull("Manual only never uses an automatic repair");
        store.SetRepairMode(TlsRepairModes.Automatic);
        store.SetRepairMode("DisableVerification").Should().BeFalse("there is no mode that disables TLS verification");

        store.SaveRepair(outcome with { IntermediateNotAfter = DateTimeOffset.UtcNow.AddDays(-1) }, pem);
        options.Container.CaBundlePathFor("https://m2lbdev.example.test").Should().BeNull("an expired repaired intermediate is never reused");
        store.SaveRepair(outcome, pem);
        store.RetireRepair("m2lbdev.example.test", 443, TlsRepairStates.NoLongerNeeded, "Server now provides the complete certificate chain.");
        options.Container.CaBundlePathFor("https://m2lbdev.example.test").Should().BeNull();
        store.Status().Repairs.Single().State.Should().Be(TlsRepairStates.NoLongerNeeded);

        // Persisted across a restart (metadata only).
        var reopened = new PerformanceLocalExecutionStore(new PerformanceTestOptions(), directory: _dir);
        reopened.Repairs.Should().ContainSingle().Which.Host.Should().Be("m2lbdev.example.test");
    }

    [Fact]
    public void NoGeneratedK6ScriptOrContainerArgument_EverDisablesTlsVerification()
    {
        var probe = K6ScriptGenerator.Probe("https://m2lbdev.example.test", "/birknext/out/probe.json");
        probe.Should().NotContainEquivalentOf("insecureSkipTLSVerify");
        var args = PodmanContainerExecutionRuntime.RunArguments(new ContainerRunSpec { Name = "birknext-test", Image = "docker.io/grafana/k6:0.52.0", Entrypoint = "/bin/cat", Command = ["/etc/ssl/certs/ca-certificates.crt"] });
        string.Join(' ', args).Should().NotContainEquivalentOf("insecure").And.Contain("--entrypoint /bin/cat");
        var source = File.ReadAllText(FindRepoFile("backend/BirkNext.Api/Services/PerformanceTests/K6ScriptAndSummary.cs"));
        source.Should().NotContain("insecureSkipTLSVerify: true").And.NotContain("insecureSkipTLSVerify:true");
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
}
