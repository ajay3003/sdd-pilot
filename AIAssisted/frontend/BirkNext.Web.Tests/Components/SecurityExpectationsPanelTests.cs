using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class SecurityExpectationsPanelTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly FrontendSecuritySettings _settings = new() {AllowedRestHosts=["manual.example.test"]};
    private ApprovedSecurityExpectations? _applied;
    public SecurityExpectationsPanelTests() {Services.AddSingleton<ISecurityExpectationApi>(_api);JSInterop.Mode=JSRuntimeMode.Loose;}
    private IRenderedComponent<SecurityExpectationsPanel> Panel(bool editable=false) => Render<SecurityExpectationsPanel>(p=>p
        .Add(c=>c.EnvironmentId,"qa").Add(c=>c.Settings,_settings).Add(c=>c.Editable,editable)
        .Add(c=>c.ApprovedChanged,a=> { _applied=a; _settings.ExpectedAuthority=a.ExpectedAuthority; _settings.ExpectedTenant=a.ExpectedTenant; _settings.ExpectedClientId=a.ExpectedClientId;
            _settings.AllowedRestHosts=a.AllowedRestHosts;_settings.AllowedGraphQlHosts=a.AllowedGraphQlHosts;_settings.AllowedCdnHosts=a.AllowedCdnHosts;_settings.Origins=a.Origins; }));
    private static void Discover(IRenderedComponent<SecurityExpectationsPanel> cut) {cut.Find("[data-testid=sec-discover]").Click();cut.WaitForAssertion(()=>cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically")); foreach(var id in cut.FindAll("button[data-testid^=sec-review-]").Select(b => b.GetAttribute("data-testid")).ToList()) cut.Find($"[data-testid={id}]").Click();}
    [Fact] public void NotAnalyzedStateHasNoGuessesAndKeepsExpectationBoundary()
    {
        var cut=Panel();cut.Markup.Should().Contain("Not analyzed").And.Contain("nothing is approved or validated automatically").And.Contain("Never enter secrets");
        cut.FindAll("[data-testid=sec-candidate]").Should().BeEmpty();cut.Find("[data-testid=sec-approved-count]").TextContent.Should().Be("6");
        cut.Markup.Should().Contain("Identity expectations").And.Contain("Allowed application hosts").And.Contain("Expected security headers");
    }
    [Fact] public void DefaultViewDoesNotRenderCandidateOrEvidenceRows()
    {
        var cut=Panel();cut.Find("[data-testid=sec-discover]").Click();
        cut.WaitForAssertion(()=>cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically"));
        cut.FindAll("[data-testid=sec-candidate],table").Should().BeEmpty();
        cut.Find("[data-testid=sec-review-GraphQlHost]").GetAttribute("aria-expanded").Should().Be("false");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoApprovedSingletonUsesAcceptOrChooseWithoutConflictOrKeepCurrent(bool ambiguous)
    {
        _api.Candidates=[_api.Make("a",SecurityExpectationField.Authority,"https://identity.example.test/a")];
        if(ambiguous) _api.Candidates.Add(_api.Make("b",SecurityExpectationField.Authority,"https://identity.example.test/b"));
        var cut=Panel();Discover(cut);
        cut.Find("[data-testid=sec-field-Authority]").TextContent.Should().NotContain("Conflict").And.NotContain("Keep current").And.NotContain("Replace");
        cut.FindAll("[data-testid=sec-accept]").Should().HaveCount(ambiguous ? 2 : 1);
        cut.Find("[data-testid=sec-accept]").TextContent.Should().Be(ambiguous ? "Choose this value" : "Accept");
    }
    [Fact] public async Task NewerSnapshotShowsNoticeAndPreservesHistoricalBinding()
    {
        await _api.DiscoverAsync("qa",new(_api.Snapshot.Id,new()));
        _api.AdditionalSources=[_api.Snapshot with {Id=Guid.NewGuid(),Archive=new("newer.zip",new string('b',64),2),AnalyzedAt=_api.Snapshot.AnalyzedAt.AddMinutes(1)}];
        var cut=Panel();cut.Markup.Should().Contain("Newer source snapshot available");
        cut.Find("[data-testid=sec-snapshot]").TextContent.Should().Contain("generic.zip").And.NotContain("newer.zip");
        cut.Find("[data-testid=sec-primary]").GetAttribute("value").Should().Be(_api.Snapshot.Id.ToString(), "the newer snapshot is offered, never switched to");
        cut.Find("[data-testid=sec-newer-snapshot]").TextContent.Should().Contain("not switched automatically");
        _api.DiscoverCalls.Should().Be(1);
    }
    [Fact] public void DiscoverShowsCandidatesSnapshotAndEvidenceButChangesNoApprovedValue()
    {
        var cut=Panel();Discover(cut);_applied.Should().BeNull();_api.DiscoverCalls.Should().Be(1);_settings.AllowedRestHosts.Should().Equal("manual.example.test");
        cut.Find("[data-testid=sec-snapshot]").TextContent.Should().Contain("zip");cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("No approved expectations were changed.");
        var evidence=cut.Find("[data-testid=sec-evidence-gql-toggle]");evidence.GetAttribute("aria-expanded").Should().Be("false");evidence.Click();
        cut.Find("[data-testid=sec-evidence-gql-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Markup.Should().Contain("Shop.Ui/appsettings.json").And.Contain("GraphQl:Endpoint").And.Contain("Confirmed");
    }
    [Fact] public void ExplicitAddPreservesManualValuesAndAcceptsWithProvenance()
    {
        var cut=Panel();Discover(cut);cut.Find("[data-candidate=gql] [data-testid=sec-accept]").Click();
        cut.WaitForAssertion(()=>_applied.Should().NotBeNull());_settings.AllowedGraphQlHosts.Should().Equal("graphql.example.test");_settings.AllowedRestHosts.Should().Equal("manual.example.test");
        _settings.Origins.Single().Origin.Should().Be(SecurityExpectationOrigin.AcceptedFromSource);
        cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("saved to approved expectations");
        JSInterop.Invocations.Should().Contain(i=>i.Identifier=="Blazor._internal.domWrapper.focus");
    }
    [Fact] public void MatchingManualValueDoesNotBecomeAcceptedFromSource()
    {
        _settings.AllowedGraphQlHosts=["graphql.example.test"];var cut=Panel();Discover(cut);
        cut.Find("[data-candidate=gql]").TextContent.Should().Contain("Matches current source").And.Contain("Matches source").And.NotContain("Accepted");
        cut.FindAll("[data-candidate=gql] [data-testid=sec-accept]").Should().BeEmpty();_settings.Origins.Should().BeEmpty();
    }
    [Fact] public void MatchingOlderApprovalDoesNotAutomaticallyAcceptANewSourceFinding()
    {
        _settings.AllowedGraphQlHosts=["graphql.example.test"];
        _settings.Origins=[new(SecurityExpectationField.GraphQlHost,"graphql.example.test",SecurityExpectationOrigin.AcceptedFromSource,Guid.NewGuid(),new string('b',64),DateTimeOffset.UtcNow,"gql")];
        var cut=Panel();Discover(cut);cut.Find("[data-candidate=gql]").TextContent.Should().Contain("Matches source").And.NotContain("Accepted");
        cut.Find("[data-testid=sec-field-GraphQlHost]").TextContent.Should().Contain("older snapshot; expectation remains approved");
        _api.Reviews.Should().BeEmpty();_settings.AllowedGraphQlHosts.Should().Equal("graphql.example.test");
    }
    [Fact] public void ConflictKeepCurrentRejectsWithoutReplacingAndReplaceIsExplicit()
    {
        _settings.ExpectedClientId="existing-client";_api.Candidates=[_api.Make("client",SecurityExpectationField.ClientId,"source-client")];
        var cut=Panel();Discover(cut);cut.Find("[data-candidate=client]").TextContent.Should().Contain("Conflict").And.Contain("Keep current").And.Contain("Replace with detected");
        cut.Find("[data-testid=sec-reject]").Click();cut.WaitForAssertion(()=>_api.Reviews.Should().HaveCount(1));
        _api.Reviews.Single().Accept.Should().BeFalse();_settings.ExpectedClientId.Should().Be("existing-client");_applied.Should().BeNull();
        Discover(cut);cut.Find("[data-testid=sec-accept]").Click();cut.WaitForAssertion(()=>_settings.ExpectedClientId.Should().Be("source-client"));_api.Reviews.Last().Request.Replace.Should().BeTrue();
    }
    [Fact] public void StaleEvidenceCannotBeAcceptedAndDoesNotUnapproveValues()
    {
        _api.ResultCurrent=false;_settings.AllowedGraphQlHosts=["legacy.example.test"];var cut=Panel();Discover(cut);
        cut.Find("[data-candidate=gql]").TextContent.Should().Contain("Stale");cut.FindAll("[data-testid=sec-accept]").Should().BeEmpty();
        cut.Find("[data-testid=sec-snapshot]").TextContent.Should().Contain("zip");_settings.AllowedGraphQlHosts.Should().Equal("legacy.example.test");
    }
    [Fact] public void ServerRejectsChangedSourceAndUiPreservesApprovedConfiguration()
    {
        var cut=Panel();Discover(cut);_api.ReviewError="Source evidence changed; refresh discovery.";cut.Find("[data-testid=sec-accept]").Click();
        cut.WaitForAssertion(()=>cut.Find("[role=alert]").TextContent.Should().Be(_api.ReviewError));_applied.Should().BeNull();_settings.AllowedGraphQlHosts.Should().BeEmpty();
    }
    [Fact] public void UnsafeValuesAreRedactedAndCannotBeAccepted()
    {
        _api.Candidates=[_api.Make("unsafe",SecurityExpectationField.RedirectUrl,"https://example.test/?access_token=DO-NOT-RENDER") with {NormalizedValue="unsafe",Explanation="password=DO-NOT-RENDER"}];
        var cut=Panel();Discover(cut);cut.Markup.Should().NotContain("DO-NOT-RENDER");cut.FindAll("[data-testid=sec-accept]").Should().BeEmpty();
    }
    [Fact] public void SafeBulkRequiresClickAndExcludesConflictingInferredAndUnresolvedCandidates()
    {
        _settings.ExpectedClientId="manual-client";_api.Candidates=[_api.Make("gql",SecurityExpectationField.GraphQlHost,"graphql.example.test"),
            _api.Make("client",SecurityExpectationField.ClientId,"other-client"),
            _api.Make("inferred",SecurityExpectationField.CdnHost,"cdn.example.test") with {EvidenceState=ArchitectureEvidenceState.Inferred,CandidateState=SecurityCandidateState.Suggested},
            _api.Make("unresolved",SecurityExpectationField.RestHost,"rest.example.test") with {EvidenceState=ArchitectureEvidenceState.Unresolved}];
        var cut=Panel();Discover(cut);_api.Reviews.Should().BeEmpty();cut.Find("[data-testid=sec-accept-safe]").Click();cut.WaitForAssertion(()=>_api.Reviews.Should().HaveCount(1));
        _api.Reviews.Single().Request.CandidateId.Should().Be("gql");_settings.ExpectedClientId.Should().Be("manual-client");_settings.AllowedCdnHosts.Should().BeEmpty();
    }
    [Fact] public void EditModeKeepsAssociatedManualLabelsAndDisablesApproval()
    {
        var cut=Panel(true);Discover(cut);foreach(var input in cut.FindAll("input,textarea")) cut.Find($"label[for='{input.Id}']").TextContent.Should().NotBeEmpty();
        cut.Find("[data-testid=sec-accept]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#sec-field-ClientId-input").Change("manual-new");_settings.ExpectedClientId.Should().Be("manual-new");_api.Reviews.Should().BeEmpty();
    }
    [Fact] public void ExistingApprovedJsonKeepsValuesAndDefaultRestoreDoesNotEraseDiscovery()
    {
        _settings.ExpectedClientId="existing-client";var cut=Panel();Discover(cut);_settings.ExpectedSecurityHeaders=[..ApprovedSecurityExpectations.DefaultHeaders];
        cut.Render();cut.FindAll("[data-testid=sec-candidate]").Should().HaveCount(1);_settings.ExpectedClientId.Should().Be("existing-client");
        cut.Find("[data-testid=sec-defaults-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }
    [Fact] public void TargetEnvironmentPassesActualProfileIdAndPersistsOnlyExplicitApproval()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());
        Services.AddSingleton(Moq.Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _=>true).SetResult("""{"activeProfileId":"actual-qa-id","profiles":[{"id":"actual-qa-id","name":"QA","environmentType":2}]}""");
        JSInterop.SetupVoid("birkNextStorage.setItem", _=>true).SetVoidResult();
        var cut=Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p=>p.Add(c=>c.InitialTab,"security"));
        var panel=cut.FindComponent<SecurityExpectationsPanel>();panel.Instance.EnvironmentId.Should().Be("actual-qa-id");
        var settings=Services.GetRequiredService<IFrontendAnalysisSettingsService>();settings.Settings.Profiles.Single().Security.AllowedGraphQlHosts.Should().BeEmpty();
        cut.Find("[data-testid=sec-discover]").Click();cut.WaitForAssertion(()=>cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically")); cut.Find("[data-testid=sec-review-GraphQlHost]").Click();
        settings.Settings.Profiles.Single().Security.AllowedGraphQlHosts.Should().BeEmpty();
        cut.Find("[data-testid=sec-accept]").Click();cut.WaitForAssertion(()=>settings.Settings.Profiles.Single().Security.AllowedGraphQlHosts.Should().Equal("graphql.example.test"));
        settings.Settings.Profiles.Single().Security.Origins.Should().Contain(p=>p.Origin==SecurityExpectationOrigin.AcceptedFromSource);
        JSInterop.Invocations.Should().Contain(i=>i.Identifier=="birkNextStorage.setItem");
    }
    [Fact] public void SourceApprovalMetadataDoesNotChangeExistingAuthenticationVerificationContext()
    {
        var profile=new FrontendAnalysisProfile {Id="qa",TargetUrl="https://shop.example.test",Security=_settings};
        var legacySecurity=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(profile.Security))!;
        legacySecurity.AsObject().Remove("origins");
        var legacyContext=System.Text.Json.JsonSerializer.Serialize(new {profile.Id,profile.TargetUrl,profile.EnvironmentType,profile.Authentication,
            profile.RequestTimeoutSeconds,profile.RetryCount,Security=legacySecurity,profile.ExpectedApiGateway,profile.AllowedRestHosts,profile.AllowedGraphQlEndpoints});
        ManualAuthenticationVerificationEvidence.Fingerprint(profile).Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyContext))));
        var evidence=ManualAuthenticationVerificationEvidence.Record(profile,ManualAuthenticationVerificationStatus.Passed);
        _settings.Origins.Add(new(SecurityExpectationField.RestHost,"manual.example.test",SecurityExpectationOrigin.AcceptedFromSource,_api.Snapshot.Id));
        evidence.StatusFor(profile).Should().Be(ManualAuthenticationVerificationStatus.Passed);
        _settings.AllowedRestHosts.Add("different.example.test");evidence.StatusFor(profile).Should().Be(ManualAuthenticationVerificationStatus.Stale);
    }
    [Fact] public async Task ManualChangesDuringPendingAcceptanceCannotBeOverwritten()
    {
        _api.ReviewGate=new(TaskCreationOptions.RunContinuationsAsynchronously);var cut=Panel();Discover(cut);
        var pending=cut.Find("[data-testid=sec-accept]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(()=>_api.Reviews.Should().HaveCount(1));
        _settings.AllowedRestHosts.Add("new-manual.example.test");cut.Render();_api.ReviewGate.SetResult();await pending;
        _applied.Should().BeNull();_settings.AllowedRestHosts.Should().Contain("new-manual.example.test");_settings.AllowedGraphQlHosts.Should().BeEmpty();
        cut.Find("[role=alert]").TextContent.Should().Contain("current expectations were preserved");
    }
    [Fact] public async Task SwitchingTargetDuringPendingAcceptanceCannotApplyToAnotherProfile()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());
        Services.AddSingleton(Moq.Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _=>true).SetResult("""{"activeProfileId":"actual-qa-id","profiles":[{"id":"actual-qa-id","name":"QA","environmentType":2},{"id":"other-id","name":"Other","environmentType":2}]}""");
        JSInterop.SetupVoid("birkNextStorage.setItem", _=>true).SetVoidResult();_api.ReviewGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut=Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p=>p.Add(c=>c.InitialTab,"security"));
        cut.Find("[data-testid=sec-discover]").Click();cut.WaitForAssertion(()=>cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically")); cut.Find("[data-testid=sec-review-GraphQlHost]").Click();
        var pending=cut.Find("[data-testid=sec-accept]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());cut.WaitForAssertion(()=>_api.Reviews.Should().HaveCount(1));
        cut.FindAll(".fa-profile-chip").Single(b=>b.TextContent.Contains("Other")).Click();_api.ReviewGate.SetResult();await pending;
        Services.GetRequiredService<IFrontendAnalysisSettingsService>().Settings.Profiles.Should().OnlyContain(p=>p.Security.AllowedGraphQlHosts.Count==0);
    }
    [Fact] public void TargetEnvironmentBulkAcceptancePreservesEveryPreviousAddition()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());Services.AddSingleton(Moq.Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _=>true).SetResult("""{"activeProfileId":"qa","profiles":[{"id":"qa","name":"QA","environmentType":2,"security":{"allowedRestHosts":["manual.example.test"]}}]}""");
        JSInterop.SetupVoid("birkNextStorage.setItem", _=>true).SetVoidResult();
        _api.Candidates=[_api.Make("gql",SecurityExpectationField.GraphQlHost,"graphql.example.test"),_api.Make("rest",SecurityExpectationField.RestHost,"new-rest.example.test")];
        var cut=Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p=>p.Add(c=>c.InitialTab,"security"));
        cut.Find("[data-testid=sec-discover]").Click();cut.WaitForAssertion(()=>cut.Find("[data-testid=sec-status]").TextContent.Should().Contain("0 approved automatically"));
        cut.Find("[data-testid=sec-accept-safe]").Click();cut.WaitForAssertion(()=>_api.Reviews.Should().HaveCount(2));
        var security=Services.GetRequiredService<IFrontendAnalysisSettingsService>().Settings.Profiles.Single().Security;
        security.AllowedRestHosts.Should().Equal("manual.example.test","new-rest.example.test");security.AllowedGraphQlHosts.Should().Equal("graphql.example.test");security.Origins.Should().HaveCount(2);
    }
    private sealed class FakeApi : ISecurityExpectationApi
    {
        public IqrSourceSnapshot Snapshot {get;}=new() {Id=Guid.NewGuid(),IntegrationId="source-analysis",Archive=new("generic.zip",new string('a',64),2),AnalyzedAt=DateTimeOffset.UtcNow};
        public List<IqrSourceSnapshot> AdditionalSources {get;set;}=[];
        public List<SecurityExpectationCandidate> Candidates {get;set;}
        public bool ResultCurrent {get;set;}=true;
        public int DiscoverCalls;public string? ReviewError;
        public TaskCompletionSource? ReviewGate;
        public List<(SecurityCandidateReviewRequest Request,bool Accept)> Reviews {get;}=[];
        private SecurityExpectationDiscoveryResult? _result;
        public FakeApi() => Candidates=[Make("gql",SecurityExpectationField.GraphQlHost,"graphql.example.test")];
        public SecurityExpectationCandidate Make(string id,SecurityExpectationField field,string value)=>new() {Id=id,FieldType=field,Value=value,NormalizedValue=SecurityExpectationValues.Normalize(field,value)??"",
            SourceSnapshotId=Snapshot.Id,SourceComponent="Shop.Ui",SourceFile="Shop.Ui/appsettings.json",SourceSymbol="GraphQl:Endpoint",Explanation="Explicit source configuration, no runtime observation.",EvidenceState=ArchitectureEvidenceState.Confirmed};
        public Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string env)=>Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(AdditionalSources.Concat([Snapshot]).ToList());
        public List<ReviewSourceScopeRequest?> ScopeRequests {get;}=[];
        /// <summary>The shared source options for the panel: metadata of the same snapshots (one repository per archive name).</summary>
        public Task<ReviewSourceOptions> SourceScopeAsync(string env, ReviewSourceScopeRequest? scope)
        {
            ScopeRequests.Add(scope);
            var all=AdditionalSources.Concat([Snapshot]).ToList();
            var latest=all.MaxBy(s=>s.AnalyzedAt)!;
            var options=new ReviewSourceOptions {Snapshots=all.Select(s=>new ReviewSourceSnapshot {SnapshotId=s.Id,RepositoryKey="generic",Repository="Generic",
                IdentityBasis="Archive file name",ArchiveName=s.Archive.FileName,Fingerprint=s.Archive.Sha256,AnalyzedAt=s.AnalyzedAt,SourceStatus=s.Status.ToString(),Latest=true}).ToList()};
            return Task.FromResult(BirkNext.Web.Tests.Pages.SourceScopeFixture.Resolve(options,scope,[]));
        }
        public Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> HistoryAsync(string env)=>Task.FromResult<IReadOnlyList<SecurityExpectationDiscoveryResult>>(_result is null?[]:[_result]);
        public Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string env,SecurityDiscoveryRequest request) {DiscoverCalls++;_result=new() {Id=Guid.NewGuid(),TargetEnvironmentId=env,SourceSnapshotId=Snapshot.Id,SourceFingerprint=Snapshot.Archive.Sha256,IsCurrent=ResultCurrent,Status=ArchitectureStatus.Complete,Candidates=[..Candidates]};return Task.FromResult(_result);}
        public async Task<SecurityCandidateReviewResponse> ReviewAsync(string env,SecurityCandidateReviewRequest request,bool accept)
        {
            if(ReviewError is not null)throw new InvalidOperationException(ReviewError);Reviews.Add((request,accept));var candidate=_result!.Candidates.Single(c=>c.Id==request.CandidateId);
            if(ReviewGate is not null) await ReviewGate.Task;
            var approved=accept?SecurityExpectationValues.Accept(request.Approved,candidate,Snapshot.Archive.Sha256,request.Replace,DateTimeOffset.UtcNow):SecurityExpectationValues.Copy(request.Approved);
            _result=_result with {Revision=_result.Revision+1,Candidates=_result.Candidates.Select(c=>c.Id==candidate.Id?c with {CandidateState=accept?SecurityCandidateState.Accepted:SecurityCandidateState.Rejected}:c).ToList()};
            return new SecurityCandidateReviewResponse(_result,approved);
        }
    }
}
