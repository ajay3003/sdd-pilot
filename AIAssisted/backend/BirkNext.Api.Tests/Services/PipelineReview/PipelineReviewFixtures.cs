using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.SourceAnalysis.Observability;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.Integrations;
using FluentAssertions;
using SourceFixtures = BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.PipelineReview;

/// <summary>
/// Generic "Contoso.Shop" repository (no project-specific names), analyzed by the real Source Analysis pipeline:
/// - pr-validation: PR trigger with path filters (API + tests only), build + unit tests through a local steps template.
/// - shop-build: CI on main with path filters; Build (unit tests, publish `app`) → Deploy DEV (deployment-job template) → Integration tests
///   (continueOnError); Infrastructure QA (Terraform plan/apply template) → Deploy QA (depends on DEV + infra, NOT on integration tests);
///   a weekly-only conditional dependency scan with continueOnError; a compliance stage from an external template repository.
/// - worker-build: CI for the worker only (path filter excludes the shared library and the API contract it consumes).
/// - shop-release: manual production pipeline with a pipeline resource on shop-build (no trigger), which rebuilds and deploys its own artifact.
/// - templates: dotnet-tests (steps), deploy-webapp (jobs), terraform (steps), unused (unreferenced).
/// </summary>
internal static class PipelineReviewFixtures
{
    private const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Shop.Common/Shop.Common.csproj" /></ItemGroup></Project>""";
    private const string Worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Shop.Common/Shop.Common.csproj" /><OpenApiReference Include="../Shop.Api/openapi.json" /></ItemGroup></Project>""";
    private const string Library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";
    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.0.0" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" /></ItemGroup></Project>""";
    private const string OpenApi = """{"openapi":"3.0.1","info":{"title":"Shop API","version":"1.0"},"paths":{"/orders":{"get":{"operationId":"ListOrders","responses":{"200":{"description":"ok"}}}}}}""";

    public const string PrValidation = """
        trigger: none
        pr:
          branches:
            include:
              - main
          paths:
            include:
              - src/Shop.Api
              - tests
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Validate
            jobs:
              - job: BuildAndTest
                steps:
                  - script: dotnet build Contoso.Shop.sln
                  - template: templates/dotnet-tests.yml
                    parameters:
                      projects: tests/Shop.Api.UnitTests/Shop.Api.UnitTests.csproj
        """;

    public const string ShopBuild = """
        trigger:
          branches:
            include:
              - main
          paths:
            include:
              - src
              - infra
        pool:
          vmImage: ubuntu-latest
        resources:
          repositories:
            - repository: platform
              type: git
              name: Platform/pipeline-templates
        variables:
          - name: deployToken
            value: SECRET_SENTINEL_PIPELINE_VARIABLE_ABCDEF0123456789
        stages:
          - stage: Build
            jobs:
              - job: Build
                steps:
                  - script: dotnet build Contoso.Shop.sln -c Release
                  - template: templates/dotnet-tests.yml
                    parameters:
                      projects: tests/Shop.Api.UnitTests/Shop.Api.UnitTests.csproj
                  - script: dotnet publish src/Shop.Api/Shop.Api.csproj -o $(Build.ArtifactStagingDirectory)/app
                  - publish: $(Build.ArtifactStagingDirectory)/app
                    artifact: app
              - job: DependencyScan
                condition: eq(variables['Build.Reason'], 'Schedule')
                steps:
                  - script: trivy fs --exit-code 1 .
                    displayName: Weekly dependency scan
                    continueOnError: true
          - stage: DeployDev
            displayName: Deploy DEV
            dependsOn: Build
            jobs:
              - template: templates/deploy-webapp.yml
                parameters:
                  environment: shop-dev
          - stage: IntegrationTests
            dependsOn: DeployDev
            jobs:
              - job: Integration
                steps:
                  - script: dotnet test tests/Shop.IntegrationTests/Shop.IntegrationTests.csproj
                    continueOnError: true
          - stage: InfraQA
            displayName: Infrastructure QA
            dependsOn: Build
            jobs:
              - job: Terraform
                steps:
                  - template: templates/terraform.yml
                    parameters:
                      environment: qa
          - stage: DeployQA
            displayName: Deploy QA
            dependsOn:
              - DeployDev
              - InfraQA
            jobs:
              - template: templates/deploy-webapp.yml
                parameters:
                  environment: shop-qa
          - stage: Compliance
            dependsOn: Build
            jobs:
              - job: Checks
                steps:
                  - template: compliance/checks.yml@platform
        """;

    public const string WorkerBuild = """
        trigger:
          branches:
            include:
              - main
          paths:
            include:
              - src/Shop.Worker
        pool:
          vmImage: ubuntu-latest
        steps:
          - script: dotnet build src/Shop.Worker/Shop.Worker.csproj
          - script: dotnet test tests/Shop.Worker.UnitTests/Shop.Worker.UnitTests.csproj
        """;

    public const string ShopRelease = """
        trigger: none
        pool:
          vmImage: ubuntu-latest
        resources:
          pipelines:
            - pipeline: shopBuild
              source: shop-build
        stages:
          - stage: BuildProd
            jobs:
              - job: Build
                steps:
                  - script: dotnet publish src/Shop.Api/Shop.Api.csproj -c Release -o $(Build.ArtifactStagingDirectory)/app
                  - publish: $(Build.ArtifactStagingDirectory)/app
                    artifact: app
          - stage: Production
            jobs:
              - deployment: DeployProd
                environment: shop-prod
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - download: current
                          artifact: app
                        - task: AzureWebApp@1
                          inputs:
                            azureSubscription: shop-prod-connection
                            appName: shop-prod
                            package: $(Pipeline.Workspace)/app
        """;

    public const string TestsTemplate = """
        parameters:
          - name: projects
            type: string
        steps:
          - task: DotNetCoreCLI@2
            displayName: Run tests
            inputs:
              command: test
              projects: ${{ parameters.projects }}
        """;

    public const string DeployTemplate = """
        parameters:
          - name: environment
            type: string
        jobs:
          - deployment: Deploy
            environment: ${{ parameters.environment }}
            strategy:
              runOnce:
                deploy:
                  steps:
                    - task: AzureWebApp@1
                      inputs:
                        azureSubscription: shop-connection
                        appName: shop-${{ parameters.environment }}
                        package: $(Pipeline.Workspace)/app/**/*.zip
        """;

    public const string TerraformTemplate = """
        parameters:
          - name: environment
            type: string
        steps:
          - script: terraform -chdir=infra init
          - script: terraform -chdir=infra plan -var-file=${{ parameters.environment }}.tfvars -out=tfplan
          - script: terraform -chdir=infra apply tfplan
        """;

    public const string UnusedTemplate = """
        parameters:
          - name: name
            type: string
        steps:
          - script: echo unused
        """;

    public static (string Path, string Content)[] Shop(params (string Path, string Content)[] overrides)
    {
        var files = new Dictionary<string, string>
        {
            ["shop/Contoso.Shop.sln"] = "",
            ["shop/src/Shop.Api/Shop.Api.csproj"] = Web,
            ["shop/src/Shop.Api/Program.cs"] = "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/orders\", () => Results.Ok());\napp.Run();\n",
            ["shop/src/Shop.Api/openapi.json"] = OpenApi,
            ["shop/src/Shop.Worker/Shop.Worker.csproj"] = Worker,
            ["shop/src/Shop.Worker/Program.cs"] = "var host = Host.CreateApplicationBuilder(args).Build();\nhost.Run();\n",
            ["shop/src/Shop.Common/Shop.Common.csproj"] = Library,
            ["shop/src/Shop.Common/Money.cs"] = "namespace Shop.Common; public sealed record Money(decimal Amount);\n",
            ["shop/tests/Shop.Api.UnitTests/Shop.Api.UnitTests.csproj"] = TestProject,
            ["shop/tests/Shop.IntegrationTests/Shop.IntegrationTests.csproj"] = TestProject,
            ["shop/tests/Shop.Worker.UnitTests/Shop.Worker.UnitTests.csproj"] = TestProject,
            ["shop/infra/main.tf"] = "resource \"azurerm_linux_web_app\" \"shop\" { name = \"app-shop-${var.env}\" }\nvariable \"env\" { default = \"dev\" }\n",
            ["shop/infra/qa.tfvars"] = "env = \"qa\"\n",
            ["shop/pipelines/pr-validation.yml"] = PrValidation,
            ["shop/pipelines/shop-build.yml"] = ShopBuild,
            ["shop/pipelines/worker-build.yml"] = WorkerBuild,
            ["shop/pipelines/shop-release.yml"] = ShopRelease,
            ["shop/pipelines/templates/dotnet-tests.yml"] = TestsTemplate,
            ["shop/pipelines/templates/deploy-webapp.yml"] = DeployTemplate,
            ["shop/pipelines/templates/terraform.yml"] = TerraformTemplate,
            ["shop/pipelines/templates/unused.yml"] = UnusedTemplate,
        };
        foreach (var (path, content) in overrides)
            if (content.Length == 0 && path.StartsWith('-')) files.Remove(path[1..]); else files[path] = content;
        return files.Select(f => (f.Key, f.Value)).ToArray();
    }

    /// <summary>A zip with fixed entry timestamps and LF line endings, so the archive fingerprint (SHA-256) is the same on every run and checkout.</summary>
    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
                writer.Write(content.ReplaceLineEndings("\n"));
            }
        return buffer.ToArray();
    }

    /// <summary>An immutable snapshot as Source Analysis stores it (architecture + evidence domains), from the given files.</summary>
    public static IqrSourceSnapshot Snapshot(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("shop.zip", Zip(files));
        error.Should().BeNull();
        var id = Guid.Parse("5a0b0000-0000-0000-0000-000000000001");
        var at = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
        var architecture = SourceArchitectureAnalyzer.Analyze(id, workspace!, at, null, default, out var input, out _);
        var observability = ObservabilitySourceAnalyzer.Analyze(id, workspace!, input, architecture, at, default);
        var domains = SourceEvidenceAnalyzer.Analyze(id, workspace!, input, at, architecture, null, observability, null, default, out _);
        return new IqrSourceSnapshot { Id = id, Archive = new("shop.zip", domains.SourceFingerprint, files.Length), AnalyzedAt = at, Architecture = architecture, EvidenceDomains = domains };
    }
}
