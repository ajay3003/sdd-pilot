using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// BirkNext's evaluator for an explicit subset of Renovate policy semantics — not a Renovate emulator. Supported: repository-level settings,
/// ignoreDeps, ignorePaths, enabledManagers / per-manager enabled, the default-disabled azure-pipelines manager, packageRules with the matchers
/// in <see cref="RenovateConfig.SupportedMatchers"/> (exact, glob, /regex/ and !negation), update types major/minor/patch from exact versions,
/// ignoreUnstable (default true), allowedVersions (regex, NuGet intervals, simple comparator ranges), enabled, automerge, groupName, schedule and
/// dependencyDashboardApproval. packageRules apply in order and every matching rule is merged over the previous ones, so a later matching rule
/// overrides the settings it sets (labels replace; addLabels append). Everything else — built-in/external preset expansion, 0.x update
/// types, four-part revisions, ranges and rangeStrategy, digest updates, custom managers — is Not assessed with its reason.
/// </summary>
public sealed class RenovatePolicyEvaluator(JsonObject config, IReadOnlyList<string> unresolvedPresets)
{
    public static readonly string[] Unsupported =
    [
        "Built-in and external presets (e.g. config:recommended) are not expanded: preset rules (grouping, workarounds, default ignorePaths) are not evaluated.",
        "0.x versions: Renovate's update type for zero-major versions is not assessed.",
        "Four-part (revision) versions, version ranges/floating versions and rangeStrategy are not simulated.",
        "Digest and pin updates, lock-file maintenance, replacements and rollbacks are not simulated.",
        "Custom/regex managers and matchers outside the supported subset make the affected rule Not assessed.",
        "Schedules are shown as configured; whether the current time is inside the schedule is not evaluated.",
    ];

    private readonly JsonArray _rules = config["packageRules"] as JsonArray ?? [];

    public IReadOnlyList<string> IgnoreDeps => RenovateConfig.Strings(config["ignoreDeps"]);
    public IReadOnlyList<string> IgnorePaths => RenovateConfig.Strings(config["ignorePaths"]);

    // ── Manager and dependency eligibility ──────────────────────────────────────────────────────────────────────────

    /// <summary>Renovate managers that are disabled unless explicitly enabled.</summary>
    public static readonly HashSet<string> DefaultDisabledManagers = new(StringComparer.Ordinal) { "azure-pipelines" };

