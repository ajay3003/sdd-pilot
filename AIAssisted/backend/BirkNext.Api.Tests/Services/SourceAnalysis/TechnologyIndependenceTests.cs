using System.IO.Compression;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Applicability;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Technology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Project/technology independence: a project whose stack BirkNext only partly understands is inventoried honestly (unsupported technologies
/// are reported, never dropped and never scored), reviews get an applicability state instead of a failure, and tool coverage stays apart from
/// project quality. Fixtures are generic and fictional: PaymentHub (Java/Spring/Kafka/AWS/GitHub Actions/React), LegacyClaims (.NET Framework/
/// SOAP/Oracle/Jenkins/ActiveMQ), DataLakeIngestion (Python/Airflow/S3, no frontend/API), UnknownTech and a document-only project.
/// </summary>
public sealed class TechnologyIndependenceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
        return buffer.ToArray();
    }

    internal static byte[] PaymentHub() => Zip(
        ("PaymentHub/pom.xml", "<project><groupId>example.payments</groupId><artifactId>payment-hub</artifactId><parent><artifactId>spring-boot-starter-parent</artifactId></parent></project>"),
        ("PaymentHub/src/main/java/example/payments/PaymentController.java", "package example.payments;\n@RestController\npublic class PaymentController { }"),
        ("PaymentHub/src/main/java/example/payments/PaymentEvents.java", "package example.payments;\npublic class PaymentEvents { }"),
        ("PaymentHub/src/main/resources/application.yml", "spring:\n  application:\n    name: payment-hub\n  kafka:\n    bootstrap-servers: kafka.example.test:9092\n  datasource:\n    url: jdbc:postgresql://db.example.test/payments\n"),
        ("PaymentHub/.github/workflows/ci.yml", "name: ci\non:\n  push:\n    branches: [main]\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: mvn -B verify\n"),
        ("PaymentHub/infra/main.tf", "provider \"aws\" {\n  region = \"eu-north-1\"\n}\nresource \"aws_s3_bucket\" \"receipts\" {\n  bucket = \"receipts\"\n}\n"),
        ("PaymentHub/Dockerfile", "FROM eclipse-temurin:21-jre\nCOPY target/app.jar /app.jar\n"),
        ("PaymentHub/web/package.json", "{\"name\":\"payment-web\",\"dependencies\":{\"react\":\"18.3.1\"}}"),
        ("PaymentHub/web/src/App.tsx", "export const App = () => null;"));

    internal static byte[] LegacyClaims() => Zip(
        ("LegacyClaims/Claims.Web/Claims.Web.csproj", "<Project ToolsVersion=\"15.0\"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType></PropertyGroup>"
            + "<ItemGroup><Reference Include=\"System.ServiceModel\" /><Reference Include=\"Oracle.ManagedDataAccess\" /></ItemGroup><ItemGroup><Compile Include=\"ClaimService.cs\" /></ItemGroup></Project>"),
        ("LegacyClaims/Claims.Web/ClaimService.cs", "namespace Claims.Web { public class ClaimService { public string Get(int id) => id.ToString(); } }"),
        ("LegacyClaims/Claims.Web/ClaimService.svc", "<%@ ServiceHost Service=\"Claims.Web.ClaimService\" %>"),
        ("LegacyClaims/contracts/ClaimService.wsdl", "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" name=\"ClaimService\"></definitions>"),
        ("LegacyClaims/db/schema.sql", "CREATE TABLE CLAIM (ID NUMBER(10) PRIMARY KEY, STATUS VARCHAR2(20) NOT NULL);"),
        ("LegacyClaims/db/pkg_claims.pkb", "CREATE OR REPLACE PACKAGE BODY PKG_CLAIMS AS END PKG_CLAIMS;"),
        ("LegacyClaims/config/broker.properties", "broker.url=tcp://activemq.example.test:61616\nqueue.claims=CLAIMS.IN\n"),
        ("LegacyClaims/Jenkinsfile", "pipeline { agent any\n stages { stage('Build') { steps { bat 'msbuild Claims.sln' } } } }"));

    internal static byte[] DataLakeIngestion() => Zip(
        ("DataLakeIngestion/dags/ingest_daily.py", "from airflow import DAG\n"),
        ("DataLakeIngestion/dags/transform.py", "def transform(): pass\n"),
        ("DataLakeIngestion/requirements.txt", "apache-airflow==2.9.0\nboto3==1.34.0\n"),
        ("DataLakeIngestion/config/settings.yaml", "landing: s3://example-landing/raw\nschedule: daily\n"),
        ("DataLakeIngestion/infra/main.tf", "provider \"aws\" {\n  region = \"eu-north-1\"\n}\nresource \"aws_s3_bucket\" \"landing\" {\n  bucket = \"example-landing\"\n}\n"));

    internal static byte[] UnknownTech() => Zip(("UnknownTech/main.zig", "pub fn main() void {}"), ("UnknownTech/build.zig", "const std = @import(\"std\");"), ("UnknownTech/README.md", "# Unknown"));

    private async Task<IqrSourceSnapshot> Analyze(string name, byte[] bytes)
    {
        var (snapshot, error) = await new IqrSourceStore(_db).AnalyzeAsync("env", IqrSourceStore.SourceAnalysisOwner, name, bytes);
        error.Should().BeNull();
        return snapshot!;
    }

    private static ProjectApplicabilityInput Input(IqrSourceSnapshot? snapshot, Action<ProjectApplicabilityInputBuilder>? configure = null)
    {
        var b = new ProjectApplicabilityInputBuilder();
        configure?.Invoke(b);
        return new ProjectApplicabilityInput
        {
            HasSourceSnapshot = snapshot is not null, Technologies = snapshot?.TechnologyCoverage?.Technologies ?? [], Capabilities = snapshot?.TechnologyCoverage?.Capabilities ?? [],
            HasBrowserTarget = b.Target, HasApiTarget = b.Target, HasRequirements = b.Requirements, ConfiguredIntegrations = b.Integrations, DomainExtensions = b.Extensions,
        };
    }

    internal sealed class ProjectApplicabilityInputBuilder
    {
        public bool Target { get; set; }
        public bool Requirements { get; set; }
        public List<string> Integrations { get; } = [];
        public List<string> Extensions { get; } = [];
    }

    private static IEnumerable<string> Ids(IqrSourceSnapshot s) => s.TechnologyCoverage!.Technologies.Select(t => t.TechnologyId);

    // ── PaymentHub ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PaymentHub_UnsupportedTechnologiesAreInventoried_NotDropped()
    {
        var s = await Analyze("PaymentHub.zip", PaymentHub());
        Ids(s).Should().Contain(["lang.java", "lang.typescript", "framework.spring", "integration.kafka", "dependency.maven", "dependency.npm", "dependency.docker",
            "pipeline.github-actions", "cloud.aws", "db.postgresql", "framework.browser-frontend"]);
        s.TechnologyCoverage!.UnsupportedSourceFiles.Should().Be(3, "two .java files and one .tsx file are detected but not analyzed");
        s.TechnologyCoverage.Technologies.Single(t => t.TechnologyId == "framework.spring").Confidence.Should().Be(DetectionConfidence.Inferred, "a parent pom is not a confirmed Spring architecture");
        s.TechnologyCoverage.Limitations.Should().Contain(l => l.StartsWith("Java detected") && l.Contains("tool limitation, not a project finding"));
        s.TechnologyCoverage.Technologies.SelectMany(t => t.Evidence).Should().NotContain(e => e.Contains("bootstrap-servers") || e.Contains("jdbc:"), "evidence is file names, never content");
    }

    [Fact]
    public async Task PaymentHub_ArchitectureIsUnsupported_NamingTheProvider_NotAFailure()
    {
        var s = await Analyze("PaymentHub.zip", PaymentHub());
        s.Architecture!.Status.Should().Be(ArchitectureStatus.Unsupported);
        s.Architecture.Limitations[0].Should().Contain("source.architecture.dotnet").And.Contain("tool limitation");
        s.Architecture.Components.Should().BeEmpty("no Java/Spring architecture is invented");
    }

    [Fact]
    public async Task PaymentHub_ApplicabilityIsHonestPerReview()
    {
        var s = await Analyze("PaymentHub.zip", PaymentHub());
        var all = ApplicabilityEvaluator.EvaluateAll(Input(s, b => b.Integrations.Add("integration.kafka")));
        all["source-analysis"].Status.Should().Be(ApplicabilityStatus.Unsupported);
        all["source-analysis"].Reason.Should().Contain("Java").And.Contain("still read");
        all["dependency-review"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable);
        all["dependency-review"].Reason.Should().Contain("Maven");
        all["dependency-review"].Action.Should().Contain("SBOM");
        all["pipeline-review"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable, "GitHub Actions is read by pattern only");
        all["azure-environment"].Status.Should().Be(ApplicabilityStatus.Unsupported);
        all["azure-environment"].Reason.Should().Contain("Amazon Web Services");
        all["integration-quality-review"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable);
        all["integration-quality-review"].Reason.Should().Contain("Apache Kafka").And.Contain("configuration only");
        all["frontend-quality-review"].Status.Should().Be(ApplicabilityStatus.NeedsConfiguration, "the source shows a browser frontend but no target is configured");
        all["security-classification-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all.Values.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Reason));
    }

    [Fact]
    public void Kafka_ShowsEveryDimension_ConfigurationIsNotRuntime()
    {
        var kafka = TechnologySupportRegistry.Find("integration.kafka")!;
        kafka.Configuration.Should().Be(SupportLevel.Partial);
        kafka.SourceAnalysis.Should().Be(SupportLevel.Partial);
        kafka.Contract.Should().Be(SupportLevel.Unsupported);
        kafka.RuntimeObservation.Should().Be(SupportLevel.Unsupported);
        kafka.ActiveTest.Should().Be(SupportLevel.Unsupported);
        kafka.Overall.Should().Be(SupportLevel.Partial);
        kafka.Dimensions.Should().HaveCount(Enum.GetValues<SupportDimension>().Length);
        TechnologySupportRegistry.Find("integration.eventhub")!.ActiveTest.Should().Be(SupportLevel.Partial, "only Event Hubs has an active test");
        TechnologySupportRegistry.Technologies.Where(t => t.ActiveTest is SupportLevel.Full).Should().BeEmpty("no active test is unrestricted");
    }

    // ── LegacyClaims ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyClaims_SoapOracleJenkinsActiveMq_AreHonest()
    {
        var s = await Analyze("LegacyClaims.zip", LegacyClaims());
        Ids(s).Should().Contain(["lang.csharp", "lang.sql", "lang.plsql", "framework.dotnet-framework", "integration.soap", "contract.wsdl", "db.oracle", "pipeline.jenkins", "integration.activemq"]);
        TechnologySupportRegistry.Find("db.oracle")!.SourceAnalysis.Should().Be(SupportLevel.Partial);
        TechnologySupportRegistry.Find("db.oracle")!.Limitations.Should().Contain("PL/SQL");
        TechnologySupportRegistry.Find("contract.wsdl")!.Contract.Should().Be(SupportLevel.Unsupported);
        TechnologySupportRegistry.Find("pipeline.jenkins")!.SourceAnalysis.Should().Be(SupportLevel.Partial);

        var all = ApplicabilityEvaluator.EvaluateAll(Input(s, b => { b.Target = true; b.Integrations.Add(IntegrationTechnology.Map(IntegrationKind.HttpApi, "Claims SOAP service")); }));
        all["source-analysis"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable);
        all["source-analysis"].Reason.Should().Contain("PL/SQL");
        all["api-quality-review"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable, "a SOAP service is not REST/GraphQL");
        all["pipeline-review"].Status.Should().Be(ApplicabilityStatus.PartiallyApplicable);
        all["integration-quality-review"].Reason.Should().Contain("SOAP");
    }

    [Fact]
    public void ACatalogedSoapService_IsNotClaimedAsRestRuntime()
    {
        IntegrationTechnology.Map(IntegrationKind.HttpApi, "Claims SOAP service").Should().Be("integration.soap");
        IntegrationTechnology.Map(IntegrationKind.Other, "Payments", "Kafka cluster").Should().Be("integration.kafka");
        IntegrationTechnology.Map(IntegrationKind.HttpApi, "Orders API").Should().Be("integration.rest");
        IntegrationTechnology.Map(IntegrationKind.EventHub, "BIRK CDC").Should().Be("integration.eventhub");
    }

    // ── DataLakeIngestion ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DataLakeIngestion_NoFrontendOrApi_IsNotApplicable_NotFailed()
    {
        var s = await Analyze("DataLakeIngestion.zip", DataLakeIngestion());
        Ids(s).Should().Contain(["lang.python", "framework.airflow", "dependency.pip", "cloud.aws", "integration.s3"]);
        var all = ApplicabilityEvaluator.EvaluateAll(Input(s));
        all["frontend-quality-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all["critical-e2e-regression"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all["api-quality-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all["dependency-review"].Status.Should().Be(ApplicabilityStatus.Unsupported);
        all["integration-quality-review"].Status.Should().Be(ApplicabilityStatus.NeedsConfiguration, "an S3 landing zone was detected but nothing is configured");
        all["pipeline-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all.Values.Select(a => a.Status).Should().OnlyContain(st => Enum.IsDefined(st), "applicability has no Failed state");
    }

    // ── UnknownTech / document-only / M2LB-like ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownTech_IsUnsupported_WithAToolLimitationReason()
    {
        var s = await Analyze("UnknownTech.zip", UnknownTech());
        s.TechnologyCoverage!.Technologies.Should().BeEmpty();
        s.TechnologyCoverage.TotalFiles.Should().Be(3);
        var a = ApplicabilityEvaluator.Evaluate("source-analysis", Input(s));
        a.Status.Should().Be(ApplicabilityStatus.Unsupported);
        a.Reason.Should().Contain("tool limitation");
    }

    [Fact]
    public void DocumentOnlyProject_ReviewsNeedEvidenceOrConfiguration_NeverFail()
    {
        var all = ApplicabilityEvaluator.EvaluateAll(Input(null, b => b.Requirements = true));
        all["quality-review"].Status.Should().Be(ApplicabilityStatus.Applicable);
        all["source-analysis"].Status.Should().Be(ApplicabilityStatus.NotEnoughEvidence);
        all["source-analysis"].Action.Should().Contain("Upload");
        all["frontend-quality-review"].Status.Should().Be(ApplicabilityStatus.NeedsConfiguration);
        all["pipeline-review"].Status.Should().Be(ApplicabilityStatus.NotEnoughEvidence);
        all["dependency-review"].Status.Should().Be(ApplicabilityStatus.NotEnoughEvidence);
        all["security-classification-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        all.Values.Should().OnlyContain(r => ScoreSemantics.ExcludedFromQuality(r.Status) || r.Status == ApplicabilityStatus.Applicable);
    }

    [Fact]
    public void SecurityClassification_IsADomainExtension_EnabledOnlyByTheAppliedTemplate()
    {
        ApplicabilityEvaluator.Evaluate("security-classification-review", Input(null)).Status.Should().Be(ApplicabilityStatus.NotApplicable);
        ApplicabilityEvaluator.Evaluate("security-classification-review", Input(null, b => b.Extensions.Add(DomainExtensionIds.M2lbChildSecurityClassification)))
            .Status.Should().Be(ApplicabilityStatus.Applicable);
        ReviewCatalog.Find("security-classification-review")!.DisplayName.Should().Contain("M2LB extension");
        ReviewCatalog.All.Where(r => r.DomainExtensionId is null).Select(r => r.DisplayName + r.Purpose).Should()
            .NotContain(t => t.Contains("BirkId") || t.Contains("Kode 6") || t.Contains("BarnRegistreringId") || t.Contains("BiRK"), "generic reviews do not require M2LB concepts");
    }

    [Fact]
    public async Task DotNetProject_IsApplicable()
    {
        var s = await Analyze("Orders.zip", Zip(
            ("Orders/Orders.Api/Orders.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"8.0.0\" /></ItemGroup></Project>"),
            ("Orders/Orders.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/orders\", () => 1); app.Run();"),
            ("Orders/azure-pipelines.yml", "trigger:\n  - main\nstages:\n  - stage: Build\n    jobs:\n      - job: Build\n        steps:\n          - script: dotnet build\n")));
        Ids(s).Should().Contain(["lang.csharp", "framework.aspnetcore", "dependency.nuget", "db.postgresql", "pipeline.azuredevops"]);
        var all = ApplicabilityEvaluator.EvaluateAll(Input(s));
        all["source-analysis"].Status.Should().Be(ApplicabilityStatus.Applicable);
        all["dependency-review"].Status.Should().Be(ApplicabilityStatus.Applicable);
        all["pipeline-review"].Status.Should().Be(ApplicabilityStatus.Applicable);
    }

    [Fact]
    public void Registry_HasStableUniqueIds_AndEveryProviderReferenceResolves()
    {
        TechnologySupportRegistry.Technologies.Select(t => t.TechnologyId).Should().OnlyHaveUniqueItems();
        TechnologySupportRegistry.Providers.Select(p => p.ProviderId).Should().OnlyHaveUniqueItems();
        TechnologySupportRegistry.Technologies.SelectMany(t => t.ProviderIds).Should().OnlyContain(id => TechnologySupportRegistry.FindProvider(id) != null);
        TechnologySupportRegistry.Providers.SelectMany(p => p.TechnologyIds).Should().OnlyContain(id => TechnologySupportRegistry.Find(id) != null);
        TechnologySupportRegistry.FindProvider(SourceArchitectureAnalyzerProviderId).Should().NotBeNull();
        foreach (var id in new[] { "source.architecture.dotnet", "contract.openapi", "pipeline.azuredevops", "cloud.azure", "dependency.nuget" })
            TechnologySupportRegistry.FindProvider(id).Should().NotBeNull(id);
        foreach (var area in new[] { TechnologyArea.Integration, TechnologyArea.Database, TechnologyArea.Dependency, TechnologyArea.Pipeline, TechnologyArea.Cloud })
            TechnologySupportRegistry.Matrix(area).Should().NotBeEmpty();
        TechnologySupportRegistry.IsSupported(SupportLevel.Planned).Should().BeFalse("planned is not support");
    }

    private const string SourceArchitectureAnalyzerProviderId = BirkNext.Api.Services.SourceArchitecture.SourceArchitectureAnalyzer.ProviderId;
}
