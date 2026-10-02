using System.Text.Json.Serialization;
using BirkNext.Applicability;

namespace BirkNext.Technology;

// ── Honest technology support ───────────────────────────────────────────────────────────────────────────────────────────
// One registry says, per technology and per dimension, how much BirkNext can actually do. A technology a project uses but BirkNext cannot
// analyze is a tool limitation (Unsupported), never a project-quality problem. Providers carry stable ids so results, docs and UI name the
// same thing. Levels describe what the code does today, not what a label suggests.

/// <summary>How well BirkNext supports one dimension of a technology. Planned is not support: it is shown as unsupported today.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportLevel { Full, Partial, Planned, Unsupported, NotApplicable }

/// <summary>What BirkNext can do with a technology. Configuration = can be described in the catalog; SourceAnalysis = semantics read from
/// source; Contract = formal contract parsed; RuntimeObservation = read-only runtime evidence; ActiveTest = BirkNext sends/probes; TestPlan = test design.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportDimension { Configuration, SourceAnalysis, Contract, RuntimeObservation, ActiveTest, TestPlan, ResultImport }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechnologyArea { Language, Framework, Integration, Database, Dependency, Pipeline, Cloud, Contract, Testing }

/// <summary>Support of one technology across every dimension, with the providers that deliver it and what they do not do.</summary>
public sealed record TechnologySupportDescriptor(string TechnologyId, string DisplayName, TechnologyArea Area,
    SupportLevel Configuration, SupportLevel SourceAnalysis, SupportLevel Contract, SupportLevel RuntimeObservation, SupportLevel ActiveTest, SupportLevel TestPlan,
    List<string> ProviderIds, string Limitations, List<Capability>? Provides = null, SupportLevel ResultImport = SupportLevel.NotApplicable)
{
    public SupportLevel Level(SupportDimension dimension) => dimension switch
    {
        SupportDimension.Configuration => Configuration,
        SupportDimension.SourceAnalysis => SourceAnalysis,
        SupportDimension.Contract => Contract,
        SupportDimension.RuntimeObservation => RuntimeObservation,
        SupportDimension.ActiveTest => ActiveTest,
        SupportDimension.ResultImport => ResultImport,
        _ => TestPlan,
    };

    [JsonIgnore] public IEnumerable<(SupportDimension Dimension, SupportLevel Level)> Dimensions =>
        Enum.GetValues<SupportDimension>().Select(d => (d, Level(d)));

    /// <summary>Full when every applicable dimension is Full; Unsupported when none is Full or Partial; otherwise Partial.</summary>
    [JsonIgnore] public SupportLevel Overall
    {
        get
        {
            var applicable = Dimensions.Where(d => d.Level != SupportLevel.NotApplicable).Select(d => d.Level).ToList();
            if (applicable.Count == 0) return SupportLevel.NotApplicable;
            if (applicable.All(l => l == SupportLevel.Full)) return SupportLevel.Full;
            return applicable.Any(l => l is SupportLevel.Full or SupportLevel.Partial) ? SupportLevel.Partial : SupportLevel.Unsupported;
        }
    }
}

/// <summary>One analyzer/provider with a stable id (e.g. <c>source.architecture.dotnet</c>). Maturity is what it does today.</summary>
public sealed record AnalysisProviderDescriptor(string ProviderId, string DisplayName, TechnologyArea Area, SupportLevel Maturity, List<string> TechnologyIds, string Description);

/// <summary>The single technology-support registry. Pure data; the backend, the coverage page, the evaluator and the tests read the same table.</summary>
public static class TechnologySupportRegistry
{
    private const SupportLevel F = SupportLevel.Full, P = SupportLevel.Partial, U = SupportLevel.Unsupported, N = SupportLevel.NotApplicable, L = SupportLevel.Planned;