    public ManagerState ManagerState(string manager)
    {
        var enabledManagers = RenovateConfig.Strings(config["enabledManagers"]);
        if (enabledManagers.Count > 0) return enabledManagers.Contains(manager) ? Dependencies.ManagerState.Enabled : Dependencies.ManagerState.DisabledByConfig;
        if (config[manager] is JsonObject managerConfig && managerConfig["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled))
            return enabled ? Dependencies.ManagerState.Enabled : Dependencies.ManagerState.DisabledByConfig;
        return DefaultDisabledManagers.Contains(manager) ? Dependencies.ManagerState.DisabledByDefault : Dependencies.ManagerState.Enabled;
    }

    /// <summary>Why Renovate would not process the dependency at all, or null.</summary>
    public string? IgnoredBy(DeclaredDependency dep)
    {
        if (config["enabled"] is JsonValue repoEnabled && repoEnabled.TryGetValue<bool>(out var on) && !on) return "Renovate is disabled for the repository (enabled: false).";
        if (IgnoreDeps.Any(i => string.Equals(i, dep.PackageName, StringComparison.OrdinalIgnoreCase))) return $"Configured ignoreDeps ({dep.PackageName}).";
        if (IgnorePaths.FirstOrDefault(p => PathMatches(dep.OwnerFile, p)) is { } path) return $"Configured ignorePaths ({path}).";
        return ManagerState(dep.Manager) switch
        {
            Dependencies.ManagerState.DisabledByConfig => $"The {dep.Manager} manager is disabled by configuration.",
            Dependencies.ManagerState.DisabledByDefault => $"The {dep.Manager} manager is disabled by Renovate's default and not enabled in this configuration.",
            _ => null,
        };
    }

    // ── Pattern semantics ───────────────────────────────────────────────────────────────────────────────────────────

    public static Regex Glob(string pattern)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { builder.Append(".*"); i++; if (i + 1 < pattern.Length && pattern[i + 1] == '/') i++; }
            else if (c == '*') builder.Append("[^/]*");
            else if (c == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>ignorePaths/matchFileNames: minimatch-style glob, or — for a pattern without wildcards — the path containing it.</summary>
    public static bool PathMatches(string path, string pattern) =>
        pattern.IndexOfAny(['*', '?', '{', '[']) < 0 ? path.Contains(pattern, StringComparison.OrdinalIgnoreCase) || path.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            : Glob(pattern).IsMatch(path);

    /// <summary>Renovate's name-list semantics: exact, glob or /regex/flags, with !negation; only negations = everything except those.</summary>
    public static bool NameListMatches(string value, IReadOnlyList<string> patterns)
    {
        bool One(string p)
        {
            if (p.Length > 2 && p[0] == '/' && p.LastIndexOf('/') > 0)
            {
                var end = p.LastIndexOf('/');
                var options = p[(end + 1)..].Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None;
                return Regex.IsMatch(value, p[1..end], options);
            }
            return p.IndexOfAny(['*', '?']) >= 0 ? Glob(p).IsMatch(value) : string.Equals(p, value, StringComparison.OrdinalIgnoreCase);
        }
        var positives = patterns.Where(p => !p.StartsWith('!')).ToList();
        var negatives = patterns.Where(p => p.StartsWith('!')).Select(p => p[1..]).ToList();
        return (positives.Count == 0 || positives.Any(One)) && !negatives.Any(One);
    }

    public static string? Category(string manager) => manager switch
    {
        "nuget" => "dotnet",
        "dockerfile" or "docker-compose" => "docker",
        "azure-pipelines" or "github-actions" => "ci",
        "npm" => "js",
        _ => null,
    };

    /// <summary>
    /// Whether rule <paramref name="index"/> matches. <paramref name="updateType"/> null = evaluate every matcher except matchUpdateTypes
    /// (a rule that only differs by update type "may apply").
    /// </summary>
    public (bool Matches, List<string> MatchedOn, List<string> Unsupported) RuleMatches(int index, DeclaredDependency dep, DependencyUpdateType? updateType)
    {
        var rule = _rules[index] as JsonObject ?? [];
        var on = new List<string>();
        var unsupported = rule.Where(p => p.Key.StartsWith("match", StringComparison.Ordinal) || p.Key.StartsWith("exclude", StringComparison.Ordinal))
            .Select(p => p.Key).Where(k => !RenovateConfig.SupportedMatchers.Contains(k)).ToList();
        bool Check(string key, Func<List<string>, bool> test, Func<List<string>, string> describe)
        {
            if (rule[key] is null) return true;
            var values = RenovateConfig.Strings(rule[key]);
            if (!test(values)) return false;
            on.Add(describe(values));
            return true;
        }
        var ok = Check("matchManagers", v => v.Contains(dep.Manager), v => $"matchManagers [{string.Join(", ", v)}] ∋ {dep.Manager}")
            && Check("matchDatasources", v => dep.Datasource is not null && v.Contains(dep.Datasource), v => $"matchDatasources [{string.Join(", ", v)}] ∋ {dep.Datasource}")
            && Check("matchCategories", v => Category(dep.Manager) is { } c && v.Contains(c), v => $"matchCategories [{string.Join(", ", v)}] ∋ {Category(dep.Manager)}")
            && Check("matchDepTypes", v => dep.DepType is not null && v.Contains(dep.DepType), v => $"matchDepTypes [{string.Join(", ", v)}] ∋ {dep.DepType}")
            && Check("matchPackageNames", v => NameListMatches(dep.PackageName, v), v => $"matchPackageNames [{string.Join(", ", v)}] matches {dep.PackageName}")
            && Check("matchDepNames", v => NameListMatches(dep.PackageName, v), v => $"matchDepNames [{string.Join(", ", v)}] matches {dep.PackageName}")
            && Check("matchPackagePatterns", v => v.Any(p => Regex.IsMatch(dep.PackageName, p, RegexOptions.IgnoreCase)), v => $"matchPackagePatterns (deprecated) [{string.Join(", ", v)}] matches {dep.PackageName}")
            && Check("matchPackagePrefixes", v => v.Any(p => dep.PackageName.StartsWith(p, StringComparison.OrdinalIgnoreCase)), v => $"matchPackagePrefixes (deprecated) [{string.Join(", ", v)}]")
            && Check("excludePackageNames", v => !v.Contains(dep.PackageName, StringComparer.OrdinalIgnoreCase), v => "excludePackageNames (deprecated) does not exclude it")
            && Check("excludePackagePatterns", v => !v.Any(p => Regex.IsMatch(dep.PackageName, p, RegexOptions.IgnoreCase)), v => "excludePackagePatterns (deprecated) does not exclude it")
            && Check("excludePackagePrefixes", v => !v.Any(p => dep.PackageName.StartsWith(p, StringComparison.OrdinalIgnoreCase)), v => "excludePackagePrefixes (deprecated) does not exclude it")
            && Check("matchFileNames", v => v.Any(p => PathMatches(dep.OwnerFile, p)), v => $"matchFileNames [{string.Join(", ", v)}] matches {dep.OwnerFile}")
            && Check("matchPaths", v => v.Any(p => PathMatches(dep.OwnerFile, p)), v => $"matchPaths (deprecated) [{string.Join(", ", v)}] matches {dep.OwnerFile}")
            && (updateType is null || Check("matchUpdateTypes", v => v.Contains(updateType.Value.ToString().ToLowerInvariant()), v => $"matchUpdateTypes [{string.Join(", ", v)}] ∋ {updateType.Value.ToString().ToLowerInvariant()}"));
        if (ok && on.Count == 0) on.Add("no matchers (applies to every dependency)");
        return (ok, on, unsupported);
    }

    public int RuleCount => _rules.Count;
    public JsonObject Rule(int index) => _rules[index] as JsonObject ?? [];

    // ── Versions ────────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Version(int[] Parts, string? Prerelease, string? Suffix)
    {
        public override string ToString() => string.Join('.', Parts) + (Prerelease is null ? "" : "-" + Prerelease) + (Suffix is null ? "" : "-" + Suffix);
    }

    /// <summary>NuGet: 1-4 numeric parts with optional -prerelease. Docker: numeric tag with an optional -suffix (e.g. 10.0-alpine) kept as compatibility.</summary>
    public static Version? ParseVersion(string? value, string manager)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = Regex.Match(value.Trim(), @"^v?(?<parts>\d+(?:\.\d+){0,3})(?:-(?<rest>[0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$");
        if (!m.Success) return null;
        var parts = m.Groups["parts"].Value.Split('.').Select(int.Parse).ToArray();
        var rest = m.Groups["rest"].Success ? m.Groups["rest"].Value : null;
        return manager is "dockerfile" or "docker-compose" or "azure-pipelines" ? new Version(parts, null, rest) : new Version(parts, rest, null);
    }

    public static int Compare(Version a, Version b)
    {
        for (var i = 0; i < Math.Max(a.Parts.Length, b.Parts.Length); i++)
        {
            var x = i < a.Parts.Length ? a.Parts[i] : 0;
            var y = i < b.Parts.Length ? b.Parts[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return (a.Prerelease, b.Prerelease) switch { (null, null) => 0, (null, _) => 1, (_, null) => -1, var (p, q) => string.CompareOrdinal(p, q) };
    }

    public static (DependencyUpdateType Type, string? Reason) UpdateType(string? current, string candidate, string manager, bool isRange = false)
    {
        if (isRange) return (DependencyUpdateType.NotAssessable, "The dependency uses a version range or floating version; rangeStrategy handling is not simulated.");
        var from = ParseVersion(current, manager);
        var to = ParseVersion(candidate, manager);
        if (from is null || to is null) return (DependencyUpdateType.NotAssessable, "Simulation is not supported for this versioning scheme (the value is not a numeric version).");
        if (from.Suffix != to.Suffix) return (DependencyUpdateType.NotAssessable, "The candidate tag suffix differs from the current one (compatibility change is not simulated).");
        if (Compare(to, from) <= 0) return (DependencyUpdateType.NotAssessable, "The candidate is not newer than the current version.");
        if (from.Parts[0] == 0) return (DependencyUpdateType.NotAssessable, "0.x versions: Renovate's update type for zero-major versions is not assessed by BirkNext.");
        var n = Math.Max(from.Parts.Length, to.Parts.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < from.Parts.Length ? from.Parts[i] : 0;
            var y = i < to.Parts.Length ? to.Parts[i] : 0;
            if (x == y) continue;
            return i switch
            {
                0 => (DependencyUpdateType.Major, null),
                1 => (DependencyUpdateType.Minor, null),
                2 => (DependencyUpdateType.Patch, null),
                _ => (DependencyUpdateType.NotAssessable, "Four-part (revision) updates are not simulated."),
            };
        }
        return (DependencyUpdateType.Patch, null); // same numbers, prerelease → stable of the same version
    }

    /// <summary>Synthetic candidates for a policy test. Never observed, published or "latest" versions.</summary>
    public static List<(string Scenario, string? Candidate, string? Reason)> SyntheticCandidates(string? current, string manager, bool isRange)
    {
        var v = isRange ? null : ParseVersion(current, manager);
        if (v is null)
        {
            var why = isRange ? "The dependency uses a version range or floating version; rangeStrategy handling is not simulated."
                : "Not a numeric version: simulation is not supported for this versioning scheme.";
            return [("Patch", null, why), ("Minor", null, why), ("Major", null, why)];
        }
        string Make(int[] parts, string? pre = null) => string.Join('.', parts) + (pre is null ? "" : "-" + pre) + (v.Suffix is null ? "" : "-" + v.Suffix);
        var p = v.Parts.Length >= 3 ? v.Parts : [.. v.Parts, .. Enumerable.Repeat(0, 3 - v.Parts.Length)];
        var result = new List<(string, string?, string?)>
        {
            ("Patch", v.Parts.Length >= 3 ? Make([p[0], p[1], p[2] + 1]) : null, v.Parts.Length >= 3 ? null : "The version has no patch part."),
            ("Minor", Make(v.Parts.Length >= 3 ? [p[0], p[1] + 1, 0] : [p[0], p[1] + 1]), null),
            ("Major", Make(v.Parts.Length >= 3 ? [p[0] + 1, 0, 0] : v.Parts.Length == 2 ? [p[0] + 1, 0] : [p[0] + 1]), null),
        };
        if (manager == "nuget") result.Add(("Prerelease major", Make([p[0] + 1, 0, 0], "beta.1"), null));
        return result;
    }

    /// <summary>allowedVersions: /regex/, NuGet interval ([1.0,2.0)), or comparators (&lt;2.0.0, &gt;=1.0 &lt;2.0, 1.x). Null = syntax not supported.</summary>
    public static bool? Allowed(string candidate, string spec, string manager)
    {
        spec = spec.Trim();
        if (spec.Length > 2 && spec[0] == '/' && spec.LastIndexOf('/') > 0)
            return Regex.IsMatch(candidate, spec[1..spec.LastIndexOf('/')], spec[(spec.LastIndexOf('/') + 1)..].Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None);
        var version = ParseVersion(candidate, manager);
        if (version is null) return null;
        var interval = Regex.Match(spec, @"^(?<lo>[\[(])\s*(?<a>[^,\s]*)\s*,\s*(?<b>[^\])\s]*)\s*(?<hi>[\])])$");
        if (interval.Success)
        {
            var a = interval.Groups["a"].Value.Length == 0 ? null : ParseVersion(interval.Groups["a"].Value, manager);
            var b = interval.Groups["b"].Value.Length == 0 ? null : ParseVersion(interval.Groups["b"].Value, manager);
            var okLow = a is null || (interval.Groups["lo"].Value == "[" ? Compare(version, a) >= 0 : Compare(version, a) > 0);
            var okHigh = b is null || (interval.Groups["hi"].Value == "]" ? Compare(version, b) <= 0 : Compare(version, b) < 0);
            return okLow && okHigh;
        }
        var all = true;
        foreach (var token in spec.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(token, @"^(?<op><=|>=|<|>|=)?v?(?<v>\d+(?:\.(?:\d+|x|\*)){0,3})$");
            if (!m.Success) return null;
            var op = m.Groups["op"].Value;
            var raw = m.Groups["v"].Value;
            if (raw.Contains('x') || raw.Contains('*'))
            {
                if (op.Length > 0) return null;
                var fixedParts = raw.Split('.').TakeWhile(s => s != "x" && s != "*").Select(int.Parse).ToArray();
                all &= fixedParts.Select((x, i) => i < version.Parts.Length && version.Parts[i] == x).All(b => b);
                continue;
            }
            var bound = ParseVersion(raw, manager)!;
            var c = Compare(version, bound);
            all &= op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, ">=" => c >= 0, _ => c == 0 };
        }
        return all;
    }

    // ── Effective policy and simulation ────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Merged = ["enabled", "automerge", "automergeType", "platformAutomerge", "groupName", "schedule", "allowedVersions", "rangeStrategy",
        "dependencyDashboardApproval", "labels", "addLabels", "minimumReleaseAge", "ignoreUnstable"];

    public (EffectivePolicy Policy, List<RuleMatch> Matches, List<string> Unsupported) Effective(DeclaredDependency dep, DependencyUpdateType updateType)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (var key in Merged.Where(k => config[k] is not null)) { values[key] = config[key]; sources[key] = "repository config"; }
        labels.AddRange(RenovateConfig.Strings(config["labels"]));
        var matches = new List<RuleMatch>();
        var unsupported = new List<string>();
        for (var i = 0; i < _rules.Count; i++)
        {
            var (ok, on, notSupported) = RuleMatches(i, dep, updateType);
            if (!ok) continue;
            var rule = Rule(i);
            if (notSupported.Count > 0) unsupported.Add($"Rule #{i + 1} uses {string.Join(", ", notSupported)} (not evaluated).");
            var applied = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in Merged.Where(k => rule[k] is not null))
            {
                if (key == "labels") labels = RenovateConfig.Strings(rule[key]);
                else if (key == "addLabels") labels.AddRange(RenovateConfig.Strings(rule[key]));
                values[key] = rule[key];
                sources[key] = $"rule #{i + 1}{(rule["description"] is { } d ? $" “{RenovateConfig.Text(d)}”" : "")}";
                applied[key] = RenovateConfig.Text(rule[key]);
            }
            matches.Add(new RuleMatch { RuleIndex = i + 1, Description = rule["description"] is { } desc ? RenovateConfig.Text(desc) : null, MatchedOn = on, Applied = applied });
        }
        bool? Bool(string key) => values.TryGetValue(key, out var v) && v is JsonValue j && j.TryGetValue<bool>(out var b) ? b : null;
        string? Str(string key) => values.TryGetValue(key, out var v) && v is not null ? RenovateConfig.Text(v) : null;
        var policy = new EffectivePolicy
        {
            Enabled = Bool("enabled") ?? true, Automerge = Bool("automerge"), AutomergeType = Str("automergeType"), GroupName = Str("groupName"),
            Schedule = values.TryGetValue("schedule", out var s) ? RenovateConfig.Strings(s) : [], AllowedVersions = Str("allowedVersions"), RangeStrategy = Str("rangeStrategy"),
            DependencyDashboardApproval = Bool("dependencyDashboardApproval"), MinimumReleaseAge = Str("minimumReleaseAge"), Labels = labels.Distinct().ToList(), Sources = sources,
            IgnoreUnstable = Bool("ignoreUnstable"),
        };
        return (policy, matches, unsupported);
    }

