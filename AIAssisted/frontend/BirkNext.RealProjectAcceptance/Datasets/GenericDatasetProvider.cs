namespace BirkNext.RealProjectAcceptance.Datasets;

/// <summary>
/// Any external archive without a dataset description (e.g. Tiltak 2, Terraform-only archives): TEST INFRASTRUCTURE ONLY. It states no expected
/// facts and pins no hash, so acceptance imports the archive through the production Project Import flow and checks that every page renders
/// against that one workspace — no page error, no console error, accessibility — without inventing what the archive should contain. A dataset
/// with known facts gets its own provider (as M2LB has). Select it with BIRKNEXT_REAL_ACCEPTANCE_DATASET=Generic and the archive path.
/// </summary>
public sealed class GenericDatasetProvider : IRealProjectDatasetProvider
{
    public const string Id = "Generic";

    public string DatasetId => Id;
    public string ArchiveEnvironmentVariable => "BIRKNEXT_REAL_PROJECT_GENERIC";

    public RealProjectDataset Describe(string? archivePath) => new()
    {
        DatasetId = Id,
        DisplayName = archivePath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : "Generic archive",
        ArchivePath = archivePath,
        ProjectNotes = "An external archive without a dataset description: pages are checked for rendering, errors and accessibility only.",
    };
}
