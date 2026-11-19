using BirkNext.ContractTesting;

namespace BirkNext.Web.Services;

/// <summary>
/// Contract Testing › Import (2026-11-18): the rules behind the source-contract inventory — filters, the summary strip, the import preview, how
/// files relate (byte-identical copies by document hash, files that declare the same operations, a fragment's possible parent schemas) and the
/// deep link other workspaces use. Pure: the registry's identity rules stay the authority; nothing here merges, imports or generates.
/// </summary>
public static class ContractImportPresentation
{
    public const string Route = "api-quality-review/contracts";

    /// <summary>A file the import list can select: not in the registry yet, changed since its latest version, or the identical document moved.</summary>
    public static bool Importable(SourceContractCandidate c) =>
        c.ImportedAs is null && c.State is SourceContractCandidateState.New or SourceContractCandidateState.Changed or SourceContractCandidateState.Moved;

    public static bool Imported(SourceContractCandidate c) => c.ImportedAs is not null || c.State == SourceContractCandidateState.UpToDate;

    /// <summary>REST (OpenAPI) or GraphQL, from the contract type the analyzer recorded (the same rule as the row's protocol label).</summary>
    public static bool IsRest(SourceContractCandidate c) => ContractSourceVariants.ProtocolLabel(c) == "REST";

    public static string Key(SourceContractCandidate c) => $"{c.SnapshotId:N}|{c.ContractId}";

    /// <param name="Protocol">"all", "rest" or "graphql".</param>
    /// <param name="State">"all", "actionable" (new and changed), "UpToDate" (already imported), "NeedsReview", "fragments", "Archived".</param>
    public sealed record Filter(string Query = "", string Protocol = "all", string State = "all");

    public static IReadOnlyList<SourceContractCandidate> Apply(IEnumerable<SourceContractCandidate> all, Filter f) => [.. all.Where(c =>
        (string.IsNullOrWhiteSpace(f.Query) || Has(c.Name, f.Query) || Has(c.File, f.Query) || Has(c.Module, f.Query) || Has(c.ProjectName, f.Query)
            || c.Operations.Any(o => Has(o, f.Query)))
        && (f.Protocol switch { "rest" => IsRest(c), "graphql" => !IsRest(c), _ => true })
        && (f.State switch
        {
            "actionable" => Importable(c),
            "UpToDate" => Imported(c),
            "NeedsReview" => c.State == SourceContractCandidateState.NeedsReview,
            "fragments" => ContractSourceVariants.IsFragment(c),
            "Archived" => c.State == SourceContractCandidateState.Archived,
            _ => true,
        }))];

    private static bool Has(string? text, string query) => text?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The summary strip: contract files and operations are counted apart (an operation in three copies of a schema is one distinct operation).</summary>
    public sealed record Summary(int Files, int Imported, int NewOrChanged, int NeedsReview, int Rest, int GraphQl, int Fragments, int OperationEntries, int DistinctOperations, int Projects);

    public static Summary Summarize(IReadOnlyCollection<SourceContractCandidate> files) => new(
        files.Count, files.Count(Imported), files.Count(Importable), files.Count(c => c.State == SourceContractCandidateState.NeedsReview),
        files.Count(c => IsRest(c)), files.Count(c => !IsRest(c)), files.Count(ContractSourceVariants.IsFragment),
        files.Sum(c => c.Operations.Count), files.SelectMany(c => c.Operations.Select(o => OperationKey(c, o))).Distinct(StringComparer.Ordinal).Count(),
        files.Select(c => c.ProjectKey).Distinct(StringComparer.Ordinal).Count());

    /// <summary>An operation's identity across files of one project and protocol (REST also per service module: two services' /health are two endpoints).</summary>
    public static string OperationKey(SourceContractCandidate c, string operation) =>
        IsRest(c) ? $"{c.ProjectKey}|rest|{c.Module}|{operation}" : $"{c.ProjectKey}|graphql|{operation}";

    /// <summary>Other files with the byte-identical document (same project): copies, never different versions.</summary>
    public static IReadOnlyList<SourceContractCandidate> IdenticalCopies(SourceContractCandidate c, IEnumerable<SourceContractCandidate> all) =>
        c.DocumentSha256 is not { Length: > 0 } sha ? [] : [.. all.Where(o => o.ContractId != c.ContractId && o.ProjectKey == c.ProjectKey && o.DocumentSha256 == sha)];

