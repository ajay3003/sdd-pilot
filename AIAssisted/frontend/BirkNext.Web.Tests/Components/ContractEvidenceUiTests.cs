using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared contract evidence on consumer pages: Source Analysis → Contracts reads decision-first counts, entry points, versions, unresolved
/// references and documentation from the stored evidence (no parser runs in the browser); Schema Studio Compare uses the shared comparison
/// engine; API Quality Review shows a snapshot's published contract only after an explicit selection, kept apart from runtime behaviour.
/// </summary>
public sealed class ContractEvidenceUiTests : BunitContext
{
    private static readonly Guid SnapshotId = Guid.Parse("5c000000-0000-0000-0000-0000000000a1");

    private static XmlSchemaElementEvidence Element(string path, decimal min = 1) => new(path, "{urn:r}" + path.Split('/').Last(), "{http://www.w3.org/2001/XMLSchema}string", min, "1", false, null, null, null, [], "sequence");

    private static SourceContract Xsd(string file, string? version, XmlSchemaElementEvidence[] elements, params XmlSchemaDependencyEvidence[] dependencies) => new()
    {
        Id = $"xsd:{file}", Type = SourceContractType.XmlSchema, Name = System.IO.Path.GetFileNameWithoutExtension(file), Version = version, File = file, Line = 1, Format = "xml",
        ParseSupport = DomainSupport.Partial, XmlSchema = new("urn:r", version, "Explicit xs:schema version attribute.", [.. elements], [], [], [.. dependencies]),
    };

    private static SourceEvidenceDomainsSnapshot Evidence()
    {
        var full = SourceEvidenceDomainsUiTests.Evidence();
        return full with
        {
            SourceSnapshotId = SnapshotId,
            Contracts = full.Contracts with
            {
                Contracts =
                [
                    Xsd("Mottak/skolenaervaer.v2.0.0.flat.m2lb.xsd", "2.0.0", [Element("Skolenaervaer"), Element("Skolenaervaer/Barn")]),
                    Xsd("Mottak/skolenaervaer.v2.0.1.flat.m2lb.xsd", "2.0.1", [Element("Skolenaervaer"), Element("Skolenaervaer/Barn")], new XmlSchemaDependencyEvidence("import", "urn:lib", "felles.xsd", false)),
                    Xsd("Mottak/felles.lib.xsd", null, []),
                    new SourceContract { Id = "openapi:api.json", Type = SourceContractType.OpenApi, Name = "Report API", Version = "1.4.0", File = "api.json", Line = 1, Format = "json",
                        Operations = [new("POST /instances", "POST", "/instances", [])] },
                ],
                Diagnostics = [new("Parse error", "broken.xsd: not a schema", "broken.xsd")],
            },
        };
    }

