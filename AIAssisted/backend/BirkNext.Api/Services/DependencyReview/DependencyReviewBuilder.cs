using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>A repository to review: its files (in memory), and optionally a separately supplied config that replaces the in-repository one.</summary>
public sealed record RepositoryInput(string Name, string ArchiveSha256, IReadOnlyList<RepositoryFile> Files, (string Name, string Content)? ConfigOverride = null);

/// <summary>
/// Builds one repository's static Renovate review: config discovery (Renovate's order), syntax and subset checks, managers and declared
/// dependencies, packageRules with the dependencies they can match, a default synthetic patch/minor/major matrix, conservative "Needs review"
/// heuristics, the security-update policy as configured, and the automation evidence the source contains (never runtime success).
/// </summary>
public static class DependencyReviewBuilder
{
    public static RepositoryDependencyReview Build(RepositoryInput input, RepositoryDependencyReview? previous)
    {
        var repo = input.Name;
        var files = input.Files;
        var (dependencies, managerFiles) = DependencyInventory.Build(repo, files);

        // Config discovery: an explicitly supplied config wins; otherwise the first file in Renovate's order at the repository root.
        var configFiles = new List<RenovateConfigFile>();
        (string Path, string Content)? used = null;
        if (input.ConfigOverride is { } supplied) used = ($"(supplied) {supplied.Name}", supplied.Content);
        foreach (var name in RenovateConfig.FileOrder)
        {
            var file = files.FirstOrDefault(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file is null || name == "package.json" && !file.Content.Contains("\"renovate\"", StringComparison.Ordinal)) continue;
            var parsed = RenovateConfig.Parse(file.Path, file.Content);
            var isUsed = used is null;
            configFiles.Add(new RenovateConfigFile
            {
                Repository = repo, Path = file.Path, Sha256 = RenovateConfig.Hash(file.Content), Used = isUsed, SyntaxValid = parsed.Root is not null, SyntaxError = parsed.Error,
                Note = isUsed ? "Renovate reads this file (first in its config-file order)." : input.ConfigOverride is not null ? "Replaced by the separately supplied configuration for this review." : "Not read: an earlier file in Renovate's config-file order exists.",
            });
            used ??= (file.Path, file.Content);
        }
        foreach (var stray in files.Where(f => RenovateConfig.IsConfigCandidate(f.Path) && !RenovateConfig.FileOrder.Contains(f.Path, StringComparer.OrdinalIgnoreCase)))
            configFiles.Add(new RenovateConfigFile { Repository = repo, Path = stray.Path, Sha256 = RenovateConfig.Hash(stray.Content), Used = false, SyntaxValid = RenovateConfig.Parse(stray.Path, stray.Content).Root is not null,
                Note = "Not a repository-root config location: Renovate does not read it as this repository's configuration." });
        if (input.ConfigOverride is { } over)
        {
            var parsed = RenovateConfig.Parse(over.Name, over.Content);
            configFiles.Insert(0, new RenovateConfigFile { Repository = repo, Path = $"(supplied) {over.Name}", Sha256 = RenovateConfig.Hash(over.Content), Used = true, SyntaxValid = parsed.Root is not null, SyntaxError = parsed.Error,
                Note = "Supplied separately for this review; it replaces the in-repository configuration." });
        }

        var managers = managerFiles.Select(m => new ManagerCoverage { Manager = m.Manager, Files = m.Files.Count, Dependencies = dependencies.Count(d => d.Manager == m.Manager), State = ManagerState.NoConfig }).ToList();
        var automation = AutomationEvidence(files);
        if (used is null)
        {
            var deps = dependencies.Select(d => d with { IgnoredBy = "No Renovate configuration in this repository." }).ToList();
            var bearing = dependencies.Count > 0;
            return new RepositoryDependencyReview
            {
                Repository = repo, ArchiveSha256 = input.ArchiveSha256, Coverage = RenovateCoverage.Missing,
                CoverageDetail = bearing ? "No Renovate configuration file in the repository. Organization-level or global Renovate configuration is not visible to BirkNext."
                    : "No Renovate configuration and no declared dependencies.",
                ConfigFiles = configFiles, Managers = managers, Dependencies = deps, AutomationEvidence = automation,
                Findings = bearing ? [new DependencyFinding { RuleId = "renovate-missing", Severity = DependencyFindingSeverity.Warning, Repository = repo, Title = "Renovate is not configured for this dependency-bearing repository",
                    Detail = $"{dependencies.Count} declared dependencies in {managers.Count} manager(s); no renovate.json, .renovaterc or equivalent at the repository root. This is not a runtime failure — there is nothing configured to run.",
                    Evidence = managers.Select(m => $"{m.Manager}: {m.Files} file(s), {m.Dependencies} dependencies").ToList() }] : [],
                SecurityUpdatePolicy = new("Security-update policy", ReviewCategoryState.NotConfigured, "No Renovate configuration."),
                RuntimeAutomation = new("Runtime automation", ReviewCategoryState.NotAssessed, automation.Count == 0 ? "No Renovate pipeline in the repository source." : string.Join(" ", automation)),
                DriftSincePrevious = previous?.ConfigHash is null ? null : "Configuration removed since the previous review.",
            };
        }

        var root = RenovateConfig.Parse(used.Value.Path, used.Value.Content);
        var configHash = RenovateConfig.Hash(used.Value.Content);
        if (root.Root is null)
        {
            return new RepositoryDependencyReview
            {
                Repository = repo, ArchiveSha256 = input.ArchiveSha256, Coverage = RenovateCoverage.Partial, CoverageDetail = $"The configuration {used.Value.Path} could not be parsed; policy is not assessed.",
                ConfigFiles = configFiles, ConfigHash = configHash, ConfigSyntaxValid = false, ValidationIssues = [root.Error ?? "Unparseable configuration."],
                Managers = managers, Dependencies = dependencies, AutomationEvidence = automation,
                Findings = [new DependencyFinding { RuleId = root.UnsupportedSyntax ? "config-syntax-unsupported" : "config-invalid", Severity = root.UnsupportedSyntax ? DependencyFindingSeverity.Info : DependencyFindingSeverity.Warning,
                    Repository = repo, Title = root.UnsupportedSyntax ? "Renovate configuration syntax not supported by BirkNext" : "Renovate configuration is not valid JSON", Detail = root.Error ?? "", Evidence = [used.Value.Path] }],
                SecurityUpdatePolicy = new("Security-update policy", ReviewCategoryState.NotAssessed, "Configuration not parsed."),
                RuntimeAutomation = new("Runtime automation", ReviewCategoryState.NotAssessed, string.Join(" ", automation)),
            };
        }

        var config = root.Root;
        var extends = RenovateConfig.Strings(config["extends"]);
        var unresolved = extends.ToList(); // No preset is expanded offline — built-in ones included.
        var evaluator = new RenovatePolicyEvaluator(config, unresolved);
        var inherited = extends.Any(e => !RenovateConfig.IsBuiltInPreset(e));
        var issues = new List<string>();
        var unknown = config.Select(p => p.Key).Where(k => !RenovateConfig.SupportedTopLevel.Contains(k)).ToList();

        var withEligibility = dependencies.Select(d => d with { IgnoredBy = evaluator.IgnoredBy(d) }).ToList();
        managers = managers.Select(m => m with
        {
            State = withEligibility.Where(d => d.Manager == m.Manager).ToList() is { Count: > 0 } ofManager && ofManager.All(d => d.IgnoredBy?.StartsWith("Configured ignorePaths", StringComparison.Ordinal) == true)
                ? ManagerState.IgnoredByPaths : evaluator.ManagerState(m.Manager),
        }).Select(m => m with
        {
            Detail = m.State switch
            {
                ManagerState.DisabledByDefault => $"Renovate's {m.Manager} manager is disabled by default and this configuration does not enable it.",
                ManagerState.DisabledByConfig => "Disabled by enabledManagers or the manager's enabled: false.",
                ManagerState.IgnoredByPaths => "Every file of this manager is excluded by ignorePaths.",
                _ => $"{withEligibility.Count(d => d.Manager == m.Manager && d.IgnoredBy is null)} of {m.Dependencies} dependencies processed.",
            },
        }).ToList();

        var rules = new List<RenovateRule>();
        for (var i = 0; i < evaluator.RuleCount; i++)
        {
            var rule = evaluator.Rule(i);
            var matched = withEligibility.Count(d => d.IgnoredBy is null && evaluator.RuleMatches(i, d, null).Matches);
            var matcherKeys = rule.Where(p => p.Key.StartsWith("match", StringComparison.Ordinal) || p.Key.StartsWith("exclude", StringComparison.Ordinal)).ToList();
            rules.Add(new RenovateRule
            {
                Index = i + 1, Description = rule["description"] is { } d ? RenovateConfig.Text(d) : null, GroupName = rule["groupName"] is { } g ? RenovateConfig.Text(g) : null,
                Matchers = matcherKeys.ToDictionary(p => p.Key, p => RenovateConfig.Strings(p.Value)),
                Settings = rule.Where(p => RenovateConfig.PolicySettings.Contains(p.Key) && p.Key != "description").ToDictionary(p => p.Key, p => RenovateConfig.Text(RenovateConfig.Redact(p.Value, p.Key))),
                UnsupportedMatchers = matcherKeys.Select(p => p.Key).Where(k => !RenovateConfig.SupportedMatchers.Contains(k)).ToList(),
                DeprecatedKeys = matcherKeys.Select(p => p.Key).Where(k => RenovateConfig.DeprecatedMatchers.Contains(k)).ToList(),
                MatchedDependencies = matched, Location = new SourceProvenance(used.Value.Path, LineOfRule(used.Value.Content, i)),
            });
            foreach (var key in rule.Select(p => p.Key).Where(k => !RenovateConfig.SupportedMatchers.Contains(k) && !RenovateConfig.PolicySettings.Contains(k) && !k.StartsWith("match", StringComparison.Ordinal) && !k.StartsWith("exclude", StringComparison.Ordinal)))
                if (!RenovateConfig.SupportedTopLevel.Contains(key)) unknown.Add($"packageRules[{i}].{key}");
            if (matcherKeys.Count == 0 && rule.Count > 0) issues.Add($"packageRules[{i}] has no matcher: it applies to every dependency.");
        }

        var simulations = DefaultMatrix(withEligibility, rules, evaluator);
        // Package-family names the heuristics compare rule text against: the repository's own top-level folders plus scopes the rules name.
        var folders = files.Select(f => f.Path.Split('/')[0]).Where(s => s.Length > 1 && !s.StartsWith('.') && !s.Contains('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var findings = Heuristics(repo, config, rules, withEligibility, simulations, evaluator, used.Value.Path, managers, folders);
        var security = SecurityPolicy(config);
        var normalized = RenovateConfig.Redact(config)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var drift = previous?.ConfigHash is null ? null : previous.ConfigHash == configHash ? "Unchanged since the previous review."
            : $"Changed since the previous review (config hash {previous.ConfigHash[..12]} → {configHash[..12]}): {RuleDiff(previous.Rules, rules)}";

        return new RepositoryDependencyReview
        {
            Repository = repo, ArchiveSha256 = input.ArchiveSha256,
            Coverage = inherited ? RenovateCoverage.Inherited : RenovateCoverage.Configured,
            CoverageDetail = inherited ? $"Configured in {used.Value.Path}, extending {string.Join(", ", extends.Where(e => !RenovateConfig.IsBuiltInPreset(e)))} (not resolved offline)." : $"Configured in {used.Value.Path}.",
            ConfigFiles = configFiles, ConfigHash = configHash, Extends = extends, UnresolvedPresets = unresolved, ConfigSyntaxValid = true, ValidationIssues = issues, UnrecognizedKeys = unknown,
            Managers = managers, Dependencies = withEligibility, Rules = rules, IgnoreDeps = [.. evaluator.IgnoreDeps], IgnorePaths = [.. evaluator.IgnorePaths],
            RepositorySettings = config.Where(p => p.Key is not ("packageRules" or "extends" or "$schema" or "ignorePaths" or "ignoreDeps")).ToDictionary(p => p.Key, p => RenovateConfig.Text(RenovateConfig.Redact(p.Value, p.Key))),
            Simulations = simulations, Findings = findings, SecurityUpdatePolicy = security,
            RuntimeAutomation = new("Runtime automation", ReviewCategoryState.NotAssessed, (automation.Count == 0 ? "No Renovate pipeline in the repository source." : string.Join(" ", automation)) + " Whether Renovate runs successfully is not assessed (no repository/pipeline connector)."),
            AutomationEvidence = automation, NormalizedConfig = normalized, DriftSincePrevious = drift,
        };
    }

    private static int LineOfRule(string content, int index)
    {
        var at = content.IndexOf("\"packageRules\"", StringComparison.Ordinal);
        if (at < 0) return 1;
        var depth = 0; var seen = -1;
        for (var i = content.IndexOf('[', at) + 1; i > 0 && i < content.Length; i++)
        {
            if (content[i] == '{') { if (depth == 0 && ++seen == index) return content[..i].Count(c => c == '\n') + 1; depth++; }
            else if (content[i] == '}') depth--;
            else if (content[i] == ']' && depth == 0) break;
        }
        return 1;
    }

    private static string RuleDiff(List<RenovateRule> before, List<RenovateRule> after)
    {
        var a = before.Select(r => r.Description ?? $"#{r.Index}").ToList();
        var b = after.Select(r => r.Description ?? $"#{r.Index}").ToList();
        var added = b.Except(a).ToList();
        var removed = a.Except(b).ToList();
        return added.Count + removed.Count == 0 ? "the same rules (settings or other keys differ)." : $"{(added.Count > 0 ? $"added {string.Join("; ", added)}" : "")}{(added.Count > 0 && removed.Count > 0 ? "; " : "")}{(removed.Count > 0 ? $"removed {string.Join("; ", removed)}" : "")}.";
    }

    /// <summary>
    /// The default test matrix: for each rule, the first dependency its non-update-type matchers select, plus one unmatched dependency per manager
    /// (default policy) — each with synthetic patch/minor/major (and prerelease for NuGet) candidates.
    /// </summary>
    private static List<PolicySimulation> DefaultMatrix(List<DeclaredDependency> deps, List<RenovateRule> rules, RenovatePolicyEvaluator evaluator)
    {
        var picks = new List<DeclaredDependency>();
        foreach (var rule in rules)
            if (deps.FirstOrDefault(d => d.IgnoredBy is null && evaluator.RuleMatches(rule.Index - 1, d, null).Matches && !picks.Contains(d)) is { } dep) picks.Add(dep);
        foreach (var manager in deps.Select(d => d.Manager).Distinct())
            if (deps.FirstOrDefault(d => d.Manager == manager && d.IgnoredBy is null && !Enumerable.Range(0, evaluator.RuleCount).Any(i => evaluator.RuleMatches(i, d, null).Matches)) is { } unmatched && !picks.Contains(unmatched))
                picks.Add(unmatched);
        foreach (var ignored in deps.Where(d => d.IgnoredBy is not null).GroupBy(d => d.IgnoredBy).Select(g => g.First()).Take(3)) picks.Add(ignored);
        return picks.SelectMany(dep => RenovatePolicyEvaluator.SyntheticCandidates(dep.CurrentValue, dep.Manager, dep.IsRange)
            .Select(c => evaluator.Simulate(dep, c.Scenario, c.Candidate, c.Reason))).ToList();
    }

    /// <summary>Scope names a rule targets: top-level folders of its file matchers and literal package-name prefixes.</summary>
    private static HashSet<string> Scopes(RenovateRule rule)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rule.Matchers.GetValueOrDefault("matchFileNames", []).Concat(rule.Matchers.GetValueOrDefault("matchPaths", [])))
            if (p.Split('/', 2)[0] is { Length: > 0 } top && !top.Contains('*')) scopes.Add(top);
        foreach (var p in rule.Matchers.GetValueOrDefault("matchPackageNames", []).Concat(rule.Matchers.GetValueOrDefault("matchPackagePrefixes", [])))
            if (Regex.Match(p.TrimStart('!', '/', '^'), @"^[A-Za-z][A-Za-z0-9]+") is { Success: true } m) scopes.Add(m.Value);
        return scopes;
    }

