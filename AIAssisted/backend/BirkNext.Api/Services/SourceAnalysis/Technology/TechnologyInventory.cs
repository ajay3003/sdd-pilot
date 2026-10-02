using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Applicability;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Technology;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.SourceAnalysis.Technology;

/// <summary>
/// Source Analysis → technology inventory: which languages, frameworks, integrations, databases, package ecosystems, pipelines, clouds and
/// contracts a snapshot contains, and which capabilities they imply — including technologies no BirkNext analyzer reads (Java, Python, Maven,
/// Kafka …), so they are reported as tool limitations instead of silently dropped. Reads archive path names, project files and the
/// configuration/evidence files already held in memory; never stores content. Detection ≠ analysis: a detected technology says nothing
/// about how well BirkNext analyzed it — the <see cref="TechnologySupportRegistry"/> does.
/// </summary>
public static class TechnologyInventory
{
    public const int Version = 1;
    private const int MaxEvidence = 5;

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "lang.csharp", [".fs"] = "lang.fsharp", [".vb"] = "lang.vbnet", [".java"] = "lang.java", [".kt"] = "lang.kotlin", [".kts"] = "lang.kotlin",
        [".ts"] = "lang.typescript", [".tsx"] = "lang.typescript", [".js"] = "lang.javascript", [".jsx"] = "lang.javascript", [".mjs"] = "lang.javascript",
        [".py"] = "lang.python", [".go"] = "lang.go", [".rb"] = "lang.ruby", [".php"] = "lang.php", [".sql"] = "lang.sql",
        [".pks"] = "lang.plsql", [".pkb"] = "lang.plsql", [".pls"] = "lang.plsql", [".plsql"] = "lang.plsql",
    };

    /// <summary>Content markers in project, configuration and evidence files (package ids, connection-string schemes, config sections). Inferred evidence.</summary>
    private static readonly (string TechnologyId, Regex Pattern)[] ContentMarkers =
    [
        ("integration.kafka", new(@"(?i)\bConfluent\.Kafka\b|\bspring\.kafka\b|\bbootstrap[._-]servers\b|\bkafka\b", RegexOptions.Compiled)),
        ("integration.rabbitmq", new(@"(?i)\bRabbitMQ\b|\bamqps?://|\bspring\.rabbitmq\b", RegexOptions.Compiled)),
        ("integration.activemq", new(@"(?i)\bactivemq\b|\bspring\.jms\b|\bApache\.NMS\b", RegexOptions.Compiled)),
        ("integration.eventhub", new(@"(?i)\bAzure\.Messaging\.EventHubs\b|\bservicebus\.windows\.net/[^""\s]*;EntityPath|\bEventHub", RegexOptions.Compiled)),
        ("integration.servicebus", new(@"(?i)\bAzure\.Messaging\.ServiceBus\b|\bWolverineFx\.AzureServiceBus\b|\bServiceBus\b", RegexOptions.Compiled)),
        ("integration.soap", new(@"(?i)\bSystem\.ServiceModel\b|\bCoreWCF\b|\bspring-ws\b|\bjaxws\b|\bsoapenv\b", RegexOptions.Compiled)),
        ("integration.grpc", new(@"(?i)\bGrpc\.AspNetCore\b|\bGrpc\.Net\.Client\b|\bio\.grpc\b", RegexOptions.Compiled)),
        ("integration.s3", new(@"(?i)\bAWSSDK\.S3\b|\bboto3\b|\bs3://", RegexOptions.Compiled)),
        ("db.oracle", new(@"(?i)\bOracle\.ManagedDataAccess\b|\bOracle\.EntityFrameworkCore\b|\bjdbc:oracle\b|\bojdbc\b", RegexOptions.Compiled)),
        ("db.postgresql", new(@"(?i)\bNpgsql\b|\bjdbc:postgresql\b|\bpostgres(?:ql)?://", RegexOptions.Compiled)),
        ("db.sqlserver", new(@"(?i)\bMicrosoft\.EntityFrameworkCore\.SqlServer\b|\bMicrosoft\.Data\.SqlClient\b|\bSystem\.Data\.SqlClient\b|\bjdbc:sqlserver\b", RegexOptions.Compiled)),
        ("db.mysql", new(@"(?i)\bMySql\.Data\b|\bPomelo\.EntityFrameworkCore\.MySql\b|\bjdbc:mysql\b|\bmysql://", RegexOptions.Compiled)),
        ("db.mongodb", new(@"(?i)\bMongoDB\.Driver\b|\bmongodb(?:\+srv)?://|\bspring\.data\.mongodb\b", RegexOptions.Compiled)),
        ("db.cosmosdb", new(@"(?i)\bMicrosoft\.Azure\.Cosmos\b|\.documents\.azure\.com", RegexOptions.Compiled)),
        ("framework.blazor-wasm", new(@"(?i)Microsoft\.NET\.Sdk\.BlazorWebAssembly|Microsoft\.AspNetCore\.Components\.WebAssembly\b", RegexOptions.Compiled)),
        ("framework.aspnetcore", new(@"(?i)Sdk=""Microsoft\.NET\.Sdk\.Web""|Microsoft\.AspNetCore\.App\b", RegexOptions.Compiled)),
        ("framework.dotnet-framework", new(@"(?i)<TargetFrameworkVersion>\s*v[1-4]\.", RegexOptions.Compiled)),
        ("framework.spring", new(@"(?i)\bspring\.(?:application|datasource|profiles)\b|\bspring-boot\b", RegexOptions.Compiled)),
        ("framework.airflow", new(@"(?i)\bapache[/-]airflow\b|\bAIRFLOW__", RegexOptions.Compiled)),
        ("framework.browser-frontend", new(@"(?i)""(?:react|@angular/core|vue|svelte|next)""\s*:", RegexOptions.Compiled)),
        ("cloud.aws", new(@"(?i)\bAWSSDK\.|\bamazonaws\.com\b|\baws_region\b|\bAWS::", RegexOptions.Compiled)),
        ("cloud.azure", new(@"(?i)\bAzure\.(?:Identity|Storage|Messaging|Security)\b|\.azurewebsites\.net\b|\.azure\.com\b|\bazurerm\b", RegexOptions.Compiled)),
        ("cloud.gcp", new(@"(?i)\bGoogle\.Cloud\.|\bgoogleapis\.com\b", RegexOptions.Compiled)),
    ];

    public static SourceTechnologyCoverage Detect(IqrSourceArchiveReader.Workspace workspace, ArchitectureSnapshot? architecture = null, SourceEvidenceDomainsSnapshot? domains = null)
    {
        var found = new Dictionary<string, Builder>(StringComparer.Ordinal);
        void Add(string id, DetectionConfidence confidence, string evidence)
        {
            if (TechnologySupportRegistry.Find(id) is not { } descriptor) return;
            if (!found.TryGetValue(id, out var b)) found[id] = b = new Builder(descriptor);
            b.Files++;
            if (confidence < b.Confidence) b.Confidence = confidence;
            if (b.Evidence.Count < MaxEvidence && !b.Evidence.Contains(evidence)) b.Evidence.Add(evidence);
        }

        var paths = workspace.AllPaths ?? workspace.Files.Select(f => f.Path).Concat((workspace.ConfigurationFiles ?? []).Select(f => f.Path))
            .Concat((workspace.EvidenceFiles ?? []).Select(f => f.Path)).ToList();
        var unsupportedSource = 0;
        foreach (var path in paths)
        {
            var file = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            var safe = IqrSourceArchiveReader.SafeLabel(path);
            if (LanguageByExtension.TryGetValue(extension, out var language))
            {
                Add(language, DetectionConfidence.Confirmed, safe);
                if (TechnologySupportRegistry.Find(language) is { } d && !TechnologySupportRegistry.IsSupported(d.SourceAnalysis)) unsupportedSource++;
            }
            switch (file.ToLowerInvariant())
            {
                case "pom.xml": case "build.gradle": case "build.gradle.kts": case "settings.gradle": Add("dependency.maven", DetectionConfidence.Confirmed, safe); break;
                case "package.json": case "package-lock.json": case "yarn.lock": case "pnpm-lock.yaml": Add("dependency.npm", DetectionConfidence.Confirmed, safe); break;
                case "requirements.txt": case "pyproject.toml": case "pipfile": case "setup.py": case "poetry.lock": Add("dependency.pip", DetectionConfidence.Confirmed, safe); break;
                case "packages.config": case "directory.packages.props": case "packages.lock.json": Add("dependency.nuget", DetectionConfidence.Confirmed, safe); break;
                case "dockerfile": case "docker-compose.yml": case "docker-compose.yaml": case "compose.yml": case "compose.yaml": Add("dependency.docker", DetectionConfidence.Confirmed, safe); break;
                case ".gitlab-ci.yml": Add("pipeline.gitlab", DetectionConfidence.Confirmed, safe); break;
                case "jenkinsfile": Add("pipeline.jenkins", DetectionConfidence.Confirmed, safe); break;
                case "bom.json": case "bom.xml": Add("dependency.sbom", DetectionConfidence.Confirmed, safe); break;
            }
            if (file.EndsWith(".cdx.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".spdx.json", StringComparison.OrdinalIgnoreCase))
                Add("dependency.sbom", DetectionConfidence.Confirmed, safe);
            if (path.Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase) && extension is ".yml" or ".yaml") Add("pipeline.github-actions", DetectionConfidence.Confirmed, safe);
            if (extension.Equals(".wsdl", StringComparison.OrdinalIgnoreCase)) { Add("contract.wsdl", DetectionConfidence.Confirmed, safe); Add("integration.soap", DetectionConfidence.StronglySupported, safe); }
            if (extension is ".asmx" or ".svc") Add("integration.soap", DetectionConfidence.StronglySupported, safe);
            if (extension is ".avsc" or ".avdl") Add("contract.avro", DetectionConfidence.Confirmed, safe);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)) Add("dependency.nuget", DetectionConfidence.StronglySupported, safe);
            if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) && path.Split('/').Any(s => s.Equals("dags", StringComparison.OrdinalIgnoreCase)))
                Add("framework.airflow", DetectionConfidence.Inferred, safe);
            if (file.Equals("index.html", StringComparison.OrdinalIgnoreCase) && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                Add("framework.browser-frontend", DetectionConfidence.Inferred, safe);
            if (file.Equals("cloudformation.yml", StringComparison.OrdinalIgnoreCase) || file.Equals("template.yaml", StringComparison.OrdinalIgnoreCase) || file.Equals("cdk.json", StringComparison.OrdinalIgnoreCase))
                Add("cloud.aws", DetectionConfidence.Inferred, safe);
        }
        if (found.ContainsKey("lang.java") && (found.ContainsKey("dependency.maven") || paths.Any(p => p.EndsWith("application.properties", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("application.yml", StringComparison.OrdinalIgnoreCase))))
            Add("framework.spring", DetectionConfidence.Inferred, "Java sources with Maven/Gradle or Spring-style application configuration");

        // Content markers: project files, configuration and evidence files only (already in memory). The file path is the evidence, never the text.
        var contentFiles = workspace.Files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            .Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? []);
        foreach (var file in contentFiles)
            foreach (var (id, pattern) in ContentMarkers)
                if (pattern.IsMatch(file.Content)) Add(id, DetectionConfidence.Inferred, IqrSourceArchiveReader.SafeLabel(file.Path));

        // Evidence the analyzers already produced (stronger than markers).
        foreach (var p in domains?.CiCd.Pipelines ?? [])
            Add(p.Platform switch
            {
                PipelinePlatform.GitHubActions => "pipeline.github-actions", PipelinePlatform.GitLabCi => "pipeline.gitlab", PipelinePlatform.Jenkins => "pipeline.jenkins",
                _ => "pipeline.azuredevops",
            }, DetectionConfidence.Confirmed, IqrSourceArchiveReader.SafeLabel(p.File));
        foreach (var provider in domains?.Infrastructure.Providers ?? [])
        {
            var name = provider.Name.ToLowerInvariant();
            var cloud = name is "azurerm" or "azuread" or "azapi" ? "cloud.azure" : name == "aws" ? "cloud.aws" : name is "google" or "google-beta" ? "cloud.gcp" : name is "kubernetes" or "helm" ? "cloud.kubernetes" : null;
            if (cloud is not null) Add(cloud, DetectionConfidence.Confirmed, IqrSourceArchiveReader.SafeLabel(provider.File));
        }
        foreach (var format in domains?.Infrastructure.Formats ?? [])
        {
            var label = format.ToString();
            if (label.Contains("Bicep", StringComparison.OrdinalIgnoreCase) || label.Contains("Arm", StringComparison.OrdinalIgnoreCase)) Add("cloud.azure", DetectionConfidence.Confirmed, label);
            if (label.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase) || label.Contains("Helm", StringComparison.OrdinalIgnoreCase)) Add("cloud.kubernetes", DetectionConfidence.Confirmed, label);
        }
        foreach (var contract in domains?.Contracts.Contracts ?? [])
        {
            var id = contract.Type switch
            {
                SourceContractType.OpenApi => "contract.openapi", SourceContractType.GraphQlSchema or SourceContractType.GraphQlOperations => "contract.graphql",
                SourceContractType.AsyncApi => "contract.asyncapi", SourceContractType.XmlSchema => "contract.xsd", SourceContractType.Protobuf => "contract.protobuf", _ => null,
            };
            if (id is not null) Add(id, DetectionConfidence.Confirmed, IqrSourceArchiveReader.SafeLabel(contract.Name));
        }

        var capabilities = new List<CapabilityEvidence>();
        void Cap(Capability c, DetectionConfidence confidence, string basis, string? technology = null, string? component = null)
        {
            if (!capabilities.Any(e => e.Capability == c && e.Technology == technology && e.Component == component))
                capabilities.Add(new CapabilityEvidence(c, confidence, basis, technology, component));
        }
        foreach (var b in found.Values)
            foreach (var c in b.Descriptor.Provides ?? [])
                Cap(c, b.Confidence, $"{b.Descriptor.DisplayName} detected ({b.Files} file(s))", b.Descriptor.DisplayName);
        if (found.Keys.Any(k => k.StartsWith("lang.", StringComparison.Ordinal))) Cap(Capability.SourceCode, DetectionConfidence.Confirmed, "Source files in the archive");
        foreach (var i in architecture?.Interfaces ?? [])
        {
            if (i.Type.Contains("REST", StringComparison.OrdinalIgnoreCase) || i.Type.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
                Cap(Capability.RestApi, DetectionConfidence.Confirmed, $"{i.Type} in source architecture", "HTTP", i.ComponentId);
            else if (i.Type.Contains("GraphQL", StringComparison.OrdinalIgnoreCase))
                Cap(Capability.GraphQlApi, DetectionConfidence.Confirmed, $"{i.Type} in source architecture", "GraphQL", i.ComponentId);
        }
        if (found.ContainsKey("contract.openapi")) Cap(Capability.RestApi, DetectionConfidence.StronglySupported, "OpenAPI contract in source", "OpenAPI");
        if (found.ContainsKey("contract.graphql")) Cap(Capability.GraphQlApi, DetectionConfidence.StronglySupported, "GraphQL schema in source", "GraphQL");
        if (domains?.Infrastructure.Status is SourceDomainStatus.Complete or SourceDomainStatus.Partial) Cap(Capability.InfrastructureAsCode, DetectionConfidence.Confirmed, "Infrastructure as Code in source");
        if (domains?.CiCd.Pipelines.Count > 0) Cap(Capability.Pipeline, DetectionConfidence.Confirmed, "Pipeline definitions in source");

        var technologies = found.Values.Select(b => new DetectedTechnology(b.Descriptor.TechnologyId, b.Descriptor.DisplayName, b.Descriptor.Area, b.Confidence, b.Files, b.Evidence))
            .OrderBy(t => t.Area).ThenBy(t => t.TechnologyId, StringComparer.Ordinal).ToList();
        var limitations = technologies.Select(t => (t, d: TechnologySupportRegistry.Find(t.TechnologyId)!))
            .Where(x => x.d.Overall == SupportLevel.Unsupported)
            .Select(x => $"{x.t.DisplayName} detected; BirkNext does not analyze it ({x.d.Limitations.TrimEnd('.')}). This is a tool limitation, not a project finding.")
            .ToList();
        return new SourceTechnologyCoverage
        {
            Version = Version, TotalFiles = paths.Count, AnalyzedFiles = workspace.Files.Count + (workspace.ConfigurationFiles?.Count ?? 0) + (workspace.EvidenceFiles?.Count ?? 0),
            UnsupportedSourceFiles = unsupportedSource, Technologies = technologies,
            Capabilities = capabilities.OrderBy(c => c.Capability).ThenBy(c => c.Technology, StringComparer.Ordinal).ToList(), Limitations = limitations,
        };
    }

    private sealed class Builder(TechnologySupportDescriptor descriptor)
    {
        public TechnologySupportDescriptor Descriptor { get; } = descriptor;
        public int Files { get; set; }
        public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Unresolved;
        public List<string> Evidence { get; } = [];
    }
}