    public static IReadOnlyList<AnalysisProviderDescriptor> Providers { get; } =
    [
        new("source.architecture.dotnet", "C# / .NET source architecture", TechnologyArea.Language, F, ["lang.csharp", "framework.aspnetcore", "framework.blazor-wasm"],
            "Components, project references, HTTP clients, messaging, datastores and interfaces from .csproj and C# syntax (Roslyn). Other languages are not analyzed."),
        new("source.database.efcore", "EF Core / migrations", TechnologyArea.Database, F, ["db.sqlserver", "db.postgresql"],
            "DbContext, entities, keys and relationships from EF Core models and migrations."),
        new("source.database.sql-ddl", "SQL DDL", TechnologyArea.Database, P, ["db.sqlserver", "db.postgresql", "db.oracle", "db.mysql"],
            "Basic CREATE/ALTER TABLE and CREATE INDEX; dialect extensions, PL/SQL, T-SQL procedures and dynamic SQL are not assessed."),
        new("source.observability.dotnet", "C# observability", TechnologyArea.Language, P, ["lang.csharp"],
            "Correlation, logging and telemetry configuration from C# and appsettings."),
        new("source.iac.terraform", "Terraform", TechnologyArea.Cloud, F, ["cloud.azure", "cloud.aws", "cloud.gcp"],
            "Static HCL structure: resources, modules, variables, providers, backends. No plan, state or provider calls."),
        new("source.iac.bicep-arm", "Bicep / ARM", TechnologyArea.Cloud, P, ["cloud.azure"], "Declarations by pattern; expressions not evaluated."),
        new("source.iac.kubernetes", "Kubernetes / Helm", TechnologyArea.Cloud, P, ["cloud.kubernetes"], "Manifests via a YAML subset reader; Helm templates not rendered."),
        new("contract.openapi", "OpenAPI", TechnologyArea.Contract, F, ["contract.openapi", "integration.rest"], "OpenAPI 3 JSON fully; YAML/Swagger 2 partially."),
        new("contract.graphql", "GraphQL SDL", TechnologyArea.Contract, F, ["contract.graphql", "integration.graphql"], "Hot Chocolate parser; schema and operations."),
        new("contract.asyncapi", "AsyncAPI", TechnologyArea.Contract, P, ["contract.asyncapi"], "Channels and operations; payload schemas not resolved."),
        new("contract.xsd", "XML Schema", TechnologyArea.Contract, P, ["contract.xsd"], "Elements and cardinality; no schema compilation."),
        new("contract.protobuf", "Protobuf", TechnologyArea.Contract, P, ["contract.protobuf", "integration.grpc"], "Messages, fields and services by pattern."),
        new("pipeline.azuredevops", "Azure Pipelines", TechnologyArea.Pipeline, F, ["pipeline.azuredevops"],
            "Triggers, stages, jobs, dependsOn, conditions (as written), templates, artifacts; optional Azure DevOps metadata (read-only)."),
        new("pipeline.github-actions", "GitHub Actions", TechnologyArea.Pipeline, P, ["pipeline.github-actions"], "Triggers, jobs, environments and run commands by pattern; reusable workflows not expanded."),
        new("pipeline.gitlab", "GitLab CI", TechnologyArea.Pipeline, P, ["pipeline.gitlab"], "Stages, jobs, environments and scripts by pattern; includes not resolved."),
        new("pipeline.jenkins", "Jenkins", TechnologyArea.Pipeline, P, ["pipeline.jenkins"], "Declarative stage names and sh/bat commands by pattern; Groovy not evaluated."),
        new("dependency.nuget", "NuGet", TechnologyArea.Dependency, F, ["dependency.nuget"],
            "csproj, Central Package Management, lock files (transitive), SDK and tools; nuget.org freshness and OSV advisories."),
        new("dependency.docker", "Container images", TechnologyArea.Dependency, P, ["dependency.docker"], "Base images and tags from Dockerfiles, Compose and pipelines; no registry freshness."),
        new("dependency.sbom", "SBOM import (CycloneDX / SPDX)", TechnologyArea.Dependency, P, ["dependency.sbom", "dependency.maven", "dependency.npm", "dependency.pip"],
            "Ecosystem-neutral inventory and OSV advisories by PURL; registry freshness only for NuGet."),
        new("runtime.eventhub.azure", "Azure Event Hubs runtime (opt-in)", TechnologyArea.Integration, P, ["integration.eventhub"],
            "ARM metadata, metrics, consumer groups, Blob checkpoints, App Insights; active send test in DEV/QA only. Off by default."),
        new("runtime.servicebus.azure", "Azure Service Bus runtime (opt-in)", TechnologyArea.Integration, P, ["integration.servicebus"], "ARM metadata (GET only). Off by default."),
        new("runtime.http", "HTTP API runtime (API Quality Review)", TechnologyArea.Integration, F, ["integration.rest", "integration.graphql"],
            "REST and GraphQL requests against a configured target with gateway authentication."),
        new("runtime.browser", "Browser runtime (Frontend Quality Review)", TechnologyArea.Framework, F, ["framework.browser-frontend"],
            "Any browser-rendered frontend, independent of its framework: accessibility, performance, security headers."),
        new("cloud.azure", "Azure environment (read-only ARM)", TechnologyArea.Cloud, P, ["cloud.azure"], "Read-only Azure Resource Manager inventory of configured subscriptions."),
        new("test.discovery.dotnet.xunit", "xUnit source test discovery (.NET)", TechnologyArea.Testing, P, ["test.xunit"],
            "Test projects from project metadata; [Fact]/[Theory], traits and explicit requirement references from C# syntax. Discovery only, never a result."),
        new("test.execution.trx", "TRX test result import", TechnologyArea.Testing, F, ["test.trx"],
            "Manual import of .trx files (VSTest logger or Microsoft.Testing.Platform). Framework-independent parsing; source correlation only where a discovery provider exists."),
    ];

