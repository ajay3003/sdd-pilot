using BirkNext.ContractTesting;

namespace BirkNext.Web.Services;

/// <summary>
/// Presentation of source-discovered contract files as candidate logical APIs and their variants. A grouping by module + protocol is a
/// proposal ("candidate logical API"), never a confirmed identity; a file's role is suggested by its location only and labelled so — the
/// registry's identity rules (relocation candidates, NeedsReview) stay the authority. Pure: no parsing, no network, no store.
/// </summary>
public static class ContractSourceVariants
{
    /// <summary>A file's role as its location suggests it (generated documentation, specification, test snapshot, client schema, extension).</summary>
    public static string RoleHint(string file)
    {
        var path = "/" + file.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        var name = System.IO.Path.GetFileName(path);
        if (name.Contains("extension")) return "Schema extension";
        if (name.Contains("snapshot") || path.Contains("/tests/") || path.Contains(".tests/") || path.Contains("/test/")) return "Test snapshot";
        if (path.Contains("/autodoc/")) return "Generated documentation";
        if (path.Contains("/specs/") || path.Contains("/contracts/")) return "Specification";
        if (path.Contains("/frontend/") || path.Contains(".web/") || path.Contains("/client")) return "Client schema";
        return "Source";
    }

    /// <summary>
    /// A GraphQL file with no root operation (Query/Mutation/Subscription field) is a fragment or extension: it may hold important types but
    /// needs its parent schema, so no endpoint tests are generated from it on its own. Not "invalid".
    /// </summary>
    public static bool IsFragment(SourceContractCandidate c) => ProtocolLabel(c) == "GraphQL" && c.OperationCount == 0;

    public static string ProtocolLabel(SourceContractCandidate c) => c.Type == "OpenApi" ? "REST" : "GraphQL";

    /// <summary>The import view's summary over the listed files (one population; each file counted once).</summary>
    public static string Summary(IReadOnlyCollection<SourceContractCandidate> files)
    {
        if (files.Count == 0) return "No contract files listed.";
        var groups = files.GroupBy(c => (c.Module, ProtocolLabel(c))).Count();
        var rest = files.Count(c => ProtocolLabel(c) == "REST");
        var actionable = files.Count(c => c.ImportedAs is null && c.State is SourceContractCandidateState.New or SourceContractCandidateState.Changed or SourceContractCandidateState.Moved);
        var review = files.Count(c => c.State == SourceContractCandidateState.NeedsReview);
        var fragments = files.Count(IsFragment);
        var imported = files.Count(c => c.ImportedAs is not null || c.State == SourceContractCandidateState.UpToDate);
        return $"{files.Count} file{(files.Count == 1 ? "" : "s")} in {groups} candidate logical API group{(groups == 1 ? "" : "s")} · {rest} REST · {files.Count - rest} GraphQL"
            + $" · {imported} already imported · {actionable} new or changed{(review > 0 ? $" · {review} need identity review" : "")}{(fragments > 0 ? $" · {fragments} GraphQL fragment{(fragments == 1 ? "" : "s")}" : "")}";
    }
}
