using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>A text file of the snapshot with its classified role and technology. Content lives in memory for this analysis only.</summary>
internal sealed record EvidenceFile(string Path, string Content, SourceFileRole Role, string Technology)
{
    private int[]? _lines;
    public int Line(int index)
    {
        _lines ??= Content.Select((c, i) => (c, i)).Where(x => x.c == '\n').Select(x => x.i).ToArray();
        var pos = Array.BinarySearch(_lines, index);
        return (pos < 0 ? ~pos : pos) + 1;
    }
    public string Directory => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>One configuration value as read from source, kept in memory during ingestion only (never persisted). Consumers that need the raw
/// public identifier (Security Expectations) read it from here at ingestion time; everything persisted is the redacted <see cref="ConfigurationEntry"/>.</summary>
internal sealed record RawConfigurationValue(string File, string Key, string Raw, string Technology, SourceEnvironmentLabel Environment, string? ProjectPath, ConfigurationEntry Entry);

/// <summary>The normalized configuration of one snapshot, in memory: redacted entries plus raw values for ingestion-time consumers.</summary>
internal sealed class SourceConfigurationModel
{
    public List<RawConfigurationValue> Values { get; } = [];
    public IEnumerable<RawConfigurationValue> ForProject(string projectPath) => Values.Where(v => v.ProjectPath == projectPath);
}

/// <summary>Everything the evidence-domain analyzers read and write for ONE immutable snapshot. Domain results are set in registry order.</summary>
internal sealed class SourceEvidenceContext
{
    public Guid SourceSnapshotId { get; init; }
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset ExtractedAt { get; init; }
    public List<EvidenceFile> Files { get; init; } = [];
    public ArchitectureInput Input { get; init; } = new();
    public ArchitectureSnapshot? Architecture { get; init; }
    public DatabaseArchitectureSnapshot? Database { get; init; }
    public SourceObservabilitySnapshot? Observability { get; init; }
    public IntegrationPathEvidence? IntegrationPath { get; init; }

    public InfrastructureEvidence? Infrastructure { get; set; }
    public ConfigurationEvidence? Configuration { get; set; }
    public PipelineEvidence? CiCd { get; set; }
    public ContractEvidence? Contracts { get; set; }
    public CrossDomainEvidence? CrossDomain { get; set; }
    public SourceConfigurationModel ConfigurationModel { get; } = new();
    /// <summary>Raw values other analyzers read (tfvars, Bicep parameters, ConfigMap data, pipeline variables), handed to Configuration in memory so nothing is parsed twice.</summary>
    public List<PendingConfigurationValue> PendingConfiguration { get; } = [];
    public List<DomainCapability> Capabilities { get; } = [];

    public IEnumerable<EvidenceFile> Role(SourceFileRole role) => Files.Where(f => f.Role == role);

    public ArchProject? ProjectOf(string path) =>
        Input.Projects.Where(p => path.StartsWith(p.Directory, StringComparison.Ordinal) && (p.Directory.Length > 0 || !path.Contains('/'))).OrderByDescending(p => p.Directory.Length).FirstOrDefault();

    /// <summary>The Architecture component that owns a file (by its project), or null — never guessed from a folder name.</summary>
    public ArchitectureComponent? ComponentOf(string path) =>
        ProjectOf(path) is { IsTest: false } project ? Architecture?.Components.FirstOrDefault(c => c.SourceProject == project.Path) : null;

    public T Envelope<T>(T result, SourceEvidenceDomain domain, int version) where T : SourceDomainResult => result with
    {
        Domain = domain, SourceSnapshotId = SourceSnapshotId, SourceFingerprint = Fingerprint, AnalyzerVersion = version, ExtractedAt = ExtractedAt,
    };
}

/// <summary>Role and technology per file from path AND content. A YAML file is a pipeline, Kubernetes manifest, OpenAPI/AsyncAPI document,
/// Helm values, Compose file, tool configuration or application configuration — never two of them by default.</summary>
internal static class SourceFileClassifier
{
    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestPath = R(@"(^|/)(tests?|specs?\.tests|[^/]*\.tests?|[^/]*tests)(/|$)");
    private static readonly Regex DocPath = R(@"(^|/)(docs?|documentation|specs|autodoc|wiki)(/|$)");
    private static readonly Regex ToolConfig = R(@"(^|/)(\.vscode|\.idea|\.specify|\.devcontainer|\.husky|\.config|\.claude|\.cursor|\.github/ISSUE_TEMPLATE)(/|$)|(^|/)(tsconfig[^/]*\.json|jsconfig\.json|\.eslintrc[^/]*|\.prettierrc[^/]*|\.stylelintrc[^/]*|renovate\.json5?|dependabot\.ya?ml|codecov\.ya?ml|\.pre-commit-config\.ya?ml|mkdocs\.ya?ml|\.markdownlint[^/]*|omnisharp\.json|stryker-config\.json|\.editorconfig|xunit\.runner\.json|dotnet-tools\.json|\.gitlab/.*template.*)$");
    private static readonly Regex ProjectMetadata = R(@"(\.csproj|\.fsproj|\.vbproj|\.sln|\.slnx|\.props|\.targets)$|(^|/)(package(-lock)?\.json|global\.json|nuget\.config|packages\.lock\.json|yarn\.lock|pnpm-lock\.yaml|Dockerfile)$");

