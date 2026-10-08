using System.Text.RegularExpressions;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Authored artifacts (Constitution, Specification, Plan, Tasks) vs the generated documentation of the SAME snapshot. Only explicit, structured
/// references are compared — "METHOD /path" literals, Key Entities bullets, Technical Context fields, explicit MUST/MUST NOT technology rules
/// and completed tasks that name a documented key. No all-to-all text matching, no similarity. Every result is a candidate for review: BirkNext
/// never decides whether the authored artifact or the generated document is wrong, and a generated document never supersedes the authored one.
/// Generated documentation is never evidence that a task is complete.
/// </summary>
internal static class AuthoredDocumentationDrift
{
    private static readonly Regex Bold = new(@"^\s*[-*]\s+\*\*([^*]{2,80})\*\*", RegexOptions.Compiled);
    private static readonly Regex TaskLine = new(@"^\s*[-*]\s+\[([ xX])\]\s+(.+)$", RegexOptions.Compiled);
    // A rule governs a technology only when the modal verb applies to it directly: "MUST NOT use X", "X is prohibited", "MUST use X".
    // A prohibition elsewhere in the sentence ("publish to X … MUST NOT contain …") is not a rule about X.
    private static readonly Regex ProhibitionBefore = new(@"(?i)\b(MUST\s+NOT|SHALL\s+NOT|MUST\s+NEVER|NEVER|DO\s+NOT|must\s+not)\s+(use|depend\s+on|introduce|adopt|add|reference|call|rely\s+on|be\s+built\s+(?:with|on))\s+(?:(?:the|an?|any)\s+)?(?:[\w.#-]+\s+){0,2}$", RegexOptions.Compiled);
    private static readonly Regex ProhibitionAfter = new(@"(?i)^(?:\s+[\w.#-]+){0,2}\s+(is|are)\s+(prohibited|forbidden|not\s+permitted|not\s+allowed|banned)\b", RegexOptions.Compiled);
    private static readonly Regex RequirementBefore = new(@"(?i)\b(MUST|SHALL)\s+(use|be\s+(?:built|implemented|hosted|stored)\s+(?:with|on|in)|be\s+based\s+on|run\s+on)\s+(?:(?:the|an?)\s+)?(?:[\w.#-]+\s+){0,2}$", RegexOptions.Compiled);
    private static readonly HashSet<string> PlanMissingCategories = new(StringComparer.Ordinal)
        { "Relational database", "Document database", "Cache", "Messaging", "Messaging framework", "GraphQL server", "Data access", "Frontend framework", "Identity" };

