using System.Globalization;
using System.Text.RegularExpressions;

namespace BirkNext.Updater;

/// <summary>
/// The identity of one BirkNext build, from its build-manifest.json (never from a file or folder name):
/// <list type="bullet">
/// <item>application version MAJOR.MINOR.PATCH (versioning/BirkNext.Version.props);</item>
/// <item>channel: CI or Release for pipeline builds, Local for a developer build, Unknown for CI without identity;</item>
/// <item>the pipeline run id (build-manifest pipelineRunId, else "ci.&lt;id&gt;" in the build version) that orders pipeline builds of one application
/// version — Azure DevOps run ids only grow;</item>
/// <item>the source commit and the full informational version shown to the tester.</item>
/// </list>
/// </summary>
public sealed partial record PackageVersion(Version Application, string Channel, long? BuildId, string BuildVersion, string Informational, string? SourceCommit)
{
    /// <summary>"1.0.0-ci.416921" → build id 416921; any other build version has none.</summary>
    public static long? BuildIdOf(string buildVersion) =>
        CiRe().Match(buildVersion) is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    public static PackageVersion? Parse(string? applicationVersion, string? channel, string? buildVersion, string? informational, string? commit, string? pipelineRunId = null)
    {
        if (applicationVersion is null || !SemverRe().IsMatch(applicationVersion) || string.IsNullOrWhiteSpace(buildVersion)) return null;
        var v = Version.Parse(applicationVersion);
        var runId = long.TryParse(pipelineRunId, NumberStyles.None, CultureInfo.InvariantCulture, out var run) ? run : BuildIdOf(buildVersion!);
        return new(new Version(v.Major, v.Minor, v.Build), string.IsNullOrWhiteSpace(channel) ? "Unknown" : channel!, runId, buildVersion!,
            string.IsNullOrWhiteSpace(informational) ? buildVersion! : informational!, string.IsNullOrWhiteSpace(commit) ? null : commit);
    }

    /// <summary>
    /// The build recorded by the launcher in the data folder (runtime-state/last-run.json holds the informational version, for example
    /// "1.0.0-ci.416921+8b7922bd…"): channel and run id are derived from the build version's form. Null when it is not a BirkNext version.
    /// </summary>
    public static PackageVersion? FromInformational(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var plus = informational.IndexOf('+');
        var build = plus < 0 ? informational : informational[..plus];
        var dash = build.IndexOf('-');
        var app = dash < 0 ? build : build[..dash];
        var suffix = dash < 0 ? "" : build[(dash + 1)..];
        var channel = suffix.Length == 0 ? "Release" : suffix.StartsWith("ci.", StringComparison.Ordinal) ? "CI" : suffix == "local" ? "Local" : "Unknown";
        var commit = plus < 0 ? null : informational[(plus + 1)..].Split('.')[0];
        return Parse(app, channel, build, informational, commit is { Length: 40 } ? commit : null);
    }

    /// <summary>A pipeline build (CI or Release) with its run id: the only builds the updater can order within one application version.</summary>
    public bool IsPipeline => Channel is "CI" or "Release" && BuildId is not null;

    /// <summary>What shows in the updater: "1.0.0-ci.416921 (commit 8b7922bd)".</summary>
    public string Display => SourceCommit is { Length: >= 8 } c ? $"{BuildVersion} (commit {c[..8]})" : BuildVersion;

    /// <summary>A folder name for the installation: the build version with anything unsafe replaced (the identity stays in the manifest).</summary>
    public string FolderName => SafeRe().Replace(BuildVersion, "-");

    [GeneratedRegex(@"^\d+\.\d+\.\d+-ci\.(\d{1,18})$")] private static partial Regex CiRe();
    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$")] private static partial Regex SemverRe();
    [GeneratedRegex(@"[^A-Za-z0-9._-]+")] private static partial Regex SafeRe();
}

public enum UpgradePath { FirstInstall, Upgrade, SameBuild, Downgrade, NotComparable }

public static class VersionRules
{
    /// <summary>
    /// How a candidate relates to the installed build:
    /// <list type="bullet">
    /// <item>a higher application version is an upgrade, whatever the channel;</item>
    /// <item>within one application version, two CI builds compare by pipeline build id;</item>
    /// <item>the same informational version (or build id) is the same build;</item>
    /// <item>anything that cannot be ordered — a Local or Unknown build against another build of the same application version — is
    /// NotComparable and refused: a developer build never silently replaces a pipeline build or the other way round.</item>
    /// </list>
    /// </summary>
    public static UpgradePath Compare(PackageVersion? installed, PackageVersion candidate)
    {
        if (installed is null) return UpgradePath.FirstInstall;
        if (string.Equals(installed.Informational, candidate.Informational, StringComparison.Ordinal)) return UpgradePath.SameBuild;
        var app = candidate.Application.CompareTo(installed.Application);
        if (app > 0) return UpgradePath.Upgrade;
        if (app < 0) return UpgradePath.Downgrade;
        if (candidate.IsPipeline && installed.IsPipeline && candidate.BuildId is { } c && installed.BuildId is { } i)
            return c > i ? UpgradePath.Upgrade : c < i ? UpgradePath.Downgrade : UpgradePath.SameBuild;
        return UpgradePath.NotComparable;
    }

    public static string Explain(UpgradePath path, PackageVersion? installed, PackageVersion candidate) => path switch
    {
        UpgradePath.FirstInstall => $"BirkNext {candidate.Display} will be installed.",
        UpgradePath.Upgrade => $"BirkNext {installed!.Display} will be upgraded to {candidate.Display}.",
        UpgradePath.SameBuild => $"BirkNext {candidate.Display} is already installed. Nothing to do.",
        UpgradePath.Downgrade => $"This package ({candidate.Display}) is older than the installed BirkNext ({installed!.Display}). The updater does not install older versions: your data may already use a newer database layout.",
        _ => $"This package ({candidate.Display}) cannot be ordered against the installed BirkNext ({installed!.Display}): one of them is not a pipeline build. Use a package from the approved Azure DevOps pipeline.",
    };
}
