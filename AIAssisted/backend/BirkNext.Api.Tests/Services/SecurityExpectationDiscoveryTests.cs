using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SecurityExpectations;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

public sealed class SecurityExpectationDiscoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string FrontendClient = "22222222-2222-2222-2222-222222222222";
    private const string ApiClient = "33333333-3333-3333-3333-333333333333";
    private static readonly (string Path, string Content)[] GenericFixture =
    [
        ("Shop.Ui/Shop.Ui.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><ItemGroup><PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.0.0"/><PackageReference Include="StrawberryShake.Blazor" Version="1.0.0"/></ItemGroup></Project>"""),
        ("Shop.Ui/Program.cs", """
            builder.Services.AddMsalAuthentication(options => builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication));
            builder.Services.AddHttpClient("Orders", c => c.BaseAddress = new Uri(builder.Configuration["Orders:BaseUrl"]));
            builder.Services.AddShopClient().ConfigureHttpClient(c => c.BaseAddress = new Uri(builder.Configuration["GraphQl:Endpoint"]));
            var unrelated = "https://unrelated.example.test";
            """),
        ("Shop.Ui/appsettings.json", """
            {"AzureAd":{"Authority":"https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111","TenantId":"11111111-1111-1111-1111-111111111111","ClientId":"22222222-2222-2222-2222-222222222222","RedirectUri":"https://shop.example.test/authentication/login-callback","ClientSecret":"DO-NOT-RETAIN-IDENTITY-CREDENTIAL"},"Orders":{"BaseUrl":"https://ORDERS.example.test.:8443/api"},"GraphQl":{"Endpoint":"https://graphql.example.test/graphql"},"Assets":{"CdnBaseUrl":"https://cdn.example.test/assets"},"UnrelatedHost":"https://unrelated.example.test"}
            """),
        ("Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Microsoft.Identity.Web" Version="1.0.0"/><PackageReference Include="HotChocolate.AspNetCore" Version="1.0.0"/></ItemGroup></Project>"""),
        ("Shop.Api/Program.cs", """
            builder.Services.AddAuthentication().AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
            builder.Services.AddGraphQLServer();
            app.Use(async (context, next) => { context.Response.Headers["Content-Security-Policy"] = "DO-NOT-RETAIN-HEADER-VALUE"; await next(); });
            app.UseHsts();
            """),
        ("Shop.Api/appsettings.json", """
            {"AzureAd":{"ClientId":"33333333-3333-3333-3333-333333333333","TenantId":"11111111-1111-1111-1111-111111111111"},"BackendBaseUrl":"https://orders.example.test:8443/api","SecurityHeaders":{"X-Frame-Options":"DENY"},"ConnectionStrings":{"Default":"Server=x;Password=DO-NOT-RETAIN-DB-CREDENTIAL"}}
            """),
    ];
    private static IqrSourceSnapshot Analyze(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
        var (workspace,error) = IqrSourceArchiveReader.Read("generic.zip", memory.ToArray());
        error.Should().BeNull();
        var snapshot = new IqrSourceSnapshot { Id=Guid.NewGuid(), IntegrationId="source-analysis", AnalyzedAt=DateTimeOffset.UtcNow, Status=SourceAnalysisStatus.Ready,
            Archive=new("generic.zip",new string('a',64),files.Length) };
        snapshot = snapshot with { Architecture=SourceArchitectureAnalyzer.Analyze(snapshot.Id,workspace!,snapshot.AnalyzedAt) };
        return snapshot with { SecurityExpectationsEvidence=SecurityExpectationSourceAnalyzer.Analyze(snapshot,workspace!) };
    }
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task Insert(AppDbContext db,IqrSourceSnapshot snapshot,string env="qa")
    {
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord {Id=snapshot.Id,EnvironmentId=env,IntegrationId="source-analysis",AnalyzedAt=snapshot.AnalyzedAt,EvidenceJson=JsonSerializer.Serialize(snapshot,Json)});
        await db.SaveChangesAsync();
    }
    private static SecurityExpectationCandidate Candidate(SecurityExpectationField field,string value,Guid snapshot) => new() {
        Id=Guid.NewGuid().ToString(),FieldType=field,Value=value,NormalizedValue=SecurityExpectationValues.Normalize(field,value)!,SourceSnapshotId=snapshot,
        SourceComponent="Shop.Ui",SourceFile="Shop.Ui/appsettings.json",SourceSymbol=field.ToString(),EvidenceState=ArchitectureEvidenceState.Confirmed,CandidateState=SecurityCandidateState.Detected };
    private static IqrSourceSnapshot Snapshot(SecurityExpectationField field,string value) {
        var id=Guid.NewGuid();return new() {Id=id,IntegrationId="source-analysis",AnalyzedAt=DateTimeOffset.UtcNow,Status=SourceAnalysisStatus.Ready,Archive=new("x.zip",new string('a',64),1),
            SecurityExpectationsEvidence=new() {SourceSnapshotId=id,SourceFingerprint=new string('a',64),Candidates=[Candidate(field,value,id)]}};
    }
    [Fact] public void GenericFixture_PreservesFrontendAndApiIdentityAndAllMeaningfulHostRoles()
    {
        var snapshot=Analyze(GenericFixture);var evidence=snapshot.SecurityExpectationsEvidence!;
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.Authority && c.Value.EndsWith(Tenant));
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.TenantId && c.Value==Tenant);
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.ClientId && c.Value==FrontendClient && c.SourceFile.StartsWith("Shop.Ui/"));
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.ClientId && c.Value==ApiClient && c.SourceFile.StartsWith("Shop.Api/"));
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.RedirectUrl && c.Value=="https://shop.example.test/authentication/login-callback");
        foreach(var field in new[]{SecurityExpectationField.BackendDomain,SecurityExpectationField.RestHost})
            evidence.Candidates.Should().Contain(c=>c.FieldType==field && c.NormalizedValue=="orders.example.test:8443");
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.GraphQlHost && c.NormalizedValue=="graphql.example.test");
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.CdnHost && c.NormalizedValue=="cdn.example.test");
        evidence.Candidates.Should().NotContain(c=>c.Value.Contains("unrelated") || c.Value.Contains("HotChocolate") || c.Value.Contains("StrawberryShake"));
        evidence.Candidates.Should().OnlyContain(c=>c.SourceSnapshotId==snapshot.Id && c.SourceFile.Length>0 && c.SourceSymbol.Length>0);
        var approved=new ApprovedSecurityExpectations();var before=JsonSerializer.Serialize(approved,Json);
        var result=SecurityExpectationDiscoveryService.Project(snapshot,"qa",approved);
        result.Candidates.Where(c=>c.FieldType==SecurityExpectationField.ClientId).Should().OnlyContain(c=>c.CandidateState==SecurityCandidateState.NeedsReview);
        JsonSerializer.Serialize(approved,Json).Should().Be(before);
        result.Candidates.Should().NotContain(c=>c.CandidateState==SecurityCandidateState.Accepted);
    }
    [Fact] public void HeadersAreNamesOnlyAndSecretsNeverLeaveTheProjection()
    {
        var evidence=Analyze(GenericFixture).SecurityExpectationsEvidence!;
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.SecurityHeader && c.Value=="Content-Security-Policy");
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.SecurityHeader && c.Value=="X-Frame-Options");
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.SecurityHeader && c.Value=="Strict-Transport-Security");
        JsonSerializer.Serialize(evidence,Json).Should().NotContain("DO-NOT-RETAIN").And.NotContain("ClientSecret").And.NotContain("ConnectionStrings");
        new ApprovedSecurityExpectations().ExpectedSecurityHeaders.Should().BeEquivalentTo(ApprovedSecurityExpectations.DefaultHeaders);
    }
    [Fact] public void SafeMalformedIdentifierIsRetainedAsUnresolvedReviewEvidence()
    {
        var files=GenericFixture.Append(("Shop.Ui/appsettings.invalid.json", """{"AzureAd":{"TenantId":"not-a-guid"}}""")).ToArray();
        var candidate=Analyze(files).SecurityExpectationsEvidence!.Candidates.Single(c=>c.FieldType==SecurityExpectationField.TenantId && c.Value=="not-a-guid");
        candidate.FormatState.Should().Be("InvalidFormat");candidate.CandidateState.Should().Be(SecurityCandidateState.NeedsReview);
        candidate.EvidenceState.Should().Be(ArchitectureEvidenceState.Unresolved);
        FluentActions.Invoking(()=>SecurityExpectationValues.Accept(new(),candidate,"fingerprint",false,DateTimeOffset.UtcNow))
            .Should().Throw<InvalidOperationException>();
    }
    [Fact] public void RecognizedAppSettingsEnvironmentFilesKeepIdentityCandidatesInSeparateScopes()
    {
        var snapshot=Analyze(("Shop.Ui/Shop.Ui.csproj","""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.0.0"/></ItemGroup></Project>"""),
            ("Shop.Ui/Program.cs","builder.Services.AddMsalAuthentication(options => builder.Configuration.Bind(\"AzureAd\", options.ProviderOptions.Authentication));"),
            ("Shop.Ui/appsettings.Development.json","""{"AzureAd":{"TenantId":"11111111-1111-1111-1111-111111111111"}}"""),
            ("Shop.Ui/appsettings.Production.json","""{"AzureAd":{"TenantId":"11111111-1111-1111-1111-111111111111"}}"""));
        var candidates=SecurityExpectationDiscoveryService.Project(snapshot,"qa",new()).Candidates.Where(c=>c.FieldType==SecurityExpectationField.TenantId).ToList();
        candidates.Should().HaveCount(2);candidates.Select(c=>c.EnvironmentScope).Should().BeEquivalentTo("Development","Production");
        candidates.Select(c=>c.Id).Should().OnlyHaveUniqueItems();
    }
    [Theory]
    [InlineData(SecurityExpectationField.RestHost,"HTTPS://API.Example.test.:8443/v1","api.example.test:8443")]
    [InlineData(SecurityExpectationField.RestHost,"API.Example.test.","api.example.test")]
    [InlineData(SecurityExpectationField.RestHost,"https://[::1]:8443/api","[::1]:8443")]
    [InlineData(SecurityExpectationField.RedirectUrl,"https://App.example.test/Callback","https://app.example.test/Callback")]
    [InlineData(SecurityExpectationField.SecurityHeader,"x-frame-options","X-Frame-Options")]
    public void NormalizationPreservesPortsAndUrlPaths(SecurityExpectationField field,string value,string expected) => SecurityExpectationValues.Normalize(field,value).Should().Be(expected);
    [Theory]
    [InlineData("https://*.example.test/callback")]
    [InlineData("/callback")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://app.example.test/?access_token=secret")]
    [InlineData("not a url")]
    public void UnsafeRedirectsAreNeverAccepted(string value) => SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,value).Should().BeNull();
    [Fact] public void UnresolvedEndpointsAndMalformedExplicitValuesProduceDiagnosticsNotGuesses()
    {
        var evidence=Analyze(("App/App.csproj","<Project Sdk=\"Microsoft.NET.Sdk.Web\"/>"),
            ("App/Program.cs","builder.Services.AddHttpClient(\"X\", c => c.BaseAddress = new Uri(builder.Configuration[\"Remote:BaseUrl\"])); builder.Services.AddGraphQLServer();"),
            ("App/appsettings.json","""{"AzureAd":{"RedirectUri":"/callback","ClientId":"client_secret=DO-NOT-RETAIN"},"GraphQl":{"Endpoint":"not an endpoint"},"RandomGuid":"11111111-1111-1111-1111-111111111111"}""")).SecurityExpectationsEvidence!;
        evidence.Candidates.Should().NotBeEmpty().And.OnlyContain(c=>c.FormatState=="InvalidFormat" && c.CandidateState==SecurityCandidateState.NeedsReview);
        evidence.Diagnostics.Should().NotBeEmpty();
        JsonSerializer.Serialize(evidence,Json).Should().NotContain("DO-NOT-RETAIN");
    }
    [Fact] public async Task DiscoverIsReadOnly_ExplicitAddPreservesManualValueAndRecordsProvenance()
    {
        using var db=Db();var snapshot=Snapshot(SecurityExpectationField.GraphQlHost,"graphql.example.test");await Insert(db,snapshot);
        var service=new SecurityExpectationDiscoveryService(db);var approved=new ApprovedSecurityExpectations {AllowedRestHosts=["manual.example.test"],AllowedGraphQlHosts=["legacy.example.test"]};
        var before=JsonSerializer.Serialize(approved,Json);var result=await service.DiscoverAsync("qa",new(snapshot.Id,approved));
        JsonSerializer.Serialize(approved,Json).Should().Be(before);
        var review=await service.ReviewAsync("qa",new(result.Id,result.Candidates.Single().Id,result.Revision,approved),true);
        approved.AllowedGraphQlHosts.Should().Equal("legacy.example.test");
        review.Approved.AllowedGraphQlHosts.Should().Equal("legacy.example.test","graphql.example.test");
        review.Approved.AllowedRestHosts.Should().Equal("manual.example.test");
        review.Approved.Origins.Single().Should().Match<SecurityExpectationProvenance>(p=>p.Origin==SecurityExpectationOrigin.AcceptedFromSource && p.SourceSnapshotId==snapshot.Id && p.SourceFingerprint==snapshot.Archive.Sha256 && p.AcceptedAt!=null);
        (await service.ListAsync("qa")).Single().Candidates.Single().CandidateState.Should().Be(SecurityCandidateState.Accepted);
        // Runtime/FQR configuration is the original approved object, never the discovery result.
        JsonSerializer.Serialize(approved,Json).Should().Be(before);
    }
    [Fact] public async Task SingletonConflictRequiresExplicitReplace_KeepCurrentDoesNotOverwrite()
    {
        using var db=Db();var snapshot=Snapshot(SecurityExpectationField.ClientId,ApiClient);await Insert(db,snapshot);
        var service=new SecurityExpectationDiscoveryService(db);var approved=new ApprovedSecurityExpectations {ExpectedClientId=FrontendClient};
        var result=await service.DiscoverAsync("qa",new(snapshot.Id,approved));var c=result.Candidates.Single();c.CandidateState.Should().Be(SecurityCandidateState.Conflict);
        await FluentActions.Invoking(()=>service.ReviewAsync("qa",new(result.Id,c.Id,0,approved),true)).Should().ThrowAsync<SecurityDiscoveryReviewException>();
        var kept=await service.ReviewAsync("qa",new(result.Id,c.Id,0,approved),false);kept.Approved.ExpectedClientId.Should().Be(FrontendClient);
        var replaced=await service.ReviewAsync("qa",new(result.Id,c.Id,kept.Discovery.Revision,approved,true),true);replaced.Approved.ExpectedClientId.Should().Be(ApiClient);
        approved.ExpectedClientId.Should().Be(FrontendClient);
    }
    [Fact] public async Task NewerSnapshotDoesNotRebindSelectedReviewOrUnapproveAnything()
    {
        using var db=Db();var a=Snapshot(SecurityExpectationField.RestHost,"a.example.test");await Insert(db,a);
        var service=new SecurityExpectationDiscoveryService(db);var approved=new ApprovedSecurityExpectations {AllowedRestHosts=["manual.example.test"]};
        var result=await service.DiscoverAsync("qa",new(a.Id,approved));
        await Insert(db,Snapshot(SecurityExpectationField.RestHost,"b.example.test") with {AnalyzedAt=a.AnalyzedAt.AddMinutes(1)});
        var accepted = await service.ReviewAsync("qa",new(result.Id,result.Candidates.Single().Id,0,approved),true);
        accepted.Approved.AllowedRestHosts.Should().Contain("a.example.test");
        var history=(await service.ListAsync("qa")).Single();history.IsCurrent.Should().BeTrue();history.SourceSnapshotId.Should().Be(a.Id);
        approved.AllowedRestHosts.Should().Equal("manual.example.test");
    }
    [Fact] public void GenericDuplicateSourceFixtureCollapsesLocationsWithoutFalseConflict()
    {
        var files = Enumerable.Range(0,10).Select(i => ($"App/appsettings.{i}.json", """{"AzureAd":{"Authority":"https://identity.example.test/a","TenantId":"11111111-1111-1111-1111-111111111111"},"ApiBaseUrl":"https://api.example.test/api","SecurityHeaders":{"X-Frame-Options":"DENY"}}""")).ToList();
        files.Add(("App/App.csproj","<Project Sdk=\"Microsoft.NET.Sdk.Web\"/>"));
        files.Add(("App/appsettings.alternate.json","""{"AzureAd":{"Authority":"https://identity.example.test/b"},"RestBaseUrl":"https://second.example.test/api"}"""));
        var result=SecurityExpectationDiscoveryService.Project(Analyze(files.ToArray()),"qa",new());
        var authorities=result.Candidates.Where(c=>c.FieldType==SecurityExpectationField.Authority).ToList();
        authorities.Should().HaveCount(2); authorities.Should().OnlyContain(c=>c.CandidateState==SecurityCandidateState.NeedsReview);
        authorities.Single(c=>c.Value.EndsWith("/a")).SupportingEvidenceCount.Should().Be(10);
        result.Candidates.Should().NotContain(c=>c.CandidateState==SecurityCandidateState.Conflict);
        result.Candidates.Single(c=>c.FieldType==SecurityExpectationField.TenantId).SupportingEvidenceCount.Should().Be(10);
        result.Candidates.Single(c=>c.FieldType==SecurityExpectationField.SecurityHeader).SupportingEvidenceCount.Should().Be(10);
    }
    [Fact] public async Task LegacyDiscoveryIsGroupedReadOnlyWithoutChangingStoredHistory()
    {
        using var db=Db(); var snapshot=Snapshot(SecurityExpectationField.RestHost,"api.example.test"); await Insert(db,snapshot);
        var old=new SecurityExpectationDiscoveryResult {TargetEnvironmentId="qa",SourceSnapshotId=snapshot.Id,SourceFingerprint=snapshot.Archive.Sha256,
            Candidates=[Candidate(SecurityExpectationField.RestHost,"api.example.test",snapshot.Id),Candidate(SecurityExpectationField.RestHost,"api.example.test",snapshot.Id)]};
        var json=JsonSerializer.Serialize(old,Json);db.SecurityExpectationDiscoveries.Add(new(){Id=old.Id,EnvironmentId="qa",SourceSnapshotId=snapshot.Id,CreatedAt=DateTimeOffset.UtcNow,EvidenceJson=json});await db.SaveChangesAsync();
        var result=(await new SecurityExpectationDiscoveryService(db).ListAsync("qa")).Single();result.Candidates.Should().HaveCount(1);result.SupportingEvidenceCount.Should().Be(1);
        result.Candidates.Single().IsCurrent.Should().BeFalse();db.SecurityExpectationDiscoveries.Single().EvidenceJson.Should().Be(json);
    }
    [Fact] public async Task RepeatedRefreshKeepsCandidateEvidenceIdentityAndIgnoreDecision()
    {
        using var db=Db(); var snapshot=Analyze(GenericFixture); await Insert(db,snapshot);
        var service=new SecurityExpectationDiscoveryService(db);
        var result=await service.DiscoverAsync("qa",new(snapshot.Id,new()));
        var candidate=result.Candidates.First(); var id=candidate.Id; var evidenceCount=candidate.SupportingEvidenceCount;
        await service.ReviewAsync("qa",new(result.Id,id,result.Revision,new()),false);
        for(var i=0;i<5;i++)
        {
            result=await service.DiscoverAsync("qa",new(snapshot.Id,new()));
            result.Candidates.First(c=>c.Id==id).CandidateState.Should().Be(SecurityCandidateState.Rejected);
            result.Candidates.First(c=>c.Id==id).SupportingEvidenceCount.Should().Be(evidenceCount);
        }
        result.Candidates.Select(c=>c.Id).Should().OnlyHaveUniqueItems();
        (await service.ListAsync("qa")).First().Candidates.Select(c=>c.Id).Should().BeEquivalentTo(result.Candidates.Select(c=>c.Id));
    }
    [Fact] public async Task ConflictingLegacyDecisionsForEquivalentValuesRemainVisibleAsConflict()
    {
        using var db=Db(); var snapshot=Snapshot(SecurityExpectationField.TenantId,"11111111-1111-1111-1111-111111111111"); await Insert(db,snapshot);
        var prior=new SecurityExpectationDiscoveryResult { Id=Guid.NewGuid(), TargetEnvironmentId="qa", SourceSnapshotId=snapshot.Id,
            SourceFingerprint=snapshot.Archive.Sha256, SourceScope=new(snapshot.Id,[]), IsCurrent=true,
            Candidates=[Candidate(SecurityExpectationField.TenantId,"{11111111-1111-1111-1111-111111111111}",snapshot.Id) with {Id="legacy-a"},
                Candidate(SecurityExpectationField.TenantId,"11111111-1111-1111-1111-111111111111",snapshot.Id) with {Id="legacy-b"}] };
        db.SecurityExpectationDiscoveries.Add(new() {Id=prior.Id,EnvironmentId="qa",SourceSnapshotId=snapshot.Id,CreatedAt=DateTimeOffset.UtcNow,
            EvidenceJson=JsonSerializer.Serialize(prior,Json),DecisionsJson=JsonSerializer.Serialize(new[] {
                new SecurityCandidateDecision("legacy-a",SecurityCandidateState.Accepted,DateTimeOffset.UtcNow),
                new SecurityCandidateDecision("legacy-b",SecurityCandidateState.Rejected,DateTimeOffset.UtcNow)},Json)});
        await db.SaveChangesAsync();
        var result=await new SecurityExpectationDiscoveryService(db).DiscoverAsync("qa",new(snapshot.Id,new()));
        result.Candidates.Should().ContainSingle().Which.CandidateState.Should().Be(SecurityCandidateState.Conflict);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRelatedScopeAggregatesSameValuesAndPreservesDifferentValues(bool different)
    {
        using var db=Db();
        var a=Snapshot(SecurityExpectationField.Authority,"https://identity.example.test/a");
        var b=Snapshot(SecurityExpectationField.Authority,different ? "https://identity.example.test/b" : "https://identity.example.test/a");
        b=b with {Archive=b.Archive with {FileName="Shared.Identity.zip"}}; // a related source is another repository (one snapshot per repository)
        await Insert(db,a); await Insert(db,b);
        var service=new SecurityExpectationDiscoveryService(db);
        var primary=await service.DiscoverAsync("qa",new(a.Id,new())); primary.Candidates.Should().HaveCount(1);
        var result=await service.DiscoverAsync("qa",new(a.Id,new(),[b.Id]));
        result.SourceScope!.RelatedSourceSnapshotIds.Should().Equal(b.Id);
        result.Candidates.Should().HaveCount(different ? 2 : 1);
        result.SupportingEvidenceCount.Should().Be(2);
        result.Candidates.Should().OnlyContain(c => c.CandidateState == (different ? SecurityCandidateState.NeedsReview : SecurityCandidateState.Detected));
        result.Candidates.SelectMany(c => c.SupportingEvidence).Select(e => e.SourceSnapshotId).Should().BeEquivalentTo([a.Id,b.Id]);
    }
    [Fact] public async Task SwitchingSnapshotRecomputesWithoutChangingPolicy()
    {
        using var db=Db(); var a=Snapshot(SecurityExpectationField.Authority,"https://identity.example.test/a");
        var b=Snapshot(SecurityExpectationField.Authority,"https://identity.example.test/b"); await Insert(db,a); await Insert(db,b);
        var approved=new ApprovedSecurityExpectations {ExpectedAuthority="https://identity.example.test/a"}; var service=new SecurityExpectationDiscoveryService(db);
        (await service.DiscoverAsync("qa",new(a.Id,approved))).Candidates.Single().CandidateState.Should().Be(SecurityCandidateState.MatchesSource);
        var next=await service.DiscoverAsync("qa",new(b.Id,approved)); next.Candidates.Single().Value.Should().EndWith("/b");
        next.Candidates.Single().CandidateState.Should().Be(SecurityCandidateState.Conflict); approved.ExpectedAuthority.Should().EndWith("/a");
    }
    [Fact] public async Task ReviewRevisionAndEnvironmentScopeAreEnforced()
    {
        using var db=Db();var snapshot=Snapshot(SecurityExpectationField.CdnHost,"cdn.example.test");await Insert(db,snapshot);
        var service=new SecurityExpectationDiscoveryService(db);var approved=new ApprovedSecurityExpectations();var result=await service.DiscoverAsync("qa",new(snapshot.Id,approved));var c=result.Candidates.Single();
        await FluentActions.Invoking(()=>service.ReviewAsync("other",new(result.Id,c.Id,0,approved),true)).Should().ThrowAsync<SecurityDiscoveryReviewException>();
        await service.ReviewAsync("qa",new(result.Id,c.Id,0,approved),false);
        await FluentActions.Invoking(()=>service.ReviewAsync("qa",new(result.Id,c.Id,0,approved),true)).Should().ThrowAsync<SecurityDiscoveryReviewException>().WithMessage("Review changed; refresh discovery.");
    }
    [Fact] public void PartialAndHistoricalUnsupportedEvidenceAreNotFailures()
    {
        var snapshot=Snapshot(SecurityExpectationField.RestHost,"api.example.test") with {Status=SourceAnalysisStatus.Partial};
        SecurityExpectationDiscoveryService.Project(snapshot,"qa",new()).Status.Should().Be(ArchitectureStatus.Complete);
        SecurityExpectationDiscoveryService.Project(snapshot with {SecurityExpectationsEvidence=null},"qa",new()).Status.Should().Be(ArchitectureStatus.Unsupported);
    }
    [Fact] public void ExistingJsonLoadsWithoutMigrationAndSourceDefaultsStaySeparate()
    {
        var existing=JsonSerializer.Deserialize<ApprovedSecurityExpectations>("""{"expectedClientId":"existing-client","allowedRestHosts":["manual.example.test"],"expectedSecurityHeaders":["X-Frame-Options"]}""",Json)!;
        existing.ExpectedClientId.Should().Be("existing-client");existing.ExpectedSecurityHeaders.Should().Equal("X-Frame-Options");existing.Origins.Should().BeEmpty();
        SecurityExpectationValues.Origin(existing,SecurityExpectationField.RestHost,"manual.example.test").Should().Be(SecurityExpectationOrigin.Existing);
    }
    [Fact] public async Task GenericFixture_IngestionPersistsExactSnapshotProjectionWithoutMutatingHistory()
    {
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
            foreach(var (path,content) in GenericFixture) {using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false));writer.Write(content);}
        using var db=Db();var store=new IqrSourceStore(db);
        var (a,error)=await store.AnalyzeAsync("qa","source-analysis","generic.zip",memory.ToArray());error.Should().BeNull();
        var retained=db.IqrSourceSnapshots.Single().EvidenceJson;
        a!.SecurityExpectationsEvidence!.SourceSnapshotId.Should().Be(a.Id);
        a.SecurityExpectationsEvidence.SourceFingerprint.Should().Be(a.Archive.Sha256);
        a.SecurityExpectationsEvidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.ClientId && c.Value==FrontendClient);
        a.SecurityExpectationsEvidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.GraphQlHost && c.Value=="graphql.example.test");
        await store.AnalyzeAsync("qa","source-analysis","generic.zip",memory.ToArray());
        db.IqrSourceSnapshots.Should().HaveCount(2);db.IqrSourceSnapshots.Single(s=>s.Id==a.Id).EvidenceJson.Should().Be(retained);
        retained.Should().NotContain("DO-NOT-RETAIN");
    }
    [Fact] public void GraphQlLiteralTargetIsAHost_ButServerTechnologyAndRandomStringsAreNot()
    {
        var snapshot=Analyze(("Web/Web.csproj","<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"StrawberryShake.Blazor\" Version=\"1.0.0\"/></ItemGroup></Project>"),
            ("Web/Program.cs","""
                builder.Services.AddShopClient().ConfigureHttpClient(c => { c.DefaultRequestHeaders.Add("Origin","https://header-origin.example.test"); c.BaseAddress = new Uri("https://graph.microsoft.com/graphql"); });
                builder.Services.AddHttpClient("Rest", c => { c.DefaultRequestHeaders.Add("Origin","https://header-origin.example.test"); c.BaseAddress = new Uri("https://rest.example.test/api"); });
                builder.Services.AddGraphQLServer(); var unrelated="https://arbitrary.example.test";
                """));
        var evidence=snapshot.SecurityExpectationsEvidence!;
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.GraphQlHost && c.NormalizedValue=="graph.microsoft.com");
        evidence.Candidates.Should().Contain(c=>c.FieldType==SecurityExpectationField.RestHost && c.NormalizedValue=="rest.example.test");
        snapshot.Architecture!.Dependencies.Should().Contain(d=>!d.IsResolved && d.Confidence.Contains("Client registered"),
            "a security endpoint fact must not change existing architecture target resolution");
        evidence.Candidates.Should().NotContain(c=>c.Value.Contains("arbitrary") || c.Value.Contains("header-origin") || c.Value.Contains("Hot Chocolate") || c.Value.Contains("Strawberry Shake"));
    }
    [Fact] public void InconsistentSnapshotBindingCannotPromoteCandidates()
    {
        var snapshot=Snapshot(SecurityExpectationField.RestHost,"api.example.test");
        snapshot=snapshot with {SecurityExpectationsEvidence=snapshot.SecurityExpectationsEvidence! with {SourceFingerprint="different"}};
        var result=SecurityExpectationDiscoveryService.Project(snapshot,"qa",new());result.Candidates.Should().BeEmpty();result.Status.Should().Be(ArchitectureStatus.Unsupported);
        result.Diagnostics.Should().Contain(d=>d.Contains("binding is inconsistent"));
    }
    [Fact] public async Task CurrentSourceDoesNotMistakeRecentIntegrationSnapshotsForStandaloneSource()
    {
        using var db=Db();var snapshot=Snapshot(SecurityExpectationField.RestHost,"api.example.test");await Insert(db,snapshot);
        foreach(var i in Enumerable.Range(0,60)) {
            var integration=snapshot with {Id=Guid.NewGuid(),IntegrationId="configured-integration",AnalyzedAt=snapshot.AnalyzedAt.AddMinutes(i+1)};
            db.IqrSourceSnapshots.Add(new() {Id=integration.Id,EnvironmentId="qa",IntegrationId=integration.IntegrationId,AnalyzedAt=integration.AnalyzedAt,EvidenceJson=JsonSerializer.Serialize(integration,Json)});
        }
        await db.SaveChangesAsync();var current=await new SecurityExpectationDiscoveryService(db).CurrentSourceAsync("qa");current!.Id.Should().Be(snapshot.Id);
    }
}
