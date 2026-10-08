using BirkNext.Web.Models;

namespace BirkNext.Web.Services.SampleProjects;

/// <summary>
/// The generic artifact discovery shared by Sample Projects and Project Import: classifies candidate Markdown documents with
/// <see cref="SampleArtifactClassifier"/>, marks byte-identical duplicates and groups detected documents by role. Pure: no I/O, no
/// project-specific names. A role with several detected documents is never resolved here — the caller requires an explicit choice.
/// </summary>
public static class ArtifactDocumentDiscovery
{
    public sealed record Candidate(string RelativePath, string FileName, string? Text);

    /// <summary>Classified documents in ordinal path order. Text is normalized (BOM removed, LF line endings) before fingerprinting.</summary>
    public static List<DiscoveredDocument> Classify(IEnumerable<Candidate> candidates) =>
        ClassifyAsync(candidates, progress: null, yieldEvery: 0).GetAwaiter().GetResult();

    /// <summary>
    /// Same classification, yielding every <paramref name="yieldEvery"/> documents so a browser page can render progress: the classifier runs
    /// the full Markdown and domain extractors per document, which takes minutes for hundreds of documents in WebAssembly.
    /// </summary>
    public static async Task<List<DiscoveredDocument>> ClassifyAsync(IEnumerable<Candidate> candidates, IProgress<(int Done, int Total)>? progress,
        int yieldEvery = 8, CancellationToken cancellationToken = default)
    {
        var documents = new List<DiscoveredDocument>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordered = candidates.OrderBy(c => c.RelativePath, StringComparer.Ordinal).ToList();
        // Identical inputs (normalized text + the path parts the classifier reads) are classified once: projects often repeat the same
        // scaffolding documents in every sub-project, and classification is the expensive step in WebAssembly.
        var classified = new Dictionary<(string Fingerprint, string PathKey), ArtifactClassification>();
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (yieldEvery > 0 && documents.Count > 0 && documents.Count % yieldEvery == 0)
            {
                progress?.Report((documents.Count, ordered.Count));
                await Task.Delay(1, cancellationToken); // lets the WebAssembly renderer paint between batches
            }
            var fingerprint = candidate.Text is null ? null : ArtifactFingerprint.Compute(Normalize(candidate.Text));
            var key = (fingerprint ?? "", SampleArtifactClassifier.PathKey(candidate.RelativePath));
            if (fingerprint is null || !classified.TryGetValue(key, out var classification))
            {
                classification = SampleArtifactClassifier.Classify(candidate.RelativePath, candidate.Text);
                if (fingerprint is not null) classified[key] = classification;
            }
            string? duplicateOf = null;
            if (fingerprint is not null && !seen.TryAdd(fingerprint, candidate.RelativePath)) duplicateOf = seen[fingerprint];
            documents.Add(new DiscoveredDocument(candidate.RelativePath, candidate.FileName, classification.Status, classification.Role, classification.Confidence,
                classification.Reasons, classification.Candidates, fingerprint, duplicateOf));
        }
        progress?.Report((documents.Count, ordered.Count));
        return documents;
    }

    /// <summary>Detected documents grouped by role in product order; <paramref name="chosen"/> gives an explicit choice among several.</summary>
    public static List<SampleRoleSummary> Roles(IReadOnlyList<DiscoveredDocument> documents, Func<WorkspaceArtifactType, string?> chosen) =>
        SampleArtifactClassifier.RoleOrder.Select(role => new SampleRoleSummary(
            role,
            documents.Where(d => d.Status == ArtifactDiscoveryStatus.Detected && d.Role == role).ToList(),
            chosen(role))).ToList();

    public static string Normalize(string text)
    {
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
