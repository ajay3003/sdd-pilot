using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Models;

/// <summary>
/// The exact artifact a workflow step reads for one role in the current workspace: its stable id (a Sample Project path or
/// a workspace document) and the fingerprint of the content the reviewer sees. Sent by the client with every step build and
/// review decision; the content itself never leaves the client.
/// </summary>
public sealed class ArtifactRevisionRef
{
    public string Role { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? FileName { get; set; }
}

/// <summary>
/// What a review decision on a step is about: the exact artifact revisions the step reads. A decision is stored with the
/// binding's <see cref="Hash"/>, so it is current only while the step still reads the same artifacts with the same content;
/// a decision under any other hash is history, and the step shows it as stale.
/// </summary>
public sealed record WorkflowArtifactBinding(string Hash, string IdentityHash, string References)
{
    /// <summary>A step that reads no artifact (none of the review steps today): one fixed binding.</summary>
    public static readonly WorkflowArtifactBinding NoArtifacts = new("no-artifacts", "no-artifacts", "");

    /// <summary>Roles the step reads: its required roles, then its optional ones.</summary>
    public static IReadOnlyList<string> RolesOf(WorkflowStepDefinition definition) =>
        definition.RequiredArtifacts.Concat(definition.OptionalArtifacts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The binding for a step, or null when a role it reads is available but no single artifact revision is identified for it
    /// (several artifacts and none selected): nothing can be decided until the reviewer chooses one. A role that is not
    /// available is left out — the step is not applicable when it is required, and reads nothing for it when optional.
    /// </summary>
    public static WorkflowArtifactBinding? For(
        WorkflowStepDefinition definition,
        IReadOnlyDictionary<string, bool> available,
        IReadOnlyCollection<ArtifactRevisionRef> artifacts)
    {
        var roles = RolesOf(definition);
        if (roles.Count == 0) return NoArtifacts;

        var refs = new List<ArtifactRevisionRef>();
        foreach (var role in roles)
        {
            if (!available.TryGetValue(role, out var isAvailable) || !isAvailable) continue;
            var reference = artifacts.FirstOrDefault(a => string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase)
                                                          && !string.IsNullOrWhiteSpace(a.ArtifactId) && !string.IsNullOrWhiteSpace(a.Fingerprint));
            if (reference is null) return null;
            refs.Add(reference);
        }
        if (refs.Count == 0) return null;

        refs = refs.OrderBy(r => r.Role, StringComparer.OrdinalIgnoreCase).ToList();
        var canonical = string.Join("\n", refs.Select(r => $"{r.Role.ToLowerInvariant()}|{r.ArtifactId}|{r.Fingerprint.ToUpperInvariant()}"));
        // Identity: which artifacts, regardless of content. The same identity with another hash means the content changed.
        var identity = string.Join("\n", refs.Select(r => $"{r.Role.ToLowerInvariant()}|{r.ArtifactId}"));
        var references = string.Join("; ", refs.Select(r => $"{r.Role}: {r.FileName ?? r.ArtifactId} @ {Short(r.Fingerprint)}"));
        return new WorkflowArtifactBinding(Sha256(canonical), Sha256(identity), references);
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The first 8 characters of a fingerprint, as shown to reviewers.</summary>
    public static string Short(string fingerprint) =>
        fingerprint.Length <= 8 ? fingerprint.ToUpperInvariant() : fingerprint[..8].ToUpperInvariant();
}
