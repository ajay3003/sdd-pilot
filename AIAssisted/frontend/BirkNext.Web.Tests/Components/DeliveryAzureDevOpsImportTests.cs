using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Import snapshot → Import from Azure DevOps: connection setup (the token is write-only), the project → repository → branch cascade, commit
/// resolution with requested vs commit time, and the hand-off of the same import preview as a ZIP.</summary>
public sealed class DeliveryAzureDevOpsImportTests : BunitContext
{
    private static readonly DateTimeOffset Requested = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeApi _api = new();

    public DeliveryAzureDevOpsImportTests()
    {
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    internal sealed class FakeApi : IDeliveryAzureDevOpsApiService
    {
        public List<AzureDevOpsConnectionView> Connections { get; } = [];
        public List<AzureDevOpsConnectionRequest> Saved { get; } = [];
        public List<AzureDevOpsResolveRequest> Resolved { get; } = [];
        public string? ResolveError { get; set; }

        public Task<(List<AzureDevOpsConnectionView> Connections, string? Error)> ConnectionsAsync(CancellationToken ct = default) => Task.FromResult((Connections.ToList(), (string?)null));
        public Task<(AzureDevOpsConnectionView? Connection, string? Error)> SaveConnectionAsync(Guid? id, AzureDevOpsConnectionRequest request, CancellationToken ct = default)
        {
            Saved.Add(request);
            var view = new AzureDevOpsConnectionView(id ?? Guid.NewGuid(), request.DisplayName, request.Organization, true, null, null);
            Connections.RemoveAll(c => c.Id == view.Id);
            Connections.Add(view);
            return Task.FromResult<(AzureDevOpsConnectionView?, string?)>((view, null));
        }
        public Task<AzureDevOpsValidationResult> ValidateAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new AzureDevOpsValidationResult(true, "Connected", "Connected to bufdir (read access to projects)."));
        public Task<AzureDevOpsList<AzureDevOpsProjectItem>> ProjectsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(new AzureDevOpsList<AzureDevOpsProjectItem>([new("p1", "Birk"), new("p2", "M2LB")], null));
        public Task<AzureDevOpsList<AzureDevOpsRepositoryItem>> RepositoriesAsync(Guid id, string project, CancellationToken ct = default) =>
            Task.FromResult(new AzureDevOpsList<AzureDevOpsRepositoryItem>(project == "M2LB" ? [new("r1", "M2LB", "main"), new("r2", "M2LB.Common", "main")] : [], null));
        public Task<AzureDevOpsList<AzureDevOpsBranchItem>> BranchesAsync(Guid id, string project, string repository, CancellationToken ct = default) =>
            Task.FromResult(new AzureDevOpsList<AzureDevOpsBranchItem>([new("main", "refs/heads/main"), new("develop", "refs/heads/develop"), new("release/1.2", "refs/heads/release/1.2")], null));
        public Task<AzureDevOpsResolveResult> ResolveAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default)
        {
            Resolved.Add(request);
            return Task.FromResult(ResolveError is { } e ? new AzureDevOpsResolveResult(null, null, e)
                : new AzureDevOpsResolveResult(Commit(request), "The latest commit before the selected time is from 23 Oct 2026 07:00 UTC (3 day(s) earlier).", null));
        }
        public Task<(DeliveryImportPreview? Preview, string? Note, string? Error)> PreviewAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default) =>
            Task.FromResult<(DeliveryImportPreview?, string?, string?)>((new DeliveryImportPreview
            {
                StagingId = Guid.NewGuid(), FileName = "M2LB@abc1234.zip", SourceType = DeliverySourceType.AzureDevOps, GitCommit = "abc1234def",
                SuggestedEffectiveAt = request.RequestedAt, AzureDevOps = Commit(request), TaskTotal = 8, TaskCompleted = 3,
            }, "gap note", null));

        private static DeliveryAzureDevOpsSource Commit(AzureDevOpsResolveRequest r) =>
            new("bufdir", r.Project, r.Repository, r.Branch, "abc1234def5678", new DateTimeOffset(2026, 10, 23, 7, 0, 0, TimeSpan.Zero)) { RequestedAt = r.RequestedAt, CommitAuthor = "Dev" };
    }

    [Fact]
    public void NoConnection_ShowsTheConfigureForm_TokenIsAPasswordField_AndNeverShownOnEdit()
    {
        var cut = Render<DeliveryAzureDevOpsImport>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=dlr-ado-connection-form]"));
        cut.Find("[data-testid=dlr-ado-pat]").GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid=dlr-ado-pat]").GetAttribute("autocomplete").Should().Be("off");

        cut.Find("[data-testid=dlr-ado-name]").Change("Bufdir");
        cut.Find("[data-testid=dlr-ado-org]").Change("https://dev.azure.com/bufdir");
        cut.Find("[data-testid=dlr-ado-pat]").Change("fake-token-123");
        cut.Find("[data-testid=dlr-ado-connection-form]").Submit();

        _api.Saved.Should().ContainSingle().Which.Pat.Should().Be("fake-token-123");
        cut.WaitForAssertion(() => cut.Find("[data-testid=dlr-ado-connection-status]").TextContent.Should().Be("Configured · not validated"));
        cut.Find("[data-testid=dlr-ado-edit]").Click();
        cut.Find("[data-testid=dlr-ado-pat]").GetAttribute("value").Should().BeNullOrEmpty("the stored token is never put back into the page");
        cut.Find("[data-testid=dlr-ado-credential]").TextContent.Should().Contain("Credential: Configured");
        cut.Find("[data-testid=dlr-ado-name]").Change("Bufdir DevOps");
        cut.Find("[data-testid=dlr-ado-connection-form]").Submit();
        _api.Saved[^1].Pat.Should().BeNull("an empty token field keeps the stored credential");
        cut.Markup.Should().NotContain("fake-token-123");
    }

    [Fact]
    public void Cascade_Resolve_ShowsRequestedAndCommitTime_ThenHandsOverThePreview()
    {
        _api.Connections.Add(new(Guid.NewGuid(), "Bufdir", "bufdir", true, "Connected", Requested));
        (DeliveryImportPreview Preview, string? Note)? handed = null;
        var cut = Render<DeliveryAzureDevOpsImport>(p => p.Add(c => c.OnPreview, (Action<(DeliveryImportPreview, string?)>)(r => handed = r)));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dlr-ado-project] option").Should().HaveCount(3));
        cut.Find("[data-testid=dlr-ado-repository]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=dlr-ado-resolve]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=dlr-ado-project]").Change("M2LB");
        cut.Find("[data-testid=dlr-ado-repository]").Change("M2LB");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dlr-ado-branch] option").Select(o => o.TextContent).Should().Equal("Choose a branch", "main", "develop", "release/1.2"));
        cut.Find("[data-testid=dlr-ado-branch]").GetAttribute("value").Should().Be("main", "the repository default branch is preselected");

        cut.Find("[data-testid=dlr-ado-at]").Change(true);
        cut.Find("[data-testid=dlr-ado-time]").Change(DeliveryReportingPresentation.LocalInput(Requested));
        cut.Find("[data-testid=dlr-ado-resolve]").Click();

        _api.Resolved.Should().ContainSingle().Which.Should().Match<AzureDevOpsResolveRequest>(r => r.Branch == "main" && r.RequestedAt == Requested && r.Repository == "M2LB");
        cut.Find("[data-testid=dlr-ado-commit-id]").TextContent.Should().Be("abc1234");
        cut.Find("[data-testid=dlr-ado-commit]").TextContent.Should().Contain("Requested").And.Contain(DeliveryReportingPresentation.DayTime(Requested));
        cut.Find("[data-testid=dlr-ado-gap]").TextContent.Should().Contain("3 day(s) earlier");

        cut.Find("[data-testid=dlr-ado-preview]").Click();
        handed.Should().NotBeNull();
        handed!.Value.Preview.AzureDevOps!.Branch.Should().Be("main");
    }

    [Fact]
    public void Latest_SendsNoTime_AndResolveErrorsAreShown()
    {
        _api.Connections.Add(new(Guid.NewGuid(), "Bufdir", "bufdir", true, null, null));
        _api.ResolveError = "No commit found on this branch before the selected date/time.";
        var cut = Render<DeliveryAzureDevOpsImport>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dlr-ado-project] option").Should().HaveCount(3));
        cut.Find("[data-testid=dlr-ado-project]").Change("M2LB");
        cut.Find("[data-testid=dlr-ado-repository]").Change("M2LB");
        cut.WaitForAssertion(() => cut.Find("[data-testid=dlr-ado-resolve]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=dlr-ado-resolve]").Click();
        _api.Resolved.Single().RequestedAt.Should().BeNull("Current/latest resolves the branch head now");
        cut.Find("[data-testid=dlr-ado-resolve-error]").TextContent.Should().Contain("No commit found");
        cut.FindAll("[data-testid=dlr-ado-preview]").Should().BeEmpty();
    }

    [Fact]
    public void ConnectionStatus_Labels()
    {
        DeliveryAzureDevOpsImport.StatusText(new(Guid.Empty, "x", "o", true, "AuthenticationFailed", null)).Should().Be("Authentication failed");
        DeliveryAzureDevOpsImport.StatusText(new(Guid.Empty, "x", "o", false, null, null)).Should().Be("Credential missing");
        DeliveryAzureDevOpsImport.StatusText(new(Guid.Empty, "x", "o", true, "AccessDenied", null)).Should().Be("Access denied");
    }
}
