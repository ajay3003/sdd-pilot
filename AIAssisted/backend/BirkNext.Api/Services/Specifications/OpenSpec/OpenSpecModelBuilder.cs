using BirkNext.Specifications;
using BirkNext.TestCoverage;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// Builds the OpenSpec source model of one import (pure, no I/O): classified documents → current baseline (capabilities, requirements, scenarios
/// with BirkNext keys) → active and archived changes with resolved delta operations → diagnostics. Active changes are NEVER applied to the
/// baseline and archived deltas are NEVER reapplied: the effective requirements are exactly what openspec/specs states. Conflicts between active
/// changes are reported, never resolved by ordering.
/// </summary>
public static class OpenSpecModelBuilder
{
    public sealed record Built(OpenSpecSourceModel Model, List<SpecificationDocumentRef> Documents, List<SpecificationRequirementEntry> Entries,
        List<StoredSpecificationEvidence> Evidence, string Fingerprint);

    /// <summary>The OpenSpec route could not produce a baseline (e.g. no openspec/ folder). Nothing is captured; nothing falls back to Spec Kit.</summary>
    public sealed class RouteProblem(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    public static Built Build(string projectKey, string? applicationKey, string? importId, string? archiveSha256, Guid? snapshotId,
        IReadOnlyList<OpenSpecSourceDiscovery.Input> documents, DateTimeOffset now, CancellationToken ct = default)
    {
        var diagnostics = new List<OpenSpecDiagnosticView>();
        void Add(string code, OpenSpecDiagnosticSeverity severity, string message, string? path, int? line, string? change = null)
        {
            if (diagnostics.Count < OpenSpecLimits.MaxDiagnostics) diagnostics.Add(new(code, severity, message, path, line, change));
            else if (diagnostics.Count == OpenSpecLimits.MaxDiagnostics)
                diagnostics.Add(new("DIAGNOSTICS_LIMIT", OpenSpecDiagnosticSeverity.Error, $"Mer enn {OpenSpecLimits.MaxDiagnostics} diagnostikkmeldinger: resten er ikke listet.", null, null));
        }

        if (documents.Count > OpenSpecLimits.MaxDocuments)
            throw new RouteProblem("OPENSPEC_TOO_MANY_DOCUMENTS", $"Importen har {documents.Count} dokumenter; OpenSpec-ruten leser høyst {OpenSpecLimits.MaxDocuments}. Ingenting er tolket.");
        var classified = OpenSpecSourceDiscovery.Classify(documents, ct);
        if (classified.Roots.Count == 0)
            throw new RouteProblem("OPENSPEC_ROOT_MISSING",
                "Applikasjonen er satt til OpenSpec, men importen har ingen openspec/-mappe. Ingen spesifikasjon er tolket, og Spec Kit-tolkning brukes ikke som reserve.");
        if (classified.Roots.Count > 1)
            Add("MULTIPLE_OPENSPEC_ROOTS", OpenSpecDiagnosticSeverity.Warning,
                $"Importen har {classified.Roots.Count} openspec/-mapper ({string.Join(", ", classified.Roots)}). Hver får egne identiteter; de slås aldri sammen.", null, null);

        // Schema: only the built-in spec-driven structure is verified. A custom schema is reported; its definition is never executed or fetched.
        var schemas = classified.Documents.Where(d => d.Document.Kind == OpenSpecDocumentKind.Configuration)
            .Select(d => (d.Document.Path, Schema: OpenSpecSourceDiscovery.SchemaOf(d.Content))).ToList();
        var unknown = schemas.Where(s => !OpenSpecSourceDiscovery.IsBuiltInSchema(s.Schema)).ToList();
        foreach (var u in unknown)
            Add("UNKNOWN_CUSTOM_SCHEMA", OpenSpecDiagnosticSeverity.Error,
                $"openspec/config.yaml erklærer skjemaet «{u.Schema}». BirkNext verifiserer bare det innebygde spec-driven-skjemaet: strukturen er tolket etter standardformatet og er IKKE VERIFISERT mot «{u.Schema}».", u.Path, null);
        foreach (var d in classified.Documents.Where(d => d.Document.Kind == OpenSpecDocumentKind.Unsupported))
            Add("UNSUPPORTED_DOCUMENT", OpenSpecDiagnosticSeverity.Warning, d.Document.Reason ?? "Ikke støttet.", d.Document.Path, null);

        // ── Current baseline ──
        var capabilities = new List<OpenSpecCapability>();
        var capabilityPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var requirementCount = 0; var scenarioCount = 0; var limitReported = false;
        var exclusions = new List<OpenSpecExclusion>();
        foreach (var d in classified.Documents.Where(d => d.Document.Kind == OpenSpecDocumentKind.CurrentSpecification).OrderBy(d => d.Document.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var capability = d.Document.Capability!;
            var scoped = OpenSpecIdentity.ScopePrefix(d.Scope) + capability;
            if (capabilityPaths.TryGetValue(scoped, out var other))
            {
                Add("AMBIGUOUS_CAPABILITY_KEY", OpenSpecDiagnosticSeverity.Error,
                    $"Capability-mappen gir samme nøkkel «{capability}» som {other}. Dokumentet er ikke tatt med i gjeldende grunnlag (ingen sammenslåing).", d.Document.Path, null);
                continue;
            }
            capabilityPaths[scoped] = d.Document.Path;
            var parsed = OpenSpecMarkdownParser.Parse(d.Content, delta: false, ct);
            foreach (var x in parsed.Diagnostics) Add(x.Code, x.Severity, x.Message, d.Document.Path, x.Line);
            var requirements = new List<OpenSpecRequirement>();
            var keys = new Dictionary<string, (string Name, int Line)>(StringComparer.Ordinal);
            if (parsed.ExcludedRequirements > 0 || parsed.ExcludedScenarios > 0)
                exclusions.Add(new(d.Document.Path, parsed.ExcludedRequirements, parsed.ExcludedScenarios, "Ugyldig kravstruktur (se diagnostikk)."));
            var skippedRequirements = 0; var skippedScenarios = 0;
            foreach (var r in parsed.Requirements)
            {
                if (requirementCount >= OpenSpecLimits.MaxRequirements || scenarioCount >= OpenSpecLimits.MaxScenarios)
                {
                    if (!limitReported) Add("LIMIT_REACHED", OpenSpecDiagnosticSeverity.Error,
                        $"Grensen på {OpenSpecLimits.MaxRequirements} krav / {OpenSpecLimits.MaxScenarios} scenarioer er nådd; resten er ikke tatt med.", d.Document.Path, r.Line);
                    limitReported = true;
                    skippedRequirements++; skippedScenarios += r.Scenarios.Count;
                    continue;
                }
                var requirement = Requirement(r, d.Scope, capability, d.Document.Path, Add);
                skippedScenarios += r.Scenarios.Count - requirement.Scenarios.Count;
                if (keys.TryGetValue(requirement.Key, out var first))
                {
                    var same = OpenSpecIdentity.Comparable(first.Name) == OpenSpecIdentity.Comparable(r.Name);
                    Add(same ? "DUPLICATE_REQUIREMENT" : "AMBIGUOUS_REQUIREMENT_KEY", OpenSpecDiagnosticSeverity.Error,
                        same ? $"Kravet «{r.Name}» er definert to ganger i samme capability (første på linje {first.Line}). Den andre definisjonen er ikke tatt med."
                             : $"Kravene «{first.Name}» (linje {first.Line}) og «{r.Name}» gir samme nøkkel. Det andre er ikke tatt med (ingen sammenslåing).",
                        d.Document.Path, r.Line);
                    skippedRequirements++; skippedScenarios += requirement.Scenarios.Count;
                    continue;
                }
                keys[requirement.Key] = (r.Name, r.Line);
                requirements.Add(requirement);
                requirementCount++;
                scenarioCount += requirement.Scenarios.Count;
                if (!requirement.HasNormativeKeyword)
                    Add("NO_NORMATIVE_KEYWORD", OpenSpecDiagnosticSeverity.Info, $"Kravet «{r.Name}» har ingen SHALL/MUST-formulering.", d.Document.Path, r.Line);
                if (requirement.Scenarios.Count == 0)
                    Add("REQUIREMENT_WITHOUT_SCENARIO", OpenSpecDiagnosticSeverity.Info, $"Kravet «{r.Name}» har ingen scenarioer (vises som kravkandidat uten steg).", d.Document.Path, r.Line);
            }
            if (skippedRequirements > 0 || skippedScenarios > 0)
                exclusions.Add(new(d.Document.Path, skippedRequirements, skippedScenarios, "Duplikat, tvetydig nøkkel eller grense nådd (se diagnostikk)."));
            if (parsed.RequirementHeadings == 0)
                Add("MISSING_REQUIREMENT_STRUCTURE", OpenSpecDiagnosticSeverity.Warning, "Spesifikasjonen har ingen «### Requirement: …»-overskrifter.", d.Document.Path, null);
            capabilities.Add(new OpenSpecCapability
            {
                Key = scoped, Folder = string.Join('/', d.CapabilityFolders), Path = d.Document.Path, Title = parsed.Title, ContentSha256 = d.Document.Sha256, Requirements = requirements,
            });
        }

        // ── Changes (active and archived) ──
        var baseline = capabilities.SelectMany(c => c.Requirements.Select(r => (Capability: c.Key, Comparable: OpenSpecIdentity.Comparable(r.Name), r.Key)))
            .ToDictionary(x => (x.Capability, x.Comparable), x => x.Key);
        var active = Changes(classified, archived: false, Add, ct);
        var archivedChanges = Changes(classified, archived: true, Add, ct);
        active = Resolve(active, baseline, capabilities.Select(c => c.Key).ToHashSet(StringComparer.Ordinal), Add);
        foreach (var c in active.Where(c => c.Deltas.Count == 0))
            Add("CHANGE_WITHOUT_DELTAS", OpenSpecDiagnosticSeverity.Info, $"Endringen «{c.ChangeId}» har ingen delta-spesifikasjoner (ingen kravpåvirkning å vise).", null, null, c.ChangeId);

        // ── Original Markdown (bounded) ──
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        var omitted = new List<string>();
        long budget = OpenSpecLimits.MaxOriginalMarkdownChars;
        foreach (var d in classified.Documents.Where(d => d.Document.Root is not null).OrderBy(d => d.Document.Path, StringComparer.Ordinal))
        {
            if (d.Content.Length <= budget) { original[d.Document.Path] = d.Content; budget -= d.Content.Length; }
            else omitted.Add(d.Document.Path);
        }
        if (omitted.Count > 0)
            Add("ORIGINAL_MARKDOWN_OMITTED", OpenSpecDiagnosticSeverity.Info, $"Originalteksten til {omitted.Count} dokument(er) er ikke lagret (over lagringsbudsjettet); hash og tolkning er lagret.", null, null);

        var model = new OpenSpecSourceModel
        {
            ProjectKey = projectKey, ApplicationKey = applicationKey, ImportId = importId, ArchiveSha256 = archiveSha256, SnapshotId = snapshotId, CapturedAt = now,
            SchemaName = schemas.Select(s => s.Schema).FirstOrDefault(s => s is not null),
            SchemaState = unknown.Count > 0 ? "Unknown" : "BuiltIn",
            Roots = classified.Roots, Documents = [.. classified.Documents.Select(d => d.Document)],
            Capabilities = capabilities, ActiveChanges = active, ArchivedChanges = archivedChanges, Diagnostics = diagnostics, Exclusions = exclusions,
            OriginalMarkdown = original, OriginalMarkdownOmitted = omitted,
        };
        return Output(model);
    }

    private static OpenSpecRequirement Requirement(OpenSpecMarkdownParser.ParsedRequirement r, string? scope, string capability, string path,
        Action<string, OpenSpecDiagnosticSeverity, string, string?, int?, string?> add)
    {
        var key = OpenSpecIdentity.RequirementKey(scope, capability, r.Name);
        var statement = r.Statement;
        var scenarios = new List<OpenSpecScenario>();
        var seen = new Dictionary<string, (string Name, int Line)>(StringComparer.Ordinal);
        foreach (var s in r.Scenarios)
        {
            var scenarioKey = OpenSpecIdentity.ScenarioKey(key, s.Name);
            if (seen.TryGetValue(scenarioKey, out var first))
            {
                add("DUPLICATE_SCENARIO", OpenSpecDiagnosticSeverity.Error,
                    $"Scenarioet «{s.Name}» under kravet «{r.Name}» gir samme nøkkel som «{first.Name}» (linje {first.Line}). Det andre er ikke tatt med.", path, s.Line, null);
                continue;
            }
            seen[scenarioKey] = (s.Name, s.Line);
            if (s.Steps.Count == 0)
                add("SCENARIO_WITHOUT_STEPS", OpenSpecDiagnosticSeverity.Warning, $"Scenarioet «{s.Name}» har ingen GIVEN/WHEN/THEN-steg. Ingen steg er laget.", path, s.Line, null);
            scenarios.Add(new OpenSpecScenario
            {
                Key = scenarioKey, Name = s.Name, Line = s.Line, Steps = [.. s.Steps],
                Given = Join(s.Given), When = Join(s.When), Then = Join(s.Then),
                TextSha256 = OpenSpecIdentity.Sha($"{s.Name}\n{string.Join("\n", s.Steps)}"),
            });
        }
        return new OpenSpecRequirement
        {
            Key = key, Capability = capability, Name = r.Name, Statement = statement, HasNormativeKeyword = OpenSpecMarkdownParser.HasNormativeKeyword(statement),
            Scenarios = scenarios, Path = path, Line = r.Line, EndLine = r.EndLine,
            TextSha256 = OpenSpecIdentity.Sha($"{r.Name}\n{statement}\n" + string.Join("\n", scenarios.Select(s => s.TextSha256))),
        };
    }

    private static string? Join(List<string> parts) => parts.Count == 0 ? null : string.Join("; ", parts);

    private static List<OpenSpecChange> Changes(OpenSpecSourceDiscovery.Result classified, bool archived,
        Action<string, OpenSpecDiagnosticSeverity, string, string?, int?, string?> add, CancellationToken ct)
    {
        var deltaKind = archived ? OpenSpecDocumentKind.ArchivedDeltaSpecification : OpenSpecDocumentKind.ActiveDeltaSpecification;
        var docKind = archived ? OpenSpecDocumentKind.ArchivedChangeDocument : OpenSpecDocumentKind.ActiveChangeDocument;
        var result = new List<OpenSpecChange>();
        foreach (var group in classified.Documents.Where(d => (d.Document.Kind == deltaKind || d.Document.Kind == docKind) && d.Document.ChangeId is not null)
                     .GroupBy(d => (d.Scope ?? "", d.Document.ChangeId!)).OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var (scope, changeId) = group.Key;
            var proposal = group.FirstOrDefault(d => System.IO.Path.GetFileName(d.Document.Path).Equals("proposal.md", StringComparison.OrdinalIgnoreCase));
            var title = proposal is null ? null : OpenSpecMarkdownParser.Parse(proposal.Content, delta: false, ct).Title;
            if (proposal is null && !archived)
                add("CHANGE_WITHOUT_PROPOSAL", OpenSpecDiagnosticSeverity.Info, $"Endringen «{changeId}» har ingen proposal.md.", null, null, changeId);
            var deltas = new List<OpenSpecDelta>();
            foreach (var d in group.Where(d => d.Document.Kind == deltaKind).OrderBy(d => d.Document.Path, StringComparer.Ordinal))
            {
                var capability = OpenSpecIdentity.ScopePrefix(scope) + d.Document.Capability;
                var parsed = OpenSpecMarkdownParser.Parse(d.Content, delta: true, ct);
                foreach (var x in parsed.Diagnostics) add(x.Code, x.Severity, x.Message, d.Document.Path, x.Line, changeId);
                if (parsed.Requirements.Count == 0 && parsed.Renames.Count == 0)
                    add("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Warning, "Delta-spesifikasjonen har ingen ADDED/MODIFIED/REMOVED/RENAMED-operasjoner.", d.Document.Path, null, changeId);
                foreach (var r in parsed.Requirements)
                {
                    var content = r.Operation is OpenSpecDeltaOperation.Added or OpenSpecDeltaOperation.Modified
                        ? Requirement(r, scope, d.Document.Capability!, d.Document.Path, (c, s, m, p, l, _) => add(c, s, m, p, l, changeId)) : null;
                    deltas.Add(new OpenSpecDelta
                    {
                        Operation = r.Operation!.Value, Capability = capability, RequirementName = r.Name, Content = content,
                        Reason = r.Reason.Count == 0 ? null : string.Join(' ', r.Reason), Path = d.Document.Path, Line = r.Line,
                        Resolution = archived ? "History" : "",
                    });
                }
                foreach (var rename in parsed.Renames)
                    deltas.Add(new OpenSpecDelta
                    {
                        Operation = OpenSpecDeltaOperation.Renamed, Capability = capability, RequirementName = rename.From, RenamedTo = rename.To,
                        Path = d.Document.Path, Line = rename.Line, Resolution = archived ? "History" : "",
                    });
            }
            result.Add(new OpenSpecChange
            {
                Key = OpenSpecIdentity.ChangeKey(scope, changeId, archived), ChangeId = changeId,
                State = archived ? OpenSpecLifecycleState.ArchivedChange : OpenSpecLifecycleState.ProposedChange, Title = title,
                Documents = [.. group.Select(d => d.Document.Path).Order(StringComparer.Ordinal)], Deltas = deltas,
            });
        }
        return result;
    }

    /// <summary>Links each active delta to the current baseline where deterministic (same capability, same normalized requirement name) and flags
    /// unresolved targets and conflicts between active changes. Never changes the baseline.</summary>
    private static List<OpenSpecChange> Resolve(List<OpenSpecChange> changes, Dictionary<(string, string), string> baseline, HashSet<string> capabilities,
        Action<string, OpenSpecDiagnosticSeverity, string, string?, int?, string?> add)
    {
        var resolved = changes.Select(c => c with
        {
            Deltas = [.. c.Deltas.Select(d =>
            {
                var target = (d.Capability, OpenSpecIdentity.Comparable(d.RequirementName));
                var exists = baseline.TryGetValue(target, out var key);
                switch (d.Operation)
                {
                    case OpenSpecDeltaOperation.Added:
                        if (!exists) return d with { Resolution = "NewRequirement" };
                        add("ADDED_TARGET_EXISTS", OpenSpecDiagnosticSeverity.Error, $"ADDED «{d.RequirementName}»: kravet finnes allerede i gjeldende grunnlag ({d.Capability}).", d.Path, d.Line, c.ChangeId);
                        return d with { Resolution = "TargetExists", BaselineRequirementKey = key };
                    case OpenSpecDeltaOperation.Renamed:
                        var toExists = d.RenamedTo is { } to && baseline.ContainsKey((d.Capability, OpenSpecIdentity.Comparable(to)));
                        if (!exists)
                        {
                            add("UNRESOLVED_DELTA_TARGET", OpenSpecDiagnosticSeverity.Error, $"RENAMED «{d.RequirementName}» → «{d.RenamedTo}»: kravet finnes ikke i gjeldende grunnlag ({d.Capability}).", d.Path, d.Line, c.ChangeId);
                            return d with { Resolution = "Unresolved" };
                        }
                        if (toExists)
                        {
                            add("ADDED_TARGET_EXISTS", OpenSpecDiagnosticSeverity.Error, $"RENAMED til «{d.RenamedTo}»: et krav med det navnet finnes allerede ({d.Capability}).", d.Path, d.Line, c.ChangeId);
                            return d with { Resolution = "TargetExists", BaselineRequirementKey = key };
                        }
                        return d with { Resolution = "Resolved", BaselineRequirementKey = key };
                    default:
                        if (exists) return d with { Resolution = "Resolved", BaselineRequirementKey = key };
                        add("UNRESOLVED_DELTA_TARGET", OpenSpecDiagnosticSeverity.Error,
                            $"{d.Operation.ToString().ToUpperInvariant()} «{d.RequirementName}»: kravet finnes ikke i gjeldende grunnlag ({(capabilities.Contains(d.Capability) ? d.Capability : d.Capability + " – capability finnes ikke")}).",
                            d.Path, d.Line, c.ChangeId);
                        return d with { Resolution = "Unresolved" };
                }
            })],
        }).ToList();

        // Two active changes touching the same requirement (by name, or the same rename target): a conflict, reported for every operation involved.
        var touches = resolved.SelectMany(c => c.Deltas.SelectMany(d => Targets(d).Select(t => (Target: t, c.ChangeId, Delta: d)))).ToList();
        var conflicts = touches.GroupBy(t => t.Target).Where(g => g.Select(x => x.ChangeId).Distinct(StringComparer.Ordinal).Count() > 1).ToList();
        var conflicted = new HashSet<OpenSpecDelta>(ReferenceEqualityComparer.Instance);
        foreach (var g in conflicts)
        {
            var ids = g.Select(x => x.ChangeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            add("CONFLICTING_ACTIVE_CHANGES", OpenSpecDiagnosticSeverity.Error,
                $"Aktive endringer {string.Join(", ", ids)} endrer samme krav («{g.First().Delta.RequirementName}», {g.Key.Item1}). Konflikten er ikke løst; ingen endring er valgt.", null, null, string.Join(",", ids));
            foreach (var x in g) conflicted.Add(x.Delta);
        }
        // Within one change, two operations on the same requirement are malformed.
        foreach (var c in resolved)
            foreach (var g in c.Deltas.SelectMany(d => Targets(d).Take(1).Select(t => (t, d))).GroupBy(x => x.t).Where(g => g.Count() > 1))
                add("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"Endringen «{c.ChangeId}» har flere operasjoner på kravet «{g.First().d.RequirementName}».", g.First().d.Path, g.First().d.Line, c.ChangeId);
        return [.. resolved.Select(c => c with { Deltas = [.. c.Deltas.Select(d => conflicted.Contains(d) ? d with { Resolution = "Conflict" } : d)] })];

        static IEnumerable<(string, string)> Targets(OpenSpecDelta d)
        {
            yield return (d.Capability, OpenSpecIdentity.Comparable(d.RequirementName));
            if (d.RenamedTo is { } to) yield return (d.Capability, OpenSpecIdentity.Comparable(to));
        }
    }

    /// <summary>The catalog and stored evidence of the CURRENT baseline only (no proposal, no archive), and the route fingerprint.</summary>
    public static Built Output(OpenSpecSourceModel model)
    {
        var refs = model.Capabilities.Select(c => new SpecificationDocumentRef(SpecificationCatalogRules.NormalizePath(c.Path), c.ContentSha256)).ToList();
        var entries = new List<SpecificationRequirementEntry>();
        var evidence = new List<StoredSpecificationEvidence>();
        foreach (var c in model.Capabilities)
        {
            var path = SpecificationCatalogRules.NormalizePath(c.Path);
            foreach (var r in c.Requirements)
            {
                var title = Text(r);
                entries.Add(new(r.Key, path, title.Length > 500 ? title[..500] : title, OpenSpecIdentity.Sha(title)));
            }
            evidence.Add(new StoredSpecificationEvidence(path, c.ContentSha256, new CoverageWorkspaceEvidence
            {
                ArtifactScope = null,
                SpecificationArtifacts = [new CoverageArtifactRef(path, path, null, c.ContentSha256)],
                SpecificationAvailable = true,
                Requirements = [.. c.Requirements.Select(r => new CoverageRequirement(r.Key, Text(r), null, OpenSpecIdentity.SourceIdentityKey(r.Key)))],
                AcceptanceScenarios = [.. c.Requirements.SelectMany(r => r.Scenarios.Select(s => new QaTestCandidate
                {
                    Id = s.Key, Title = s.Name, Source = $"OpenSpec-scenario ({c.Folder} › {r.Name})", Ownership = TestOwnership.QaManual,
                    RequirementIds = [r.Key], Given = s.Given, When = s.When, Then = s.Then,
                    // No user story: OpenSpec scenarios stand under their requirement, and none is invented.
                    StoryKey = null, NumberInStory = null,
                    SourceIdentityKey = OpenSpecIdentity.SourceIdentityKey(s.Key),
                }))],
                Stories = [], Clarifications = [],
            }));
        }
        var inventory = string.Join("\n", model.Documents.Where(d => d.Root is not null).OrderBy(d => d.Path, StringComparer.Ordinal).Select(d => $"{d.Path}\u001f{d.Kind}\u001f{d.Sha256}"));
        var fingerprint = SpecificationFormatMarker.OpenSpecPrefix + OpenSpecIdentity.Sha($"{OpenSpecIdentity.Version}\n{model.SchemaState}\n{inventory}\n#\n"
            + string.Join("\n", entries.Select(e => $"{e.Path}\u001f{e.RequirementId}\u001f{e.TextSha256}")))[..60];
        return new(model, refs, entries, evidence, fingerprint);
    }

    /// <summary>The requirement's wording as the catalog and evidence carry it: «Name: statement» (or the name alone when the source states no text).</summary>
    public static string Text(OpenSpecRequirement r) => r.Statement.Length == 0 ? r.Name : $"{r.Name}: {r.Statement}";
}