    private static string RuleText(JsonObject rule) => string.Join(" ", new[] { "description", "groupName", "commitMessageTopic", "prBodyNotes" }.Select(k => RenovateConfig.Text(rule[k])));

    /// <summary>Conservative "Needs review" heuristics. They describe what the configuration does; they never claim what anyone intended.</summary>
    private static List<DependencyFinding> Heuristics(string repo, JsonObject config, List<RenovateRule> rules, List<DeclaredDependency> deps, List<PolicySimulation> sims,
        RenovatePolicyEvaluator evaluator, string configPath, List<ManagerCoverage> managers, List<string> folders)
    {
        var findings = new List<DependencyFinding>();
        var knownScopes = rules.SelectMany(Scopes).Concat(folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var json = evaluator.Rule(rule.Index - 1);
            var text = RuleText(json);
            var own = Scopes(rule);
            var where = rule.Location is { } l ? $"{l.File}:{l.Line}" : configPath;
            // 1. Rule text names a different package family than its matchers target.
            // Whole-word matches only, so "Hendelse" is not found inside "HendelseAdapter".
            var mentioned = knownScopes.Where(s => !own.Contains(s) && Regex.IsMatch(text, $@"\b{Regex.Escape(s)}\b", RegexOptions.IgnoreCase)).ToList();
            if (own.Count > 0 && mentioned.Count > 0)
                findings.Add(new DependencyFinding
                {
                    RuleId = "rule-scope-inconsistent", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo, RuleIndex = rule.Index,
                    Title = "Renovate rule name/scope appears inconsistent with package matcher",
                    Detail = $"Rule #{rule.Index} “{rule.Description}” targets {string.Join(", ", own)} ({string.Join("; ", rule.Matchers.Select(m => $"{m.Key} [{string.Join(", ", m.Value)}]"))}), but its text refers to {string.Join(", ", mentioned)}. Possible stale or copy-pasted rule text; the matchers — not the text — decide what it applies to.",
                    Evidence = [where, $"Text: {text.Trim()}", $"Matches {rule.MatchedDependencies} declared dependenc{(rule.MatchedDependencies == 1 ? "y" : "ies")} in the analyzed source."],
                });
            // 2. Update-type wording vs matchUpdateTypes.
            var types = rule.Matchers.GetValueOrDefault("matchUpdateTypes", []);
            var saysMajor = Regex.IsMatch(text, @"\bmajor\b", RegexOptions.IgnoreCase);
            var saysMinorPatch = Regex.IsMatch(text, @"\b(minor|patch)\b", RegexOptions.IgnoreCase);
            if (saysMajor && !saysMinorPatch && (types.Count == 0 || !types.Contains("major")))
                findings.Add(new DependencyFinding { RuleId = "rule-update-type-broad", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo, RuleIndex = rule.Index,
                    Title = "Rule text says “major” but the rule is not limited to major updates",
                    Detail = $"Rule #{rule.Index} “{rule.Description}” has {(types.Count == 0 ? "no matchUpdateTypes, so it applies to every update type" : $"matchUpdateTypes [{string.Join(", ", types)}]")}{(rule.Settings.TryGetValue("enabled", out var en) && en == "false" ? " and enabled: false — it may disable more than major updates" : "")}.",
                    Evidence = [where] });
            if (saysMinorPatch && !saysMajor && types.Contains("major"))
                findings.Add(new DependencyFinding { RuleId = "rule-update-type-broad", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo, RuleIndex = rule.Index,
                    Title = "Rule text says minor/patch but the rule also matches major updates", Detail = $"Rule #{rule.Index} “{rule.Description}” has matchUpdateTypes [{string.Join(", ", types)}].", Evidence = [where] });
            // 3. Manager wording vs matchManagers.
            var managersOfRule = rule.Matchers.GetValueOrDefault("matchManagers", []);
            if (managersOfRule.Count > 0 && Regex.IsMatch(text, @"\bnuget\b", RegexOptions.IgnoreCase) && !managersOfRule.Contains("nuget")
                || managersOfRule.Count > 0 && Regex.IsMatch(text, @"\bdocker\b", RegexOptions.IgnoreCase) && !managersOfRule.Any(m => m.StartsWith("docker", StringComparison.Ordinal)))
                findings.Add(new DependencyFinding { RuleId = "rule-manager-inconsistent", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo, RuleIndex = rule.Index,
                    Title = "Rule text names a package manager its matchManagers do not include", Detail = $"Rule #{rule.Index} “{rule.Description}”: matchManagers [{string.Join(", ", managersOfRule)}].", Evidence = [where] });
            // 4. Rules that match nothing in the analyzed source.
            if (rule.MatchedDependencies == 0 && rule.UnsupportedMatchers.Count == 0)
                findings.Add(new DependencyFinding { RuleId = "rule-matches-nothing", Severity = DependencyFindingSeverity.Info, Repository = repo, RuleIndex = rule.Index,
                    Title = "Rule matches no declared dependency in the analyzed source",
                    Detail = $"Rule #{rule.Index} “{rule.Description}” ({string.Join("; ", rule.Matchers.Select(m => $"{m.Key} [{string.Join(", ", m.Value)}]"))}) selects nothing here. It may be stale, or the configuration may be newer or older than the analyzed source.",
                    Evidence = [where] });
            // 5. Broad disable.
            if (rule.Settings.TryGetValue("enabled", out var enabled) && enabled == "false" && !rule.Matchers.Keys.Any(k => k is "matchPackageNames" or "matchDepNames" or "matchPackagePatterns" or "matchPackagePrefixes" or "matchFileNames"))
                findings.Add(new DependencyFinding { RuleId = "rule-broad-disable", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo, RuleIndex = rule.Index,
                    Title = "enabled: false without a package or file scope", Detail = $"Rule #{rule.Index} “{rule.Description}” disables updates for every dependency its other matchers select ({rule.MatchedDependencies} here).", Evidence = [where] });
        }
        // 6. Duplicate matchers with conflicting outcomes.
        foreach (var group in rules.GroupBy(r => string.Join("|", r.Matchers.OrderBy(m => m.Key).Select(m => $"{m.Key}={string.Join(",", m.Value)}"))).Where(g => g.Count() > 1 && g.Key.Length > 0))
            if (group.Select(r => string.Join(",", r.Settings.Where(s => s.Key is "enabled" or "automerge").OrderBy(s => s.Key).Select(s => $"{s.Key}={s.Value}"))).Distinct().Count() > 1)
                findings.Add(new DependencyFinding { RuleId = "rule-duplicate-conflict", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo,
                    Title = "Rules with identical matchers set different outcomes", Detail = $"Rules {string.Join(", ", group.Select(r => $"#{r.Index}"))}: the later one wins for the settings it sets.", Evidence = [configPath] });
        // 7. Major updates that would automerge.
        var majorAutomerge = sims.Where(s => s.UpdateType == DependencyUpdateType.Major && s.Result is PolicyResult.Allowed or PolicyResult.DeferredBySchedule && s.Effective.Automerge == true).ToList();
        if (majorAutomerge.Count > 0)
            findings.Add(new DependencyFinding
            {
                RuleId = "major-automerge", Severity = DependencyFindingSeverity.NeedsReview, Repository = repo,
                Title = "Major updates would be automerged",
                Detail = $"Automerge is enabled ({majorAutomerge[0].Effective.Sources.GetValueOrDefault("automerge")}) and no matching major rule sets automerge: false — including rules whose PR notes warn about breaking changes. Behaviour of presets (not expanded) could differ.",
                Evidence = majorAutomerge.Select(s => $"{s.PackageName} ({s.OwnerFile}): synthetic major → automerge yes, rules {string.Join(", ", s.MatchedRules.Select(m => $"#{m.RuleIndex}"))}").Distinct().Take(5).ToList(),
            });
        // 8. Managers present but not processed; ignorePaths removing files.
        foreach (var manager in managers.Where(m => m.State is ManagerState.DisabledByDefault or ManagerState.DisabledByConfig or ManagerState.IgnoredByPaths))
            findings.Add(new DependencyFinding { RuleId = "manager-not-processed", Severity = DependencyFindingSeverity.Info, Repository = repo,
                Title = $"{manager.Manager} dependencies are not processed by Renovate", Detail = $"{manager.Files} file(s), {manager.Dependencies} dependencies: {manager.Detail}", Evidence = [configPath] });
        if (config["extends"] is not null)
            findings.Add(new DependencyFinding { RuleId = "preset-unresolved", Severity = DependencyFindingSeverity.Info, Repository = repo,
                Title = "Presets not expanded", Detail = $"extends [{string.Join(", ", RenovateConfig.Strings(config["extends"]))}] is not expanded offline; rules and defaults those presets add are not part of this review, so policy analysis is partial.", Evidence = [configPath] });
        return findings;
    }

    private static ReviewCategory SecurityPolicy(JsonObject config)
    {
        var keys = new[] { "vulnerabilityAlerts", "osvVulnerabilityAlerts" }.Where(k => config[k] is not null).ToList();
        var ruleSecurity = (config["packageRules"] as JsonArray ?? []).OfType<JsonObject>().Any(r => RenovateConfig.Strings(r["matchCategories"]).Contains("security") || r["isVulnerabilityAlert"] is not null);
        return keys.Count == 0 && !ruleSecurity
            ? new("Security-update policy", ReviewCategoryState.NotConfigured,
                "No security-specific policy (vulnerabilityAlerts / osvVulnerabilityAlerts) in the repository config; what presets add is not assessed. Dependency update policy is not vulnerability status, and no vulnerability source is connected, so no vulnerability claim is made.")
            : new("Security-update policy", ReviewCategoryState.Partial, $"Configured: {string.Join(", ", keys)}{(ruleSecurity ? " (and a security-specific packageRule)" : "")}. Whether security updates are raised depends on the platform's alert source, which is not assessed.");
    }

    /// <summary>Renovate automation defined in the source (e.g. a pipeline running the Renovate image). Presence only — never "runs successfully".</summary>
    private static List<string> AutomationEvidence(IReadOnlyList<RepositoryFile> files)
    {
        var evidence = new List<string>();
        foreach (var file in files.Where(f => (f.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                     && Regex.IsMatch(f.Content, @"\brenovate/renovate|^\s*renovate\b|npx\s+renovate", RegexOptions.Multiline | RegexOptions.IgnoreCase)))
        {
            var image = Regex.Match(file.Content, @"renovate/renovate:?(?<tag>[\w.\-]*)");
            var cron = Regex.Match(file.Content, @"cron:\s*['""]?(?<cron>[^'""\n]+)");
            var display = Regex.Match(file.Content, @"displayName:\s*['""]?(?<name>Weekly[^'""\n]*|[^'""\n]*Renovate run[^'""\n]*)", RegexOptions.IgnoreCase);
            var privateRegistry = Regex.Match(file.Content, @"""hostType""\s*:\s*""(?<type>\w+)""\s*,\s*""matchHost""\s*:\s*""(?<host>[^""]+)""");
            evidence.Add($"Pipeline {file.Path}: Renovate image {(image.Success ? $"renovate/renovate:{image.Groups["tag"].Value}" : "not pinned")}{(cron.Success ? $", schedule cron '{cron.Groups["cron"].Value.Trim()}'" : "")}{(display.Success ? $" ({display.Groups["name"].Value.Trim()})" : "")}"
                + (privateRegistry.Success ? $"; private registry host rule configured ({privateRegistry.Groups["type"].Value}, {privateRegistry.Groups["host"].Value}; credentials redacted)" : "") + ".");
        }
        return evidence;
    }
}