    public PolicySimulation Simulate(DeclaredDependency dep, string scenario, string? candidate, string? candidateReason)
    {
        PolicySimulation Result(PolicyResult result, DependencyUpdateType type, string explanation, EffectivePolicy? policy = null, List<RuleMatch>? matches = null, List<string>? limitations = null) => new()
        {
            Repository = dep.Repository, PackageName = dep.PackageName, Manager = dep.Manager, Datasource = dep.Datasource, OwnerFile = dep.OwnerFile,
            CurrentValue = dep.CurrentValue, CandidateVersion = candidate, IsSyntheticCandidate = true, Scenario = scenario, UpdateType = type,
            Effective = policy ?? new EffectivePolicy(), MatchedRules = matches ?? [], Result = result, Explanation = explanation,
            Limitations = [.. (limitations ?? []), .. unresolvedPresets.Select(p => $"Preset {p} is not expanded; its rules are not part of this result.")],
        };
        if (IgnoredBy(dep) is { } ignored) return Result(PolicyResult.Ignored, DependencyUpdateType.NotAssessable, $"Ignored by Renovate: {ignored} This is not \"up to date\".");
        if (candidate is null) return Result(PolicyResult.NotAssessable, DependencyUpdateType.NotAssessable, candidateReason ?? "No synthetic candidate for this scenario.");
        var (type, reason) = UpdateType(dep.CurrentValue, candidate, dep.Manager, dep.IsRange);
        if (type == DependencyUpdateType.NotAssessable) return Result(PolicyResult.NotAssessable, type, reason!);
        var (policy, matches, unsupported) = Effective(dep, type);
        if (unsupported.Count > 0) return Result(PolicyResult.NotAssessable, type, "A matching rule uses a matcher BirkNext does not evaluate.", policy, matches, unsupported);
        var candidateVersion = ParseVersion(candidate, dep.Manager)!;
        var currentVersion = ParseVersion(dep.CurrentValue, dep.Manager)!;
        var ruleText = matches.Count == 0 ? "No packageRule matches; repository-level settings apply." : $"Matched {string.Join(", ", matches.Select(m => $"#{m.RuleIndex}"))}.";
        if (candidateVersion.Prerelease is not null && currentVersion.Prerelease is null && (policy.IgnoreUnstable ?? true))
            return Result(PolicyResult.NotProposed, type, $"{ruleText} The synthetic candidate is a prerelease and the current version is stable: Renovate's ignoreUnstable (default true) does not propose it.", policy, matches);
        if (!policy.Enabled) return Result(PolicyResult.Blocked, type, $"{ruleText} Renovate update disabled by policy (enabled: false from {policy.Sources.GetValueOrDefault("enabled")}).", policy, matches);
        if (policy.AllowedVersions is { } allowed)
        {
            var within = Allowed(candidate, allowed, dep.Manager);
            if (within is null) return Result(PolicyResult.NotAssessable, type, $"{ruleText} allowedVersions \"{allowed}\" uses syntax BirkNext does not evaluate.", policy, matches);
            if (within == false) return Result(PolicyResult.BlockedByVersionConstraint, type, $"{ruleText} The candidate is outside allowedVersions \"{allowed}\" ({policy.Sources.GetValueOrDefault("allowedVersions")}).", policy, matches);
        }
        if (policy.DependencyDashboardApproval == true) return Result(PolicyResult.RequiresApproval, type, $"{ruleText} dependencyDashboardApproval requires approval in the Dependency Dashboard before a PR.", policy, matches);
        if (policy.Schedule.Count > 0 && !policy.Schedule.All(s => s.Equals("at any time", StringComparison.OrdinalIgnoreCase)))
            return Result(PolicyResult.DeferredBySchedule, type, $"{ruleText} Allowed by policy, but Renovate processes it only within schedule: {string.Join("; ", policy.Schedule)}.", policy, matches);
        return Result(PolicyResult.Allowed, type, $"{ruleText} Allowed by policy{(policy.GroupName is { } g ? $", grouped as “{g}”" : "")}; automerge {(policy.Automerge == true ? "enabled" : "not enabled")}.", policy, matches);
    }
}