    /// <summary>"Prohibited", "Required" or null: whether the line states a rule that governs <paramref name="term"/> directly.</summary>
    internal static string? RuleFor(string line, TechnologyVocabulary.Term term)
    {
        foreach (Match m in term.Pattern.Matches(line))
        {
            var before = line[..m.Index];
            var window = before.Length > 80 ? before[^80..] : before;
            var after = line[(m.Index + m.Length)..];
            if (ProhibitionBefore.IsMatch(window) || ProhibitionAfter.IsMatch(after)) return "Prohibited";
            if (RequirementBefore.IsMatch(window)) return "Required";
        }
        return null;
    }
    private static readonly Regex TechnicalField = new(@"^\s*(?:[-*]\s*)?\*\*(Language/Version|Primary Dependencies|Storage|Target Platform|Project Type|Testing)\*\*\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<CrossArtifactDriftCandidate> Compare(GeneratedDocumentationSnapshot generated, IReadOnlyList<AuthoredArtifactInput> artifacts)
    {
        var drift = new List<CrossArtifactDriftCandidate>();
        foreach (var artifact in artifacts.Where(a => !string.IsNullOrWhiteSpace(a.Content)))
        {
            var (modules, scopeLabel) = Scope(generated, artifact.SourcePath);
            var moduleIds = modules.Select(m => m.ModuleId).ToHashSet(StringComparer.Ordinal);
            var docs = generated.Documents.Where(d => moduleIds.Contains(d.ModuleId) && d.Origin == DocumentationOrigin.Generated).ToList();
            if (docs.Count == 0) continue;
            var moduleId = modules.Count == 1 ? modules[0].ModuleId : "module:*";
            var lines = artifact.Content.Replace("\r\n", "\n").Split('\n');
            DriftEvidenceRef Authored(string label, int line = 0, string? value = null) =>
                new(artifact.Role, $"{artifact.Role} · {SafeName(artifact.DisplayName)}", SafeName(artifact.SourcePath ?? artifact.DisplayName), line, SafeName(artifact.ArtifactId), artifact.Fingerprint, value ?? label);
            List<DriftEvidenceRef> Docs(Func<GeneratedDocumentationEvidence, bool> having) =>
                docs.Where(having).Select(d => new DriftEvidenceRef("Generated documentation", $"{GeneratedDocumentationText.Label(d.DocumentKind)} · {System.IO.Path.GetFileName(d.SafeRelativePath)}", d.SafeRelativePath, 0, d.EvidenceId, d.ContentFingerprint)).ToList();
            GeneratedDocumentationFreshnessStatus? Fresh(Func<GeneratedDocumentationEvidence, bool> having) =>
                docs.Where(having).Any(d => d.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale) ? GeneratedDocumentationFreshnessStatus.Stale : docs.Where(having).Select(d => (GeneratedDocumentationFreshnessStatus?)d.FreshnessStatus).FirstOrDefault();
            var sourceRoutes = modules.SelectMany(m => m.SourceKeys.Routes.Concat(m.SourceKeys.AuxiliaryRoutes)).ToList();
            var sourceEntities = modules.SelectMany(m => m.SourceKeys.Entities).ToList();
            DriftEvidenceRef SourceRef(string family, bool present) => new("Source evidence", present ? $"{family}: present in source" : $"{family}: not found in source", scopeLabel, 0, null, null, present ? "present" : "not found");

            switch (Role(artifact.Role))
            {
                case "Specification":
                {
                    var sink = new DriftSink(generated.SourceSnapshotId, moduleId, CrossArtifactDriftType.SpecificationDrift);
                    var documentedRoutes = docs.SelectMany(d => d.Keys.Routes).ToList();
                    if (documentedRoutes.Count > 0)
                        foreach (var (route, line) in Lines(lines, l => StructuredKeys.InlineRoutes(l)).DistinctBy(x => x.Value))
                            if (!documentedRoutes.Any(r => StructuredKeys.SameRoute(r, route)))
                            {
                                var inSource = sourceRoutes.Any(r => StructuredKeys.SameRoute(r, route));
                                sink.Add(drift, "Specified routes", route, DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Confirmed,
                                    [Authored(route, line)], [.. Docs(d => d.Keys.Routes.Count > 0), SourceRef("Route", inSource)],
                                    $"The specification states {route}; no generated document of {scopeLabel} documents it.{(inSource ? " Source declares it — the generated documentation may be outdated." : "")}",
                                    "Specified intent and generated documentation differ. The generated document does not supersede the specification; either may need an update.",
                                    Fresh(d => d.Keys.Routes.Count > 0), artifact.Fingerprint);
                            }
                    var documentedEntities = docs.SelectMany(d => d.Keys.Entities).SelectMany(e => e.Split('|')).Select(StructuredKeys.Name).ToHashSet(StringComparer.Ordinal);
                    if (documentedEntities.Count > 0)
                        foreach (var (entity, line) in KeyEntities(lines))
                            if (!documentedEntities.Contains(StructuredKeys.Name(entity)))
                            {
                                var inSource = sourceEntities.Any(e => e.Split('|').Select(StructuredKeys.Name).Contains(StructuredKeys.Name(entity)));
                                sink.Add(drift, "Key entities", entity, DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.StronglySupported,
                                    [Authored(entity, line)], [.. Docs(d => d.Keys.Entities.Count > 0), SourceRef("Entity", inSource)],
                                    $"The specification names key entity '{entity}'; the generated data-model documentation of {scopeLabel} does not.{(inSource ? " The Database analysis has it." : "")}",
                                    "A conceptual entity may be modelled under another name or not persisted; the specification remains the authored intent.",
                                    Fresh(d => d.Keys.Entities.Count > 0), artifact.Fingerprint);
                            }
                    break;
                }
                case "Constitution":
                {
                    var sink = new DriftSink(generated.SourceSnapshotId, moduleId, CrossArtifactDriftType.ConstitutionDrift);
                    var documented = docs.SelectMany(d => d.Keys.Technologies).ToHashSet(StringComparer.Ordinal);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var terms = TechnologyVocabulary.Match(line);
                        if (terms.Count == 0) continue;
                        var prohibited = terms.Where(t => RuleFor(line, TechnologyVocabulary.ById(t)!) == "Prohibited").ToList();
                        var requiredTerms = terms.Where(t => RuleFor(line, TechnologyVocabulary.ById(t)!) == "Required").ToList();
                        {
                            foreach (var term in prohibited.Where(documented.Contains))
                                sink.Add(drift, "Technology rules", $"Prohibited: {TechnologyVocabulary.Name(term)}", DriftDifferenceKind.ValueMismatch, ArchitectureEvidenceState.Inferred,
                                    [Authored($"Line {i + 1}: prohibits {TechnologyVocabulary.Name(term)}", i + 1)], Docs(d => d.Keys.Technologies.Contains(term)),
                                    $"The constitution prohibits {TechnologyVocabulary.Name(term)} (line {i + 1}); generated documentation of {scopeLabel} names it.",
                                    "Explicit rule vs documented technology. The documentation may describe an exception, a test-only use or outdated content — review before acting.",
                                    Fresh(d => d.Keys.Technologies.Contains(term)), artifact.Fingerprint);
                        }
                        {
                            foreach (var term in requiredTerms)
                            {
                                var required = TechnologyVocabulary.ById(term)!;
                                if (!required.Exclusive || documented.Contains(term)) continue;
                                var competing = documented.Select(TechnologyVocabulary.ById).Where(t => t is not null && t.Exclusive && t.Category == required.Category && t.Id != term).ToList();
                                foreach (var other in competing)
                                    sink.Add(drift, "Technology rules", $"Required: {required.Name}", DriftDifferenceKind.ValueMismatch, ArchitectureEvidenceState.Inferred,
                                        [Authored($"Line {i + 1}: requires {required.Name}", i + 1)], Docs(d => d.Keys.Technologies.Contains(other!.Id)),
                                        $"The constitution requires {required.Name} ({required.Category}, line {i + 1}); generated documentation of {scopeLabel} names {other!.Name} and not {required.Name}.",
                                        "Explicit rule vs documented technology of the same exclusive category. Requires review; nothing is decided automatically.",
                                        Fresh(d => d.Keys.Technologies.Contains(other.Id)), artifact.Fingerprint);
                            }
                        }
                    }
                    break;
                }
                case "Plan":
                {
                    var sink = new DriftSink(generated.SourceSnapshotId, moduleId, CrossArtifactDriftType.PlanDrift);
                    var documented = docs.SelectMany(d => d.Keys.Technologies).ToHashSet(StringComparer.Ordinal);
                    if (documented.Count == 0) break;
                    foreach (var (field, value, line) in TechnicalContext(lines))
                        foreach (var term in TechnologyVocabulary.Match(value))
                        {
                            var planned = TechnologyVocabulary.ById(term)!;
                            if (documented.Contains(term)) continue;
                            var competing = documented.Select(TechnologyVocabulary.ById).Where(t => t is not null && t.Exclusive && planned.Exclusive && t.Category == planned.Category).ToList();
                            if (competing.Count > 0)
                                foreach (var other in competing)
                                    sink.Add(drift, "Technical context", $"{field}: {planned.Name}", DriftDifferenceKind.ValueMismatch, ArchitectureEvidenceState.Inferred,
                                        [Authored($"{field}: {planned.Name}", line)], Docs(d => d.Keys.Technologies.Contains(other!.Id)),
                                        $"The plan's {field} names {planned.Name}; generated documentation of {scopeLabel} names {other!.Name} ({planned.Category}) and not {planned.Name}.",
                                        "The plan may predate a technology change, or the documentation may be outdated.", Fresh(d => d.Keys.Technologies.Contains(other.Id)), artifact.Fingerprint);
                            else if (PlanMissingCategories.Contains(planned.Category))
                                sink.Add(drift, "Technical context", $"{field}: {planned.Name}", DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.Inferred,
                                    [Authored($"{field}: {planned.Name}", line)], Docs(_ => true).Take(5).ToList(),
                                    $"The plan's {field} names {planned.Name}; no generated document of {scopeLabel} names it.",
                                    "Generated documentation may omit a dependency, or the plan may list something that was not adopted.", Fresh(_ => true), artifact.Fingerprint);
                        }
                    break;
                }
                case "Tasks":
                {
                    var sink = new DriftSink(generated.SourceSnapshotId, moduleId, CrossArtifactDriftType.DeliveryDrift);
                    var documentedEntities = docs.SelectMany(d => d.Keys.Entities).SelectMany(e => e.Split('|')).Where(n => n.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var documentedResources = docs.SelectMany(d => d.Keys.MessagingResources).Where(n => n.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var sourceResources = modules.SelectMany(m => m.SourceKeys.MessagingResources).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (TaskLine.Match(lines[i]) is not { Success: true } task || task.Groups[1].Value == " ") continue;
                        var text = task.Groups[2].Value;
                        var taskId = Regex.Match(text, @"^(T\d+)\b").Value is { Length: > 0 } id ? id : $"line {i + 1}";
                        void Unverified(string family, string key, string documentedAs, Func<GeneratedDocumentationEvidence, bool> having) =>
                            sink.Add(drift, family, $"{taskId}: {key}", DriftDifferenceKind.MissingOnRight, ArchitectureEvidenceState.StronglySupported,
                                [Authored($"{taskId} marked complete", i + 1, key)], [.. Docs(having), SourceRef(family, false)],
                                $"Task completion not independently verified: {taskId} is marked complete and generated documentation describes {documentedAs}, but no source evidence of {scopeLabel} shows it.",
                                "Generated documentation is supporting evidence only and never verifies a task. Implementation Review assesses completion against source; this may also be a route/library BirkNext does not read.",
                                Fresh(having), artifact.Fingerprint, ["Implementation Review (task results)"]);
                        if (sourceRoutes.Count > 0)
                            foreach (var route in StructuredKeys.InlineRoutes(text).Distinct())
                                if (docs.Any(d => d.Keys.Routes.Any(r => StructuredKeys.SameRoute(r, route))) && !sourceRoutes.Any(r => StructuredKeys.SameRoute(r, route)))
                                    Unverified("Routes", route, route, d => d.Keys.Routes.Any(r => StructuredKeys.SameRoute(r, route)));
                        if (sourceEntities.Count > 0)
                            foreach (var entity in documentedEntities.Where(e => Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(e)}(?![\w])")))
                                if (!sourceEntities.Any(s => s.Split('|').Select(StructuredKeys.Name).Contains(StructuredKeys.Name(entity))))
                                    Unverified("Entities", entity, $"entity '{entity}'", d => d.Keys.Entities.Any(x => x.Split('|').Contains(entity, StringComparer.OrdinalIgnoreCase)));
                        if (sourceResources.Count > 0)
                            foreach (var resource in documentedResources.Where(r => text.Contains(r, StringComparison.OrdinalIgnoreCase) && !sourceResources.Contains(r)))
                                Unverified("Messaging resources", resource, $"messaging resource '{resource}'", d => d.Keys.MessagingResources.Contains(resource, StringComparer.OrdinalIgnoreCase));
                    }
                    break;
                }
            }
        }
        return drift;
    }

    /// <summary>The modules an authored artifact belongs to: the module whose root contains its path, else every module (a workspace-level artifact).</summary>
    internal static (List<GeneratedDocumentationModule> Modules, string Label) Scope(GeneratedDocumentationSnapshot generated, string? sourcePath)
    {
        var path = (sourcePath ?? "").Replace('\\', '/').TrimStart('/');
        var match = generated.Modules.Where(m => m.RootPath.Length > 0 && path.StartsWith(m.RootPath + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.RootPath.Length).FirstOrDefault();
        return match is not null ? ([match], $"scope {match.RootPath}/") : (generated.Modules, "the workspace");
    }

    internal static string Role(string role) => role.Trim().ToLowerInvariant() switch
    {
        "specification" or "spec" => "Specification",
        "constitution" => "Constitution",
        "plan" => "Plan",
        "tasks" or "task" => "Tasks",
        _ => role,
    };

    private static IEnumerable<(string Value, int Line)> Lines(string[] lines, Func<string, IEnumerable<string>> read)
    {
        for (var i = 0; i < lines.Length; i++) foreach (var value in read(lines[i])) yield return (value, i + 1);
    }

    /// <summary>Spec-Kit "Key Entities" bullets: "- **Order**: …" under a heading that names entities.</summary>
    internal static IEnumerable<(string Entity, int Line)> KeyEntities(string[] lines)
    {
        var inSection = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"^#{1,6}\s")) { inSection = Regex.IsMatch(line, @"(?i)\b(key\s+)?entit(y|ies)\b"); continue; }
            if (!inSection) continue;
            if (Bold.Match(line) is { Success: true } m)
            {
                var name = m.Groups[1].Value.Trim().TrimEnd(':').Trim();
                if (DocumentStructure.IsIdentifier(name.Replace(" ", ""))) yield return (name.Replace(" ", ""), i + 1);
            }
        }
    }

    /// <summary>Plan "Technical Context" fields (Spec-Kit): "**Storage**: PostgreSQL".</summary>
    internal static IEnumerable<(string Field, string Value, int Line)> TechnicalContext(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
            if (TechnicalField.Match(lines[i]) is { Success: true } m && !Regex.IsMatch(m.Groups[2].Value, @"(?i)NEEDS CLARIFICATION|N/A"))
                yield return (m.Groups[1].Value, m.Groups[2].Value, i + 1);
    }

    private static string SafeName(string? value) => GeneratedDocumentationDetector.Safe(value ?? "");
}