    public static IReadOnlyList<TechnologySupportDescriptor> Technologies { get; } =
    [
        // Languages: only C# has semantic analysis. Everything else is detected (file inventory) and reported Unsupported — no fake support.
        new("lang.csharp", "C#", TechnologyArea.Language, N, F, N, N, N, N, ["source.architecture.dotnet", "source.observability.dotnet"], "Roslyn syntax only; no compilation or semantic model.", [Capability.SourceCode]),
        new("lang.fsharp", "F#", TechnologyArea.Language, N, U, N, N, N, N, [], "Not analyzed."),
        new("lang.vbnet", "VB.NET", TechnologyArea.Language, N, U, N, N, N, N, [], "Not analyzed."),
        new("lang.java", "Java", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only. No Java/Spring architecture, database or observability analysis."),
        new("lang.kotlin", "Kotlin", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only."),
        new("lang.typescript", "TypeScript", TechnologyArea.Language, N, U, N, N, N, N, [], "Source not analyzed; a browser frontend built with it is reviewed at runtime by Frontend Quality Review."),
        new("lang.javascript", "JavaScript", TechnologyArea.Language, N, U, N, N, N, N, [], "Source not analyzed; runtime frontend review is framework-independent."),
        new("lang.python", "Python", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only."),
        new("lang.go", "Go", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only."),
        new("lang.ruby", "Ruby", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only."),
        new("lang.php", "PHP", TechnologyArea.Language, N, U, N, N, N, N, [], "Detected only."),
        new("lang.sql", "SQL", TechnologyArea.Language, N, P, N, N, N, N, ["source.database.sql-ddl"], "Basic DDL only."),
        new("lang.plsql", "PL/SQL", TechnologyArea.Language, N, U, N, N, N, N, [], "Packages, procedures and triggers are not analyzed."),

        // Frameworks.
        new("framework.aspnetcore", "ASP.NET Core", TechnologyArea.Framework, N, F, N, N, N, N, ["source.architecture.dotnet"], "", [Capability.BackendApplication]),
        new("framework.dotnet-framework", ".NET Framework (classic csproj)", TechnologyArea.Framework, N, P, N, N, N, N, ["source.architecture.dotnet"],
            "C# syntax is read; web.config/app.config, packages.config and WCF/ASMX service semantics are not.", [Capability.BackendApplication]),
        new("framework.blazor-wasm", "Blazor WebAssembly", TechnologyArea.Framework, N, F, N, F, N, N, ["source.architecture.dotnet", "runtime.browser"],
            "Enables the WASM security and performance engines.", [Capability.FrontendApplication]),
        new("framework.browser-frontend", "Browser frontend (any framework)", TechnologyArea.Framework, N, N, N, F, P, P, ["runtime.browser"],
            "Runtime review is framework-independent; React/Angular/Vue source is not analyzed.", [Capability.FrontendApplication]),
        new("framework.spring", "Spring / Spring Boot", TechnologyArea.Framework, N, U, N, N, N, N, [], "Detected from Maven/Gradle files only; no Spring architecture analysis.", [Capability.BackendApplication]),
        new("framework.airflow", "Apache Airflow", TechnologyArea.Framework, N, U, N, N, N, N, [], "Detected only; DAGs are not analyzed."),

        // Integrations: configuration ≠ runtime ≠ active test. Only Event Hubs has an active test; Kafka/RabbitMQ/SOAP are catalog-only.
        new("integration.eventhub", "Azure Event Hubs", TechnologyArea.Integration, F, P, N, P, P, P, ["runtime.eventhub.azure"],
            "Runtime evidence is opt-in (Azure off by default); the active send test runs in DEV/QA only.", [Capability.EventStreaming]),
        new("integration.servicebus", "Azure Service Bus", TechnologyArea.Integration, F, P, N, P, U, P, ["runtime.servicebus.azure"],
            "Runtime is ARM metadata only (GET); no send/receive test.", [Capability.Messaging]),
        new("integration.kafka", "Apache Kafka", TechnologyArea.Integration, P, P, U, U, U, U, [],
            "Catalog entry (type Other) and C# Confluent.Kafka correlation/Kafka Connect detection only. No broker, topic, consumer-lag or schema-registry evidence; no active test.",
            [Capability.EventStreaming]),
        new("integration.rabbitmq", "RabbitMQ", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only.", [Capability.Messaging]),
        new("integration.activemq", "ActiveMQ / JMS", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only.", [Capability.Messaging]),
        new("integration.rest", "REST / HTTP API", TechnologyArea.Integration, F, P, F, F, P, P, ["contract.openapi", "runtime.http"],
            "Runtime through API Quality Review against a configured target; active tests are read-only requests.", [Capability.RestApi]),
        new("integration.graphql", "GraphQL API", TechnologyArea.Integration, F, P, F, F, P, P, ["contract.graphql", "runtime.http"], "", [Capability.GraphQlApi]),
        new("integration.soap", "SOAP / WSDL", TechnologyArea.Integration, P, U, U, U, U, U, [],
            "No WSDL analyzer and no SOAP runtime client; XSD files are parsed as XML Schema only.", [Capability.SoapApi]),
        new("integration.grpc", "gRPC", TechnologyArea.Integration, P, U, P, U, U, U, ["contract.protobuf"], ".proto contracts by pattern only.", [Capability.GrpcApi]),
        new("integration.file", "File / SFTP", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only.", [Capability.FileIntegration]),
        new("integration.s3", "Amazon S3", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only.", [Capability.FileIntegration]),
        new("integration.database", "Database integration", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only; schema comes from Source Analysis.", [Capability.RelationalDatabase]),
        new("integration.other", "Other integration", TechnologyArea.Integration, P, U, U, U, U, U, [], "Catalog entry only."),
        new("integration.scim", "SCIM provisioning", TechnologyArea.Integration, F, P, P, P, U, P, [], "GET-only runtime checks.", [Capability.Authentication]),

        // Databases.
        new("db.sqlserver", "SQL Server", TechnologyArea.Database, N, F, N, N, N, N, ["source.database.efcore", "source.database.sql-ddl"], "Schema from EF Core and basic DDL; no live database connection.", [Capability.RelationalDatabase]),
        new("db.postgresql", "PostgreSQL", TechnologyArea.Database, N, F, N, N, N, N, ["source.database.efcore", "source.database.sql-ddl"], "Schema from EF Core and basic DDL; no live database connection.", [Capability.RelationalDatabase]),
        new("db.oracle", "Oracle", TechnologyArea.Database, N, P, N, N, N, N, ["source.database.sql-ddl"],
            "Only basic CREATE TABLE/INDEX DDL is read; PL/SQL, packages, sequences/triggers semantics and Oracle-specific types are not assessed.", [Capability.RelationalDatabase]),
        new("db.mysql", "MySQL / MariaDB", TechnologyArea.Database, N, P, N, N, N, N, ["source.database.sql-ddl"], "Basic DDL only.", [Capability.RelationalDatabase]),
        new("db.mongodb", "MongoDB", TechnologyArea.Database, N, U, N, N, N, N, [], "Detected only.", [Capability.DocumentDatabase]),
        new("db.cosmosdb", "Azure Cosmos DB", TechnologyArea.Database, N, U, N, N, N, N, [], "Detected only.", [Capability.DocumentDatabase]),

        // Dependency ecosystems: NuGet is the only ecosystem with native manifest reading; others go through an SBOM import.
        new("dependency.nuget", "NuGet", TechnologyArea.Dependency, N, F, N, N, N, N, ["dependency.nuget"], "", [Capability.PackageInventory]),
        new("dependency.docker", "Container images", TechnologyArea.Dependency, N, P, N, N, N, N, ["dependency.docker"], "No registry freshness.", [Capability.Containerized]),
        new("dependency.maven", "Maven / Gradle", TechnologyArea.Dependency, N, U, N, N, N, N, ["dependency.sbom"],
            "pom.xml/build.gradle are not read. Import a CycloneDX/SPDX SBOM for an ecosystem-neutral inventory and OSV advisories."),
        new("dependency.npm", "npm / Yarn / pnpm", TechnologyArea.Dependency, N, U, N, N, N, N, ["dependency.sbom"], "package.json is read only for Renovate configuration. Import an SBOM."),
        new("dependency.pip", "pip / Poetry", TechnologyArea.Dependency, N, U, N, N, N, N, ["dependency.sbom"], "requirements.txt/pyproject.toml are not read. Import an SBOM."),
        new("dependency.sbom", "SBOM (CycloneDX / SPDX)", TechnologyArea.Dependency, N, P, N, N, N, N, ["dependency.sbom"], "Registry freshness only for NuGet packages.", [Capability.PackageInventory]),

        // Pipelines.
        new("pipeline.azuredevops", "Azure Pipelines", TechnologyArea.Pipeline, N, F, N, P, N, N, ["pipeline.azuredevops"], "Run history only with optional read-only Azure DevOps access.", [Capability.Pipeline]),
        new("pipeline.github-actions", "GitHub Actions", TechnologyArea.Pipeline, N, P, N, U, N, N, ["pipeline.github-actions"], "Pattern-based; no run history.", [Capability.Pipeline]),
        new("pipeline.gitlab", "GitLab CI", TechnologyArea.Pipeline, N, P, N, U, N, N, ["pipeline.gitlab"], "Pattern-based; includes not resolved; no run history.", [Capability.Pipeline]),
        new("pipeline.jenkins", "Jenkins", TechnologyArea.Pipeline, N, P, N, U, N, N, ["pipeline.jenkins"], "Pattern-based; Groovy not evaluated; no run history.", [Capability.Pipeline]),

        // Cloud.
        new("cloud.azure", "Microsoft Azure", TechnologyArea.Cloud, N, F, N, P, N, N, ["source.iac.terraform", "source.iac.bicep-arm", "cloud.azure"], "Runtime is read-only ARM inventory.", [Capability.CloudHosted]),
        new("cloud.aws", "Amazon Web Services", TechnologyArea.Cloud, N, P, N, U, N, N, ["source.iac.terraform"],
            "Terraform resources are inventoried generically; CloudFormation/CDK are detected only; no AWS runtime access.", [Capability.CloudHosted]),
        new("cloud.gcp", "Google Cloud", TechnologyArea.Cloud, N, P, N, U, N, N, ["source.iac.terraform"], "Terraform inventory only; no GCP runtime access.", [Capability.CloudHosted]),
        new("cloud.kubernetes", "Kubernetes", TechnologyArea.Cloud, N, P, N, U, N, N, ["source.iac.kubernetes"], "Manifests only; no cluster access.", [Capability.Kubernetes]),

        // Contracts.
        new("contract.openapi", "OpenAPI", TechnologyArea.Contract, N, N, F, N, N, N, ["contract.openapi"], "YAML/Swagger 2 partial."),
        new("contract.graphql", "GraphQL SDL", TechnologyArea.Contract, N, N, F, N, N, N, ["contract.graphql"], ""),
        new("contract.asyncapi", "AsyncAPI", TechnologyArea.Contract, N, N, P, N, N, N, ["contract.asyncapi"], "Payload schemas not resolved."),
        new("contract.xsd", "XML Schema (XSD)", TechnologyArea.Contract, N, N, P, N, N, N, ["contract.xsd"], "No schema compilation."),
        new("contract.wsdl", "WSDL", TechnologyArea.Contract, N, N, U, N, N, N, [], "No WSDL analyzer."),
        new("contract.protobuf", "Protobuf", TechnologyArea.Contract, N, N, P, N, N, N, ["contract.protobuf"], "Pattern-based."),
        new("contract.avro", "Avro", TechnologyArea.Contract, N, N, U, N, N, N, [], "Not parsed."),

        // Testing: source test discovery (SourceAnalysis) and execution-result import (ResultImport) are separate dimensions.
        new("test.xunit", "xUnit", TechnologyArea.Testing, N, P, N, N, N, N, ["test.discovery.dotnet.xunit"],
            "Syntax-only: custom FactAttribute subclasses, inherited tests and MemberData rows are not resolved. Results come from TRX import.", null, U),
        new("test.nunit", "NUnit", TechnologyArea.Testing, N, U, N, N, N, N, [], "Detected only: no source test discovery. Results import from TRX without source correlation.", null, U),
        new("test.mstest", "MSTest", TechnologyArea.Testing, N, U, N, N, N, N, [], "Detected only: no source test discovery. Results import from TRX without source correlation.", null, U),
        new("test.trx", "TRX test results", TechnologyArea.Testing, N, N, N, N, N, N, ["test.execution.trx"],
            "Manual .trx import. No automatic Azure DevOps retrieval; raw TRX and code coverage are not imported.", null, F),
        new("test.junit", "JUnit XML results", TechnologyArea.Testing, N, N, N, N, N, N, [], "No JUnit provider.", null, U),
        new("test.playwright", "Playwright", TechnologyArea.Testing, N, U, N, N, N, N, [], "Detected only: no Playwright source discovery or result provider.", null, U),
    ];

    private static readonly Dictionary<string, TechnologySupportDescriptor> ById = Technologies.ToDictionary(t => t.TechnologyId, StringComparer.Ordinal);
    private static readonly Dictionary<string, AnalysisProviderDescriptor> ProvidersById = Providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);

    public static TechnologySupportDescriptor? Find(string technologyId) => ById.GetValueOrDefault(technologyId);
    public static AnalysisProviderDescriptor? FindProvider(string providerId) => ProvidersById.GetValueOrDefault(providerId);
    public static IEnumerable<TechnologySupportDescriptor> Matrix(TechnologyArea area) => Technologies.Where(t => t.Area == area);

    /// <summary>Planned counts as Unsupported today: a roadmap item is not a capability.</summary>
    public static bool IsSupported(SupportLevel level) => level is SupportLevel.Full or SupportLevel.Partial;

    public static string Label(SupportLevel level) => level switch
    {
        SupportLevel.Full => "Supported",
        SupportLevel.Partial => "Partial",
        SupportLevel.Planned => "Planned (not supported yet)",
        SupportLevel.Unsupported => "Not supported",
        _ => "Not applicable",
    };

    public static string Label(SupportDimension dimension) => dimension switch
    {
        SupportDimension.SourceAnalysis => "Source analysis",
        SupportDimension.RuntimeObservation => "Runtime observation",
        SupportDimension.ActiveTest => "Active test",
        SupportDimension.TestPlan => "Test plan",
        SupportDimension.ResultImport => "Result import",
        _ => dimension.ToString(),
    };
}

// ── Detected technologies of one source snapshot ───────────────────────────────────────────────────────────────────────

/// <summary>A technology seen in a source snapshot, with bounded path evidence (redacted names, never content).</summary>
public sealed record DetectedTechnology(string TechnologyId, string DisplayName, TechnologyArea Area, DetectionConfidence Confidence, int Files, List<string> Evidence);

/// <summary>What the snapshot contains and how much of it BirkNext analyzed. Analysis coverage, never quality.</summary>
public sealed record SourceTechnologyCoverage
{
    public int Version { get; init; } = 1;
    public int TotalFiles { get; init; }
    /// <summary>Files whose language/format BirkNext reads semantically (C#, project files, configuration, IaC, contracts, pipelines).</summary>
    public int AnalyzedFiles { get; init; }
    /// <summary>Source files in a language BirkNext detects but does not analyze.</summary>
    public int UnsupportedSourceFiles { get; init; }
    public List<DetectedTechnology> Technologies { get; init; } = [];
    public List<CapabilityEvidence> Capabilities { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

// ── Review catalog and applicability evaluation ────────────────────────────────────────────────────────────────────────

/// <summary>One review/area BirkNext offers, and what makes it applicable.</summary>
public sealed record ReviewDescriptor(string ReviewId, string DisplayName, string Route, List<Capability> Requires, string Purpose, string? DomainExtensionId = null);

/// <summary>Everything the evaluator needs to know about the selected project. Source facts come from the latest Source Analysis snapshot;
/// targets and workspace artifacts come from the UI; domain extensions are explicit opt-ins (never inferred from a hostname).</summary>
public sealed record ProjectApplicabilityInput
{
    public bool HasSourceSnapshot { get; init; }
    public List<DetectedTechnology> Technologies { get; init; } = [];
    public List<CapabilityEvidence> Capabilities { get; init; } = [];
    public bool HasBrowserTarget { get; init; }
    public bool HasApiTarget { get; init; }
    /// <summary>Integration kinds configured in the catalog (as technology ids, e.g. integration.eventhub).</summary>
    public List<string> ConfiguredIntegrations { get; init; } = [];
    public bool HasRequirements { get; init; }
    public bool HasDocumentation { get; init; }
    public bool HasSbom { get; init; }
    public List<string> DomainExtensions { get; init; } = [];

    public bool Has(Capability c) => Capabilities.Any(e => e.Capability == c);
    public bool Detected(string technologyId) => Technologies.Any(t => t.TechnologyId == technologyId);
}

public static class DomainExtensionIds
{
    /// <summary>M2LB child security classification (BarnRegistreringId, BirkId, Kode 6/7). Active only after the M2LB template is explicitly applied.</summary>
    public const string M2lbChildSecurityClassification = "m2lb.child-security-classification";
}

public static class ReviewCatalog
{
    public static IReadOnlyList<ReviewDescriptor> All { get; } =
    [
        new("source-analysis", "Source Analysis", "source-analysis", [Capability.SourceCode], "Architecture, database, observability and evidence domains from an uploaded source archive."),
        new("quality-review", "Quality Review", "quality-review", [Capability.Requirements], "Requirements, documentation and traceability quality."),
        new("frontend-quality-review", "Frontend Quality Review", "frontend-quality-review", [Capability.FrontendApplication, Capability.BrowserTarget], "Accessibility, performance and security of a browser frontend."),
        new("api-quality-review", "API Quality Review", "api-quality-review", [Capability.ApiTarget], "REST and GraphQL contract, security and performance against a configured target."),
        new("integration-quality-review", "Integration Quality Review", "integration-quality-review", [Capability.IntegrationCatalog], "Configuration, contracts and runtime evidence of configured integrations."),
        new("performance-test-review", "Performance Test Review", "performance-test-review", [Capability.ApiTarget], "Controlled HTTP/API load tests with explicit latency, throughput and error expectations."),
        new("dependency-review", "Dependency Review", "dependency-review", [Capability.PackageInventory], "Package freshness and advisories."),
        new("pipeline-review", "Pipeline Review", "pipeline-review", [Capability.Pipeline], "Build and deployment pipeline structure."),
        new("azure-environment", "Azure Environment", "azure-environment", [Capability.CloudHosted], "Read-only Azure Resource Manager inventory."),
        new("critical-e2e-regression", "Critical E2E Regression", "critical-e2e-regression", [Capability.BrowserTarget], "Critical user journeys in a browser."),
        new("security-classification-review", "Security Classification (M2LB extension)", "security-classification-review", [Capability.DomainExtension],
            "Domain-specific: M2LB child classification rules. Not a generic security review.", DomainExtensionIds.M2lbChildSecurityClassification),
    ];

    public static ReviewDescriptor? Find(string reviewId) => All.FirstOrDefault(r => r.ReviewId == reviewId);
}

/// <summary>Pure applicability rules. Never returns a failure: a review that cannot run is a coverage state, not a quality result.</summary>
public static class ApplicabilityEvaluator
{
    public static IReadOnlyDictionary<string, ReviewApplicability> EvaluateAll(ProjectApplicabilityInput input) =>
        ReviewCatalog.All.ToDictionary(r => r.ReviewId, r => Evaluate(r.ReviewId, input), StringComparer.Ordinal);

    public static ReviewApplicability Evaluate(string reviewId, ProjectApplicabilityInput input)
    {
        var review = ReviewCatalog.Find(reviewId) ?? throw new ArgumentException($"Unknown review '{reviewId}'.", nameof(reviewId));
        return reviewId switch
        {
            "source-analysis" => SourceAnalysis(review, input),
            "quality-review" => input.HasRequirements
                ? Result(review, ApplicabilityStatus.Applicable, "Requirements are loaded for the selected project.", input)
                : Result(review, ApplicabilityStatus.NotEnoughEvidence, "No requirements are loaded; there is nothing to assess yet.", input, "Select a project with specification documents."),
            "frontend-quality-review" => Frontend(review, input),
            "critical-e2e-regression" => input.HasBrowserTarget
                ? Result(review, ApplicabilityStatus.Applicable, "A browser target is configured.", input)
                : NeedsTargetOrNotApplicable(review, input, Capability.FrontendApplication, "Configure a target application URL."),
            "api-quality-review" => Api(review, input),
            "performance-test-review" => Performance(review, input),
            "integration-quality-review" => Integration(review, input),
            "dependency-review" => Dependency(review, input),
            "pipeline-review" => Pipeline(review, input),
            "azure-environment" => Azure(review, input),
            "security-classification-review" => input.DomainExtensions.Contains(DomainExtensionIds.M2lbChildSecurityClassification)
                ? Result(review, ApplicabilityStatus.Applicable, "The M2LB child security classification extension is enabled for this environment.", input)
                : Result(review, ApplicabilityStatus.NotApplicable,
                    "Domain-specific extension for M2LB child classification (Kode 6/7, BirkId). It is not enabled for this project and is not a generic security review.", input,
                    "Apply the M2LB integration template only if this is an M2LB environment."),
            _ => Result(review, ApplicabilityStatus.NotEnoughEvidence, "No applicability rule.", input),
        };
    }

    private static ReviewApplicability SourceAnalysis(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (!input.HasSourceSnapshot)
            return Result(review, ApplicabilityStatus.NotEnoughEvidence, "No source archive has been analyzed.", input, "Upload a source archive in Source Analysis.");
        var languages = input.Technologies.Where(t => t.Area == TechnologyArea.Language).ToList();
        var supported = languages.Where(t => TechnologySupportRegistry.Find(t.TechnologyId) is { } d && TechnologySupportRegistry.IsSupported(d.SourceAnalysis)).ToList();
        var unsupported = languages.Except(supported).ToList();
        if (input.Technologies.Count == 0)
            return Result(review, ApplicabilityStatus.Unsupported, "No technology BirkNext recognises was found in the archive; nothing could be analyzed (a tool limitation, not a project finding).", input);
        if (languages.Count == 0)
            return Result(review, ApplicabilityStatus.PartiallyApplicable, "No programming language BirkNext recognises was found; configuration, IaC, contracts and pipelines are still read.", input);
        if (supported.Count == 0)
            return Result(review, ApplicabilityStatus.Unsupported,
                $"Source language semantics are not supported for {Names(unsupported)}. Configuration, IaC, contracts and pipelines in the archive are still read.", input);
        if (unsupported.Count > 0)
            return Result(review, ApplicabilityStatus.PartiallyApplicable, $"{Names(supported)} analyzed; {Names(unsupported)} detected but not analyzed.", input);
        return Result(review, ApplicabilityStatus.Applicable, $"{Names(supported)} source analyzed.", input);
    }

    private static ReviewApplicability Frontend(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (input.HasBrowserTarget)
            return Result(review, ApplicabilityStatus.Applicable, "A browser target is configured; the runtime review is framework-independent.", input);
        return NeedsTargetOrNotApplicable(review, input, Capability.FrontendApplication, "Configure a target application URL.");
    }

    private static ReviewApplicability NeedsTargetOrNotApplicable(ReviewDescriptor review, ProjectApplicabilityInput input, Capability indicator, string action)
    {
        if (input.Has(indicator))
            return Result(review, ApplicabilityStatus.NeedsConfiguration, "The source shows a browser frontend, but no target URL is configured.", input, action);
        if (input.HasSourceSnapshot)
            return Result(review, ApplicabilityStatus.NotApplicable, "No browser frontend was detected in the analyzed source and no target is configured.", input, action);
        return Result(review, ApplicabilityStatus.NeedsConfiguration, "No target URL is configured.", input, action);
    }

    private static ReviewApplicability Api(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        var http = input.Has(Capability.RestApi) || input.Has(Capability.GraphQlApi);
        var other = input.Has(Capability.SoapApi) || input.Has(Capability.GrpcApi);
        if (input.HasApiTarget)
            return other && !http
                ? Result(review, ApplicabilityStatus.PartiallyApplicable, "A target is configured, but the detected API style (SOAP/gRPC) is not supported; only HTTP checks apply.", input)
                : Result(review, ApplicabilityStatus.Applicable, "An API target is configured.", input);
        if (other && !http)
            return Result(review, ApplicabilityStatus.Unsupported, "The source exposes SOAP or gRPC services; API Quality Review supports REST and GraphQL only.", input);
        if (http) return Result(review, ApplicabilityStatus.NeedsConfiguration, "The source exposes an HTTP API, but no API target is configured.", input, "Configure a target and discover endpoints.");
        if (input.HasSourceSnapshot && !input.Has(Capability.BackendApplication))
            return Result(review, ApplicabilityStatus.NotApplicable, "No API was detected in the analyzed source and no target is configured.", input);
        return Result(review, ApplicabilityStatus.NeedsConfiguration, "No API target is configured.", input, "Configure a target and discover endpoints.");
    }

    /// <summary>HTTP/API load tests need an HTTP target; SOAP/gRPC-only services are not supported by the HTTP providers.</summary>
    private static ReviewApplicability Performance(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        var http = input.Has(Capability.RestApi) || input.Has(Capability.GraphQlApi);
        var other = input.Has(Capability.SoapApi) || input.Has(Capability.GrpcApi);
        if (input.HasApiTarget)
            return other && !http
                ? Result(review, ApplicabilityStatus.PartiallyApplicable, "A target is configured, but the detected API style (SOAP/gRPC) is not supported by the HTTP load-test providers.", input)
                : Result(review, ApplicabilityStatus.Applicable, "An HTTP target is configured; define a scenario, workload and thresholds.", input);
        if (http) return Result(review, ApplicabilityStatus.NeedsConfiguration, "The source exposes an HTTP API, but no target is configured.", input, "Configure a target application URL.");
        if (input.HasSourceSnapshot && !input.Has(Capability.BackendApplication) && !input.Has(Capability.FrontendApplication))
            return Result(review, ApplicabilityStatus.NotApplicable, "No HTTP/API target was detected in the analyzed source and no target is configured.", input);
        return Result(review, ApplicabilityStatus.NeedsConfiguration, "No HTTP target is configured.", input, "Configure a target application URL.");
    }

    private static ReviewApplicability Integration(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (input.ConfiguredIntegrations.Count > 0)
        {
            var deep = input.ConfiguredIntegrations.Where(id => TechnologySupportRegistry.Find(id) is { } d && TechnologySupportRegistry.IsSupported(d.RuntimeObservation)).Distinct().ToList();
            var shallow = input.ConfiguredIntegrations.Except(deep).Distinct().ToList();
            if (deep.Count == 0)
                return Result(review, ApplicabilityStatus.PartiallyApplicable,
                    $"Configured integrations ({Names(shallow)}) are assessed for configuration only; BirkNext has no runtime provider for them.", input);
            return shallow.Count == 0
                ? Result(review, ApplicabilityStatus.Applicable, "Configured integrations have configuration and runtime evidence providers.", input)
                : Result(review, ApplicabilityStatus.PartiallyApplicable, $"Runtime evidence for {Names(deep)}; configuration only for {Names(shallow)}.", input);
        }
        if (input.Has(Capability.Messaging) || input.Has(Capability.EventStreaming) || input.Has(Capability.FileIntegration))
            return Result(review, ApplicabilityStatus.NeedsConfiguration, "The source shows integrations, but none is configured.", input, "Add integrations in Integrations.");
        return input.HasSourceSnapshot
            ? Result(review, ApplicabilityStatus.NotApplicable, "No integrations are configured or detected in the analyzed source.", input, "Add integrations in Integrations if the project has them.")
            : Result(review, ApplicabilityStatus.NeedsConfiguration, "No integrations are configured.", input, "Add integrations in Integrations.");
    }

    private static ReviewApplicability Dependency(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (input.HasSbom) return Result(review, ApplicabilityStatus.Applicable, "An SBOM provides an ecosystem-neutral package inventory.", input);
        if (!input.HasSourceSnapshot)
            return Result(review, ApplicabilityStatus.NotEnoughEvidence, "No source archive or SBOM is available.", input, "Upload source in Source Analysis or import an SBOM.");
        var ecosystems = input.Technologies.Where(t => t.Area == TechnologyArea.Dependency).ToList();
        var native = ecosystems.Where(t => TechnologySupportRegistry.Find(t.TechnologyId) is { } d && TechnologySupportRegistry.IsSupported(d.SourceAnalysis)).ToList();
        var unsupported = ecosystems.Except(native).ToList();
        if (ecosystems.Count == 0) return Result(review, ApplicabilityStatus.NotApplicable, "No package manifests were found in the analyzed source.", input);
        if (native.Count == 0)
            return Result(review, ApplicabilityStatus.Unsupported, $"Manifests for {Names(unsupported)} are not read natively.", input, "Import a CycloneDX or SPDX SBOM.");
        return unsupported.Count == 0
            ? Result(review, ApplicabilityStatus.Applicable, $"{Names(native)} manifests are read.", input)
            : Result(review, ApplicabilityStatus.PartiallyApplicable, $"{Names(native)} read; {Names(unsupported)} not read natively.", input, "Import an SBOM to cover the rest.");
    }

    private static ReviewApplicability Pipeline(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (!input.HasSourceSnapshot)
            return Result(review, ApplicabilityStatus.NotEnoughEvidence, "No source archive has been analyzed.", input, "Upload source that contains pipeline definitions.");
        var pipelines = input.Technologies.Where(t => t.Area == TechnologyArea.Pipeline).ToList();
        if (pipelines.Count == 0)
            return Result(review, ApplicabilityStatus.NotApplicable, "No pipeline definitions were found in the analyzed source (they may live in another repository).", input);
        var full = pipelines.Where(t => TechnologySupportRegistry.Find(t.TechnologyId)?.SourceAnalysis == SupportLevel.Full).ToList();
        return full.Count == pipelines.Count
            ? Result(review, ApplicabilityStatus.Applicable, $"{Names(full)} definitions are analyzed.", input)
            : Result(review, ApplicabilityStatus.PartiallyApplicable, $"{Names(pipelines.Except(full))} definitions are read by pattern only.", input);
    }

    private static ReviewApplicability Azure(ReviewDescriptor review, ProjectApplicabilityInput input)
    {
        if (input.Detected("cloud.azure") || input.ConfiguredIntegrations.Any(i => i is "integration.eventhub" or "integration.servicebus"))
            return Result(review, ApplicabilityStatus.Applicable, "Azure evidence was found.", input);
        var other = input.Technologies.Where(t => t.TechnologyId is "cloud.aws" or "cloud.gcp").ToList();
        if (other.Count > 0)
            return Result(review, ApplicabilityStatus.Unsupported, $"The project uses {Names(other)}; BirkNext has runtime environment analysis for Azure only.", input);
        return input.HasSourceSnapshot
            ? Result(review, ApplicabilityStatus.NotApplicable, "No Azure evidence was found in the analyzed source.", input)
            : Result(review, ApplicabilityStatus.NotEnoughEvidence, "No source archive has been analyzed.", input);
    }

    private static string Names(IEnumerable<DetectedTechnology> technologies) => string.Join(", ", technologies.Select(t => t.DisplayName).Distinct());
    private static string Names(IEnumerable<string> technologyIds) =>
        string.Join(", ", technologyIds.Select(id => TechnologySupportRegistry.Find(id)?.DisplayName ?? id).Distinct());

    private static ReviewApplicability Result(ReviewDescriptor review, ApplicabilityStatus status, string reason, ProjectApplicabilityInput input, string? action = null)
    {
        var available = review.Requires.Where(c => Available(c, input)).ToList();
        return new ReviewApplicability
        {
            Status = status, Reason = reason, Action = action, RequiredCapabilities = [.. review.Requires], AvailableCapabilities = available,
            MissingCapabilities = review.Requires.Except(available).ToList(),
            Evidence = input.Capabilities.Where(c => review.Requires.Contains(c.Capability)).Select(c => c.Basis).Distinct().Take(5).ToList(),
        };
    }

    private static bool Available(Capability c, ProjectApplicabilityInput input) => c switch
    {
        Capability.SourceCode => input.HasSourceSnapshot,
        Capability.BrowserTarget => input.HasBrowserTarget,
        Capability.ApiTarget => input.HasApiTarget,
        Capability.Requirements => input.HasRequirements,
        Capability.Documentation => input.HasDocumentation,
        Capability.IntegrationCatalog => input.ConfiguredIntegrations.Count > 0,
        Capability.PackageInventory => input.HasSbom || input.Has(c),
        Capability.DomainExtension => input.DomainExtensions.Count > 0,
        _ => input.Has(c),
    };
}

/// <summary>Maps a configured catalog entry onto a registry technology. Kafka, RabbitMQ, ActiveMQ and SOAP are catalogued as Other/HttpApi
/// today, so their names decide — the catalog type alone would claim REST runtime support for a SOAP service.</summary>
public static class IntegrationTechnology
{
    public static string Map(BirkNext.Integrations.IntegrationKind kind, params string?[] names)
    {
        var text = string.Join(" ", names.Where(n => !string.IsNullOrWhiteSpace(n)));
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("kafka")) return "integration.kafka";
        if (Has("rabbit")) return "integration.rabbitmq";
        if (Has("activemq") || Has("jms")) return "integration.activemq";
        if (Has("soap") || Has("wsdl")) return "integration.soap";
        if (Has("grpc")) return "integration.grpc";
        if (Has("graphql")) return "integration.graphql";
        return kind switch
        {
            BirkNext.Integrations.IntegrationKind.EventHub => "integration.eventhub",
            BirkNext.Integrations.IntegrationKind.ServiceBus => "integration.servicebus",
            BirkNext.Integrations.IntegrationKind.HttpApi => "integration.rest",
            BirkNext.Integrations.IntegrationKind.Database => "integration.database",
            BirkNext.Integrations.IntegrationKind.File => Has("s3") ? "integration.s3" : "integration.file",
            BirkNext.Integrations.IntegrationKind.IdentityProvisioning => "integration.scim",
            _ => "integration.other",
        };
    }
}

// ── Project coverage (backend → coverage page) ─────────────────────────────────────────────────────────────────────────

/// <summary>Source-derived and catalog-derived facts for one environment; the UI adds targets and workspace artifacts and evaluates applicability.</summary>
public sealed record ProjectTechnologyCoverage
{
    public string EnvironmentId { get; init; } = "";
    public Guid? SourceSnapshotId { get; init; }
    public string? SourceArchive { get; init; }
    public DateTimeOffset? AnalyzedAt { get; init; }
    /// <summary>Null when no snapshot exists or the snapshot predates technology inventory (re-analyze to see it).</summary>
    public SourceTechnologyCoverage? Source { get; init; }
    public List<string> ConfiguredIntegrations { get; init; } = [];
    public List<string> DomainExtensions { get; init; } = [];
    public List<string> Notices { get; init; } = [];
}