    public static (SourceFileRole Role, string Technology) Classify(string path, string content)
    {
        var name = System.IO.Path.GetFileName(path);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tf" ) return (SourceFileRole.InfrastructureAsCode, "Terraform");
        if (ext is ".tfvars") return (SourceFileRole.InfrastructureAsCode, "Terraform variables");
        if (ext is ".bicep" or ".bicepparam") return (SourceFileRole.InfrastructureAsCode, "Bicep");
        if (name.Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Pipeline, "Jenkins");
        if (ext is ".graphql" or ".graphqls" or ".gql") return (TestPath.IsMatch(path) ? SourceFileRole.Test : SourceFileRole.Contract, "GraphQL");
        if (ext == ".xsd") return (SourceFileRole.Contract, "XML Schema");
        if (ext is ".proto") return (SourceFileRole.Contract, "Protobuf");
        if (ext is ".properties") return (SourceFileRole.Configuration, "Properties file");
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Configuration, "Environment file");
        if (ext is ".cs")
        {
            if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) || path.Contains("/Generated/", StringComparison.OrdinalIgnoreCase)
                || content.StartsWith("// <auto-generated", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Generated, "C#");
            return (TestPath.IsMatch(path) ? SourceFileRole.Test : SourceFileRole.SourceCode, "C#");
        }
        if (ext is ".sql") return (SourceFileRole.SourceCode, "SQL");
        if (ProjectMetadata.IsMatch(path)) return (SourceFileRole.ProjectMetadata, ext is ".json" ? "package manifest" : name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "Dockerfile" : "project file");
        if (ToolConfig.IsMatch(path)) return (SourceFileRole.ToolConfiguration, ext.TrimStart('.'));
        if (ext is ".json") return ClassifyJson(path, name, content);
        if (ext is ".yml" or ".yaml") return ClassifyYaml(path, name, content);
        return (SourceFileRole.Unknown, ext.TrimStart('.'));
    }

    private static (SourceFileRole, string) ClassifyJson(string path, string name, string content)
    {
        var head = content.Length > 4000 ? content[..4000] : content;
        if (Regex.IsMatch(head, @"""openapi""\s*:\s*""3|""swagger""\s*:\s*""2")) return (SourceFileRole.Contract, "OpenAPI");
        if (Regex.IsMatch(head, @"""asyncapi""\s*:")) return (SourceFileRole.Contract, "AsyncAPI");
        if (Regex.IsMatch(head, @"""\$schema""\s*:\s*""[^""]*deploymentTemplate")) return (SourceFileRole.InfrastructureAsCode, "ARM template");
        if (Regex.IsMatch(head, @"""\$schema""\s*:\s*""[^""]*json-schema\.org")) return (SourceFileRole.Contract, "JSON Schema");
        if (name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Configuration, "launchSettings");
        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            return (SourceFileRole.Configuration, path.Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase) ? "Frontend appsettings" : "ASP.NET Core appsettings");
        if (name.Equals(".graphqlrc.json", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Configuration, "GraphQL client configuration");
        if (name.Equals("local.settings.json", StringComparison.OrdinalIgnoreCase) || name.Equals("host.json", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Configuration, "Azure Functions settings");
        if (name.Equals("nswag.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".nswag", StringComparison.OrdinalIgnoreCase) || name.Equals("openapitools.json", StringComparison.OrdinalIgnoreCase))
            return (SourceFileRole.Configuration, "Client generator configuration");
        if (TestPath.IsMatch(path)) return (SourceFileRole.Test, "json");
        if (Regex.IsMatch(name, @"(config|settings)", RegexOptions.IgnoreCase) && !DocPath.IsMatch(path)) return (SourceFileRole.Configuration, "Application configuration");
        return (DocPath.IsMatch(path) ? SourceFileRole.Documentation : SourceFileRole.Unknown, "json");
    }

    private static (SourceFileRole, string) ClassifyYaml(string path, string name, string content)
    {
        var head = content.Length > 6000 ? content[..6000] : content;
        bool Root(string key) => Regex.IsMatch(head, $@"(?m)^{Regex.Escape(key)}\s*:");
        if (Root("openapi") || Root("swagger")) return (SourceFileRole.Contract, "OpenAPI");
        if (Root("asyncapi")) return (SourceFileRole.Contract, "AsyncAPI");
        if (path.Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Pipeline, "GitHub Actions");
        if (name.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.Pipeline, "GitLab CI");
        if (Regex.IsMatch(name, @"^(docker-)?compose(\.[\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)) return (SourceFileRole.Configuration, "Docker Compose");
        if (name.Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.InfrastructureAsCode, "Helm chart");
        if (path.Contains("/templates/", StringComparison.OrdinalIgnoreCase) && head.Contains("{{") && Regex.IsMatch(head, @"(?m)^\s*kind\s*:")) return (SourceFileRole.InfrastructureAsCode, "Helm template");
        if (Regex.IsMatch(head, @"(?m)^apiVersion\s*:") && Regex.IsMatch(head, @"(?m)^kind\s*:")) return (SourceFileRole.InfrastructureAsCode, "Kubernetes manifest");
        if (Regex.IsMatch(name, @"^values([.-][\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)) return (SourceFileRole.Configuration, "Helm values");
        var azure = Regex.IsMatch(name, @"^azure-pipelines", RegexOptions.IgnoreCase) || Regex.IsMatch(path, @"(^|/)(\.pipelines?|\.azuredevops|\.azure-pipelines|pipelines?)/", RegexOptions.IgnoreCase);
        var pipelineShape = (Root("steps") || Root("jobs") || Root("stages") || Root("extends")) && (Root("trigger") || Root("pr") || Root("pool") || Root("parameters") || Root("variables") || Root("resources") || Root("schedules") || Regex.IsMatch(head, @"(?m)^\s*-\s*(task|script|bash|pwsh|powershell|template|checkout|job|stage|deployment)\s*:"));
        if (pipelineShape && (azure || Regex.IsMatch(head, @"(?m)^\s*-\s*(task|template|stage|deployment)\s*:") || Root("trigger") || Root("pr")))
            return (SourceFileRole.Pipeline, "Azure Pipelines");
        if (Root("stages") && Regex.IsMatch(head, @"(?m)^\s+script\s*:") && !azure) return (SourceFileRole.Pipeline, "GitLab CI");
        if (TestPath.IsMatch(path)) return (SourceFileRole.Test, "yaml");
        if (DocPath.IsMatch(path)) return (SourceFileRole.Documentation, "yaml");
        if (Regex.IsMatch(name, @"^(appsettings|application|config|settings)([.-][\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)) return (SourceFileRole.Configuration, "YAML application configuration");
        if (name.Equals("Pulumi.yaml", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Pulumi.", StringComparison.OrdinalIgnoreCase)) return (SourceFileRole.InfrastructureAsCode, "Pulumi");
        return (SourceFileRole.Unknown, "yaml");
    }

    /// <summary>The shared environment normalization (<see cref="SourceEnvironments"/>) — one implementation for analyzers and consumers.</summary>
    public static SourceEnvironmentLabel Environment(string raw) => SourceEnvironments.Normalize(raw);

    /// <summary>An environment named by a file-name suffix ("appsettings.QA.json", "orders-prod.yml", "qa.tfvars") — a naming convention, not a deployment.</summary>
    public static SourceEnvironmentLabel? EnvironmentFromName(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(".auto", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        return SourceEnvironments.FromName(stem);
    }
}

/// <summary>A raw key/value another domain analyzer already read (tfvars, Bicep parameters, ConfigMap data, pipeline variables), handed to the
/// Configuration analyzer in memory. Never persisted; Configuration classifies and redacts it like every other entry.</summary>
internal sealed record PendingConfigurationValue(string File, int Line, string Key, string Raw, string Technology, SourceEnvironmentLabel Environment, bool Sensitive);
