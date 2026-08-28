using System.Diagnostics;
using System.Net;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using BirkNext.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Integration;

public sealed class FrontendZapPassiveIntegrationTests
{
    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_HealthyControl_StartsPinnedZapAndNormalizesPassiveResult()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/healthy"));
        Console.WriteLine($"ZAP-TEST healthy status={result.ExecutionStatus} version={result.ZapVersion} requests={topology.RequestCount("/healthy")} durationMs={result.DurationMs} alerts={result.Findings.Count}");
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Assessed, result.EngineError);
        result.ZapVersion.Should().NotBeNullOrWhiteSpace();
        topology.RequestCount("/healthy").Should().BeGreaterThan(0);
    }

    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_MissingNosniff_ProducesSanitizedNormalizedPassiveFinding()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/missing-nosniff?access_token=SECRET-ZAP-TOKEN-12345"));
        var observed = result.Findings.FirstOrDefault(f => f.PluginId == "10021");
        Console.WriteLine($"ZAP-TEST missing status={result.ExecutionStatus} version={result.ZapVersion} requests={topology.RequestCount("/missing-nosniff")} durationMs={result.DurationMs} finding={observed?.PluginId}|{observed?.AlertRef}|{observed?.Name}|{observed?.Risk}|{observed?.Confidence}|{observed?.Url}|{observed?.InstancesCount}|evidence-sanitized={observed?.Evidence.Contains("SECRET-ZAP-TOKEN-12345") == false}|solution={observed?.Solution}");
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Assessed, result.EngineError);
        topology.RequestCount("/missing-nosniff").Should().BeGreaterThan(0);
        var finding = result.Findings.Should().Contain(f => f.PluginId == "10021").Subject;
        finding.Risk.Should().Be("Low");
        finding.Evidence.Should().NotContain("SECRET-ZAP-TOKEN-12345");
    }

    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_PassiveAssessment_DoesNotCrawlLinkedPages()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/crawl-root"));
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Assessed, result.EngineError);
        topology.RequestCount("/crawl-root").Should().BeGreaterThan(0);
        topology.RequestCount("/should-not-be-crawled-1").Should().Be(0);
        topology.RequestCount("/should-not-be-crawled-2").Should().Be(0);
    }

    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_SameOriginRedirect_IsAssessed()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/redirect-same"));
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Assessed, result.EngineError);
        topology.RequestCount("/redirect-same").Should().BeGreaterThan(0);
        topology.RequestCount("/redirect-final").Should().BeGreaterThan(0);
    }

    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_CrossOriginRedirect_IsBlockedBeforeNavigation()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartDualAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/redirect-cross"));
        Console.WriteLine($"ZAP-TEST cross-origin status={result.ExecutionStatus} version={result.ZapVersion} requests={topology.RequestCount("/redirect-cross")} requests-untrusted={topology.RequestCountUntrusted("/redirect-final")} durationMs={result.DurationMs}");
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Skipped, result.EngineError);
        topology.RequestCount("/redirect-cross").Should().BeGreaterThan(0);
        topology.RequestCountUntrusted("/redirect-final").Should().Be(0, "untrusted origin should never be accessed");
    }

    [Fact, Trait("Category", "FrontendZapPassiveIntegration")]
    public async Task RealZap_SensitiveRedirect_IsBlockedBeforeNavigation()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        await using var topology = await TestTopology.StartAsync();
        var result = await topology.Service.ReviewAsync(topology.Request("/redirect-sensitive"));
        Console.WriteLine($"ZAP-TEST sensitive status={result.ExecutionStatus} version={result.ZapVersion} requests={topology.RequestCount("/redirect-sensitive")} durationMs={result.DurationMs}");
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.Skipped, result.EngineError);
        topology.RequestCount("/redirect-sensitive").Should().BeGreaterThan(0);
    }

    private sealed class TestTopology : IAsyncDisposable
    {
        private const string Image = FrontendZapPassiveReviewService.Image;
        private readonly string _network, _fixture;
        private readonly string? _fixtureUntrusted;
        public FrontendZapPassiveReviewService Service { get; }
        private TestTopology(string network, string fixture, string? fixtureUntrusted, FrontendZapPassiveReviewService service) { _network = network; _fixture = fixture; _fixtureUntrusted = fixtureUntrusted; Service = service; }
        public PassiveSecurityReviewRequest Request(string path) => new($"http://{_fixture}:8081{path}", "local-zap-fixture", $"http://{_fixture}:8081", "Internal", TimeoutSeconds: 300);
        public int RequestCount(string path) => Run("podman", "logs", _fixture).Output.Split("REQ ", StringSplitOptions.RemoveEmptyEntries).Count(x => x.StartsWith(path, StringComparison.Ordinal));
        public int RequestCountUntrusted(string path) => _fixtureUntrusted == null ? 0 : Run("podman", "logs", _fixtureUntrusted).Output.Split("REQ ", StringSplitOptions.RemoveEmptyEntries).Count(x => x.StartsWith(path, StringComparison.Ordinal));
        public static async Task<TestTopology> StartAsync()
        {
            var id = Guid.NewGuid().ToString("N"); var network = $"birknext-zap-test-{id}"; var fixture = $"birknext-zap-fixture-{id}";
            Run("podman", "network", "create", "--label", "birknext.engine=zap-passive-test", network).ExitCode.Should().Be(0);
            Run("podman", "run", "-d", "--rm", "--name", fixture, "--network", network, "--label", "birknext.engine=zap-passive-test", Image, "python3", "-c", $"import base64;exec(base64.b64decode('{Script()}'))").ExitCode.Should().Be(0);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["FrontendPassiveSecurity:Enabled"]="true", ["FrontendPassiveSecurity:ContainerRuntime"]="podman", ["FrontendPassiveSecurity:ContainerNetwork"] = network, [$"FrontendPassiveSecurity:TrustedProfiles:local-zap-fixture:BaseUrl"]=$"http://{fixture}:8081", [$"FrontendPassiveSecurity:TrustedProfiles:local-zap-fixture:EnvironmentType"]="Internal" }).Build();
            var service = new FrontendZapPassiveReviewService(NullLogger<FrontendZapPassiveReviewService>.Instance, new(new BrowserTargetValidator(), config), new PassiveSecurityEvidenceSanitizer(), config, new ZapProcessRunner());
            await Task.Delay(250);
            return new(network, fixture, null, service);
        }
        public static async Task<TestTopology> StartDualAsync()
        {
            var id = Guid.NewGuid().ToString("N"); var network = $"birknext-zap-test-{id}"; var fixture = $"birknext-zap-fixture-{id}"; var fixtureUntrusted = $"birknext-zap-untrusted-{id}";
            Run("podman", "network", "create", "--label", "birknext.engine=zap-passive-test", network).ExitCode.Should().Be(0);
            Run("podman", "run", "-d", "--rm", "--name", fixture, "--network", network, "--label", "birknext.engine=zap-passive-test", "-e", $"UNTRUSTED_HOST={fixtureUntrusted}", Image, "python3", "-c", $"import base64;exec(base64.b64decode('{ScriptWithParams()}'))").ExitCode.Should().Be(0);
            Run("podman", "run", "-d", "--rm", "--name", fixtureUntrusted, "--network", network, "--label", "birknext.engine=zap-passive-test", Image, "python3", "-c", $"import base64;exec(base64.b64decode('{Script()}'))").ExitCode.Should().Be(0);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["FrontendPassiveSecurity:Enabled"]="true", ["FrontendPassiveSecurity:ContainerRuntime"]="podman", ["FrontendPassiveSecurity:ContainerNetwork"] = network, [$"FrontendPassiveSecurity:TrustedProfiles:local-zap-fixture:BaseUrl"]=$"http://{fixture}:8081", [$"FrontendPassiveSecurity:TrustedProfiles:local-zap-fixture:EnvironmentType"]="Internal" }).Build();
            var service = new FrontendZapPassiveReviewService(NullLogger<FrontendZapPassiveReviewService>.Instance, new(new BrowserTargetValidator(), config), new PassiveSecurityEvidenceSanitizer(), config, new ZapProcessRunner());
            await Task.Delay(250);
            return new(network, fixture, fixtureUntrusted, service);
        }
        public async ValueTask DisposeAsync() { Run("podman", "rm", "--force", _fixture); if (_fixtureUntrusted != null) Run("podman", "rm", "--force", _fixtureUntrusted); Run("podman", "network", "rm", _network); await Task.CompletedTask; }
        private static string Script() => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"from http.server import BaseHTTPRequestHandler,HTTPServer
