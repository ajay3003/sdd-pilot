using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Automatic Test Data XML fixture draft after a Project Import: from the snapshot that import produced, over the real Skolenærvær XSD, with the child
/// bound by alias (never the reporter), schema-valid, saved as a DRAFT version of the canonical fixture — and skipped with its reason whenever a
/// person must decide (no project, a new application, an ambiguous or absent XSD, a mapping without an approved asset, already generated).
/// </summary>
public sealed class XmlFixtureAutoDraftTests
{
    private const string Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";
    private const string XsdFile = "Buf.Meldingsutveksler.TestData/TestData/xsds/v3/xsd/" + Contract + ".xsd";
    private const string ChildPath = "Klient.Identifikator.Fodselsnummer";
    private static readonly Guid Imported = Guid.Parse("953d0e55-077a-42fb-94cf-5b1aaa8fc202");

    private static string RealSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", Contract + ".xsd"));
    }

    private static IntegrationTemplateSchemaCandidate Xsd(Guid snapshot, string file = XsdFile) =>
        new(snapshot, "Tiltak 2 - Sikkert digitalt økosystem.zip", Contract, file) { MatchesContract = true, Version = "2.0.0" };

    private static IntegrationTemplateView Context(params IntegrationTemplateSchemaCandidate[] xsds) =>
        new("skolenaervaer-report-intake", "Report intake", "", IntegrationKind.HttpApi, "skolenaervaer", "Skolenærvær Testing")
        {
            SchemaName = Contract, SchemaCandidates = xsds, CanonicalFixtureAlias = "skolenaervaer-valid-report", FixtureCapabilities = ["skolenaervaer-report"],
            FixtureBindings = [new("Child", "child", ChildPath, "The child the report is about."), new("Reporter", "reporter", null, "Submits the report as the Altinn actor; not part of the XML.")],
        };

    private static TestDataAssetSummary Identity(string alias, string capability) => new()
    {
        Id = $"altinn-local:{capability}:{alias}", Alias = alias, Label = alias, Kind = TestDataKind.EntityReference, Capabilities = ["person", capability],
        Approval = TestDataApproval.Approved, Sensitivity = TestDataSensitivity.Sensitive, IsDefault = true,
    };

    private static readonly List<TestDataAssetSummary> Defaults = [Identity("skolenaervaer-reporter-default", "reporter"), Identity("skolenaervaer-child-default", "child")];

    private sealed class Backend
    {
        public List<TestDataXmlBindRequest> Binds { get; } = [];
        public List<string> Saved { get; } = [];
    }

    private static (IServiceProvider Services, Backend Backend) Setup(IntegrationTemplateView context, List<TestDataAssetSummary>? assets = null, bool project = true)
    {
        var backend = new Backend();
        var services = new ServiceCollection();
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>([context], null));
        services.AddSingleton(journeys.Object);
        var text = RealSchema();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var schemas = new Mock<ISourceSchemaApiService>();
        schemas.Setup(s => s.SourceSchemasAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new SourceSchemaPackage(id, "fp", "Tiltak 2.zip", DateTimeOffset.UnixEpoch, true, 0, [new SourceSchemaFile(XsdFile, text, fingerprint)]));
        services.AddSingleton(schemas.Object);
        var testData = new Mock<ITestDataApiService>();
        testData.Setup(t => t.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataCatalogView(assets ?? Defaults, [], []), (string?)null));
        testData.Setup(t => t.BindXmlFixtureAsync(It.IsAny<TestDataXmlBindRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((TestDataXmlBindRequest request, CancellationToken _) =>
        {
            backend.Binds.Add(request);
            return (System.Text.RegularExpressions.Regex.Replace(request.Xml, "<Fodselsnummer>[^<]*</Fodselsnummer>", "<Fodselsnummer>04061076692</Fodselsnummer>"), []);
        });
        testData.Setup(t => t.SaveCanonicalFixtureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string json, CancellationToken _) =>
        {
            backend.Saved.Add(json);
            return (backend.Saved.Count + 1, (string?)null);
        });
        services.AddSingleton(testData.Object);
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", Project = project ? new EnvironmentProject { ProjectName = "M2LB" } : null };
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = profile });
        services.AddSingleton(contexts.Object);
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.SetupGet(s => s.ActiveProfile).Returns(profile);
        services.AddSingleton(settings.Object);
        return (services.BuildServiceProvider(), backend);
    }

    [Fact]
    public async Task The_imported_snapshot_gives_a_schema_valid_draft_with_the_child_bound_by_alias_and_no_reporter()
    {
        var (services, backend) = Setup(Context(Xsd(Imported), Xsd(Guid.NewGuid())));
        var outcome = (await XmlFixtureAutoDraft.RunAsync(services, Imported, intoCurrentApplication: true)).Single();

        outcome.Created.Should().BeTrue(outcome.Message);
        outcome.Version.Should().Be(2);
        outcome.Message.Should().Contain("Draft version 2 of skolenaervaer-valid-report").And.Contain(Contract + ".xsd").And.Contain("version 2.0.0")
            .And.Contain("snapshot 953d0e55").And.Contain("approved version stays in use");
        backend.Binds.Should().ContainSingle().Which.Bindings.Should().Equal([new TestDataXmlBinding("skolenaervaer-child-default", ChildPath)],
            "the child is filled in by the backend from Test Data; the reporter submits and is not in the XML");
        var saved = backend.Saved.Single();
        var facts = System.Text.Json.JsonDocument.Parse(saved).RootElement.GetProperty("testData");
        facts.GetProperty("alias").GetString().Should().Be("skolenaervaer-valid-report");
        facts.GetProperty("approved").GetBoolean().Should().BeFalse("a draft; approval is a person's step in Test Data");
        facts.GetProperty("facts").GetProperty("sourceSelection").GetString().Should().StartWith("Automatic after Project Import · the imported snapshot");
        saved.Should().Contain(Imported.ToString(), "the fixture records the exact snapshot it was generated from");
        saved.Should().NotContain("05859497324", "the reporter's identifier is never in the fixture");
    }

    [Fact]
    public async Task It_skips_with_a_reason_when_a_person_must_decide()
    {
        // A new application: not this environment's evidence.
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported))).Services, Imported, intoCurrentApplication: false)).Single()
            .Should().Match<XmlFixtureAutoDraftOutcome>(o => !o.Created && o.Message.Contains("new application"));
        // No Target Environment → Project binding.
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported)), project: false).Services, Imported, true)).Single().Message.Should().Contain("bound to a project");
        // The import's snapshot does not declare the contract.
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Guid.NewGuid()))).Services, Imported, true)).Single().Message.Should().Contain("declares no XSD named as the contract");
        // Two files named as the contract in the same snapshot: a decision, never a guess.
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported), Xsd(Imported, "other/" + Contract + ".xsd"))).Services, Imported, true)).Single()
            .Message.Should().Contain("needs a decision");
        // No approved child.
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported)), [Identity("skolenaervaer-reporter-default", "reporter")]).Services, Imported, true)).Single()
            .Message.Should().Contain("No approved Test Data asset for " + ChildPath);
        // Already generated from this snapshot (a re-import of the same bytes reuses the snapshot).
        var existing = new TestDataAssetSummary
        {
            Id = "schema-studio:skolenaervaer-valid-report", Alias = "skolenaervaer-valid-report", Kind = TestDataKind.Structured, MediaType = "application/xml",
            Schema = new TestDataSchemaReference(XsdFile, Imported),
        };
        var (services, backend) = Setup(Context(Xsd(Imported)), [.. Defaults, existing]);
        (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single().Message.Should().Contain("already has a version generated from this snapshot");
        backend.Saved.Should().BeEmpty();
    }

    // ── NEXT58A: visible skip kinds, failures kept and retryable, idempotent retry ───────────────────────────────────────────────

    [Fact]
    public async Task Skips_are_classified_so_only_technical_failures_offer_retry()
    {
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported))).Services, Imported, intoCurrentApplication: false)).Single().Kind.Should().Be(XmlFixtureAutoDraftKind.NotApplicable);
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported), Xsd(Imported, "other/" + Contract + ".xsd"))).Services, Imported, true)).Single()
            .Should().Match<XmlFixtureAutoDraftOutcome>(o => o.Kind == XmlFixtureAutoDraftKind.NeedsDecision && !o.Retryable);
        (await XmlFixtureAutoDraft.RunAsync(Setup(Context(Xsd(Imported)), [Identity("skolenaervaer-reporter-default", "reporter")]).Services, Imported, true)).Single()
            .Kind.Should().Be(XmlFixtureAutoDraftKind.NeedsDecision);
        XmlFixtureAutoDraftOutcome.Label(XmlFixtureAutoDraftKind.Failed).Should().Be("Failed");
    }

    [Fact]
    public async Task A_package_whose_snapshot_lacks_the_contract_never_yields_a_created_fixture()
    {
        // Stands in for the unavailable Tiltak 2 package: without its XSD in the imported snapshot nothing is created, and nothing claims it was.
        var (services, backend) = Setup(Context(Xsd(Guid.NewGuid())));
        var outcome = (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single();
        outcome.Should().Match<XmlFixtureAutoDraftOutcome>(o => !o.Created && o.Kind == XmlFixtureAutoDraftKind.NotApplicable && o.Version == null);
        backend.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_catalog_creates_nothing_keeps_the_failure_and_a_retry_is_idempotent()
    {
        var (services, backend) = Setup(Context(Xsd(Imported)));
        var testData = Mock.Get(services.GetRequiredService<ITestDataApiService>());
        testData.Setup(t => t.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((TestDataCatalogView?)null, "502 Bad Gateway"));
        var failed = (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single();
        failed.Should().Match<XmlFixtureAutoDraftOutcome>(o => o.Kind == XmlFixtureAutoDraftKind.Failed && o.Retryable && o.Message.Contains("502 Bad Gateway") && o.Message.Contains("no duplicate draft"));
        backend.Saved.Should().BeEmpty("without the catalog it is unknown whether a draft exists, so nothing is generated");

        // Retry (no reimport): the catalog is reachable again → the draft is created once.
        var catalog = new List<TestDataAssetSummary>(Defaults);
        testData.Setup(t => t.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => (new TestDataCatalogView(catalog, [], []), (string?)null));
        (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single().Kind.Should().Be(XmlFixtureAutoDraftKind.Created);
        backend.Saved.Should().HaveCount(1);

        // Retrying again after the catalog lists the draft: already generated from this snapshot — no second draft.
        catalog.Add(new TestDataAssetSummary
        {
            Id = "schema-studio:skolenaervaer-valid-report", Alias = "skolenaervaer-valid-report", Kind = TestDataKind.Structured, MediaType = "application/xml",
            Schema = new TestDataSchemaReference(XsdFile, Imported), DraftVersion = 2,
        });
        var again = (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single();
        again.Should().Match<XmlFixtureAutoDraftOutcome>(o => o.Kind == XmlFixtureAutoDraftKind.AlreadyCurrent && !o.Retryable && o.Message.Contains("draft v2 awaiting approval"));
        backend.Saved.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_refused_save_or_unreadable_test_packs_stay_visible_as_retryable_failures()
    {
        var (services, backend) = Setup(Context(Xsd(Imported)));
        Mock.Get(services.GetRequiredService<ITestDataApiService>()).Setup(t => t.SaveCanonicalFixtureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((int?)null, "409 conflict"));
        (await XmlFixtureAutoDraft.RunAsync(services, Imported, true)).Single()
            .Should().Match<XmlFixtureAutoDraftOutcome>(o => o.Kind == XmlFixtureAutoDraftKind.Failed && o.Message.Contains("409 conflict"));

        var (unreadable, _) = Setup(Context(Xsd(Imported)));
        Mock.Get(unreadable.GetRequiredService<IIntegrationJourneysApiService>())
            .Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        (await XmlFixtureAutoDraft.RunAsync(unreadable, Imported, true)).Single()
            .Should().Match<XmlFixtureAutoDraftOutcome>(o => o.Kind == XmlFixtureAutoDraftKind.Failed && o.Retryable && o.Message.Contains("test packs could not be read"));
    }
}
