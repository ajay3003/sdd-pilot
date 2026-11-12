using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.Api.Services.IntegrationSuites.Composite;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ManualReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationSuites;

/// <summary>
/// Multi-source E2E: four separately imported synthetic packages (Altinn/MU, MU/MM handoff, MM/Skolenærvær, Skolenærvær/Bufdata) are discovered per
/// stage, associated in a versioned Composite Source Context with human approvals, checked for static compatibility, generated into one composite
/// suite whose transaction plan is a Draft Contract Testing chain, approved, and executed as ONE run against a loopback integration that processes
/// asynchronously and keeps its own ledger (the oracle — never BirkNext's output). Also: missing/ambiguous sources, incompatible schema, wrong
/// correlation, timeouts, unknown outcome, partial drafts, subflows, regeneration impact and no duplicates.
/// </summary>
public sealed class CompositeSuiteTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-composite-" + Guid.NewGuid().ToString("N"));
    private readonly string _db = $"composite-{Guid.NewGuid():N}";
    private LoopbackHttpTestServer _server = null!;
    private ServiceProvider _services = null!;
    private readonly SyntheticIntegration _target = new();

    // ── Synthetic packages ──────────────────────────────────────────────────────────────────────────────────────────────────

    private const string Ns = "urn:synthetic:skolenaervaer.v2.0.1";
    private static string Xsd(string ns) => $"""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="{ns}" xmlns="{ns}" elementFormDefault="qualified"><xs:element name="Fravaer"><xs:complexType><xs:sequence><xs:element name="Periode" type="xs:string"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private static string Code(string ns) => $"namespace {ns};\npublic static class Service\n{{\n    public static string Name() => \"{ns}\";\n}}\n";

    private static string Api(string title, string paths, string schemas) =>
        $$"""{ "openapi": "3.0.3", "info": { "title": "{{title}}", "version": "1.0" }, "components": { "schemas": { {{schemas}} } }, "paths": { {{paths}} } }""";
    private static string Get(string path, string param, string schema) =>
        "\"" + path + "\": " + $$"""{ "get": { "parameters": [ { "name": "{{param}}", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/{{schema}}" } } } }, "404": { "description": "not yet" } } } }""";
    private static string Obj(string name, params string[] fields) =>
        "\"" + name + "\": { \"type\": \"object\", \"required\": [" + string.Join(",", fields.Select(f => $"\"{f}\"")) + "], \"properties\": { " + string.Join(",", fields.Select(f => $"\"{f}\": {{ \"type\": \"string\" }}")) + " } }";

    private static readonly string AltinnApi = Api("Synthetic Altinn app",
        """
        "/altinn/submissions": { "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/SubmissionRequest" } } } },
          "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Submission" } } } } } } }
        """,
        """
        "SubmissionRequest": { "type": "object", "required": ["childRef", "period", "reporterRef"], "properties": {
            "childRef": { "type": "string", "enum": ["SYN-CHILD-0001"] }, "period": { "type": "string", "enum": ["2026-10"] }, "reporterRef": { "type": "string", "enum": ["SYN-REPORTER-01"] } } },
        """ + Obj("Submission", "submissionId", "instanceId"));
    private static readonly string MuApi = Api("Synthetic MU", Get("/mu/messages/{submissionId}", "submissionId", "MuMessage"), Obj("MuMessage", "messageId", "submissionId", "status"));
    private static readonly string MmApi = Api("Synthetic MM", Get("/mm/messages/{messageId}", "messageId", "MmMessage"), Obj("MmMessage", "messageId", "reportId", "validation"));
    private static string SkoleApi(bool withRead = true) => Api("Synthetic Skoletjenesten",
        (withRead ? Get("/skole/reports/{reportId}", "reportId", "Report") + "," : "") +
        """
        "/skole/periods/{period}/lock": { "post": { "parameters": [ { "name": "period", "in": "path", "required": true, "schema": { "type": "string" } } ],
          "responses": { "200": { "description": "locked", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Lock" } } } } } } }
        """, Obj("Report", "reportId", "messageId", "period", "status") + "," + Obj("Lock", "period", "deliveryId"));
    private static readonly string PersonApi = Api("Synthetic Person", Get("/person/lookups/{reportId}", "reportId", "Lookup"), Obj("Lookup", "reportId", "found"));
    private static readonly string BufdataApi = Api("Synthetic Bufdata delivery", Get("/bufdata/deliveries/{period}", "period", "Delivery"), Obj("Delivery", "period", "deliveryId", "rows"));

    private static (string, string)[] PackageA() =>
    [
        ("Synth.Integration.sln", ""),
        ("Synth.AltinnApp/Synth.AltinnApp.csproj", Csproj), ("Synth.AltinnApp/Program.cs", Code("Synth_AltinnApp")), ("Synth.AltinnApp/openapi.json", AltinnApi),
        ("Synth.Meldingsutveksler.Service/Synth.Meldingsutveksler.Service.csproj", Csproj), ("Synth.Meldingsutveksler.Service/Program.cs", Code("Synth_Meldingsutveksler_Service")), ("Synth.Meldingsutveksler.Service/openapi.json", MuApi),
        ("Synth.Meldingsutveksler.Service/Schemas/skolenaervaer.xsd", Xsd(Ns)),
    ];
    private static (string, string)[] PackageB(string ns = Ns) =>
    [
        ("Synth.Handoff.sln", ""),
        ("Synth.MeldingsutvekslerReceiver/Synth.MeldingsutvekslerReceiver.csproj", Csproj), ("Synth.MeldingsutvekslerReceiver/Program.cs", Code("Synth_MeldingsutvekslerReceiver")), ("Synth.MeldingsutvekslerReceiver/openapi.json", MmApi),
        ("Synth.MeldingsutvekslerReceiver/Skjema/skolenaervaer.xsd", Xsd(ns)),
    ];
    private static (string, string)[] PackageC(bool withRead = true) =>
    [
        ("Synth.Skole.sln", ""),
        ("SkoleService/Synth.SkoleService.Api/Synth.SkoleService.Api.csproj", Csproj), ("SkoleService/Synth.SkoleService.Api/Program.cs", Code("Synth_SkoleService_Api")), ("SkoleService/openapi.json", SkoleApi(withRead)),
        ("Person/Synth.Person.Api/Synth.Person.Api.csproj", Csproj), ("Person/Synth.Person.Api/Program.cs", Code("Synth_Person_Api")), ("Person/openapi.json", PersonApi),
    ];
    private static (string, string)[] PackageD() =>
    [
        ("Synth.Delivery.sln", ""),
        ("Synth.Bufdata.Delivery/Synth.Bufdata.Delivery.csproj", Csproj), ("Synth.Bufdata.Delivery/Program.cs", Code("Synth_Bufdata_Delivery")), ("Synth.Bufdata.Delivery/openapi.json", BufdataApi),
    ];

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(_target.HandleAsync);
        _server.Start();
        var authored = new AuthoredTestDataStore(_dir);
        var registryStore = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AuthoredTestDataProvider(authored)], null, registryStore);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_db));
        services.AddLogging();
        services.AddScoped<IqrSourceStore>();
        services.AddScoped<IReviewSourceEvidenceProvider>(sp => new ReviewSourceEvidenceProvider(sp.GetRequiredService<IqrSourceStore>()));
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient(ContractSourcePolicy.CreateHandler())));
        services.AddSingleton(authored);
        services.AddSingleton(catalog);
        services.AddScoped<TestDataResourceService>();
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        services.AddScoped<ManualReviewDecisionStore>();
        services.AddSingleton<IIntegrationJourneyPackRegistry>(new IntegrationJourneyPackRegistry([]));
        services.AddSingleton(new IntegrationJourneyRunner([], [], TimeProvider.System));
        services.AddSingleton(registryStore);
        services.AddScoped<ContractTestDataService>();
        services.AddScoped<CompositeContextService>();
        services.AddScoped<IntegrationSuiteService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<Guid> Import(string archive, (string, string)[] files)
    {
        using var scope = _services.CreateScope();
        var bytes = ProjectImportServiceTests.Zip(files);
        var ws = IqrSourceArchiveReader.ReadDetailed(archive, bytes, default).Workspace!;
        var snapshot = await scope.ServiceProvider.GetRequiredService<IqrSourceStore>().AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, archive, bytes, ws, default);
        snapshot.Status.Should().NotBe(SourceAnalysisStatus.Failed, string.Join(" | ", snapshot.Limitations));
        return snapshot.Id;
    }

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<CompositeContextSaveResult> Save(CompositeContextSaveRequest r) => With(sp => sp.GetRequiredService<CompositeContextService>().SaveAsync(r, "Test lead", default));
    private Task<IntegrationSuiteGenerateResult> Generate(Guid context, bool partial = false) => With(sp => sp.GetRequiredService<IntegrationSuiteService>()
        .GenerateAsync(new IntegrationSuiteGenerateRequest { ScopeId = IntegrationSuiteComposer.SkoleNaervaerScope, CompositeContextId = context, AllowPartial = partial, IncludeSecurity = true }, "Tester", default));

    private CompositeRunRequest Run(params string[] stages) => new()
    {
        Stages = [.. stages], EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local", ConfirmMutatingCalls = true, Authorized = true,
    };

    private async Task<(Guid A, Guid B, Guid C, Guid D)> ImportAll() =>
        (await Import("package-a.zip", PackageA()), await Import("package-b.zip", PackageB()), await Import("package-c.zip", PackageC()), await Import("package-d.zip", PackageD()));

    private static CompositeContextSaveRequest Approved((Guid A, Guid B, Guid C, Guid D) p) => new()
    {
        ScopeId = IntegrationSuiteComposer.SkoleNaervaerScope, OwningProject = "synth-skole",
        Associations =
        [
            new(p.A, ["altinn", "mu"], CompositeApproval.Approved),
            new(p.B, ["mm"], CompositeApproval.Approved),
            new(p.C, ["skole-intake", "person-tjeneste", "period-lock"], CompositeApproval.Approved),
            new(p.D, ["bufdata"], CompositeApproval.Approved),
        ],
    };

    private async Task<CompositeRunView> WaitForRunAsync(Guid suiteId, Guid runId)
    {
        for (var i = 0; i < 300; i++)
        {
            var view = await With(sp => sp.GetRequiredService<IntegrationSuiteService>().CompositeRunAsync(suiteId, runId, default));
            if (view is not null && view.Verdict != CompositeRunVerdict.Running && view.ExecutionState is not ("Planned" or "Queued" or "Running")) return view;
            await Task.Delay(100);
        }
        throw new TimeoutException("composite run did not finish");
    }

    // ── Discovery, context, compatibility ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_maps_each_stage_to_evidence_backed_sources_across_separate_projects()
    {
        var p = await ImportAll();
        var discovery = (await With(sp => sp.GetRequiredService<CompositeContextService>().DiscoverAsync(IntegrationSuiteComposer.SkoleNaervaerScope, null, default)))!;
        CompositeStageDiscovery Stage(string id) => discovery.Stages.Single(s => s.StageId == id);
        Stage("altinn").Candidates.Should().ContainSingle(c => c.SnapshotId == p.A && c.Modules.SequenceEqual(new[] { "Synth.AltinnApp" }));
        Stage("mu").Candidates.Should().ContainSingle(c => c.SnapshotId == p.A && c.Modules.SequenceEqual(new[] { "Synth.Meldingsutveksler.Service" }),
            "MM's folder is named after MU but its exclusion words rule it out");
        Stage("mm").Candidates.Should().ContainSingle(c => c.SnapshotId == p.B);
        Stage("skole-intake").Candidates.Should().ContainSingle(c => c.SnapshotId == p.C && c.Artifacts.Any(a => a.StartsWith("OpenApi")));
        Stage("person-tjeneste").Candidates.Should().ContainSingle(c => c.SnapshotId == p.C && c.Modules.SequenceEqual(new[] { "Person" }));
        Stage("bufdata").Candidates.Should().ContainSingle(c => c.SnapshotId == p.D);
        discovery.Stages.Should().OnlyContain(s => s.Class == CompositeMatchClass.Candidate, "one project per stage, nothing approved yet");
        discovery.Stages.SelectMany(s => s.Candidates).Select(c => c.ProjectKey).Distinct().Should().HaveCount(4, "four separate projects, never merged");

        // A second project claiming the same stage makes it ambiguous; a newer snapshot of the SAME project is history, not an alternative.
        await Import("package-b-copy.zip", PackageB().Select(f => f.Item1 == "Synth.Handoff.sln" ? ("Synth.Handoff2.sln", "") : f).ToArray());
        await Import("package-c.zip", PackageC());
        var again = (await With(sp => sp.GetRequiredService<CompositeContextService>().DiscoverAsync(IntegrationSuiteComposer.SkoleNaervaerScope, null, default)))!;
        again.Stages.Single(s => s.StageId == "mm").Class.Should().Be(CompositeMatchClass.Ambiguous);
        again.Stages.Single(s => s.StageId == "skole-intake").Should().Match<CompositeStageDiscovery>(s => s.Class == CompositeMatchClass.Candidate && s.Candidates.Single().OlderSnapshots == 1);
    }

    [Fact]
    public async Task A_context_references_snapshots_records_decisions_and_reports_source_coverage_and_compatibility()
    {
        var p = await ImportAll();
        var proposed = await Save(Approved(p) with { Associations = [.. Approved(p).Associations.Select(a => a with { Approval = CompositeApproval.Proposed })] });
        proposed.Problems.Should().BeEmpty();
        var ctx = proposed.Context!;
        ctx.Revision.Should().Be(1);
        ctx.SourceCoverage!.State.Should().Be(CompositeReadiness.NeedsReview, "candidates are not approved");
        ctx.Snapshots.Should().OnlyContain(s => s.ArchiveSha256.Length == 64 && s.DecidedBy == null);
        JsonSerializer.Serialize(ctx).Should().NotContain("\"openapi\"", "the context references sources; it never copies a contract");

        var approved = (await Save(Approved(p) with { ExpectedRevision = 1 })).Context!;
        approved.Revision.Should().Be(2);
        approved.SourceCoverage!.State.Should().Be(CompositeReadiness.Ready);
        approved.Snapshots.Should().OnlyContain(s => s.Approval == CompositeApproval.Approved && s.DecidedBy == "Test lead" && s.DecidedAt != null);
        approved.Changes.Should().Contain(c => c.Contains("Proposed → Approved"));
        var muToMm = approved.Compatibility.Single(t => t.Key == "mu->mm");
        muToMm.Dimensions.Single(d => d.Name == "Message schema (XML)").State.Should().Be(TransitionCompatibility.Compatible, "both sides declare " + Ns);
        approved.Compatibility.Single(t => t.Key == "period-lock->bufdata").Dimensions.Should().Contain(d => d.Name == "Messaging channel");

        (await Save(Approved(p) with { ExpectedRevision = 1 })).Conflict.Should().BeTrue("a stale editor never overwrites a newer revision");
        (await Save(Approved(p))).Unchanged.Should().BeTrue("the same decisions are not a new revision");
        (await Save(Approved(p) with { Associations = [new(Guid.NewGuid(), ["mu"], CompositeApproval.Approved)] })).Problems.Should().ContainSingle(x => x.Contains("not found"));
        (await Save(Approved(p) with { Associations = [new(p.D, ["mu"], CompositeApproval.Approved)] })).Problems.Should().ContainSingle(x => x.Contains("manual association needs a reason"));

        // Component-level mixing: two different versions of ONE source (a second branch archive of package C) in one context are refused, never stitched.
        var c2 = await Import("package-c-branch.zip", [.. PackageC(), ("NOTES.md", "another branch")]);
        var mixed = await Save(Approved(p) with
        {
            Associations = [new(p.A, ["altinn", "mu"], CompositeApproval.Approved), new(p.B, ["mm"], CompositeApproval.Approved),
                new(p.C, ["skole-intake"], CompositeApproval.Approved), new(c2, ["person-tjeneste", "period-lock"], CompositeApproval.Approved), new(p.D, ["bufdata"], CompositeApproval.Approved)],
        });
        mixed.Context.Should().BeNull();
        mixed.Problems.Should().ContainSingle(x => x.Contains("2 different versions") && x.Contains("never combined"));
        var sameContent = await Import("package-c-rezip.zip", PackageC());
        (await Save(Approved(p) with
        {
            Associations = [new(p.A, ["altinn", "mu"], CompositeApproval.Approved), new(p.B, ["mm"], CompositeApproval.Approved),
                new(p.C, ["skole-intake"], CompositeApproval.Approved), new(sameContent, ["person-tjeneste", "period-lock"], CompositeApproval.Approved), new(p.D, ["bufdata"], CompositeApproval.Approved)],
        })).Problems.Should().NotContain(x => x.Contains("different versions"), "a content-identical re-import is the same version");
    }

    [Fact]
    public async Task An_incompatible_schema_version_blocks_source_coverage_until_accepted_with_a_reason()
    {
        var a = await Import("package-a.zip", PackageA());
        var b = await Import("package-b.zip", PackageB("urn:synthetic:skolenaervaer.v1.9.0"));
        var c = await Import("package-c.zip", PackageC());
        var d = await Import("package-d.zip", PackageD());
        var ctx = (await Save(Approved((a, b, c, d)))).Context!;
        var t = ctx.Compatibility.Single(x => x.Key == "mu->mm");
        t.Overall.Should().Be(TransitionCompatibility.Incompatible);
        t.Dimensions.Single(x => x.Name == "Message schema (XML)").Detail.Should().Contain("v2.0.1").And.Contain("v1.9.0");
        ctx.SourceCoverage!.State.Should().Be(CompositeReadiness.Blocked);
        (await Generate(ctx.Id)).Error.Should().Contain("Blocked");

        var accepted = (await Save(Approved((a, b, c, d)) with { Acceptances = [new("mu->mm", "MU transports the payload opaquely; MM validates its own version")] })).Context!;
        accepted.Compatibility.Single(x => x.Key == "mu->mm").Should().Match<CompositeTransition>(x => x.Overall == TransitionCompatibility.Incompatible && x.Accepted && x.AcceptedBy == "Test lead",
            "acceptance is recorded beside the assessment; the state is never rewritten to compatible");
        accepted.SourceCoverage!.State.Should().Be(CompositeReadiness.Ready);
    }

    // ── Generation, plan, execution ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_continuous_synthetic_transaction_is_verified_end_to_end_against_an_independent_ledger()
    {
        var p = await ImportAll();
        var ctx = (await Save(Approved(p))).Context!;
        var generated = await Generate(ctx.Id);
        generated.Error.Should().BeNull();
        var suite = generated.Suite!;
        var composite = suite.Composite!;
        composite.Sources.Select(s => s.SnapshotId).Should().BeEquivalentTo(new[] { p.A, p.B, p.C, p.D });
        suite.Contracts.Should().OnlyContain(c => c.SnapshotId != null && c.ProjectKey != null && c.Stages.Count > 0);
        suite.Contracts.Single(c => c.Stages.Contains("mu")).ProjectKey.Should().Be(suite.Contracts.Single(c => c.Stages.Contains("altinn")).ProjectKey, "package A serves both stages");
        composite.Plan.Steps.Select(s => (s.StageId, s.Role)).Should().Equal(("altinn", "Executor"), ("mu", "Observer"), ("mm", "Observer"), ("skole-intake", "Observer"),
            ("person-tjeneste", "Observer"), ("period-lock", "Action"), ("bufdata", "Observer"));
        composite.Plan.Steps.Where(s => s.FromStep != null).Select(s => $"{s.Parameter}<-{s.FromField}").Should()
            .Equal("submissionId<-submissionId", "messageId<-messageId", "reportId<-reportId", "reportId<-reportId", "period<-period", "period<-period");
        composite.Plan.State.Should().Be("Draft");
        composite.Readiness.Single(r => r.Level == "Stage execution").State.Should().Be(CompositeReadiness.Ready,
            "a data-changing step is confirmed per run and the Target Environment is chosen per run: " + string.Join(" | ", composite.Readiness.Single(r => r.Level == "Stage execution").Items));
        composite.Readiness.Single(r => r.Level == "Complete E2E").State.Should().Be(CompositeReadiness.NeedsReview, string.Join(" | ", composite.Readiness.SelectMany(r => r.Items.Prepend(r.Level + " " + r.State))));
        composite.Blockers.Should().Contain(b => b.Id == "plan:approve" && b.Owner == "Test lead");

        var service = (IServiceProvider sp) => sp.GetRequiredService<IntegrationSuiteService>();
        (await With(sp => service(sp).ExecuteCompositeAsync(suite.Id, Run(), "Tester", default))).Preflight.Problems.Should().Contain(x => x.Contains("draft"));
        _target.Ledger.Should().BeEmpty("nothing is sent before the plan is approved");
        (await With(sp => service(sp).ApprovePlanAsync(suite.Id, "Test lead", default))).Error.Should().BeNull();
        (await With(sp => service(sp).ExecuteCompositeAsync(suite.Id, Run() with { Authorized = false }, "Tester", default))).State.Should().Be("NotAuthorized");
        (await With(sp => service(sp).ExecuteCompositeAsync(suite.Id, Run() with { ConfirmMutatingCalls = false }, "Tester", default))).Preflight.MutatingOperations
            .Should().Equal("altinn: POST /altinn/submissions", "period-lock: POST /skole/periods/{period}/lock");
        (await With(sp => service(sp).ExecuteCompositeAsync(suite.Id, Run() with { IsProduction = true }, "Tester", default))).State.Should().Be("Refused");
        _target.Ledger.Should().BeEmpty();

        var started = await With(sp => service(sp).ExecuteCompositeAsync(suite.Id, Run(), "Tester", default));
        started.RunId.Should().NotBeNull(started.Message);
        started.Preflight.Kind.Should().Be(CompositeRunKind.FullE2E);
        var view = await WaitForRunAsync(suite.Id, started.RunId!.Value);
        view.Verdict.Should().Be(CompositeRunVerdict.FullE2EVerified, view.VerdictReason + " | " + string.Join(" | ", view.Stages.Select(s => $"{s.StageId} {s.State} {s.Detail}")));
        view.Correlation.Should().HaveCount(6).And.OnlyContain(l => l.State == CorrelationState.Confirmed);

        // Independent oracle: the synthetic integration's own ledger of one transaction.
        var tx = _target.Transactions.Should().ContainSingle().Subject;
        tx.Transitions.Should().Equal("submitted", "mu-received", "mm-validated", "skole-ingested", "person-looked-up", "period-locked", "bufdata-delivered");
        view.Correlation.Single(l => l.Boundary == "altinn → mu").Value.Should().Be(tx.SubmissionId);
        view.Correlation.Single(l => l.Boundary == "mu → mm").Value.Should().Be(tx.MessageId);
        view.Correlation.Single(l => l.Boundary == "mm → skole-intake").Value.Should().Be(tx.ReportId);
        _target.Ledger.Count(l => l.Method == "POST").Should().Be(2, "each data-changing call exactly once — never retried");
        _target.Ledger.Count(l => l.Path.StartsWith("/mu/", StringComparison.Ordinal)).Should().BeGreaterThan(1, "the asynchronous MU stage was observed with bounded waiting");

        var read = (await With(sp => service(sp).GetAsync(suite.Id, default)))!;
        read.Outcome.Should().Be(SuiteExecutionOutcome.Verified);
        read.Composite!.Coverage.Single(r => r.Kind == "Complete E2E").Level.Should().Be(CompositeCoverageLevel.Verified);
        read.Composite.Stages.Should().OnlyContain(s => s.Coverage == CompositeCoverageLevel.Verified);
    }

    [Fact]
    public async Task Failures_are_reported_as_what_they_are_never_as_a_pass()
    {
        var p = await ImportAll();
        var suite = (await Generate((await Save(Approved(p))).Context!.Id)).Suite!;
        Func<IServiceProvider, IntegrationSuiteService> svc = sp => sp.GetRequiredService<IntegrationSuiteService>();
        await With(sp => svc(sp).ApprovePlanAsync(suite.Id, "Test lead", default));

        // Subflow: Altinn → MU only — SUBFLOW_VERIFIED, never full E2E.
        var sub = await With(sp => svc(sp).ExecuteCompositeAsync(suite.Id, Run("altinn", "mu"), "Tester", default));
        sub.Preflight.Kind.Should().Be(CompositeRunKind.SubflowTest);
        (await WaitForRunAsync(suite.Id, sub.RunId!.Value)).Verdict.Should().Be(CompositeRunVerdict.SubflowVerified);
        (await With(sp => svc(sp).ExecuteCompositeAsync(suite.Id, Run("mm", "skole-intake"), "Tester", default))).Preflight.Problems.Should().Contain(x => x.Contains("prefix"));

        // Failed handoff: MM rejects the message → MM stage fails, later stages are blocked, never sent.
        _target.Mode = "mm-reject";
        var failed = await WaitForRunAsync(suite.Id, (await With(sp => svc(sp).ExecuteCompositeAsync(suite.Id, Run(), "Tester", default))).RunId!.Value);
        failed.Verdict.Should().Be(CompositeRunVerdict.Incomplete, failed.VerdictReason);
        failed.Stages.Single(s => s.StageId == "mm").State.Should().Be(CompositeStageState.TimedOut, "a rejected message never appears: not observed — the timeout is not proof of failure");
        failed.Stages.Single(s => s.StageId == "skole-intake").State.Should().Be(CompositeStageState.Blocked);

        // Wrong correlation: MM answers with another submission's message id.
        _target.Mode = "wrong-correlation";
        var wrong = await WaitForRunAsync(suite.Id, (await With(sp => svc(sp).ExecuteCompositeAsync(suite.Id, Run(), "Tester", default))).RunId!.Value);
        wrong.Verdict.Should().Be(CompositeRunVerdict.WrongCorrelation, string.Join(" | ", wrong.Correlation.Select(c => $"{c.Boundary} {c.State} {c.Evidence}")));

        // Unknown outcome: the submission is never answered → the data-changing call is not retried.
        _target.Mode = "submit-hangs";
        var before = _target.Ledger.Count(l => l.Method == "POST");
        var unknown = await WaitForRunAsync(suite.Id, (await With(sp => svc(sp).ExecuteCompositeAsync(suite.Id, Run(), "Tester", default))).RunId!.Value);
        unknown.Verdict.Should().Be(CompositeRunVerdict.UnknownOutcome, unknown.VerdictReason + " | " + string.Join(" | ", unknown.Stages.Select(s => $"{s.StageId} {s.State} {s.Detail}")));
        _target.Ledger.Count(l => l.Method == "POST").Should().Be(before + 1, "sent once, never repeated on an uncertain outcome");

        var read = (await With(sp => svc(sp).GetAsync(suite.Id, default)))!;
        read.Outcome.Should().NotBe(SuiteExecutionOutcome.Verified, "separate subflow runs are never combined into a full E2E result");
        read.Composite!.Coverage.Single(r => r.Kind == "Complete E2E").Level.Should().NotBe(CompositeCoverageLevel.Verified);
    }

    [Fact]
    public async Task Missing_observer_and_missing_stage_keep_the_e2e_unverifiable_and_partial_drafts_are_explicit()
    {
        var a = await Import("package-a.zip", PackageA());
        var b = await Import("package-b.zip", PackageB());
        var c = await Import("package-c.zip", PackageC(withRead: false));
        var ctx = (await Save(new CompositeContextSaveRequest
        {
            ScopeId = IntegrationSuiteComposer.SkoleNaervaerScope, OwningProject = "synth-skole",
            Associations = [new(a, ["altinn", "mu"], CompositeApproval.Approved), new(b, ["mm"], CompositeApproval.Approved), new(c, ["skole-intake", "person-tjeneste", "period-lock"], CompositeApproval.Approved)],
        })).Context!;
        ctx.SourceCoverage!.State.Should().Be(CompositeReadiness.Partial, "the Bufdata stage has no source");
        (await Generate(ctx.Id)).Error.Should().Contain("partial draft explicitly");
        var suite = (await Generate(ctx.Id, partial: true)).Suite!;
        suite.Composite!.PartialDraft.Should().BeTrue();
        suite.Status.Should().Be(SuiteStatus.Draft);
        suite.Composite.Stages.Single(s => s.StageId == "skole-intake").Observer.Should().Be("NOT_OBSERVABLE", "no read contract for the report");
        suite.Composite.Plan.Missing.Should().Contain(m => m.Contains("Skoletjenesten ingestion") && m.Contains("NOT_OBSERVABLE"));
        suite.Composite.Readiness.Single(r => r.Level == "Complete E2E").State.Should().NotBe(CompositeReadiness.Ready);
        suite.Composite.Blockers.Should().Contain(x => x.Id == "source:bufdata" && x.Severity == "Blocking");
        suite.Composite.BufdataDelivery.Should().StartWith("SPECIFIED_BUT_NOT_RUNTIME_VERIFIED");
    }

    [Fact]
    public async Task A_changed_source_snapshot_is_proposed_with_its_stages_and_regeneration_continues_without_duplicates()
    {
        var p = await ImportAll();
        var ctx = (await Save(Approved(p))).Context!;
        var first = (await Generate(ctx.Id)).Suite!;
        (await Generate(ctx.Id)).Reused.Should().BeTrue("unchanged inputs give the same revision");
        var contractsBefore = await With(sp => sp.GetRequiredService<AppDbContext>().ApiContracts.CountAsync());
        var chainsBefore = await With(sp => sp.GetRequiredService<AppDbContext>().ContractTestChains.Select(x => x.ChainId).Distinct().CountAsync());

        var c2 = await Import("package-c.zip", PackageC().Select(f => f.Item1 == "Person/openapi.json" ? (f.Item1, Api("Synthetic Person", Get("/person/lookups/{reportId}", "reportId", "Lookup"), Obj("Lookup", "reportId", "found", "checkedAt"))) : f).ToArray());
        var current = (await With(sp => sp.GetRequiredService<CompositeContextService>().GetAsync(ctx.Id, default)))!;
        current.NewerSources.Should().ContainSingle(n => n.NewerSnapshotId == c2 && n.AffectedStages.SequenceEqual(new[] { "skole-intake", "person-tjeneste", "period-lock" }));
        var next = (await Save(Approved((p.A, p.B, c2, p.D)))).Context!;
        next.Revision.Should().Be(2);
        next.Changes.Should().Contain(x => x.Contains("snapshot") && x.Contains("→"));

        var regenerated = (await Generate(next.Id)).Suite!;
        regenerated.SuiteKey.Should().Be(first.SuiteKey);
        regenerated.Revision.Should().Be(2);
        regenerated.Drift.Should().Contain(d => d.StartsWith("Person/openapi.json") && d.Contains("v1 → v2"), string.Join(" | ", first.Contracts.Concat(regenerated.Contracts).Select(c => $"{c.SourceFile} {c.ContractKey} v{c.ContractVersion} {c.ImportOutcome} {c.ProjectKey}")));
        (await With(sp => sp.GetRequiredService<AppDbContext>().ApiContracts.CountAsync())).Should().Be(contractsBefore, "a changed source continues its contract (next version), never a second contract");
        var person = regenerated.Composite!.Plan.Steps.Single(s => s.StageId == "person-tjeneste");
        person.TestId.Should().NotBeNull("the positive test still bound to v1 is used; contract evolution never rebinds it silently");
        regenerated.Composite.Stages.Single(s => s.StageId == "person-tjeneste").WhyNotVerified.Should().NotContain("has no positive contract test");
        IntegrationSuiteService.VersionOf("synthetic-person@v12", "synthetic-person").Should().Be(12);
        IntegrationSuiteService.VersionOf("synthetic-person-x@v1", "synthetic-person").Should().BeNull();
        (await With(sp => sp.GetRequiredService<IntegrationSuiteService>().GetAsync(first.Id, default)))!.Composite!.Sources.Should().Contain(s => s.SnapshotId == p.C, "history keeps the exact source versions");
        _ = chainsBefore;
    }

    [Fact]
    public void Associating_or_approving_needs_the_write_policy_and_running_needs_the_execute_policy()
    {
        string? Policy(string action) => typeof(BirkNext.Api.Controllers.IntegrationSuitesController).GetMethod(action)!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().SingleOrDefault()?.Policy;
        Policy("SaveContext").Should().Be(BirkNextPermissions.IntegrationConfigurationWritePolicy, "an association never grants access by itself");
        Policy("ApprovePlan").Should().Be(BirkNextPermissions.IntegrationConfigurationWritePolicy);
        Policy("RunComposite").Should().Be(BirkNextPermissions.ActiveEventExecutePolicy);
        Policy("Discover").Should().BeNull("discovery is read-only");
        Policy("Preflight").Should().BeNull("a preflight sends nothing");
    }

    // ── Pure rules ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Real_shaped_namespaces_of_MU_and_MM_are_incompatible_and_infrastructure_is_only_supporting()
    {
        CompositeSourceRules.Family("https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1.flat").Should().Be("https://bufdir.no/barnevern.tiltak.skolenaervaer");
        CompositeSourceRules.SnapshotFacts F(string project, Dictionary<string, List<string>> units, params (string Module, string Ns)[] xsd) => new()
        {
            Id = Guid.NewGuid(), ProjectKey = project, ArchiveFileName = project + ".zip", ArchiveSha256 = new string('a', 64), AnalyzedAt = DateTimeOffset.UnixEpoch, Status = "Ready",
            Units = new(units, StringComparer.OrdinalIgnoreCase), HasApplicationCode = units.Count > 0, HasInfrastructure = units.Count == 0,
            Contracts = [.. xsd.Select(x => new CompositeSourceRules.ContractFact(Guid.NewGuid().ToString(), $"{x.Module}/s.xsd", x.Module, "s", SourceContractType.XmlSchema, x.Ns, [], [], [], []))],
        };
        var tiltak = F("tiltak-2", new() { ["Buf.Meldingsutveksler.Service"] = ["Buf.Meldingsutveksler.Service"], ["Buf.Meldingsutveksler.TestData"] = ["Buf.Meldingsutveksler.TestData"] },
            ("Buf.Meldingsutveksler.TestData", "https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0.flat"), ("Buf.Meldingsutveksler.TestData", "https://bufdir.no/barnevern.tiltak.skolenaervaer.v0.8.2.flat"));
        var m2lb = F("m2lb", new() { ["MeldingsutvekslerReceiver"] = ["MeldingsutvekslerReceiver", "M2LB.MeldingsutvekslerReceiver.Meldingsutveksler"], ["SkoleService"] = ["SkoleService"] },
            ("MeldingsutvekslerReceiver", "https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1.flat"));
        var iac = F("terraform-iac-m2lb", []);
        var scope = IntegrationSuiteComposer.Scope(IntegrationSuiteComposer.SkoleNaervaerScope)!;
        var discovery = CompositeSourceRules.Discover(scope, [tiltak, m2lb, iac], null);
        discovery.Stages.Single(s => s.StageId == "mu").Candidates.Should().ContainSingle(c => c.ProjectKey == "tiltak-2", "M2LB's MM project named after MU is excluded");
        discovery.Stages.Single(s => s.StageId == "mm").Candidates.Should().ContainSingle(c => c.ProjectKey == "m2lb");
        discovery.Stages.Single(s => s.StageId == "altinn").Should().Match<CompositeStageDiscovery>(s => s.Class == CompositeMatchClass.Confirmed && s.Candidates.Count == 0 && s.ReferenceSource != null);
        discovery.Supporting.Should().ContainSingle(c => c.ProjectKey == "terraform-iac-m2lb" && c.Kind == CompositeSourceKind.Infrastructure);
        var failedIac = F("terraform-iac-meldingsutveksler", []) with { Status = "Failed" };
        var withFailed = CompositeSourceRules.Discover(scope, [tiltak, m2lb, iac, failedIac], null);
        withFailed.Supporting.Should().ContainSingle(c => c.ProjectKey == "terraform-iac-meldingsutveksler" && c.Kind == CompositeSourceKind.Infrastructure && c.AnalysisStatus == "Failed",
            "an archive that failed analysis only for lack of application code is still supporting evidence");
        withFailed.Stages.SelectMany(s => s.Candidates).Should().NotContain(c => c.ProjectKey == "terraform-iac-meldingsutveksler");

        // Baseline correction: two versions of M2LB with different content and no analysis baseline → neither is a candidate (import order never
        // decides); with a baseline exactly that version is; a content-identical re-import is one version.
        var m2lbNorsk = m2lb with { Id = Guid.NewGuid(), ArchiveFileName = "M2LB (5).zip", ContentKey = "c5", AnalyzedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var m2lbEn = m2lb with { ContentKey = "c7", AnalyzedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var unresolved = CompositeSourceRules.Discover(scope, [tiltak, m2lbEn, m2lbNorsk, iac], null, new Dictionary<string, Guid>());
        unresolved.Stages.SelectMany(s => s.Candidates).Should().NotContain(c => c.ProjectKey == "m2lb", "BASELINE_NOT_SELECTED: no M2LB version is a candidate");
        unresolved.Limitations.Should().Contain(l => l.StartsWith("BASELINE_NOT_SELECTED: m2lb (2 versions)"));
        var chosen = CompositeSourceRules.Discover(scope, [tiltak, m2lbEn, m2lbNorsk, iac], null, new Dictionary<string, Guid> { ["m2lb"] = m2lbEn.Id });
        chosen.Stages.Single(s => s.StageId == "mm").Candidates.Should().ContainSingle(c => c.SnapshotId == m2lbEn.Id && c.OlderSnapshots == 1);
        var rezip = m2lbEn with { Id = Guid.NewGuid(), ArchiveFileName = "M2LB.zip", ArchiveSha256 = new string('b', 64), AnalyzedAt = DateTimeOffset.UnixEpoch.AddDays(3) };
        CompositeSourceRules.Discover(scope, [tiltak, m2lbEn, rezip, iac], null, new Dictionary<string, Guid>())
            .Stages.Single(s => s.StageId == "mm").Candidates.Should().ContainSingle(c => c.SnapshotId == rezip.Id, "identical content needs no decision");

        var sides = new Dictionary<string, CompositeSourceRules.Side>
        {
            ["mu"] = new("mu", [(tiltak, ["Buf.Meldingsutveksler.Service", "Buf.Meldingsutveksler.TestData"], true)], null),
            ["mm"] = new("mm", [(m2lb, ["MeldingsutvekslerReceiver"], true)], null),
        };
        var t = CompositeSourceRules.Compatibility(scope, sides, [], null).Single(x => x.Key == "mu->mm");
        t.Dimensions.Single(d => d.Name == "Message schema (XML)").State.Should().Be(TransitionCompatibility.Incompatible);
        t.Overall.Should().Be(TransitionCompatibility.Incompatible);
        t.Dimensions.Single(d => d.Name == "Authentication").State.Should().Be(TransitionCompatibility.NotAssessed, "never inferred from source");
    }

    // ── Synthetic integration with its own ledger (the oracle) ──────────────────────────────────────────────────────────────

    private sealed class SyntheticIntegration
    {
        public sealed class Tx
        {
            public string SubmissionId = "", MessageId = "", ReportId = "", Period = "", DeliveryId = "";
            public DateTimeOffset Submitted;
            public List<string> Transitions = [];
        }
        public volatile string Mode = "normal";
        public ConcurrentQueue<(string Method, string Path)> Ledger { get; } = new();
        public ConcurrentBag<Tx> Transactions { get; } = [];
        private const int HopMs = 600;

        public async Task HandleAsync(System.Net.HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath;
            Ledger.Enqueue((c.Request.HttpMethod, path));
            var now = DateTimeOffset.UtcNow;
            object? body = null; var status = 404;
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Tx? Find(Func<Tx, bool> f) => Transactions.FirstOrDefault(f);
            bool Ready(Tx t, int hops) => now - t.Submitted >= TimeSpan.FromMilliseconds(HopMs * hops);
            void Mark(Tx t, string transition) { lock (t.Transitions) if (!t.Transitions.Contains(transition)) t.Transitions.Add(transition); }
            if (c.Request.HttpMethod == "POST" && path == "/altinn/submissions")
            {
                if (Mode == "submit-hangs") { await Task.Delay(16000); c.Response.Abort(); return; }
                var tx = new Tx { SubmissionId = Guid.NewGuid().ToString(), MessageId = "msg-" + Guid.NewGuid().ToString("N")[..12], ReportId = "rep-" + Guid.NewGuid().ToString("N")[..12], Period = "2026-10", Submitted = now };
                Transactions.Add(tx); Mark(tx, "submitted");
                status = 201; body = new { submissionId = tx.SubmissionId, instanceId = "inst-" + tx.SubmissionId[..8] };
            }
            else if (seg is ["mu", "messages", var sid] && Find(t => t.SubmissionId == sid) is { } mu && Ready(mu, 1))
            { Mark(mu, "mu-received"); status = 200; body = new { messageId = mu.MessageId, submissionId = mu.SubmissionId, status = "Stored" }; }
            else if (seg is ["mm", "messages", var mid] && Find(t => t.MessageId == mid) is { } mm && Ready(mm, 2) && Mode != "mm-reject")
            { Mark(mm, "mm-validated"); status = 200; body = new { messageId = Mode == "wrong-correlation" ? "msg-other" : mm.MessageId, reportId = mm.ReportId, validation = "Valid" }; }
            else if (seg is ["skole", "reports", var rid] && Find(t => t.ReportId == rid) is { } sk && Ready(sk, 3))
            { Mark(sk, "skole-ingested"); status = 200; body = new { reportId = sk.ReportId, messageId = sk.MessageId, period = sk.Period, status = "Received" }; }
            else if (seg is ["person", "lookups", var prid] && Find(t => t.ReportId == prid) is { } pe && pe.Transitions.Contains("skole-ingested"))
            { Mark(pe, "person-looked-up"); status = 200; body = new { reportId = pe.ReportId, found = "true" }; }
            else if (c.Request.HttpMethod == "POST" && seg is ["skole", "periods", var period, "lock"] && Find(t => t.Period == period && t.Transitions.Contains("person-looked-up") && t.DeliveryId == "") is { } lk)
            { lk.DeliveryId = "del-" + Guid.NewGuid().ToString("N")[..10]; lk.Submitted = now - TimeSpan.FromMilliseconds(HopMs * 3); Mark(lk, "period-locked"); status = 200; body = new { period, deliveryId = lk.DeliveryId }; }
            else if (seg is ["bufdata", "deliveries", var dp] && Find(t => t.Period == dp && t.DeliveryId != "") is { } bd && Ready(bd, 4))
            { Mark(bd, "bufdata-delivered"); status = 200; body = new { period = bd.Period, deliveryId = bd.DeliveryId, rows = "1" }; }
            c.Response.StatusCode = status; c.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body is null ? """{"error":"not found"}""" : JsonSerializer.Serialize(body));
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }
}
