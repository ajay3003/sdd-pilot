using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>One possible Target Application with its confidence and the evidence behind it.</summary>
public sealed record ApplicationCandidate(string Name, ProjectEvidencePackage? Package, string Confidence, IReadOnlyList<string> Reasons)
{
    public bool Strong => Confidence is TargetApplicationInference.VeryHigh or TargetApplicationInference.High;
}

/// <summary>The inference result: a unique strong application (applied automatically), several candidates or a conflict (Needs review), or none.</summary>
public sealed record ApplicationInference(IReadOnlyList<ApplicationCandidate> Candidates, ApplicationCandidate? Unique, string State, string Summary)
{
    public static readonly ApplicationInference None = new([], null, TargetApplicationInference.NoEvidence, "No application evidence yet: import the application's source or run Check target application.");
}

/// <summary>
/// Infers WHICH application a Target Environment represents from evidence BirkNext already holds, so a tester never types it in:
/// <list type="bullet">
/// <item>Source evidence (identity): imported primary application sources (<see cref="ProjectEvidencePackage.IsApplicationCandidate"/> — never
/// shared libraries, infrastructure, contracts or generated docs). The package's application name is the candidate, not its ZIP file name.</item>
/// <item>Target evidence (confirmation): the target host naming the candidate ("m2lbdev.bufetat.no" → M2LB). A detected framework, identity
/// provider or suggested profile name is supporting evidence only — never an identity by itself.</item>
/// </list>
/// Confidence: one source candidate + agreeing host = Very High; one source candidate = High; several candidates with exactly one named by the
/// host = High; a host naming a different known application = Conflict (Needs review); several without a host decision = Needs review; a host
/// alone = Medium (suggested, never applied). Only a unique High/Very High candidate is applied automatically.
/// </summary>
public static class TargetApplicationInference
{
    public const string VeryHigh = "Very High", High = "High", Medium = "Medium";
    public const string Detected = "Detected", NeedsReview = "Needs review", Conflict = "Conflict", NoEvidence = "No evidence";

    /// <param name="primaryImports">The imports the workspace's application contexts classify as a primary application source (a related
    /// application source such as another component's package is not a candidate). Null when no classification exists: then every package
    /// holding application source is a candidate.</param>
    public static ApplicationInference Infer(IReadOnlyList<ProjectEvidencePackage> packages, string? targetUrl, TargetEnvironmentDetectionResult? detection,
        IEnumerable<string>? knownNames = null, IReadOnlySet<string>? primaryImports = null)
    {
        var sources = packages.Where(p => p.IsApplicationCandidate && (primaryImports is not { Count: > 0 } || primaryImports.Contains(p.ImportId)))
            .GroupBy(p => p.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.ImportedAt).First()).ToList();
        var host = Host(targetUrl);
        var supporting = Supporting(detection, host);
        bool Named(string name) => host is not null && Compact(name).Length >= 3 && Compact(host).Contains(Compact(name), StringComparison.Ordinal);
        ApplicationCandidate Candidate(ProjectEvidencePackage package, string confidence)
        {
            var reasons = new List<string> { $"Imported primary application source: {package.ArchiveFileName}" };
            if (Named(package.DisplayName)) reasons.Add($"Target host {host} names {package.DisplayName}");
            if (detection?.SuggestedProfileName is { Length: > 0 } profile && Compact(profile).Contains(Compact(package.DisplayName), StringComparison.Ordinal))
                reasons.Add($"Detected profile name \"{profile}\" agrees (supporting only)");
            reasons.AddRange(supporting);
            return new(package.DisplayName.Trim(), package, confidence, reasons);
        }