    [Fact]
    public void Source_analysis_contracts_are_decision_first_from_stored_evidence()
    {
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, Evidence()).Add(x => x.SourceName, "mottak.zip"));
        var overview = cut.Find("[data-testid=sd-contracts-overview]").TextContent;
        overview.Should().Contain("Contracts5").And.Contain("XSD3").And.Contain("OpenAPI1");
        cut.Find("[data-testid=sd-contracts-failed]").TextContent.Should().Be("1", "one malformed contract is counted, the others stay");
        cut.Find("[data-testid=sd-contracts-review-count]").TextContent.Should().Be("3");
        cut.Find("[data-testid=sd-contracts-version-group]").TextContent.Should().Contain("2 versions (2.0.0, 2.0.1)").And.Contain("no version is selected automatically");

        for (var i = 0; i < cut.FindAll("[data-testid=sd-contracts-group-toggle]").Count; i++) cut.FindAll("[data-testid=sd-contracts-group-toggle]")[i].Click();
        for (var i = 0; i < cut.FindAll("[data-testid=sd-contract-toggle]").Count; i++) cut.FindAll("[data-testid=sd-contract-toggle]")[i].Click();
        var meanings = cut.FindAll("[data-testid=sd-contract-meaning]").Select(m => m.TextContent).ToList();
        meanings.Should().Contain(m => m.Contains("Reusable library schema") && m.Contains("Reusable schema definitions · no message entry point detected"));
        meanings.Should().Contain(m => m.Contains("Message schema") && m.Contains("Message entry point: Skolenaervaer"));
        meanings.Should().Contain(m => m.Contains("Published API") && m.Contains("1 operation(s)"));
        cut.Find("[data-testid=sd-contract-unresolved]").TextContent.Should().Contain("Unresolved reference: import felles.xsd").And.Contain("not fetched");
        cut.FindAll("[data-testid=sd-contract-versions]").Select(v => v.TextContent).Should().Contain(v => v.Contains("1.4.0 (OpenAPI info.version (document version — not the application or deployment version))"));
        cut.FindAll("[data-testid=sd-contract-open-studio]").Select(a => a.GetAttribute("href")).Should()
            .Contain($"schema-studio?snapshot={SnapshotId}&contract=Mottak%2Fskolenaervaer.v2.0.1.flat.m2lb.xsd");
        cut.Find("[data-testid=sd-contracts-doc-text]").TextContent.Should().Contain("## Contracts — mottak.zip").And.Contain("Unresolved reference: import felles.xsd").And.NotContain("<xs:");
    }

    [Fact]
    public void Schema_studio_compare_uses_the_shared_engine_with_cautious_classes()
    {
        const string Root = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:r" elementFormDefault="qualified" version="2.0.0"><xs:element name="Report"><xs:complexType><xs:sequence><xs:element name="Id" type="xs:string"/>EXTRA</xs:sequence></xs:complexType></xs:element></xs:schema>""";
        var studio = new SchemaStudioService();
        var before = studio.Import(new Dictionary<string, string> { ["report.xsd"] = Root.Replace("EXTRA", "") }, "report");
        var required = studio.Import(new Dictionary<string, string> { ["report.xsd"] = Root.Replace("EXTRA", """<xs:element name="Child" type="xs:string"/>""") }, "report");
        var optional = studio.Import(new Dictionary<string, string> { ["report.xsd"] = Root.Replace("EXTRA", """<xs:element name="Note" type="xs:string" minOccurs="0"/>""") }, "report");

        SchemaStudioPresentation.CompareChanges(before, required).Should().ContainSingle().Which.Classification.Should().Be(ContractComparison.PotentialBreaking);
        SchemaStudioPresentation.CompareChanges(before, optional).Should().ContainSingle().Which.Classification.Should().Be(ContractComparison.NonBreakingCandidate);
        SchemaStudioPresentation.ChangeLine(SchemaStudioPresentation.CompareChanges(before, required)[0]).Should().StartWith("Potential breaking change: Added declared required element Report/Child");
        SchemaStudioPresentation.Changes(SchemaStudioPresentation.CompareChanges(before, required), before, required).Should().Be(new SchemaChangeSummary(1, 0, 0, 0, 1));
        SchemaStudioPresentation.ContractEvidence(before).Should().Be("Message schema · Message entry point: Report");
        SchemaStudioPresentation.Contract(before).Should().BeSameAs(SchemaStudioPresentation.Contract(before), "projected once per package, not per render");
    }

    [Fact]
    public void Api_quality_review_source_contracts_need_an_explicit_snapshot_and_stay_separate_from_runtime()
    {
        var older = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new SourceArchive("api-v1.zip", "a", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z"),
            EvidenceDomains = Evidence() };
        var newer = older with { Id = Guid.NewGuid(), Archive = new SourceArchive("api-v2.zip", "b", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-20T08:00:00Z") };
        var api = new Mock<IIntegrationCatalogApiService>();
        api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([newer, older]);
        Services.AddSingleton(api.Object);

        var cut = Render<SourceContractEvidencePanel>(p => p.Add(x => x.EnvironmentId, "dev"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=source-contract-snapshot]"));
        cut.Find("[data-testid=source-contract-snapshot]").GetAttribute("value").Should().BeEmpty("no snapshot — and never the newest — is selected automatically");
        cut.FindAll("[data-testid=source-contract]").Should().BeEmpty();

        cut.Find("[data-testid=source-contract-snapshot]").Change(older.Id.ToString());
        var contract = cut.Find("[data-testid=source-contract]").TextContent;
        contract.Should().Contain("Report API").And.Contain("OpenAPI").And.Contain("1.4.0 — OpenAPI info.version").And.Contain("POST /instances");
        cut.FindAll("[data-testid=source-contract]").Should().HaveCount(1, "only API contracts are shown on the API review");
        cut.Find("[data-testid=source-contract-runtime]").TextContent.Should().Contain("separate evidence");
    }
}