from urllib.parse import urlparse
class H(BaseHTTPRequestHandler):
 def do_GET(self):
  p=urlparse(self.path).path; print('REQ '+p,flush=True)
  b=b'<html><title>fixture</title><body>healthy</body></html>'
  h={'Content-Type':'text/html','Content-Length':str(len(b))}
  if p=='/missing-nosniff': pass
  elif p=='/redirect-same': self.send_response(302); self.send_header('Location','/redirect-final'); self.end_headers(); return
  elif p=='/redirect-sensitive': self.send_response(302); self.send_header('Location','http://169.254.169.254/latest/meta-data/'); self.end_headers(); return
  elif p=='/crawl-root': b=b""<html><a href='/should-not-be-crawled-1'>one</a><a href='/should-not-be-crawled-2'>two</a></html>""; h['Content-Length']=str(len(b))
  elif p=='/healthy': h['X-Content-Type-Options']='nosniff'
  else: b=b'fixture'; h['Content-Length']=str(len(b))
  self.send_response(200); [self.send_header(k,v) for k,v in h.items()]; self.end_headers(); self.wfile.write(b)
HTTPServer(('0.0.0.0',8081),H).serve_forever()
"));
        private static string ScriptWithParams() => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"import os
from http.server import BaseHTTPRequestHandler,HTTPServer
from urllib.parse import urlparse
untrusted_host=os.environ.get('UNTRUSTED_HOST','untrusted')
class H(BaseHTTPRequestHandler):
 def do_GET(self):
  p=urlparse(self.path).path; print('REQ '+p,flush=True)
  b=b'<html><title>fixture</title><body>healthy</body></html>'
  h={'Content-Type':'text/html','Content-Length':str(len(b))}
  if p=='/missing-nosniff': pass
  elif p=='/redirect-same': self.send_response(302); self.send_header('Location','/redirect-final'); self.end_headers(); return
  elif p=='/redirect-cross': self.send_response(302); self.send_header('Location',f'http://{untrusted_host}:8081/redirect-final'); self.end_headers(); return
  elif p=='/redirect-sensitive': self.send_response(302); self.send_header('Location','http://169.254.169.254/latest/meta-data/'); self.end_headers(); return
  elif p=='/crawl-root': b=b""<html><a href='/should-not-be-crawled-1'>one</a><a href='/should-not-be-crawled-2'>two</a></html>""; h['Content-Length']=str(len(b))
  elif p=='/healthy': h['X-Content-Type-Options']='nosniff'
  else: b=b'fixture'; h['Content-Length']=str(len(b))
  self.send_response(200); [self.send_header(k,v) for k,v in h.items()]; self.end_headers(); self.wfile.write(b)
HTTPServer(('0.0.0.0',8081),H).serve_forever()
"));
        private static (int ExitCode,string Output) Run(string file, params string[] args) { using var p=new Process{StartInfo=new ProcessStartInfo(file){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}}; foreach(var a in args)p.StartInfo.ArgumentList.Add(a); p.Start(); var o=p.StandardOutput.ReadToEnd(); p.WaitForExit(); return (p.ExitCode,o); }
    }
}
