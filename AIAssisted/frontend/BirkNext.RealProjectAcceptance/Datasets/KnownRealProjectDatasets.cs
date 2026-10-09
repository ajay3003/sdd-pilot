namespace BirkNext.RealProjectAcceptance.Datasets;

/// <summary>
/// The dataset descriptors acceptance knows about. Adding a dataset = one <see cref="IRealProjectDatasetProvider"/> here; no feature,
/// runner or production code changes. The archives themselves always stay outside the repository.
/// </summary>
public static class KnownRealProjectDatasets
{
    /// <summary>Selects the dataset of a run (defaults to the first registered dataset).</summary>
    public const string DatasetEnvironmentVariable = "BIRKNEXT_REAL_ACCEPTANCE_DATASET";
    /// <summary>Explicit archive path for the selected dataset; overrides the dataset's own variable.</summary>
    public const string ArchiveEnvironmentVariable = "BIRKNEXT_REAL_ACCEPTANCE_ARCHIVE";

    public static RealProjectDatasetRegistry Registry() => new([new M2lbDatasetProvider()]);

    /// <summary>The configured dataset of this run and its preparation (archive presence + fingerprint), from environment variables.</summary>
    public static DatasetPreparation PrepareFromEnvironment(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var registry = Registry();
        var id = env(DatasetEnvironmentVariable) is { Length: > 0 } configured ? configured : registry.DatasetIds.First();
        var dataset = registry.Resolve(id, env(ArchiveEnvironmentVariable), env);
        return DatasetPreparation.Prepare(dataset);
    }
}