        var others = (knownNames ?? []).Concat(packages.Select(p => p.DisplayName)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 1)
        {
            var only = sources[0];
            // The source says one application, the target host names a different one: never auto-associate.
            if (host is not null && !Named(only.DisplayName) && others.FirstOrDefault(n => !string.Equals(n, only.DisplayName, StringComparison.OrdinalIgnoreCase) && Named(n)) is { } other)
                return new([Candidate(only, Medium), new(other, null, Medium, [$"Target host {host} names {other}"])], null, Conflict,
                    $"The imported application source is {only.DisplayName}, but the target host {host} names {other}. Choose the application.");
            var candidate = Candidate(only, Named(only.DisplayName) ? VeryHigh : High);
            return new([candidate], candidate, Detected, candidate.Confidence == VeryHigh
                ? $"Target host + imported primary source agree on {candidate.Name}."
                : $"{candidate.Name} is the only imported application source{(host is null ? "" : $"; the target host {host} does not name it")}.");
        }
        if (sources.Count > 1)
        {
            var named = sources.Where(p => Named(p.DisplayName)).ToList();
            if (named.Count == 1)
            {
                var chosen = Candidate(named[0], High);
                return new([chosen, .. sources.Where(p => p != named[0]).Select(p => Candidate(p, Medium))], chosen, Detected,
                    $"{sources.Count} application sources are imported; the target host {host} names {chosen.Name}.");
            }
            return new([.. sources.Select(p => Candidate(p, Medium))], null, NeedsReview,
                $"{sources.Count} application sources are imported and nothing decides between them. Choose the application.");
        }
        if (others.FirstOrDefault(Named) is { } known)
            return new([new(known, null, Medium, [$"Target host {host} names {known}", .. supporting])], null, NeedsReview,
                $"The target host suggests {known}, but no application source is imported for it.");
        return ApplicationInference.None;
    }

    /// <summary>
    /// Applies the inference to the profile: a unique strong candidate becomes the Target Application when none is set (origin Detected); an
    /// existing application of the same name — manually added or chosen — is confirmed in place (provenance upgraded, never duplicated); an
    /// explicit different choice always wins. True when the profile changed.
    /// </summary>
    public static bool Apply(FrontendAnalysisProfile profile, ApplicationInference inference, DateTimeOffset now)
    {
        if (inference.Unique is not { Strong: true } unique) return false;
        var current = TargetApplications.Of(profile);
        if (current is null)
            return TargetApplications.Set(profile, new TargetApplicationIdentity
            {
                Name = unique.Name, ImportId = unique.Package?.ImportId, ArchiveSha256 = unique.Package?.ArchiveSha256, SetAt = now,
                Origin = TargetApplicationOrigins.Detected, Confidence = unique.Confidence, Reasons = [.. unique.Reasons],
            }, now);
        if (!TargetApplications.SameApplication(current, unique.Name)) return false;
        var stored = profile.TargetApplication ?? current;
        var reasons = unique.Reasons.ToList();
        if (stored.Confidence == unique.Confidence && stored.Reasons is { } old && old.SequenceEqual(reasons)
            && (stored.Origin == TargetApplicationOrigins.Detected || stored.ConfirmedByDetectionAt is not null)) return false;
        stored.Confidence = unique.Confidence;
        stored.Reasons = reasons;
        if (stored.Origin != TargetApplicationOrigins.Detected) stored.ConfirmedByDetectionAt ??= now;
        stored.ImportId ??= unique.Package?.ImportId;
        stored.ArchiveSha256 ??= unique.Package?.ArchiveSha256;
        if (profile.TargetApplication is null) { stored.Legacy = false; profile.TargetApplication = stored; }
        return true;
    }

    /// <summary>"Detected automatically · Very High confidence", "Confirmed by target detection · originally added manually", "Added manually" …</summary>
    public static string Provenance(TargetApplicationIdentity identity) =>
        identity.Origin == TargetApplicationOrigins.Detected ? $"Detected automatically{(identity.Confidence is { } c ? $" · {c} confidence" : "")}"
        : identity.ConfirmedByDetectionAt is not null ? $"Confirmed by detection{(identity.Confidence is { } confirmed ? $" · {confirmed} confidence" : "")} · originally {(identity.Manual ? "added manually" : "chosen by you")}"
        : identity.Origin == TargetApplicationOrigins.Chosen || !identity.Manual && identity.ImportId is not null ? "Chosen by you"
        : identity.Manual ? "Added manually"
        : "Named application";

    private static IReadOnlyList<string> Supporting(TargetEnvironmentDetectionResult? detection, string? host)
    {
        if (detection is not { Success: true }) return [];
        var list = new List<string>();
        if (detection.Reachability == TargetReachability.Reachable) list.Add($"Target {host ?? "URL"} reachable");
        if (detection.DetectedClientFramework is { } framework) list.Add($"Frontend framework detected: {framework} (supporting only)");
        if (detection.DetectedAuthenticationType != FrontendAuthenticationType.None) list.Add($"Identity provider detected: {detection.DetectedAuthenticationType} (supporting only)");
        return list;
    }

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ? uri.Host.ToLowerInvariant() : null;

    private static string Compact(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
