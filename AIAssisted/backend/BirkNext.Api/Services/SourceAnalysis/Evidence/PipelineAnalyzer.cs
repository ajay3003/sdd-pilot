using System.Text.RegularExpressions;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → CI/CD. Reads pipeline DEFINITIONS (Azure Pipelines, GitHub Actions, GitLab CI; Jenkinsfiles by pattern): triggers and path
/// filters, stages, jobs, deployment environments, templates, and per step the tool/command family it runs (build, tests, coverage, scans,
/// SBOM, container, IaC, deploy, migrations). A step's presence means the pipeline intends to run it — never that it ran or passed; execution
/// history is not read. Script text, variable values and secrets are never stored: steps keep a tool label and safe targets, secrets their names.
/// </summary>
internal sealed class PipelineAnalyzer : ISourceEvidenceDomainAnalyzer
{
    /// <summary>v2: job dependencies/conditions, step conditions/continueOnError/strategy phase/artifact publish+consume, pipeline and repository
    /// resources, template uses with level and literal parameters, smoke/API/contract/performance tests, health checks, rollback, manual approval.</summary>
    public const int Version = 2;
    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.CiCd, "CI/CD pipeline analyzer", Version, 1,
        ["Azure Pipelines", "GitHub Actions", "GitLab CI", "Jenkins"], [],
        ["Pipelines", "Triggers", "Stages", "Jobs", "Job and stage dependencies", "Conditions", "Steps by kind", "Artifacts published/consumed", "Pipeline and repository resources",
         "Deployment environments", "Template uses", "Secret references (names)", "Dependency automation"]);

    public void Failed(SourceEvidenceContext context, string reason) => context.CiCd = context.Envelope(new PipelineEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.CiCd, Version);

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Command families inside scripts. Order matters only for the label; one script can yield several steps.
    private static readonly (Regex Pattern, PipelineStepKind Kind, string Tool)[] Commands =
    [
        (R(@"\bdotnet\s+restore\b"), PipelineStepKind.Restore, "dotnet restore"),
        (R(@"\bdotnet\s+build\b"), PipelineStepKind.Build, "dotnet build"),
        (R(@"\bdotnet\s+test\b"), PipelineStepKind.Test, "dotnet test"),
        (R(@"\bdotnet\s+(publish|pack)\b|\b(dotnet\s+)?nuget\s+push\b"), PipelineStepKind.Publish, "dotnet publish/pack"),
        (R(@"\b(npm\s+(ci|install)|yarn(\s+install)?\s*$|pnpm\s+(i|install))\b"), PipelineStepKind.Restore, "npm/yarn install"),
        (R(@"\b(npm\s+run\s+build|ng\s+build|vite\s+build|yarn\s+build)\b"), PipelineStepKind.Build, "frontend build"),
        (R(@"\b(npm\s+(run\s+)?test|jest|vitest|karma|ng\s+test|yarn\s+test)\b"), PipelineStepKind.FrontendTest, "frontend tests"),
        (R(@"\b(playwright\s+test|npx\s+playwright|cypress\s+run|selenium)\b"), PipelineStepKind.E2ETest, "E2E tests"),
        (R(@"\b(axe|pa11y|lighthouse(-ci)?|lhci)\b"), PipelineStepKind.AccessibilityTest, "accessibility/lighthouse"),
        (R(@"(--collect\s*[:=]?\s*""?XPlat Code Coverage|coverlet|/p:CollectCoverage|reportgenerator|--coverage)"), PipelineStepKind.Coverage, "code coverage"),
        (R(@"\b(npm\s+audit|yarn\s+audit|dotnet\s+list\s+[^\n]*--vulnerable|dependency-check|snyk\s+test|osv-scanner|retire\b|dotnet-retire|trivy\s+fs|grype\s+dir)"), PipelineStepKind.DependencyScan, "dependency scan"),
        (R(@"\b(trivy\s+(image|config)|aquasec/trivy|docker\s+scout|grype\s+(?!dir)\S|gitleaks|checkov|tfsec|kics|zap-|owasp\s+zap|semgrep)"), PipelineStepKind.SecurityScan, "security scan"),
        (R(@"\b(cyclonedx|syft|sbom-tool|dotnet\s+CycloneDX)\b"), PipelineStepKind.Sbom, "SBOM generation"),
        (R(@"\b(sonar-scanner|dotnet\s+sonarscanner|codeql|dotnet\s+format|eslint|tflint|stylecop)\b"), PipelineStepKind.StaticAnalysis, "static analysis"),
        (R(@"\bterraform\s+(-chdir=\S+\s+)?(init|validate|plan|fmt)\b"), PipelineStepKind.InfrastructurePlan, "terraform init/plan"),
        (R(@"\bterraform\s+(-chdir=\S+\s+)?(apply|destroy)\b"), PipelineStepKind.InfrastructureDeploy, "terraform apply"),
        (R(@"\b(az\s+deployment\s+(group|sub|mg|tenant)\s+(create|what-if)|az\s+bicep\s+build|New-Az(ResourceGroup)?Deployment)\b"), PipelineStepKind.InfrastructureDeploy, "Bicep/ARM deployment"),
        (R(@"\bhelm\s+(upgrade|install)\b"), PipelineStepKind.ApplicationDeploy, "helm upgrade"),
        (R(@"\bkubectl\s+(apply|rollout|set\s+image)\b"), PipelineStepKind.ApplicationDeploy, "kubectl apply"),
        (R(@"\b(az\s+containerapp\s+(update|create|up)|az\s+webapp\s+(deploy|deployment|config\s+container\s+set)|az\s+functionapp\s+(deployment|config)|func\s+azure\s+functionapp\s+publish)\b"), PipelineStepKind.ApplicationDeploy, "Azure CLI deploy"),
        (R(@"\b(docker\s+(buildx\s+)?build|az\s+acr\s+build|podman\s+build|buildah\s+bud)\b"), PipelineStepKind.ContainerBuild, "container build"),
        (R(@"\b(docker|podman)\s+push\b"), PipelineStepKind.ContainerPush, "container push"),
        // Generating a migration script or bundle is build work; applying one (database update, sqlcmd, flyway/liquibase, running the bundle) is a deployment.
        (R(@"\bdotnet\s+ef\s+migrations\s+(script|bundle)\b"), PipelineStepKind.Build, "migration bundle/script build"),
        (R(@"\b(dotnet\s+ef\s+database\s+update|sqlcmd|flyway\s+(migrate|repair)|liquibase\s+update|\./efbundle|efbundle\.exe|\befbundle\s+--)"), PipelineStepKind.DatabaseMigration, "database migration"),
        (R(@"\baz\s+keyvault\s+secret\s+(show|download)\b"), PipelineStepKind.SecretRetrieval, "Key Vault secret read"),
        (R(@"\b(newman\s+run|karate\b|postman\s+collection)"), PipelineStepKind.ApiTest, "API tests"),
        (R(@"\b(pact(-broker)?\s+(verify|can-i-deploy|publish)|schemathesis|openapi-diff|oasdiff|graphql-inspector)"), PipelineStepKind.ContractTest, "contract checks"),
        (R(@"\b(k6\s+run|jmeter\s+-n|artillery\s+run|locust\b|nbomber)"), PipelineStepKind.PerformanceTest, "performance tests"),
        (R(@"\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr)\b[^\n]*(/health|/healthz|/ready|/readiness|/alive|/liveness|/ping)\b"), PipelineStepKind.HealthCheck, "health check"),
        (R(@"\b(kubectl\s+rollout\s+undo|helm\s+rollback)\b"), PipelineStepKind.Rollback, "rollback"),
        (R(@"\baz\s+webapp\s+deployment\s+slot\s+swap\b"), PipelineStepKind.ApplicationDeploy, "slot swap"),
    ];

    private static readonly Dictionary<string, (PipelineStepKind Kind, string Tool)> Tasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VSTest"] = (PipelineStepKind.Test, "VSTest"), ["PublishTestResults"] = (PipelineStepKind.Test, "PublishTestResults"),
        ["PublishCodeCoverageResults"] = (PipelineStepKind.Coverage, "PublishCodeCoverageResults"), ["TerraformInstaller"] = (PipelineStepKind.InfrastructurePlan, "terraform install"),
        ["AzureResourceManagerTemplateDeployment"] = (PipelineStepKind.InfrastructureDeploy, "ARM/Bicep deployment task"), ["AzureResourceGroupDeployment"] = (PipelineStepKind.InfrastructureDeploy, "ARM deployment task"),
        ["HelmDeploy"] = (PipelineStepKind.ApplicationDeploy, "Helm"), ["Kubernetes"] = (PipelineStepKind.ApplicationDeploy, "kubectl task"), ["KubernetesManifest"] = (PipelineStepKind.ApplicationDeploy, "Kubernetes manifest task"),
        ["AzureWebApp"] = (PipelineStepKind.ApplicationDeploy, "Azure Web App"), ["AzureRmWebAppDeployment"] = (PipelineStepKind.ApplicationDeploy, "Azure App Service deploy"),
        ["AzureFunctionApp"] = (PipelineStepKind.ApplicationDeploy, "Azure Function App"), ["AzureContainerApps"] = (PipelineStepKind.ApplicationDeploy, "Azure Container Apps"),
        ["AzureStaticWebApp"] = (PipelineStepKind.ApplicationDeploy, "Azure Static Web App"), ["AzureKeyVault"] = (PipelineStepKind.SecretRetrieval, "Azure Key Vault task"),
        ["SonarQubePrepare"] = (PipelineStepKind.StaticAnalysis, "SonarQube"), ["SonarCloudPrepare"] = (PipelineStepKind.StaticAnalysis, "SonarCloud"),
        ["SonarQubeAnalyze"] = (PipelineStepKind.StaticAnalysis, "SonarQube"), ["SonarCloudAnalyze"] = (PipelineStepKind.StaticAnalysis, "SonarCloud"),
        ["AdvancedSecurity-Codeql-Init"] = (PipelineStepKind.StaticAnalysis, "CodeQL"), ["AdvancedSecurity-Codeql-Analyze"] = (PipelineStepKind.StaticAnalysis, "CodeQL"),
        ["AdvancedSecurity-Dependency-Scanning"] = (PipelineStepKind.DependencyScan, "Advanced Security dependency scanning"),
        ["MicrosoftSecurityDevOps"] = (PipelineStepKind.SecurityScan, "Microsoft Security DevOps"), ["WhiteSource"] = (PipelineStepKind.DependencyScan, "Mend/WhiteSource"),
        ["dependency-check-build-task"] = (PipelineStepKind.DependencyScan, "OWASP Dependency-Check"), ["SnykSecurityScan"] = (PipelineStepKind.DependencyScan, "Snyk"),
        ["CredScan"] = (PipelineStepKind.SecurityScan, "CredScan"), ["ComponentGovernanceComponentDetection"] = (PipelineStepKind.DependencyScan, "Component Governance"),
        ["SqlAzureDacpacDeployment"] = (PipelineStepKind.DatabaseMigration, "SQL dacpac deployment"), ["PublishPipelineArtifact"] = (PipelineStepKind.Publish, "Publish pipeline artifact"),
        ["PublishBuildArtifacts"] = (PipelineStepKind.Publish, "Publish build artifacts"),
    };

    private static readonly (Regex Pattern, PipelineStepKind Kind, string Tool)[] Actions =
    [
        (R(@"^github/codeql-action/"), PipelineStepKind.StaticAnalysis, "CodeQL"), (R(@"^aquasecurity/trivy-action"), PipelineStepKind.SecurityScan, "Trivy"),
        (R(@"^anchore/(sbom|syft)-action"), PipelineStepKind.Sbom, "SBOM action"), (R(@"^actions/dependency-review-action"), PipelineStepKind.DependencyScan, "Dependency review"),
        (R(@"^snyk/actions"), PipelineStepKind.DependencyScan, "Snyk"), (R(@"^docker/build-push-action"), PipelineStepKind.ContainerBuild, "docker build-push"),
        (R(@"^azure/webapps-deploy"), PipelineStepKind.ApplicationDeploy, "Azure Web App deploy"), (R(@"^azure/functions-action"), PipelineStepKind.ApplicationDeploy, "Azure Functions deploy"),
        (R(@"^azure/container-apps-deploy-action"), PipelineStepKind.ApplicationDeploy, "Container Apps deploy"), (R(@"^azure/arm-deploy"), PipelineStepKind.InfrastructureDeploy, "ARM/Bicep deploy"),
        (R(@"^azure/k8s-deploy"), PipelineStepKind.ApplicationDeploy, "Kubernetes deploy"), (R(@"^hashicorp/setup-terraform"), PipelineStepKind.InfrastructurePlan, "terraform setup"),
        (R(@"^sonarsource/"), PipelineStepKind.StaticAnalysis, "Sonar"), (R(@"^microsoft/security-devops-action"), PipelineStepKind.SecurityScan, "Microsoft Security DevOps"),
        (R(@"^actions/upload-artifact"), PipelineStepKind.Publish, "upload artifact"), (R(@"^dorny/test-reporter|^EnricoMi/publish-unit-test-result"), PipelineStepKind.Test, "test report"),
        (R(@"^azure/login"), PipelineStepKind.Other, "azure/login"), (R(@"^Azure/get-keyvault-secrets"), PipelineStepKind.SecretRetrieval, "Key Vault secrets"),
    ];

    private sealed class PipelineBuilder(EvidenceFile file, PipelinePlatform platform, SourceEvidenceContext context)
    {
        public EvidenceFile File { get; } = file;
        public PipelinePlatform Platform { get; } = platform;
        public SourceEvidenceContext Context { get; } = context;
        public List<PipelineTrigger> Triggers { get; } = [];
        public List<PipelineStage> Stages { get; } = [];
        public List<string> Jobs { get; } = [];
        public List<PipelineStep> Steps { get; } = [];
        public HashSet<string> Environments { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Artifacts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Templates { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Secrets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Variables { get; } = new(StringComparer.Ordinal);
        public bool Federated { get; set; }
        public bool Approvals { get; set; }
        public List<PipelineJob> JobDetails { get; } = [];
        public List<PipelineResource> Resources { get; } = [];
        public List<PipelineTemplateUse> TemplateUses { get; } = [];
        public List<string> Parameters { get; } = [];
        public Dictionary<string, string> ParameterDefaults { get; } = new(StringComparer.Ordinal);
        /// <summary>Per-step metadata of the YAML step node being read (applies to every step derived from it).</summary>
        public StepMeta Meta { get; set; } = StepMeta.None;

        public void Use(string template, string level, string? stage, string? job, YamlNode node, int order)
        {
            var path = template.Split('@')[0].Trim();
            var alias = template.Contains('@') ? template[(template.IndexOf('@') + 1)..].Trim() : null;
            var external = alias is not null && !alias.Equals("self", StringComparison.OrdinalIgnoreCase);
            var resolved = external || path.Contains("${{") || path.Contains("$(") ? null : ResolveFile(this, path);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (node["parameters"] is { Kind: YamlKind.Map } p)
                foreach (var (key, value) in p.Entries.Take(40))
                    if (value.Kind == YamlKind.Scalar && !SourceEvidenceRedaction.SensitiveKey(key) && SourceEvidenceRedaction.SafeLiteral(key, value.Value) is { } safe)
                        parameters[SourceEvidenceRedaction.SafePath(key)] = safe;
            TemplateUses.Add(new PipelineTemplateUse
            {
                Template = SourceEvidenceRedaction.SafePath(template), ResolvedPath = resolved is null ? null : SourceEvidenceRedaction.SafePath(resolved),
                RepositoryAlias = alias is null ? null : SourceEvidenceRedaction.SafePath(alias), Level = level, Stage = stage is null ? null : SourceEvidenceRedaction.SafePath(stage),
                Job = job is null ? null : SourceEvidenceRedaction.SafePath(job), Parameters = parameters, Line = node.Line, Order = order,
            });
        }
        public SourceEnvironmentLabel FileEnvironment { get; set; } = SourceEnvironmentLabel.Default;

        public void Step(string name, PipelineStepKind kind, string tool, string? stage, string? job, string? workingDirectory, string? environment, IEnumerable<string> targets, int line) =>
            Steps.Add(new PipelineStep
            {
                Id = $"{SourceEvidenceRedaction.SafePath(File.Path)}#{Steps.Count + 1}", Name = SourceEvidenceRedaction.SafePath(name.Length > 120 ? name[..120] : name), Kind = kind,
                Tool = SourceEvidenceRedaction.SafePath(tool), Stage = stage is null ? null : SourceEvidenceRedaction.SafePath(stage), Job = job is null ? null : SourceEvidenceRedaction.SafePath(job),
                WorkingDirectory = workingDirectory is null ? null : SourceEvidenceRedaction.SafePath(workingDirectory), Environment = environment is null ? null : SourceEvidenceRedaction.SafePath(environment),
                Targets = targets.Select(SourceEvidenceRedaction.SafePath).Where(t => t.Length > 0).Distinct().Take(8).ToList(), Line = line,
                Condition = Meta.Condition, ContinueOnError = Meta.ContinueOnError, StrategyPhase = Meta.Phase, Order = Meta.Order,
                ArtifactsPublished = [.. Meta.Published], ArtifactsConsumed = [.. Meta.Consumed],
            });

        public void Variable(string name, string? raw, int line)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Variables.Add(SourceEvidenceRedaction.SafePath(name));
            Context.PendingConfiguration.Add(new(File.Path, line, name, raw ?? "", $"{SourceDomainText.Label(Platform)} variables", FileEnvironment, SourceEvidenceRedaction.SensitiveKey(name)));
        }
    }

    internal sealed record StepMeta(string? Condition, bool ContinueOnError, string? Phase, int Order, List<string> Published, List<PipelineArtifactUse> Consumed)
    {
        public static readonly StepMeta None = new(null, false, null, 0, [], []);
    }

    /// <summary>A condition/expression as display-safe text: secret-shaped tokens redacted, characters restricted, length bounded. Never evaluated.</summary>
    internal static string? SafeExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var e = Regex.Replace(expression.Trim(), @"\s+", " ");
        e = Regex.Replace(e, @"[A-Za-z0-9+/=_\-]{32,}", m => SourceEvidenceRedaction.SecretShaped(m.Value) ? "[redacted]" : m.Value);
        e = Regex.Replace(e, @"[^\p{L}\p{N}_./<>\[\]{}*~^=@$() `:+?,\-#!|'""&]", "_");
        return e.Length > 240 ? e[..240] + "…" : e;
    }

    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        context.Capabilities.AddRange([
            new(SourceEvidenceDomain.CiCd, "Azure Pipelines", DomainSupport.Partial, "Triggers, path filters, pipeline/repository resources, stages and jobs with dependsOn and conditions, deployment environments and strategies, tasks, artifact publish/download and template uses (with literal parameters) via a YAML subset reader; commands inside scripts are recognized by pattern; conditions and template expressions are recorded as written, never evaluated; templates stay separate files (consumers compose them)."),
            new(SourceEvidenceDomain.CiCd, "GitHub Actions", DomainSupport.Partial, "on: triggers, jobs, environments, actions and run commands by pattern; reusable workflows are listed, not expanded."),
            new(SourceEvidenceDomain.CiCd, "GitLab CI", DomainSupport.Partial, "Stages, jobs, environments, rules/only and scripts by pattern; includes are not resolved."),
            new(SourceEvidenceDomain.CiCd, "Jenkins", DomainSupport.Partial, "Declarative stage names, sh/bat commands and triggers by pattern; Groovy is not evaluated."),
        ]);
        var pipelines = new List<PipelineDefinition>();
        var diagnostics = new List<SourceDomainDiagnostic>();
        foreach (var file in context.Role(SourceFileRole.Pipeline))
        {
            ct.ThrowIfCancellationRequested();
            var platform = file.Technology switch { "GitHub Actions" => PipelinePlatform.GitHubActions, "GitLab CI" => PipelinePlatform.GitLabCi, "Jenkins" => PipelinePlatform.Jenkins, _ => PipelinePlatform.AzurePipelines };
            var b = new PipelineBuilder(file, platform, context);
            var template = false;
            if (platform == PipelinePlatform.Jenkins) Jenkins(b);
            else
            {
                var doc = MiniYaml.Documents(file.Content, out var partial).FirstOrDefault();
                if (doc is null || doc.Kind != YamlKind.Map) { diagnostics.Add(new("Parse error", "Pipeline YAML could not be read.", SourceEvidenceRedaction.SafePath(file.Path))); continue; }
                if (partial) diagnostics.Add(new("Partial YAML", "Anchors, aliases or unsupported YAML constructs were not resolved.", SourceEvidenceRedaction.SafePath(file.Path)));
                template = platform == PipelinePlatform.AzurePipelines && !doc.Has("trigger") && !doc.Has("pr") && !doc.Has("schedules") && (doc.Has("parameters") || !doc.Has("pool"))
                    && !doc.Has("name") && (doc.Has("steps") || doc.Has("jobs") || doc.Has("stages"));
                if (!template) b.FileEnvironment = SourceFileClassifier.EnvironmentFromName(file.Name) ?? SourceEnvironmentLabel.Default;
                switch (platform)
                {
                    case PipelinePlatform.AzurePipelines: Azure(b, doc, template); break;
                    case PipelinePlatform.GitHubActions: GitHub(b, doc); break;
                    default: GitLab(b, doc); break;
                }
            }
            foreach (Match m in Regex.Matches(file.Content, @"\$\{\{\s*secrets\.([A-Za-z0-9_]+)\s*\}\}")) b.Secrets.Add($"secret: {m.Groups[1].Value}");
            pipelines.Add(new PipelineDefinition
            {
                Id = SourceEvidenceRedaction.SafePath(file.Path), Platform = platform, File = SourceEvidenceRedaction.SafePath(file.Path), Name = SourceEvidenceRedaction.SafePath(System.IO.Path.GetFileNameWithoutExtension(file.Path)),
                IsTemplate = template, Triggers = b.Triggers, Stages = b.Stages, Jobs = [.. b.Jobs.Distinct()], Steps = b.Steps, Environments = [.. b.Environments.Order(StringComparer.Ordinal)],
                Artifacts = [.. b.Artifacts.Order(StringComparer.Ordinal)], Templates = [.. b.Templates.Order(StringComparer.Ordinal)], SecretReferences = [.. b.Secrets.Order(StringComparer.Ordinal)],
                VariableNames = [.. b.Variables.Order(StringComparer.Ordinal)], UsesFederatedCredentials = b.Federated, ApprovalsDeclared = b.Approvals,
                JobDetails = b.JobDetails, Resources = b.Resources, TemplateUses = b.TemplateUses, Parameters = [.. b.Parameters.Distinct()], ParameterDefaults = b.ParameterDefaults,
            });
        }
        var automation = context.Files.Where(f => Regex.IsMatch(f.Path, @"(^|/)(\.github/)?(renovate\.json5?|\.renovaterc(\.json)?|dependabot\.ya?ml)$", RegexOptions.IgnoreCase))
            .Select(f => SourceEvidenceRedaction.SafePath(f.Path)).ToList();
        context.CiCd = context.Envelope(new PipelineEvidence
        {
            Status = pipelines.Count == 0 ? SourceDomainStatus.NotDetected : SourceDomainStatus.Partial,
            StatusReason = pipelines.Count == 0 ? "No pipeline definition detected in the selected source. CI/CD may be defined elsewhere (another repository or the CI system's UI)."
                : "Pipeline definitions are read statically by a subset reader; templates, expressions and conditions are not evaluated.",
            Technologies = [.. pipelines.Select(p => SourceDomainText.Label(p.Platform)).Distinct().Order(StringComparer.Ordinal)],
            Pipelines = pipelines, DependencyAutomation = automation, Diagnostics = diagnostics,
            Limitations = [SourceDomainText.SourceBoundary, "A step in a pipeline definition means the pipeline intends to run it — not that it ran or passed. Execution history is not read."],
        }, SourceEvidenceDomain.CiCd, Version);
    }

    // ── Azure Pipelines ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Azure(PipelineBuilder b, YamlNode doc, bool template)
    {
        Trigger(b, doc["trigger"], "push", defaultWhenMissing: !template);
        Trigger(b, doc["pr"], "pull-request", defaultWhenMissing: false);
        foreach (var schedule in doc.List("schedules"))
            b.Triggers.Add(new PipelineTrigger { Type = "schedule", Schedule = SourceEvidenceRedaction.SafePath(schedule.Str("cron") ?? ""), BranchesInclude = Branches(schedule["branches"], "include"), Line = schedule.Line });
        foreach (var resource in doc["resources"]?.List("pipelines") ?? [])
        {
            var trigger = resource["trigger"];
            var declared = trigger is not null && trigger.Value is not ("none" or "false");
            if (declared) b.Triggers.Add(new PipelineTrigger { Type = "pipeline-resource", BranchesInclude = [SourceEvidenceRedaction.SafePath(resource.Str("source") ?? resource.Str("pipeline") ?? "")], Line = resource.Line });
            b.Resources.Add(new PipelineResource
            {
                Kind = "pipeline", Alias = SourceEvidenceRedaction.SafePath(resource.Str("pipeline") ?? ""), Source = SourceEvidenceRedaction.SafePath(resource.Str("source") ?? resource.Str("pipeline") ?? ""),
                Project = resource.Str("project") is { } project ? SourceEvidenceRedaction.SafePath(project) : null, TriggerDeclared = declared,
                TriggerBranches = trigger is { Kind: YamlKind.Map } tm ? Branches(tm["branches"], "include") : declared ? ["(all branches)"] : [], Line = resource.Line,
            });
        }
        foreach (var repository in doc["resources"]?.List("repositories") ?? [])
            b.Resources.Add(new PipelineResource
            {
                Kind = "repository", Alias = SourceEvidenceRedaction.SafePath(repository.Str("repository") ?? ""), Source = SourceEvidenceRedaction.SafePath(repository.Str("name") ?? ""),
                RepositoryType = repository.Str("type") is { } type ? SourceEvidenceRedaction.SafePath(type) : null, Ref = repository.Str("ref") is { } rf ? SourceEvidenceRedaction.SafePath(rf) : null,
                TriggerDeclared = repository.Has("trigger") && repository["trigger"]?.Value is not ("none" or "false"), Line = repository.Line,
            });
        foreach (var parameter in doc.List("parameters"))
            if (parameter.Str("name") is { } pn)
            {
                b.Parameters.Add(SourceEvidenceRedaction.SafePath(pn));
                if (parameter["default"] is { Kind: YamlKind.Scalar } d && !SourceEvidenceRedaction.SensitiveKey(pn) && SourceEvidenceRedaction.SafeLiteral(pn, d.Value) is { } safe)
                    b.ParameterDefaults[SourceEvidenceRedaction.SafePath(pn)] = safe;
            }
        if (doc["parameters"] is { Kind: YamlKind.Map } parameterMap) foreach (var (pn, _) in parameterMap.Entries) b.Parameters.Add(SourceEvidenceRedaction.SafePath(pn));
        Variables(b, doc["variables"]);
        if (doc["extends"] is { } ext && ext.Str("template") is { } extends) { b.Templates.Add(Resolve(b, extends)); b.Use(extends, "extends", null, null, ext, 0); }
        var stageOrder = 0;
        foreach (var stage in doc.List("stages"))
        {
            if (stage.Str("template") is { } t)
            {
                b.Templates.Add(Resolve(b, t)); b.Use(t, "stages", null, null, stage, stageOrder++);
                b.Step($"template {t}", PipelineStepKind.Template, "template", null, null, null, null, [Resolve(b, t)], stage.Line); continue;
            }
            var name = stage.Str("stage") ?? "(stage)";
            var jobs = stage.List("jobs").ToList();
            var deployment = jobs.Any(j => j.Has("deployment"));
            var env = jobs.Select(EnvironmentName).FirstOrDefault(e => e is not null);
            b.Stages.Add(new PipelineStage(SourceEvidenceRedaction.SafePath(name), stage.Str("displayName") is { } d ? SourceEvidenceRedaction.SafePath(d) : null,
                stage.List("dependsOn").Select(x => SourceEvidenceRedaction.SafePath(x.Value ?? "")).Where(x => x.Length > 0).ToList(), deployment, env is null ? null : SourceEvidenceRedaction.SafePath(env), stage.Line)
            { DependsOnDeclared = stage.Has("dependsOn"), Condition = SafeExpression(stage.Str("condition")), Order = stageOrder++ });
            Variables(b, stage["variables"]);
            var jobOrder = 0;
            foreach (var job in jobs) Job(b, job, name, jobOrder++);
        }
        var topJob = 0;
        foreach (var job in doc.List("jobs")) Job(b, job, null, topJob++);
        Steps(b, doc.List("steps"), null, null, null);
    }

    private static string? EnvironmentName(YamlNode job) => job["environment"] switch { { Kind: YamlKind.Scalar } s => s.Value, { Kind: YamlKind.Map } m => m.Str("name"), _ => null };

    private static void Job(PipelineBuilder b, YamlNode job, string? stage, int order = 0)
    {
        if (job.Str("template") is { } t)
        {
            b.Templates.Add(Resolve(b, t)); b.Use(t, "jobs", stage, null, job, order);
            b.Step($"template {t}", PipelineStepKind.Template, "template", stage, null, null, null, [Resolve(b, t)], job.Line); return;
        }
        var name = job.Str("job") ?? job.Str("deployment") ?? "(job)";
        b.Jobs.Add(SourceEvidenceRedaction.SafePath(name));
        var env = EnvironmentName(job);
        if (env is not null) b.Environments.Add(SourceEvidenceRedaction.SafePath(env));
        var strategyName = job["strategy"] is { Kind: YamlKind.Map } sm ? sm.Entries.Select(e => e.Key).FirstOrDefault() : null;
        b.JobDetails.Add(new PipelineJob
        {
            Name = SourceEvidenceRedaction.SafePath(name), DisplayName = job.Str("displayName") is { } dn ? SourceEvidenceRedaction.SafePath(dn) : null,
            Stage = stage is null ? null : SourceEvidenceRedaction.SafePath(stage), DependsOn = job.List("dependsOn").Select(x => SourceEvidenceRedaction.SafePath(x.Value ?? "")).Where(x => x.Length > 0).ToList(),
            DependsOnDeclared = job.Has("dependsOn"), Condition = SafeExpression(job.Str("condition")), Deployment = job.Has("deployment"),
            Environment = env is null ? null : SourceEvidenceRedaction.SafePath(env), Strategy = strategyName is null ? null : SourceEvidenceRedaction.SafePath(strategyName),
            ContinueOnError = job.Str("continueOnError") is "true" or "True", Order = order, Line = job.Line,
        });
        Variables(b, job["variables"]);
        var stepOrder = Steps(b, job.List("steps"), stage, name, env, null, 0);
        // Deployment strategies nest their steps (runOnce/rolling/canary → preDeploy/deploy/routeTraffic/postRouteTraffic/on.success|failure).
        if (job["strategy"] is { Kind: YamlKind.Map } strategy)
            foreach (var (_, phaseSet) in strategy.Entries)
                foreach (var (phaseName, phase) in phaseSet.Entries)
                {
                    stepOrder = Steps(b, phase.List("steps"), stage, name, env, phaseName, stepOrder);
                    foreach (var (outcomeName, outcome) in phase.Entries.Where(e => e.Value.Kind == YamlKind.Map))
                        stepOrder = Steps(b, outcome.List("steps"), stage, name, env, $"{phaseName}.{outcomeName}", stepOrder);
                }
    }

    private static void Steps(PipelineBuilder b, IEnumerable<YamlNode> steps, string? stage, string? job, string? environment) => Steps(b, steps, stage, job, environment, null, 0);

    /// <summary>Reads the steps of one job (or strategy phase) in order; returns the next step position.</summary>
    private static int Steps(PipelineBuilder b, IEnumerable<YamlNode> steps, string? stage, string? job, string? environment, string? phase, int order)
    {
        foreach (var step in steps.Where(s => s.Kind == YamlKind.Map))
        {
            var display = step.Str("displayName") ?? step.Str("name");
            var wd = step.Str("workingDirectory") ?? step["inputs"]?.Str("workingDirectory");
            var meta = new StepMeta(SafeExpression(step.Str("condition")), step.Str("continueOnError") is "true" or "True", phase is null ? null : SourceEvidenceRedaction.SafePath(phase), order++, [], []);
            b.Meta = meta;
            try
            {
            if (step.Str("template") is { } t)
            {
                b.Templates.Add(Resolve(b, t)); b.Use(t, "steps", stage, job, step, meta.Order);
                b.Step(display ?? $"template {t}", PipelineStepKind.Template, "template", stage, job, wd, environment, [Resolve(b, t)], step.Line);
                continue;
            }
            if (step.Str("publish") is { } publish)
            {
                var artifact = SourceEvidenceRedaction.SafePath(step.Str("artifact") ?? publish);
                b.Artifacts.Add(artifact);
                b.Meta = meta with { Published = [artifact] };
                b.Step(display ?? "publish artifact", PipelineStepKind.Publish, "publish", stage, job, wd, environment, [], step.Line);
                continue;
            }
            if (step.Str("download") is { } download)
            {
                if (download is "none") { b.Meta = meta with { Consumed = [new("(none)", "none")] }; b.Step(display ?? "download none", PipelineStepKind.ArtifactDownload, "download none", stage, job, wd, environment, [], step.Line); continue; }
                var artifact = step.Str("artifact") is { } da ? SourceEvidenceRedaction.SafePath(da) : "(all artifacts)";
                b.Meta = meta with { Consumed = [new(artifact, SourceEvidenceRedaction.SafePath(download))] };
                b.Step(display ?? $"download {download}", PipelineStepKind.ArtifactDownload, "download", stage, job, wd, environment, [], step.Line);
                continue;
            }
            if (step.Has("checkout")) continue;
            if (step.Str("task") is { } task)
            {
                var taskName = task.Split('@')[0];
                if (taskName.Equals("ManualValidation", StringComparison.OrdinalIgnoreCase))
                {
                    b.Approvals = true;
                    b.Step(display ?? task, PipelineStepKind.ManualApproval, "ManualValidation", stage, job, wd, environment, [], step.Line);
                    continue;
                }
                var inputs = step["inputs"];
                if (taskName is "PublishPipelineArtifact" or "PublishBuildArtifacts")
                {
                    var artifact = SourceEvidenceRedaction.SafePath(inputs?.Str("artifact") ?? inputs?.Str("artifactName") ?? inputs?.Str("ArtifactName") ?? "drop");
                    b.Artifacts.Add(artifact);
                    b.Meta = meta with { Published = [artifact] };
                }
                if (taskName is "DownloadPipelineArtifact" or "DownloadBuildArtifacts")
                {
                    var source = inputs?.Str("source") ?? inputs?.Str("buildType") ?? "current";
                    var from = source is "current" ? "current" : (inputs?.Str("pipeline") ?? inputs?.Str("definition")) is { } def ? $"pipeline:{def}" : source;
                    b.Meta = meta with { Consumed = [new(SourceEvidenceRedaction.SafePath(inputs?.Str("artifact") ?? inputs?.Str("artifactName") ?? "(all artifacts)"), SourceEvidenceRedaction.SafePath(from))] };
                    b.Step(display ?? task, PipelineStepKind.ArtifactDownload, "Download pipeline artifact", stage, job, wd, environment, [], step.Line);
                    continue;
                }
                if (taskName.Equals("AzureKeyVault", StringComparison.OrdinalIgnoreCase)) b.Secrets.Add($"Key Vault task: {SourceEvidenceRedaction.SafePath(inputs?.Str("KeyVaultName") ?? "(vault)")}");
                if (inputs?.Str("azureSubscription") is not null || inputs?.Str("connectedServiceNameARM") is not null || inputs?.Str("environmentServiceNameAzureRM") is not null)
                    b.Secrets.Add($"service connection: {SourceEvidenceRedaction.SafePath(inputs?.Str("azureSubscription") ?? inputs?.Str("connectedServiceNameARM") ?? inputs?.Str("environmentServiceNameAzureRM") ?? "")}");
                var script = inputs?.Str("inlineScript") ?? inputs?.Str("script") ?? inputs?.Str("Inline") ?? inputs?.Str("arguments");
                var command = inputs?.Str("command")?.ToLowerInvariant();
                if (Regex.IsMatch(taskName, @"^Terraform(TaskV\d|CLI|Task)$", RegexOptions.IgnoreCase) || taskName.StartsWith("TerraformTask", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = command is "apply" or "destroy" ? PipelineStepKind.InfrastructureDeploy : PipelineStepKind.InfrastructurePlan;
                    b.Step(display ?? task, kind, $"terraform {command ?? "(command)"}", stage, job, wd, environment, TerraformTargets(wd, inputs?.Str("commandOptions")), step.Line);
                }
                else if (taskName.Equals("DotNetCoreCLI", StringComparison.OrdinalIgnoreCase) && command is not null)
                {
                    var kind = command switch { "test" => TestKind($"{inputs?.Str("projects")} {display}"), "build" => PipelineStepKind.Build, "restore" => PipelineStepKind.Restore, "publish" or "pack" or "push" => PipelineStepKind.Publish, _ => PipelineStepKind.Other };
                    b.Step(display ?? task, kind, $"dotnet {command}", stage, job, wd, environment, inputs?.Str("projects") is { } p ? [p] : [], step.Line);
                    if (kind is PipelineStepKind.Test or PipelineStepKind.UnitTest or PipelineStepKind.IntegrationTest or PipelineStepKind.E2ETest && (inputs?.Str("arguments") ?? "").Contains("Code Coverage", StringComparison.OrdinalIgnoreCase))
                        b.Step(display ?? task, PipelineStepKind.Coverage, "code coverage", stage, job, wd, environment, [], step.Line);
                }
                else if (taskName.Equals("Docker", StringComparison.OrdinalIgnoreCase))
                {
                    if (command is null or "build" or "buildandpush") b.Step(display ?? task, PipelineStepKind.ContainerBuild, "Docker task", stage, job, wd, environment, [], step.Line);
                    if (command is "push" or "buildandpush") b.Step(display ?? task, PipelineStepKind.ContainerPush, "Docker task", stage, job, wd, environment, [], step.Line);
                }
                else if (taskName.Equals("Npm", StringComparison.OrdinalIgnoreCase))
                    b.Step(display ?? task, command is "ci" or "install" ? PipelineStepKind.Restore : (inputs?.Str("customCommand") ?? "").Contains("test", StringComparison.OrdinalIgnoreCase) ? PipelineStepKind.FrontendTest : PipelineStepKind.Build, "npm task", stage, job, wd, environment, [], step.Line);
                else if (Tasks.TryGetValue(taskName, out var known))
                    b.Step(display ?? task, known.Kind, known.Tool, stage, job, wd, environment, inputs?.Str("csmFile") is { } f ? [f] : inputs?.Str("templateLocation") is { } l ? [l] : [], step.Line);
                if (script is not null) Script(b, script, display ?? task, stage, job, wd, environment, step.Line);
                else if (!Tasks.ContainsKey(taskName) && !taskName.StartsWith("Terraform", StringComparison.OrdinalIgnoreCase) && taskName is not ("DotNetCoreCLI" or "Docker" or "Npm"))
                    b.Step(display ?? task, PipelineStepKind.Other, task, stage, job, wd, environment, [], step.Line);
                continue;
            }
            foreach (var key in new[] { "script", "bash", "pwsh", "powershell" })
                if (step.Str(key) is { } script) { Script(b, script, display ?? key, stage, job, wd, environment, step.Line); break; }
            if (step["env"] is { Kind: YamlKind.Map } env)
                foreach (var (name, value) in env.Entries)
                    if (value.Value is { } v && Regex.IsMatch(v, @"\$\((?:[A-Za-z0-9_.]*(secret|token|password|key|pat|accesstoken)[A-Za-z0-9_.]*)\)", RegexOptions.IgnoreCase))
                        b.Secrets.Add($"secret variable: {SourceEvidenceRedaction.SafePath(Regex.Match(v, @"\$\(([^)]+)\)").Groups[1].Value)}");
            }
            finally { b.Meta = StepMeta.None; }
        }
        return order;
    }

    internal static PipelineStepKind TestKind(string hint) => hint switch
    {
        _ when Regex.IsMatch(hint, @"(?i)smoke") => PipelineStepKind.SmokeTest,
        _ when Regex.IsMatch(hint, @"(?i)(contract|pact)") => PipelineStepKind.ContractTest,
        _ when Regex.IsMatch(hint, @"(?i)(perf|load|stress|k6|jmeter|nbomber)") => PipelineStepKind.PerformanceTest,
        _ when Regex.IsMatch(hint, @"(?i)(e2e|endtoend|end-to-end|playwright|acceptance|ui\.?tests)") => PipelineStepKind.E2ETest,
        _ when Regex.IsMatch(hint, @"(?i)(api\.?tests?|apitest|newman|postman)") => PipelineStepKind.ApiTest,
        _ when Regex.IsMatch(hint, @"(?i)integration") => PipelineStepKind.IntegrationTest,
        _ when Regex.IsMatch(hint, @"(?i)(unit)") => PipelineStepKind.UnitTest,
        _ when Regex.IsMatch(hint, @"(?i)(a11y|accessibility|axe)") => PipelineStepKind.AccessibilityTest,
        _ => PipelineStepKind.Test,
    };

    private static List<string> TerraformTargets(string? workingDirectory, string? options)
    {
        var targets = new List<string>();
        if (workingDirectory is not null) targets.Add(workingDirectory);
        foreach (Match m in Regex.Matches(options ?? "", @"-var-file[= ]""?([^""\s]+)")) targets.Add(m.Groups[1].Value);
        return targets;
    }

    /// <summary>One step per command family a script runs. The script text itself is never stored.</summary>
    private static void Script(PipelineBuilder b, string script, string name, string? stage, string? job, string? workingDirectory, string? environment, int line)
    {
        var found = false;
        foreach (var (pattern, kind, tool) in Commands)
        {
            foreach (Match m in pattern.Matches(script))
            {
                found = true;
                var statement = Statement(script, m.Index);
                var targets = new List<string>();
                var stepKind = kind;
                if (kind == PipelineStepKind.Test)
                {
                    var project = Regex.Match(statement, @"(?:--project\s+|test\s+)([\w./\\-]+\.(csproj|sln|slnx|dll))", RegexOptions.IgnoreCase);
                    if (project.Success) targets.Add(project.Groups[1].Value.Replace('\\', '/'));
                    stepKind = TestKind(project.Success ? project.Groups[1].Value : statement);
                    if (Regex.IsMatch(statement, @"(--collect\s*[:=]?\s*""?XPlat Code Coverage|/p:CollectCoverage)", RegexOptions.IgnoreCase)) { } // coverage counted by its own pattern
                }
                if (kind is PipelineStepKind.InfrastructurePlan or PipelineStepKind.InfrastructureDeploy)
                {
                    if (Regex.Match(statement, @"-chdir=""?([^""\s]+)") is { Success: true } chdir) targets.Add(chdir.Groups[1].Value);
                    else if (workingDirectory is not null) targets.Add(workingDirectory);
                    foreach (Match v in Regex.Matches(statement, @"-var-file[= ]""?([^""\s]+)")) targets.Add(v.Groups[1].Value);
                    foreach (Match f in Regex.Matches(statement, @"--template-file\s+""?([^""\s]+)|-TemplateFile\s+""?([^""\s]+)")) targets.Add(f.Groups[1].Success ? f.Groups[1].Value : f.Groups[2].Value);
                }
                // v2: what a build builds — project/solution paths and the container build context (paths only; arguments and values are not kept).
                if (stepKind is PipelineStepKind.Build or PipelineStepKind.Publish)
                    foreach (Match p in Regex.Matches(statement, @"(?:--project\s+|\b(?:build|publish|pack|bundle)\s+)""?([\w./\\-]+(?:\.(?:csproj|fsproj|sln|slnx))?)""?", RegexOptions.IgnoreCase))
                        if (p.Groups[1].Value.Contains('/') || p.Groups[1].Value.Contains('\\') || Regex.IsMatch(p.Groups[1].Value, @"\.(csproj|fsproj|sln|slnx)$", RegexOptions.IgnoreCase)) targets.Add(p.Groups[1].Value.Replace('\\', '/'));
                if (kind == PipelineStepKind.ContainerBuild)
                {
                    if (Regex.Match(statement, @"\s(?:-f|--file)\s+""?([^""\s]+)") is { Success: true } dockerfile) targets.Add(dockerfile.Groups[1].Value.Replace('\\', '/'));
                    if (Regex.Match(statement.TrimEnd(), @"\s([^\s-][^\s]*)$") is { Success: true } context && context.Groups[1].Value.Contains('/') && !context.Groups[1].Value.Contains(':') && Regex.IsMatch(statement, @"\b(docker|podman)\s+(buildx\s+)?build\b|\baz\s+acr\s+build\b")) targets.Add(context.Groups[1].Value.Replace('\\', '/'));
                }
                if (kind == PipelineStepKind.ApplicationDeploy && Regex.Match(statement, @"helm\s+(?:upgrade|install)\s+(?:--install\s+)?\S+\s+(\S+)") is { Success: true } chart) targets.Add(chart.Groups[1].Value);
                if (kind == PipelineStepKind.ApplicationDeploy && Regex.Match(statement, @"kubectl\s+apply\s+(?:-k|-f)\s+(\S+)") is { Success: true } manifests) targets.Add(manifests.Groups[1].Value);
                b.Step(name, stepKind, tool, stage, job, workingDirectory, environment, targets.Where(t => !t.Contains("$(") && !t.Contains("${")), line);
            }
        }
        if (Regex.IsMatch(script, @"\baz\s+login\b[^\n]*--federated-token|\bARM_USE_OIDC\b|\baddSpnToEnvironment\b", RegexOptions.IgnoreCase)) b.Federated = true;
        foreach (Match m in Regex.Matches(script, @"\$\(([A-Za-z0-9_.]*(?:secret|token|password|pat|apikey|accesskey)[A-Za-z0-9_.]*)\)", RegexOptions.IgnoreCase))
            if (!m.Groups[1].Value.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) b.Secrets.Add($"secret variable: {SourceEvidenceRedaction.SafePath(m.Groups[1].Value)}");
        if (!found) b.Step(name, PipelineStepKind.Other, "script", stage, job, workingDirectory, environment, [], line);
    }

    /// <summary>The command statement around a match (joined over "\" line continuations) — used only to read targets, never stored.</summary>
    private static string Statement(string script, int index)
    {
        var start = script.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = index;
        while (true)
        {
            var next = script.IndexOf('\n', end);
            if (next < 0) { end = script.Length; break; }
            if (!script[..next].TrimEnd().EndsWith('\\') && !script[..next].TrimEnd().EndsWith('`')) { end = next; break; }
            end = next + 1;
        }
        return script[start..end];
    }

    private static void Trigger(PipelineBuilder b, YamlNode? node, string type, bool defaultWhenMissing)
    {
        if (node is null)
        {
            if (defaultWhenMissing) b.Triggers.Add(new PipelineTrigger { Type = type, BranchesInclude = ["(all branches — no trigger key)"] });
            return;
        }
        if (node.Kind == YamlKind.Scalar)
        {
            if (node.Value is "none" or "false") b.Triggers.Add(new PipelineTrigger { Type = "none", Disables = type, Line = node.Line });
            else if (node.Value is { } branch) b.Triggers.Add(new PipelineTrigger { Type = type, BranchesInclude = [SourceEvidenceRedaction.SafePath(branch)], Line = node.Line });
            return;
        }
        if (node.Kind == YamlKind.Seq) { b.Triggers.Add(new PipelineTrigger { Type = type, BranchesInclude = node.Items.Select(i => SourceEvidenceRedaction.SafePath(i.Value ?? "")).ToList(), Line = node.Line }); return; }
        b.Triggers.Add(new PipelineTrigger
        {
            Type = type, BranchesInclude = Branches(node["branches"], "include"), BranchesExclude = Branches(node["branches"], "exclude"),
            PathsInclude = Branches(node["paths"], "include"), PathsExclude = Branches(node["paths"], "exclude"), Line = node.Line,
        });
    }

    private static List<string> Branches(YamlNode? node, string key) => node switch
    {
        null => [],
        { Kind: YamlKind.Seq } when key == "include" => node.Items.Select(i => SourceEvidenceRedaction.SafePath(i.Value ?? "")).ToList(),
        { Kind: YamlKind.Map } => node.Strings(key).Select(SourceEvidenceRedaction.SafePath).ToList(),
        _ => [],
    };

    private static void Variables(PipelineBuilder b, YamlNode? node)
    {
        if (node is null) return;
        if (node.Kind == YamlKind.Map) { foreach (var (name, value) in node.Entries) b.Variable(name, value.Value, value.Line); return; }
        foreach (var item in node.Items.Where(i => i.Kind == YamlKind.Map))
        {
            if (item.Str("group") is { } group) b.Secrets.Add($"variable group: {SourceEvidenceRedaction.SafePath(group)}");
            else if (item.Str("template") is { } t) b.Templates.Add(Resolve(b, t));
            else if (item.Str("name") is { } name) b.Variable(name, item.Str("value"), item.Line);
        }
    }

    /// <summary>The snapshot file a local template path names, or null when it is not in the snapshot.</summary>
    private static string? ResolveFile(PipelineBuilder b, string path)
    {
        var resolved = path.StartsWith('/') ? path.TrimStart('/') : SourceArchitecture.ArchitectureInput.Normalize($"{b.File.Directory}/{path}");
        return (b.Context.Files.FirstOrDefault(f => f.Path == resolved) ?? b.Context.Files.FirstOrDefault(f => f.Path.EndsWith("/" + resolved, StringComparison.Ordinal)))?.Path;
    }

    private static string Resolve(PipelineBuilder b, string template)
    {
        var path = template.Split('@')[0].Trim();
        if (path.Contains("${{") || path.Contains("$(")) return SourceEvidenceRedaction.SafePath(path);
        var resolved = path.StartsWith('/') ? path.TrimStart('/') : SourceArchitecture.ArchitectureInput.Normalize($"{b.File.Directory}/{path}");
        // Azure resolves "/x" against the repository root; an archive may wrap the repository in a top folder, so match by suffix too.
        var match = b.Context.Files.FirstOrDefault(f => f.Path == resolved) ?? b.Context.Files.FirstOrDefault(f => f.Path.EndsWith("/" + resolved, StringComparison.Ordinal));
        return SourceEvidenceRedaction.SafePath(match?.Path ?? resolved);
    }

    // ── GitHub Actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static void GitHub(PipelineBuilder b, YamlNode doc)
    {
        var on = doc["on"] ?? doc["true"];
        switch (on)
        {
            case { Kind: YamlKind.Scalar, Value: { } single }: b.Triggers.Add(new PipelineTrigger { Type = GitHubTrigger(single), Line = on.Line }); break;
            case { Kind: YamlKind.Seq }: foreach (var i in on.Items) b.Triggers.Add(new PipelineTrigger { Type = GitHubTrigger(i.Value ?? ""), Line = i.Line }); break;
            case { Kind: YamlKind.Map }:
                foreach (var (name, value) in on.Entries)
                {
                    if (name == "schedule") { foreach (var s in value.Items) b.Triggers.Add(new PipelineTrigger { Type = "schedule", Schedule = SourceEvidenceRedaction.SafePath(s.Str("cron") ?? ""), Line = s.Line }); continue; }
                    b.Triggers.Add(new PipelineTrigger
                    {
                        Type = GitHubTrigger(name), BranchesInclude = value.Strings("branches").Select(SourceEvidenceRedaction.SafePath).ToList(), BranchesExclude = value.Strings("branches-ignore").Select(SourceEvidenceRedaction.SafePath).ToList(),
                        PathsInclude = value.Strings("paths").Select(SourceEvidenceRedaction.SafePath).ToList(), PathsExclude = value.Strings("paths-ignore").Select(SourceEvidenceRedaction.SafePath).ToList(), Line = value.Line,
                    });
                }
                break;
        }
        if (doc["permissions"]?.Str("id-token") == "write") b.Federated = true;
        Variables(b, doc["env"]);
        foreach (var (jobName, job) in doc["jobs"]?.Entries ?? [])
        {
            b.Jobs.Add(SourceEvidenceRedaction.SafePath(jobName));
            if (job["permissions"]?.Str("id-token") == "write") b.Federated = true;
            var env = EnvironmentName(job);
            if (env is not null) b.Environments.Add(SourceEvidenceRedaction.SafePath(env));
            if (job.Str("uses") is { } reusable) { b.Templates.Add(SourceEvidenceRedaction.SafePath(reusable)); b.Step($"reusable workflow {reusable}", PipelineStepKind.Template, "reusable workflow", null, jobName, null, env, [reusable], job.Line); }
            Variables(b, job["env"]);
            foreach (var step in job.List("steps").Where(s => s.Kind == YamlKind.Map))
            {
                var display = step.Str("name");
                var wd = step.Str("working-directory");
                if (step.Str("uses") is { } uses)
                {
                    var action = uses.Split('@')[0];
                    if (Actions.FirstOrDefault(a => a.Pattern.IsMatch(action)) is { Pattern: not null } known)
                    {
                        b.Step(display ?? action, known.Kind, known.Tool, null, jobName, wd, env, [], step.Line);
                        if (action.StartsWith("docker/build-push-action", StringComparison.OrdinalIgnoreCase) && step["with"]?.Str("push") == "true")
                            b.Step(display ?? action, PipelineStepKind.ContainerPush, known.Tool, null, jobName, wd, env, [], step.Line);
                        if (action.StartsWith("azure/login", StringComparison.OrdinalIgnoreCase) && step["with"]?.Has("client-id") == true && step["with"]?.Has("creds") != true) b.Federated = true;
                    }
                    else if (!Regex.IsMatch(action, @"^actions/(checkout|setup-\w+|cache|download-artifact)$", RegexOptions.IgnoreCase))
                        b.Step(display ?? action, PipelineStepKind.Other, action, null, jobName, wd, env, [], step.Line);
                    continue;
                }
                if (step.Str("run") is { } run) Script(b, run, display ?? "run", null, jobName, wd, env, step.Line);
            }
        }
    }

    private static string GitHubTrigger(string name) => name switch
    {
        "push" => "push", "pull_request" or "pull_request_target" => "pull-request", "workflow_dispatch" => "manual", "schedule" => "schedule",
        "workflow_run" or "workflow_call" => "pipeline-resource", _ => SourceEvidenceRedaction.SafePath(name),
    };

    // ── GitLab CI ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> GitLabReserved = new(StringComparer.Ordinal) { "stages", "variables", "include", "default", "workflow", "image", "services", "before_script", "after_script", "cache" };

    private static void GitLab(PipelineBuilder b, YamlNode doc)
    {
        foreach (var stage in doc.Strings("stages")) b.Stages.Add(new PipelineStage(SourceEvidenceRedaction.SafePath(stage), null, [], false, null, doc["stages"]!.Line));
        foreach (var include in doc.List("include")) b.Templates.Add(SourceEvidenceRedaction.SafePath(include.Value ?? include.Str("local") ?? include.Str("template") ?? include.Str("project") ?? "(include)"));
        Variables(b, doc["variables"]);
        var rules = false;
        foreach (var (name, job) in doc.Entries.Where(e => !GitLabReserved.Contains(e.Key) && !e.Key.StartsWith('.') && e.Value.Kind == YamlKind.Map))
        {
            b.Jobs.Add(SourceEvidenceRedaction.SafePath(name));
            var env = EnvironmentName(job);
            if (env is not null) b.Environments.Add(SourceEvidenceRedaction.SafePath(env));
            if (job.Str("when") == "manual") b.Approvals = true;
            rules |= job.Has("rules") || job.Has("only") || job.Has("except");
            var script = string.Join('\n', job.List("script").Select(s => s.Value ?? ""));
            if (script.Length > 0) Script(b, script, name, job.Str("stage"), name, null, env, job.Line);
        }
        b.Triggers.Add(new PipelineTrigger { Type = rules || doc.Has("workflow") ? "push (rules)" : "push", BranchesInclude = ["(per rules/only — not evaluated)"] });
    }

    // ── Jenkins (pattern-based) ─────────────────────────────────────────────────────────────────────────────────────────

    private static void Jenkins(PipelineBuilder b)
    {
        var text = b.File.Content;
        foreach (Match m in Regex.Matches(text, @"\bstage\s*\(\s*['""]([^'""]+)['""]\s*\)")) b.Stages.Add(new PipelineStage(SourceEvidenceRedaction.SafePath(m.Groups[1].Value), null, [], false, null, b.File.Line(m.Index)));
        foreach (Match m in Regex.Matches(text, @"\b(sh|bat|pwsh|powershell)\s*\(?\s*(?:script\s*:\s*)?('''[\s\S]*?'''|""""""[\s\S]*?""""""|'[^']*'|""[^""]*"")"))
            Script(b, m.Groups[2].Value, m.Groups[1].Value, null, null, null, null, b.File.Line(m.Index));
        if (Regex.IsMatch(text, @"\bcron\s*\(")) b.Triggers.Add(new PipelineTrigger { Type = "schedule", Schedule = "(cron in Jenkinsfile)" });
        if (Regex.IsMatch(text, @"\bpollSCM\s*\(|\bgithubPush\s*\(")) b.Triggers.Add(new PipelineTrigger { Type = "push" });
        if (Regex.IsMatch(text, @"\binput\s*(\(|\{)")) b.Approvals = true;
        foreach (Match m in Regex.Matches(text, @"credentials\s*\(\s*['""]([^'""]+)['""]")) b.Secrets.Add($"credentials: {SourceEvidenceRedaction.SafePath(m.Groups[1].Value)}");
    }
}
