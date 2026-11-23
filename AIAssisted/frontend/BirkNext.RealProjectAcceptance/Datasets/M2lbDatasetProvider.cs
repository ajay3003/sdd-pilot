namespace BirkNext.RealProjectAcceptance.Datasets;

/// <summary>
/// The first real dataset: the external M2LB archive (never committed). TEST INFRASTRUCTURE ONLY — nothing in BirkNext production
/// references this type. Expectations are semantic facts established from this exact archive's contents (file inventory of the
/// SHA-256 below: Azure pipeline YAML under .pipeline/, autodoc OpenAPI documents, GraphQL SDL files, autodoc generated documentation,
/// .csproj package manifests, appsettings configuration, XML Schemas of the Meldingsutveksler receiver; no Terraform) — not guesses and not exact counts, except the
/// Explorer Text Coverage baseline, which applies only when the archive hash matches.
/// </summary>
public sealed class M2lbDatasetProvider : IRealProjectDatasetProvider
{
    public const string Id = "M2LB";
    // Re-pinned 2026-11-23 to AIAssisted/testdata/M2LB.zip: the earlier acceptance archive (c850a1b2…) is no longer available.
    public const string Sha256 = "22339207cd2ef2f75ea418f66037c2ccacd8df7aad3cd89ddf73bd2a439c28f0";

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
        // BIRKNEXT_REAL_PROJECT_M2LB_SHA256 may pin another approved archive version; the default is the testdata archive.
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
            ["contract-xsd"] = Real("MeldingsutvekslerReceiver/**/Skjema/*.xsd message schemas exist"),
            ["iac-terraform"] = None("the archive contains no .tf files"),
            ["generated-documentation"] = Real("autodoc/ generated documentation exists"),
            // Component/journey names below are acceptance expectations about this dataset only; production code knows none of them.
            ["test-coverage-review"] = Real("xUnit test projects and service/adapter components exist", "Person", "Tjeneste", "Hendelse"),
            ["ai-generated-code-review"] = Real("C# source exists: deterministic code-risk rules execute (no expected finding count)"),
            ["fqr-runtime"] = RuntimeOnly("no runtime target is paired with this dataset"),
            ["aqr-runtime"] = RuntimeOnly("no runtime target is paired with this dataset"),
        },
        // Explorer Text Coverage of archive-owned documents in this exact archive (2026-10-09).
        // Re-recorded after making the diagnostic exclude built-in fixtures and reusable .specify/templates
        // documents from the imported-project comparison. The former 185 / 39,225 baseline mixed those
        // reusable templates into the project artifact set; coverage of the former archive (c850a1b2…) was 149 / 35,850.
        // 2026-11-23: re-recorded for the current archive (22339207…) from a hash-matched run: 141 / 27,630, nothing missing or unsupported.
        // Only applied when the archive hash matches.
        HashBoundBaselines = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["explorer-coverage.documents"] = 141,
            ["explorer-coverage.blocks"] = 27_630,
            ["explorer-coverage.direct"] = 13_441,
            ["explorer-coverage.structured"] = 12_775,
            ["explorer-coverage.ignored"] = 1_414,
            ["explorer-coverage.unsupported"] = 0,
            ["explorer-coverage.missing"] = 0,
        },
    };
}