    /// <summary>Other files of the project that declare some of the same operations (generated docs, a client copy, a test snapshot) but are not identical.</summary>
    public static IReadOnlyList<(SourceContractCandidate File, int Shared)> Related(SourceContractCandidate c, IEnumerable<SourceContractCandidate> all)
    {
        if (c.Operations.Count == 0) return [];
        var mine = c.Operations.Select(o => OperationKey(c, o)).ToHashSet(StringComparer.Ordinal);
        return [.. all.Where(o => o.ContractId != c.ContractId && o.ProjectKey == c.ProjectKey && IsRest(o) == IsRest(c)
                && (c.DocumentSha256 is null || o.DocumentSha256 != c.DocumentSha256))
            .Select(o => (File: o, Shared: o.Operations.Count(x => mine.Contains(OperationKey(o, x)))))
            .Where(x => x.Shared > 0).OrderByDescending(x => x.Shared).ThenBy(x => x.File.File, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>For a GraphQL file without root operations: the GraphQL files with root operations in the same project and service module — the
    /// schemas it may extend. A proposal from location and protocol only (the analyzer records no "extends" link), labelled so.</summary>
    public static IReadOnlyList<SourceContractCandidate> PossibleParents(SourceContractCandidate fragment, IEnumerable<SourceContractCandidate> all) =>
        !ContractSourceVariants.IsFragment(fragment) ? [] : [.. all.Where(o => o.ContractId != fragment.ContractId && o.ProjectKey == fragment.ProjectKey
            && !IsRest(o) && o.Module == fragment.Module && o.OperationCount > 0)];

    /// <summary>What an import of the selected files would do, before anything is written.</summary>
    public sealed record ImportPreview(int Contracts, int Apis, IReadOnlyList<string> Projects, int New, int Changed, int Moved, int AlreadyImported, int Fragments,
        int IdenticalPairs, int StructuralOnly, int Operations, bool NeedsProjectConfirmation);

    public static ImportPreview Preview(IReadOnlyList<SourceContractCandidate> selected, string? project)
    {
        var shas = selected.Where(c => c.DocumentSha256 is { Length: > 0 }).GroupBy(c => (c.ProjectKey, c.DocumentSha256)).Sum(g => g.Count() - 1);
        var projects = selected.Select(c => c.ProjectName).Distinct(StringComparer.Ordinal).ToList();
        return new(selected.Count, selected.Select(c => (c.ProjectKey, c.Module, IsRest(c))).Distinct().Count(), projects,
            selected.Count(c => c.State == SourceContractCandidateState.New), selected.Count(c => c.State == SourceContractCandidateState.Changed),
            selected.Count(c => c.State == SourceContractCandidateState.Moved), selected.Count(Imported), selected.Count(ContractSourceVariants.IsFragment), shas,
            selected.Count(c => !c.HasDocument), selected.Sum(c => c.OperationCount),
            string.IsNullOrEmpty(project) && selected.Select(c => c.ProjectKey).Distinct(StringComparer.Ordinal).Count() > 1);
    }

    /// <summary>
    /// The deep link to one source contract file (and optionally one operation) — the stable route other workspaces use: the Source Analysis snapshot
    /// and the exact file path inside it (never a name match). Contract Testing opens the import view with that file's details, or the imported
    /// contract's operation when it is already in the registry.
    /// </summary>
    public static string SourceLink(Guid snapshotId, string file, string? operation = null) =>
        $"{Route}?mode=import&snapshot={snapshotId:D}&file={Uri.EscapeDataString(file.Replace('\\', '/'))}" + (operation is { Length: > 0 } op ? $"&operation={Uri.EscapeDataString(op)}" : "");

    /// <summary>The file a deep link names, in the listed inventory (snapshot + exact path); null when it is not listed (another baseline, removed).</summary>
    public static SourceContractCandidate? Find(IEnumerable<SourceContractCandidate> all, Guid? snapshotId, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var path = file.Replace('\\', '/');
        var inFile = all.Where(c => string.Equals(c.File.Replace('\\', '/'), path, StringComparison.Ordinal)).ToList();
        return snapshotId is { } s ? inFile.FirstOrDefault(c => c.SnapshotId == s) : inFile.Count == 1 ? inFile[0] : null;
    }

    /// <summary>The registry key of an imported file ("children-api@v2" → "children-api").</summary>
    public static string? ImportedKey(SourceContractCandidate c)
    {
        var reference = c.ImportedAs ?? (c.State == SourceContractCandidateState.UpToDate ? c.ContractKey : null);
        if (reference is null) return null;
        var at = reference.LastIndexOf("@v", StringComparison.Ordinal);
        return at > 0 ? reference[..at] : reference;
    }
}
