namespace BirkNext.RealProjectAcceptance.Datasets;

/// <summary>
/// The first real dataset: the external M2LB archive (never committed). TEST INFRASTRUCTURE ONLY — nothing in BirkNext production
/// references this type. Expectations are semantic facts established from this exact archive's contents (file inventory of the
/// SHA-256 below: Azure pipeline YAML under .pipeline/, autodoc OpenAPI documents, GraphQL SDL files, autodoc generated documentation,
/// .csproj package manifests, appsettings configuration; no Terraform and no XSD files) — not guesses and not exact counts, except the
/// Explorer Text Coverage baseline, which applies only when the archive hash matches.
/// </summary>
public sealed class M2lbDatasetProvider : IRealProjectDatasetProvider
{
    public const string Id = "M2LB";
    public const string Sha256 = "c850a1b2813bbf6e2a9eba1f311d63ff0332eacf35764cd6b767489ac22dc41e";

    public string DatasetId => Id;
    public string ArchiveEnvironmentVariable => "BIRKNEXT_REAL_PROJECT_M2LB";

    private static FeatureExpectation Real(string reason, params string[] mentions) => new() { Presence = ExpectedDataPresence.RealData, Reason = reason, MustMention = mentions };
    private static FeatureExpectation None(string reason) => new() { Presence = ExpectedDataPresence.NotApplicable, Reason = reason };
    private static FeatureExpectation RuntimeOnly(string reason) => new() { Presence = ExpectedDataPresence.RuntimeNotVerified, Reason = reason };

    public RealProjectDataset Describe(string? archivePath) => new()
    {
        DatasetId = Id,
        DisplayName = "M2LB (modulert 2. linje barnevern)",
        ArchivePath = archivePath,
        // BIRKNEXT_REAL_PROJECT_M2LB_SHA256 may pin another approved archive version; the default is the historical acceptance archive.
        ExpectedSha256 = Environment.GetEnvironmentVariable("BIRKNEXT_REAL_PROJECT_M2LB_SHA256") is { Length: 64 } pinned ? pinned : Sha256,
        ProjectNotes = ".NET/C# services with a Blazor frontend, GraphQL and REST APIs, Azure Pipelines and Spec Kit documents per module.",
        ExpectedTechnologyHints = [".NET", "C#", "Blazor", "GraphQL"],
        ExpectedArtifactFamilies = ["Specification", "Constitution", "Plan", "Tasks", "Data Model"],
        Tags = ["dotnet", "blazor", "graphql", "azure-pipelines", "spec-kit"],
        Expectations = new Dictionary<string, FeatureExpectation>(StringComparer.Ordinal)
        {
            ["project-import"] = Real("the archive contains Spec Kit documents and source"),
            ["specification-explorer"] = Real("specs/*/spec.md documents exist"),
            ["constitution-explorer"] = Real(".specify/memory/constitution.md documents exist"),
            ["plan-explorer"] = Real("specs/*/plan.md documents exist"),
            ["task-explorer"] = Real("specs/*/tasks.md documents exist"),
            ["data-model-explorer"] = Real("data-model.md documents exist"),
            ["document-quality-review"] = Real("Spec Kit documents exist"),
            ["source-analysis"] = Real("C# projects exist", "C#"),
            ["technology-coverage"] = Real(".NET source exists"),
            ["dependency-review"] = Real(".csproj package manifests exist"),
            ["pipeline-review"] = Real("Azure Pipelines YAML exists under .pipeline/"),
            ["environment-analysis"] = Real("appsettings configuration exists"),
            ["contract-openapi"] = Real("autodoc/openapi.yaml documents exist"),
            ["contract-graphql"] = Real(".graphql SDL files exist"),
            ["contract-xsd"] = None("the archive contains no .xsd files"),
            ["iac-terraform"] = None("the archive contains no .tf files"),
            ["generated-documentation"] = Real("autodoc/ generated documentation exists"),
            ["ai-generated-code-review"] = Real("C# source exists: deterministic code-risk rules execute (no expected finding count)"),
            ["fqr-runtime"] = RuntimeOnly("no runtime target is paired with this dataset"),
            ["aqr-runtime"] = RuntimeOnly("no runtime target is paired with this dataset"),
        },
        // Explorer Text Coverage of archive-owned documents in this exact archive (2026-10-09).
        // Re-recorded after making the diagnostic exclude built-in fixtures and reusable .specify/templates
        // documents from the imported-project comparison. The former 185 / 39,225 baseline mixed those
        // reusable templates into the project artifact set; current archive-owned coverage is 149 / 35,850.
        // Only applied when the archive hash matches.
        HashBoundBaselines = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["explorer-coverage.documents"] = 149,
            ["explorer-coverage.blocks"] = 35_850,
            ["explorer-coverage.direct"] = 24_635,
            ["explorer-coverage.structured"] = 9_921,
            ["explorer-coverage.ignored"] = 1_294,
            ["explorer-coverage.unsupported"] = 0,
            ["explorer-coverage.missing"] = 0,
        },
    };
}
