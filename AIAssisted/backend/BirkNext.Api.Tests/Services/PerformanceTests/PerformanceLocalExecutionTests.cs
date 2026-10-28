using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Path = System.IO.Path;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Local execution setup → Container TLS trust: a machine-local CA bundle chosen in System Settings (metadata only), validated before it is
/// stored, used by both the network check and the run, recorded by the container network check, invalidated by any change, and never a way to
/// disable TLS verification. Local throwaway test CAs only.
/// </summary>
public sealed class PerformanceLocalExecutionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "birknext-local-exec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (string CertificatePem, string KeyPem) TestCa(string name = "BirkNext Local Test Root")
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static (PerformanceLocalExecutionStore Store, PerformanceTestOptions Options, string Dir) Store(string? configured = null)
    {
        var dir = TempDir();
        var options = new PerformanceTestOptions { Container = new PerformanceContainerOptions { CaBundlePath = configured } };
        return (new PerformanceLocalExecutionStore(options, directory: Path.Combine(dir, "state")), options, dir);
    }

    private static PerformanceReachability Probe(string state, string origin = "https://qa.example.test", int? code = null) =>
        new() { TargetOrigin = origin, State = state, Reachable = state == PerformanceProbeStates.Reachable, ErrorCode = code, CheckedAt = Now, Detail = state };

    [Fact]
    public void Without_a_bundle_the_state_is_not_configured_and_the_default_store_applies()
    {
        var (store, options, _) = Store();
        var status = store.Status();
        status.TrustState.Should().Be(ContainerTlsTrustStates.NotConfigured);
        status.Source.Should().Be(ContainerTlsTrustSources.None);
        status.Stages.Should().OnlyContain(s => s.State == "Not checked");
        options.Container.EffectiveCaBundlePath.Should().BeNull();
    }

    [Theory]
    [InlineData("corp.pfx", "PFX")]
    [InlineData("corp.p12", "PFX")]
    [InlineData("corp.key", "key files are refused")]
    [InlineData("corp.txt", "Only .pem, .crt or .cer")]
    public void Keys_pfx_and_other_extensions_are_refused_and_nothing_is_stored(string file, string message)
    {
        var (store, options, dir) = Store();
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, TestCa().CertificatePem);
        var (ok, text, _) = store.Configure(path, Now);
        ok.Should().BeFalse();
        text.Should().Contain(message);
        options.Container.EffectiveCaBundlePath.Should().BeNull("a refused bundle is never mounted");
    }

    [Fact]
    public void A_pem_with_a_private_key_is_refused()
    {
        var (store, options, dir) = Store();
        var (pem, key) = TestCa();
        var path = Path.Combine(dir, "bundle.pem");
        File.WriteAllText(path, pem + key);
        store.Configure(path, Now).Message.Should().Contain("private key");
        options.Container.EffectiveCaBundlePath.Should().BeNull();
    }

    [Fact]
    public void A_valid_bundle_is_configured_mounted_for_check_and_run_and_persisted_as_metadata_only()
    {
        var (store, options, dir) = Store(configured: "C:\\config\\ignored.pem");
        var pem = TestCa().CertificatePem;
        var path = Path.Combine(dir, "corp-roots.crt");
        File.WriteAllText(path, pem);

        var (ok, _, bundle) = store.Configure(path, Now);
        ok.Should().BeTrue();
        options.Container.EffectiveCaBundlePath.Should().Be(path, "System Settings wins over the configuration file");
        var status = store.Status();
        status.Should().Match<PerformanceLocalExecutionStatus>(s => s.TrustState == ContainerTlsTrustStates.Configured && s.Source == ContainerTlsTrustSources.SystemSettings
            && s.Certificates == 1 && s.Fingerprint == bundle.Fingerprint && s.BundleFileName == "corp-roots.crt" && s.ConfiguredAt == Now);

        // The same options feed the run and the network check: one trust.
        var provider = new K6PerformanceTestProvider(options, new PerformanceTestReviewTests.FakeRuntime(), NullLogger<K6PerformanceTestProvider>.Instance);
        provider.Environment()["SSL_CERT_FILE"].Should().Be("/birknext/ca/ca.pem");

        var persisted = File.ReadAllText(Path.Combine(dir, "state", PerformanceLocalExecutionStore.FileName));
        persisted.Should().NotContain("BEGIN CERTIFICATE").And.NotContain(pem[30..60]).And.Contain(bundle.Fingerprint);

        // A new process (new store over the same folder) restores the machine-local choice.
        var options2 = new PerformanceTestOptions();
        new PerformanceLocalExecutionStore(options2, directory: Path.Combine(dir, "state"));
        options2.Container.EffectiveCaBundlePath.Should().Be(path);
    }

    [Fact]
    public void A_check_validates_trust_and_any_change_needs_a_recheck()
    {
        var (store, options, dir) = Store();
        File.WriteAllText(Path.Combine(dir, "a.pem"), TestCa("A").CertificatePem);
        store.Configure(Path.Combine(dir, "a.pem"), Now);

        store.Record(Probe(PerformanceProbeStates.Reachable));
        var validated = store.Status();
        validated.TrustState.Should().Be(ContainerTlsTrustStates.Validated);
        validated.Stages.Should().OnlyContain(s => s.State == "Passed");

        File.WriteAllText(Path.Combine(dir, "b.pem"), TestCa("B").CertificatePem);
        store.Configure(Path.Combine(dir, "b.pem"), Now);
        store.Status().TrustState.Should().Be(ContainerTlsTrustStates.NeedsRecheck, "a different CA invalidates the earlier validation");

        store.Record(Probe(PerformanceProbeStates.Reachable));
        store.Status().TrustState.Should().Be(ContainerTlsTrustStates.Validated);
        options.Container.GetType().GetProperty(nameof(PerformanceContainerOptions.Image))!.Should().NotBeNull();
    }

    [Fact]
    public void A_tls_trust_failure_is_classified_by_stage_and_suggests_the_corporate_ca()
    {
        var (store, _, _) = Store();
        store.Record(Probe(PerformanceProbeStates.TlsTrustFailure, code: 1310));
        var status = store.Status();
        status.TrustState.Should().Be(ContainerTlsTrustStates.ValidationFailed);
        status.TrustDetail.Should().Contain("TLS trust").And.Contain("corporate CA bundle");
        status.Stages.Select(s => (s.Key, s.State)).Should().Equal(("runtime", "Passed"), ("image", "Passed"), ("dns", "Passed"), ("connection", "Passed"), ("tls", "Failed"), ("http", "Not reached"));
    }

    [Fact]
    public void Stages_classify_dns_proxy_and_plain_http_targets()
    {
        PerformanceNetworkStages.From(new ContainerTlsValidation { TargetOrigin = "https://x.test", ProbeState = PerformanceProbeStates.DnsFailure })
            .Single(s => s.State == "Failed").Key.Should().Be("dns");
        PerformanceNetworkStages.From(new ContainerTlsValidation { TargetOrigin = "https://x.test", ProbeState = PerformanceProbeStates.ProxyFailure })
            .Single(s => s.State == "Failed").Key.Should().Be("connection");
        PerformanceNetworkStages.From(new ContainerTlsValidation { TargetOrigin = "http://x.test", ProbeState = PerformanceProbeStates.Reachable })
            .Single(s => s.Key == "tls").State.Should().Be("Not applicable");
    }

    [Fact]
    public void Runtime_or_image_problems_are_not_recorded_as_trust_validation()
    {
        var (store, _, _) = Store();
        store.Record(Probe(PerformanceProbeStates.ImageMissing));
        store.Status().LastValidation.Should().BeNull();
    }

    [Fact]
    public void An_invalid_bundle_from_configuration_fails_validation_and_is_never_bypassed()
    {
        var (store, _, dir) = Store(configured: Path.Combine(TempDir(), "missing.pem"));
        var status = store.Status();
        status.TrustState.Should().Be(ContainerTlsTrustStates.ValidationFailed);
        status.Source.Should().Be(ContainerTlsTrustSources.ConfigurationFile);
        status.TrustDetail.Should().Contain("TLS verification will not be disabled");
        _ = dir;
    }

    [Fact]
    public void Removing_the_bundle_restores_the_configuration_file_value_and_leaves_the_file()
    {
        var (store, options, dir) = Store();
        var path = Path.Combine(dir, "corp.pem");
        File.WriteAllText(path, TestCa().CertificatePem);
        store.Configure(path, Now);
        store.Remove();
        options.Container.EffectiveCaBundlePath.Should().BeNull();
        store.Status().TrustState.Should().Be(ContainerTlsTrustStates.NotConfigured);
        File.Exists(path).Should().BeTrue("BirkNext never deletes the user's certificate file");
    }

    [Fact]
    public async Task The_registry_records_every_container_network_check_as_trust_validation()
    {
        var (store, options, _) = Store();
        var registry = new PerformanceTestProviderRegistry([new StubProvider(Probe(PerformanceProbeStates.TlsTrustFailure, code: 1310))], options, store);
        await registry.CheckReachabilityAsync(new PerformanceTestDefinition { ProviderId = PerformanceProviderIds.K6, TargetOrigin = "https://qa.example.test" });
        store.Status().LastValidation!.ProbeState.Should().Be(PerformanceProbeStates.TlsTrustFailure);
    }

    [Fact]
    public void No_option_disables_tls_verification()
    {
        typeof(PerformanceLocalExecutionStatus).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Insecure", StringComparison.OrdinalIgnoreCase) || n.Contains("Skip", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubProvider(PerformanceReachability result) : IPerformanceTestProvider
    {
        public string ProviderId => PerformanceProviderIds.K6;
        public string DisplayName => "stub";
        public PerformanceProviderCapabilities Capabilities { get; } = new();
        public Task<PerformanceProviderStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new PerformanceProviderStatus());
        public IReadOnlyList<string> Validate(PerformanceTestDefinition definition) => [];
        public Task<PerformanceProviderResult> ExecuteAsync(PerformanceProviderInput input, IProgress<string>? progress, CancellationToken ct) => throw new InvalidOperationException("No load test runs in these tests.");
        public Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition definition, CancellationToken ct = default) => Task.FromResult(result);
        public Task<string?> PrepareAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> CleanupOrphansAsync(IReadOnlySet<Guid> activeRunIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
