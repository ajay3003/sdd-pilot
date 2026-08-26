using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendPassiveSecurity;

public class PassiveSecurityLifecycleTests
{
    [Fact]
    public async Task Launch_timeout_returns_timed_out_and_force_removes_container()
    {
        var runner=new LifecycleRunner(timeoutOnLaunch:true); var result=await Service(runner).ReviewAsync(Request());
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.TimedOut); result.Findings.Should().BeEmpty(); result.HighCount.Should().Be(0);
        runner.Commands.Should().Contain(c=>c.Contains("rm --force birknext-zap-passive-"));
    }

    [Fact]
    public async Task Cancellation_returns_engine_error_and_force_removes_container()
    {
        var runner=new LifecycleRunner(cancelOnLaunch:true); using var cts=new CancellationTokenSource(); cts.Cancel();
        var result=await Service(runner).ReviewAsync(Request(),cts.Token);
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.EngineError); result.Findings.Should().BeEmpty();
        runner.Commands.Should().Contain(c=>c.Contains("rm --force birknext-zap-passive-"));
    }

    [Fact]
    public async Task Launch_failure_still_force_removes_container()
    {
        var runner=new LifecycleRunner(failLaunch:true); var result=await Service(runner).ReviewAsync(Request());
        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.EngineError); runner.Commands.Last().Should().Contain("rm --force");
    }

    private static PassiveSecurityReviewRequest Request()=>new("https://example.com/","trusted","https://example.com","Public",TimeoutSeconds:10);
    private static FrontendZapPassiveReviewService Service(IZapProcessRunner runner)
    { var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"FrontendPassiveSecurity:Enabled","true"},{"FrontendPassiveSecurity:TrustedProfiles:trusted:BaseUrl","https://example.com"},{"FrontendPassiveSecurity:TrustedProfiles:trusted:EnvironmentType","Public"}}).Build(); return new(NullLogger<FrontendZapPassiveReviewService>.Instance,new(new BrowserTargetValidator(),cfg),new(),cfg,runner); }

    private sealed class LifecycleRunner(bool timeoutOnLaunch=false,bool cancelOnLaunch=false,bool failLaunch=false) : IZapProcessRunner
    {
        public List<string> Commands { get; }=[];
        public Task<ZapProcessResult> RunAsync(string file,IReadOnlyList<string> args,int timeoutMs,CancellationToken ct)
        {
            var command=string.Join(' ',args); Commands.Add(command);
            if (args.Count>0 && args[0]=="version") return Task.FromResult(new ZapProcessResult(0,"27.0.0",""));
            if (args.Count>1 && args[0]=="image") return Task.FromResult(new ZapProcessResult(0,"ghcr.io/zaproxy/zaproxy@sha256:test",""));
            if (args.Contains("-version")) return Task.FromResult(new ZapProcessResult(0,"ZAP 2.16.1",""));
            if (args.Count>0 && args[0]=="run") return Task.FromResult(timeoutOnLaunch ? new ZapProcessResult(-1,"","",TimedOut:true) : cancelOnLaunch ? new ZapProcessResult(-1,"","",Cancelled:true) : failLaunch ? new ZapProcessResult(1,"","launch failed") : new ZapProcessResult(0,"id",""));
            return Task.FromResult(new ZapProcessResult(0,"",""));
        }
    }
}
