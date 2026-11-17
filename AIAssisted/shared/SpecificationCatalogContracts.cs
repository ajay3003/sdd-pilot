namespace BirkNext.Specifications;

/// <summary>One requirement as the server read it from a specification file of an imported project archive (the shared specification parser,
/// the same one the browser uses). <see cref="TextSha256"/> identifies the requirement's wording, so a later revision can tell "changed" from
/// "unchanged" without keeping the text twice.</summary>
public sealed record SpecificationRequirementEntry(string RequirementId, string Path, string Title, string TextSha256);

/// <summary>A specification file the catalog was read from: archive path and SHA-256 of its bytes as imported.</summary>
public sealed record SpecificationDocumentRef(string Path, string Sha256);

/// <summary>
/// The server-held requirement catalog of a project: captured at Project Import while the archive's documents are in memory, immutable once
/// written. A new revision is written only when the specification content changes; a re-import of the same content reuses the revision.
/// </summary>
public sealed record SpecificationCatalogView(Guid Id, string ProjectKey, int Revision, DateTimeOffset CapturedAt, string Fingerprint, string? ArchiveSha256,
    Guid? SnapshotId, List<SpecificationDocumentRef> Documents, List<SpecificationRequirementEntry> Requirements);

/// <summary>How a requirement reference relates to the project's server-held specification today. Independent of the human review state: a
/// confirmed link can still point at a requirement that is no longer in the current specification.</summary>
public enum RequirementSourceState
{
    /// <summary>Found in the current server catalog with the wording it had when it was linked.</summary>
    Current = 0,
    /// <summary>Found in the current server catalog, but its wording changed since it was linked (or the link predates wording hashes).</summary>
    ChangedSinceLinked = 1,
    /// <summary>The current server catalog does not contain it (removed, renamed or moved to another file).</summary>
    NotInCurrentSpecification = 2,
    /// <summary>The project has no server catalog (imported before the server kept one, or no specification in the archive): only the browser's
    /// reading backs the link.</summary>
    NoServerCatalog = 3,
}

/// <summary>A reference to check (a link as recorded).</summary>
public sealed record RequirementReference(string RequirementId, string? SpecificationPath, string? LinkedTextSha256 = null);

/// <summary>The check of one reference against the current server catalog.</summary>
public sealed record RequirementReferenceCheck(string RequirementId, string? SpecificationPath, RequirementSourceState State, string Detail,
    int? CatalogRevision, string? CurrentTitle, string? CurrentPath);

public sealed record RequirementReferenceCheckRequest(List<RequirementReference> References);

public sealed record RequirementReferenceCheckResult(string ProjectKey, int? CatalogRevision, DateTimeOffset? CapturedAt, List<RequirementReferenceCheck> Checks);

/// <summary>The provenance a requirement link records when it is made.</summary>
public static class RequirementLinkProvenance
{
    /// <summary>Checked against the server-held catalog captured from the imported specification.</summary>
    public const string ServerSpecification = "server-specification";
    /// <summary>Checked only against the requirement list the browser parsed and sent (projects without a server catalog; links made before it existed).</summary>
    public const string BrowserSpecification = "browser-specification";

    public static string Label(string? basis) => basis switch
    {
        ServerSpecification => "Checked against the imported specification on the server",
        BrowserSpecification => "Checked against the specification as read in the browser (not verified on the server)",
        _ => "Recorded before link checks were kept",
    };

    public static string Label(RequirementSourceState state) => state switch
    {
        RequirementSourceState.Current => "In the current specification",
        RequirementSourceState.ChangedSinceLinked => "Wording changed since it was linked",
        RequirementSourceState.NotInCurrentSpecification => "Not in the current specification",
        _ => "No server copy of the specification",
    };
}

public static class SpecificationCatalogRules
{
    /// <summary>Archive-relative path, forward slashes, no leading slash; compared case-insensitively.</summary>
    public static string NormalizePath(string? path) => (path ?? "").Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>The same specification file: equal paths, or one is the other below an archive root folder ("LendoPlus/specs/x/spec.md" and
    /// "specs/x/spec.md"). Never a bare file-name match across folders.</summary>
    public static bool SamePath(string? a, string? b)
    {
        var x = NormalizePath(a); var y = NormalizePath(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        var (longer, shorter) = x.Length > y.Length ? (x, y) : (y, x);
        return shorter.Contains('/') && longer.EndsWith("/" + shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Requirement identity as the shared reference grammar normalizes it (FR-1 = FR-001), compared case-insensitively.</summary>
    public static string NormalizeId(string? id) => BirkNext.Sdd.RequirementReferenceParser.Normalize((id ?? "").Trim()).ToUpperInvariant();
}
