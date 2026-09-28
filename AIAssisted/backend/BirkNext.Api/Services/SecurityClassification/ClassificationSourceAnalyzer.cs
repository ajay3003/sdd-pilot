using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.Scim;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SecurityClassification;

/// <summary>
/// Security-classification evidence from uploaded source: syntax-only (Roslyn parse, nothing compiled or executed), every fact with file:line.
/// The classification levels and their meaning are READ from the source's reference data, never assumed. The production CDC path is followed
/// as written: which method builds the CdcEvent, what value the guard input gets, whether the mapper reads the same field. A guard that
/// exists is reported separately from the value it receives; a unit test that constructs a CdcEvent directly is not production-path coverage.
/// </summary>
public static class ClassificationSourceAnalyzer
{
    public const int Version = 1;
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    internal sealed record Code(string Path, CompilationUnitSyntax Root, string Text, string? Project, bool IsTest);

    public static ClassificationSourceEvidence Analyze(string environmentId, IReadOnlyList<SourceArchive> archives, ScimSourceSet files, DateTimeOffset now)
    {
        var projects = files.Code.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Dir: Dir(f.Path), Name: System.IO.Path.GetFileNameWithoutExtension(f.Path),
                IsTest: System.IO.Path.GetFileNameWithoutExtension(f.Path).Contains("Test", StringComparison.OrdinalIgnoreCase) || f.Content.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal)))
            .ToList();
        var code = files.Code.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(f =>
        {
            var owner = projects.Where(p => f.Path.StartsWith(p.Dir, StringComparison.Ordinal)).OrderByDescending(p => p.Dir.Length).Select(p => ((string, bool)?)(p.Name, p.IsTest)).FirstOrDefault();
            var tree = CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(f.Content), Parse, f.Path);
            return new Code(f.Path, tree.GetCompilationUnitRoot(), f.Content, owner?.Item1, owner?.Item2 ?? f.Path.Contains("/tests/", StringComparison.OrdinalIgnoreCase));
        }).ToList();
        var production = code.Where(c => !c.IsTest).ToList();
        var tests = code.Where(c => c.IsTest).ToList();
        var facts = new List<ClassificationFact>();
        var limitations = new List<string> { "Syntax-only analysis: behaviour of referenced libraries whose source was not uploaded is not assessed." };

        // ── Classification model ─────────────────────────────────────────────────────────────────────────────────────
        var (levels, modelFacts) = Model(production);
        facts.AddRange(modelFacts);
        if (levels.Count == 0)
            return new ClassificationSourceEvidence
            {
                EnvironmentId = environmentId, AnalyzedAt = now, AnalyzerVersion = Version, Archives = archives.ToList(), Detected = false, Facts = facts,
                Limitations = ["No classification reference data (levels with KreverGradertTilgang) was found; nothing else was analyzed."],
            };
        facts.AddRange(Terminology(levels, files.Documents, tests));

        // ── CDC pipeline ─────────────────────────────────────────────────────────────────────────────────────────────
        var cdc = CdcPath(production);
        facts.AddRange(cdc.Facts);

        // ── Person module access paths ───────────────────────────────────────────────────────────────────────────────
        facts.AddRange(AccessPaths(production));
        facts.AddRange(Resolvers(production));
        facts.AddRange(GrantsAndEmergency(production, files.Documents));
        facts.AddRange(PrivacyAndObservability(production));

        var coverage = TestCoverage(tests);
        var pipeline = PipelineStages(cdc, levels);
        var proposed = cdc.Deserializer is { } d ? Proposed(d.Type, d.Method, cdc.GuardType ?? "SecurityClassificationGuard", cdc.EventType ?? "CdcEvent", cdc.PayloadField ?? "Sikkerhetsnivå") : [];
        if (cdc.Deserializer is null) limitations.Add("No production method that builds the CDC event was found; the CDC propagation path is not assessed.");

        return new ClassificationSourceEvidence
        {
            EnvironmentId = environmentId, AnalyzedAt = now, AnalyzerVersion = Version, Archives = archives.ToList(), Detected = true, Levels = levels,
            Facts = facts, Pipeline = pipeline, TestCoverage = coverage, ProposedTests = proposed, Limitations = limitations,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static ClassificationFact Fact(string id, ClassificationArea area, string title, ClassificationState state, string detail, IEnumerable<SourceLocation>? locations = null,
        ClassificationTestType type = ClassificationTestType.Static) =>
        new() { Id = id, Area = area, TestType = type, Title = title, State = state, Detail = detail, Locations = locations?.Distinct().Take(6).ToList() ?? [] };

    private static SourceLocation Loc(Code code, SyntaxNode node) => new(code.Path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static SourceLocation Loc(Code code, int position) => new(code.Path, code.Root.SyntaxTree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)).StartLinePosition.Line + 1);

    private static string Dir(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    private static string Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        _ => "",
    };

    private static string? Literal(ExpressionSyntax? e) => e is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } l ? l.Token.ValueText : null;

    private static IEnumerable<(Code Code, MethodDeclarationSyntax Method)> Methods(IEnumerable<Code> code) =>
        code.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => (c, m)));

    private static string LineText(Code code, int line) => code.Text.Split('\n').ElementAtOrDefault(line - 1)?.Trim() ?? "";

    // ── Model ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static (List<ClassificationLevel> Levels, List<ClassificationFact> Facts) Model(List<Code> production)
    {
        var facts = new List<ClassificationFact>();
        var type = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(t => (Code: c, Type: t)))
            .FirstOrDefault(t => t.Type.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == "KreverGradertTilgang")
                && t.Type.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == "Nivaa"));
        if (type.Type is null)
        {
            facts.Add(Fact("model-reference", ClassificationArea.Model, "Classification reference data", ClassificationState.NotFound, "No type with Nivaa and KreverGradertTilgang was found."));
            return ([], facts);
        }
        var typeName = type.Type.Identifier.Text;
        var levels = new List<ClassificationLevel>();
        foreach (var c in production)
            foreach (var creation in c.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => o.Type.ToString() == typeName && o.Initializer is not null))
            {
                var values = creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>().ToDictionary(a => a.Left.ToString(), a => a.Right);
                if (!values.TryGetValue("Nivaa", out var nivaa) || !int.TryParse(nivaa.ToString(), out var level)) continue;
                string? S(string key) => values.TryGetValue(key, out var v) ? Literal(v) : null;
                levels.Add(new ClassificationLevel
                {
                    Nivaa = level, Verdi = S("Verdi") ?? "", BiRKKode = S("BiRKKode"), ElementsKode = S("ElementsKode"), Beskrivelse = S("Beskrivelse") ?? "",
                    KreverGradertTilgang = values.TryGetValue("KreverGradertTilgang", out var k) && k.ToString() == "true", Location = Loc(c, creation),
                });
            }
        levels = levels.GroupBy(l => l.Nivaa).Select(g => g.First()).OrderBy(l => l.Nivaa).ToList();
        var location = Loc(type.Code, type.Type);
        facts.Add(Fact("model-reference", ClassificationArea.Model, "Classification reference data", levels.Count > 0 ? ClassificationState.SourceVerified : ClassificationState.NotFound,
            levels.Count > 0 ? $"{typeName} seed rows: {string.Join("; ", levels.Select(l => $"{l.Nivaa} = {l.Verdi}{(l.BiRKKode is not null ? $" ({l.BiRKKode}" + (l.ElementsKode is not null ? $" / {l.ElementsKode})" : ")") : "")}{(l.KreverGradertTilgang ? ", KreverGradertTilgang" : "")}"))}."
                : $"{typeName} exists, but no seed rows with a numeric Nivaa were found.", levels.Select(l => l.Location).OfType<SourceLocation>().Prepend(location)));
        if (levels.Count == 0) return (levels, facts);
        var graded = levels.Where(l => l.KreverGradertTilgang).Select(l => l.Nivaa).ToList();
        facts.Add(Fact("model-graded-levels", ClassificationArea.Model, "Levels requiring graded access", graded.SequenceEqual(levels.Where(l => l.Nivaa >= 2).Select(l => l.Nivaa)) ? ClassificationState.SourceVerified : ClassificationState.Warning,
            $"KreverGradertTilgang = true for level(s) {string.Join(", ", graded)}; false for {string.Join(", ", levels.Where(l => !l.KreverGradertTilgang).Select(l => l.Nivaa))}. Levels with graded access use the same flag — the source has no separate, higher permission for one of them.",
            levels.Select(l => l.Location).OfType<SourceLocation>()));
        var unique = production.Select(c => (Code: c, Match: Regex.Match(c.Text, @"HasIndex\(\w+\s*=>\s*\w+\.Nivaa\)\s*\.IsUnique\(\)"))).FirstOrDefault(x => x.Match.Success);
        facts.Add(Fact("model-unique", ClassificationArea.Model, "Numeric levels are unique", unique.Code is not null && levels.Select(l => l.Nivaa).Distinct().Count() == levels.Count ? ClassificationState.SourceVerified : ClassificationState.Warning,
            unique.Code is not null ? "A unique index on Nivaa enforces one row per level; comparisons use the numeric Nivaa." : "No unique index on Nivaa was found.",
            unique.Code is null ? null : [Loc(unique.Code, unique.Match.Index)]));
        if (type.Type.GetLeadingTrivia().ToString() is var doc && doc.Contains("NEVER modify", StringComparison.OrdinalIgnoreCase))
            facts.Add(Fact("model-immutable", ClassificationArea.Model, "Seeded levels documented as immutable", ClassificationState.SourceVerified, "The reference type documents that seeded Nivaa values must never change.", [location]));
        var threshold = production.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "KreverGradertTilgang").Select(m => (Code: c, Method: m))).FirstOrDefault();
        if (threshold.Method is not null)
        {
            var rule = Regex.Match(threshold.Method.ToString(), @"Nivaa\s*(>=|>)\s*(\d)");
            facts.Add(Fact("model-rule-consistency", ClassificationArea.Model, "Graded-access rule vs reference flag", ClassificationState.Warning,
                $"{(threshold.Method.Parent as TypeDeclarationSyntax)?.Identifier.Text}.KreverGradertTilgang computes it from Nivaa {rule.Value} while the reference data carries a KreverGradertTilgang flag; access paths use the flag. They agree for the seeded rows, but they are two sources of truth.",
                [Loc(threshold.Code, threshold.Method)]));
        }
        return (levels, facts);
    }

    private static IEnumerable<ClassificationFact> Terminology(List<ClassificationLevel> levels, List<SourceFile> documents, List<Code> tests)
    {
        var code = levels.Where(l => l.BiRKKode is not null).ToDictionary(l => l.Nivaa, l => Regex.Replace(l.BiRKKode!, @"\s+", " "));
        var conflicts = new List<(string Where, SourceLocation Location)>();
        foreach (var doc in documents)
            foreach (Match m in Regex.Matches(doc.Content, @"(?:level|nivå|nivaa|security level)\s*(\d)\s*\((Kode\s*[67])\)", RegexOptions.IgnoreCase))
                if (code.TryGetValue(int.Parse(m.Groups[1].Value), out var expected) && !string.Equals(Regex.Replace(m.Groups[2].Value, @"\s+", " "), expected, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add(($"\"{m.Value}\"", new SourceLocation(doc.Path, ScimSourceAnalyzer.Line(doc.Content, m.Value))));
        foreach (var t in tests)
            foreach (var method in t.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var named = Regex.Match(method.Identifier.Text, @"Kode([67])");
                var level = Regex.Match(method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? "", @"level:\s*(\d)");
                if (named.Success && level.Success && code.TryGetValue(int.Parse(level.Groups[1].Value), out var expected) && !expected.EndsWith(named.Groups[1].Value, StringComparison.Ordinal))
                    conflicts.Add(($"test {method.Identifier.Text} uses level {level.Groups[1].Value}", Loc(t, method)));
            }
        if (conflicts.Count > 0)
            yield return Fact("model-terminology", ClassificationArea.Model, "Kode 6/7 naming conflicts with the reference data", ClassificationState.Warning,
                $"The reference data defines {string.Join(" and ", code.Select(c => $"level {c.Key} = {c.Value}"))}, but {conflicts.Count} place(s) state the opposite: {string.Join("; ", conflicts.Take(4).Select(c => c.Where))}. The numeric level decides behaviour; the text can mislead reviewers.",
                conflicts.Select(c => c.Location));
    }

    // ── CDC path ────────────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record CdcResult(List<ClassificationFact> Facts, (string Type, string Method)? Deserializer, string? GuardType, string? EventType, string? PayloadField,
        bool ConstantGuardInput, int? ConstantValue, bool GuardBeforeMapping, bool MapperReadsPayload, List<int> GuardedLevels, bool DeleteFromBefore, bool DeletesDiscarded);

    private static CdcResult CdcPath(List<Code> production)
    {
        var facts = new List<ClassificationFact>();
        // The CDC event type: a record with a Sikkerhetsnivaa parameter.
        var eventRecord = production.SelectMany(c => c.Root.DescendantNodes().OfType<RecordDeclarationSyntax>().Select(r => (Code: c, Record: r)))
            .FirstOrDefault(r => r.Record.ParameterList?.Parameters.Any(p => p.Identifier.Text == "Sikkerhetsnivaa" && p.Type?.ToString() is "int" or "int?") == true);
        var eventType = eventRecord.Record?.Identifier.Text;
        var levelParameter = eventRecord.Record?.ParameterList!.Parameters.Select((p, i) => (p.Identifier.Text, i)).First(p => p.Text == "Sikkerhetsnivaa");
        if (eventRecord.Record is not null)
            facts.Add(Fact("cdc-event", ClassificationArea.Pipeline, $"{eventType}.{levelParameter!.Value.Text}", ClassificationState.SourceVerified,
                $"The CDC event carries the classification as {levelParameter.Value.Text} (the guard's input).", [Loc(eventRecord.Code, eventRecord.Record)]));

        // Production construction of the event: a method in production code that returns the event type and constructs it.
        (Code Code, MethodDeclarationSyntax Method, ObjectCreationExpressionSyntax Creation)? builder = null;
        if (eventType is not null)
            foreach (var (c, m) in Methods(production))
                if (m.ReturnType.ToString().TrimEnd('?') == eventType && m.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault(o => o.Type.ToString() == eventType) is { } creation)
                { builder = (c, m, creation); break; }
        bool constant = false, readsPayload = false, deleteFromBefore = false;
        int? constantValue = null;
        string? comment = null;
        (string, string)? deserializer = null;
        if (builder is { } b)
        {
            deserializer = ((b.Method.Parent as TypeDeclarationSyntax)?.Identifier.Text ?? "?", b.Method.Identifier.Text);
            var args = b.Creation.ArgumentList?.Arguments.ToList() ?? [];
            var argument = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == levelParameter?.Text) ?? (levelParameter is { } lp && lp.i < args.Count ? args[lp.i] : null);
            var line = argument is null ? 0 : Loc(b.Code, argument).Line;
            comment = line > 0 && Regex.Match(LineText(b.Code, line), @"//\s*(.+)$") is { Success: true } cm ? cm.Groups[1].Value.Trim() : null;
            if (argument?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal) { constant = true; constantValue = int.Parse(literal.Token.ValueText); }
            readsPayload = !constant && Regex.IsMatch(b.Method.ToString(), @"Sikkerhetsniv");
            deleteFromBefore = Regex.IsMatch(b.Method.ToString(), @"==\s*""d""\s*\?\s*""before""");
            var where = argument is null ? Loc(b.Code, b.Creation) : Loc(b.Code, argument);
            facts.Add(Fact("cdc-deserializer", ClassificationArea.Pipeline, $"Production deserialization ({deserializer.Value.Item1}.{deserializer.Value.Item2})",
                constant ? ClassificationState.IssueDetected : readsPayload ? ClassificationState.SourceVerified : ClassificationState.Warning,
                constant ? $"The production path builds {eventType} with {levelParameter?.Text}: {constantValue}{(comment is not null ? $" (\"// {comment}\")" : "")} — a constant, whatever the payload says. Security classification is present in the payload/model, but the production guard input is populated with a constant/default value."
                    : readsPayload ? $"{levelParameter?.Text} is read from the event payload." : $"{levelParameter?.Text} is set from an expression that does not visibly read the classification field: {argument?.Expression}.",
                [where]));
            facts.Add(Fact("cdc-envelope", ClassificationArea.Pipeline, "Debezium envelope handling", deleteFromBefore ? ClassificationState.SourceVerified : ClassificationState.Warning,
                deleteFromBefore ? "payload.op selects the record: \"before\" for deletes (op d), \"after\" for create/update/read." : "The before/after selection for deletes was not found.", [Loc(b.Code, b.Method)]));
        }
        else facts.Add(Fact("cdc-deserializer", ClassificationArea.Pipeline, "Production deserialization", ClassificationState.NotFound, $"No production method constructs {eventType ?? "the CDC event"}."));

        // Guard.
        var guard = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(t => (Code: c, Type: t)))
            .FirstOrDefault(t => eventType is not null && t.Type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.ParameterList.Parameters.Any(p => p.Type?.ToString() == eventType) && m.ToString().Contains(levelParameter?.Text ?? "Sikkerhetsnivaa", StringComparison.Ordinal)));
        var guarded = new List<int>();
        if (guard.Type is not null)
        {
            var text = guard.Type.ToString();
            var pattern = Regex.Match(text, @"is\s+not\s+\(([\d\s or]+)\)");
            if (pattern.Success) guarded = Regex.Matches(pattern.Groups[1].Value, @"\d").Select(m => int.Parse(m.Value)).ToList();
            else if (Regex.Match(text, @"Sikkerhetsnivaa\s*>=\s*(\d)") is { Success: true } ge) guarded = Enumerable.Range(int.Parse(ge.Groups[1].Value), 4 - int.Parse(ge.Groups[1].Value)).ToList();
            var metric = Regex.Match(text, @"CreateCounter<\w+>\(""([^""]+)""\)");
            facts.Add(Fact("guard-logic", ClassificationArea.Guard, guard.Type.Identifier.Text, guarded.Count > 0 ? ClassificationState.SourceVerified : ClassificationState.Warning,
                guarded.Count > 0 ? $"Rejects an event whose {levelParameter?.Text} is {string.Join(" or ", guarded)}{(text.Contains("LogCritical", StringComparison.Ordinal) ? "; logs Critical with table name and time only" : "")}{(text.Contains("Alert", StringComparison.Ordinal) ? "; raises an alert" : "")}{(metric.Success ? $"; increments {metric.Groups[1].Value}" : "")}. The guard logic is correct for its input — it only protects when the input carries the real level."
                    : "The guard's rejection rule was not recognised.", [Loc(guard.Code, guard.Type)]));
            if (metric.Success)
                facts.Add(Fact("guard-metric", ClassificationArea.Observability, $"Metric {metric.Groups[1].Value}", ClassificationState.Configured,
                    $"Defined in source. Runtime value not read (no telemetry source): Not available — never reported as 0 rejections. With a constant guard input the counter cannot increase.", [Loc(guard.Code, guard.Type)], ClassificationTestType.NonFunctional));
        }
        else facts.Add(Fact("guard-logic", ClassificationArea.Guard, "Classification guard", ClassificationState.NotFound, "No guard evaluating the CDC event's classification was found."));

        // Router: guard first, deletes after, mapper reading the payload field.
        var route = Methods(production).FirstOrDefault(m => m.Method.ToString().Contains(".Evaluate(", StringComparison.Ordinal) && m.Method.ToString().Contains(".Map(", StringComparison.Ordinal));
        var guardFirst = false;
        var deletesDiscarded = false;
        if (route.Method is not null)
        {
            var text = route.Method.ToString();
            guardFirst = text.IndexOf(".Evaluate(", StringComparison.Ordinal) < text.IndexOf(".Map(", StringComparison.Ordinal);
            deletesDiscarded = Regex.IsMatch(text, @"OperationType\.Delete\)\s*\n?\s*return[^;]*Discarded");
            facts.Add(Fact("cdc-router", ClassificationArea.Pipeline, "Guard runs before mapping", guardFirst ? ClassificationState.SourceVerified : ClassificationState.IssueDetected,
                (guardFirst ? "The router evaluates the guard before any mapper runs." : "A mapper runs before the guard.") + (deletesDiscarded ? " Delete events are discarded after the guard, so a delete's classification is never delivered." : ""),
                [Loc(route.Code, route.Method)]));
        }
        var mapper = production.Select(c => (Code: c, Match: Regex.Match(c.Text, @"\(\s*\w+\.Payload\s*,\s*""(Sikkerhetsniv[^""]*)""\s*\)"))).FirstOrDefault(x => x.Match.Success);
        var payloadField = mapper.Code is null ? null : mapper.Match.Groups[1].Value;
        if (mapper.Code is not null)
        {
            var at = Loc(mapper.Code, mapper.Match.Index);
            facts.Add(Fact("cdc-field-name", ClassificationArea.Pipeline, $"Payload field \"{payloadField}\"", payloadField!.Contains('å') ? ClassificationState.SourceVerified : ClassificationState.Warning,
                $"The mapper reads \"{payloadField}\"{(payloadField.Contains('å') ? " (Unicode å, U+00E5)" : " — the BiRK column is spelled with å; verify the transliteration")}; the event property is spelled {levelParameter?.Text ?? "Sikkerhetsnivaa"}.", [at]));
            facts.Add(Fact("cdc-mapper-guard-consistency", ClassificationArea.Pipeline, "Mapper and guard read the classification from different sources",
                constant ? ClassificationState.IssueDetected : ClassificationState.SourceVerified,
                constant ? $"The mapper reads \"{payloadField}\" from the payload, while the guard reads {eventType}.{levelParameter?.Text}, which the production path sets to {constantValue}. For a level-2/3 row the guard allows the event and the mapper then forwards the real level — the two values diverge."
                    : "Guard and mapper derive the classification from the same payload.", [at]));
            var mapMethod = mapper.Code.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text.Contains("Sikkerhetsniv", StringComparison.Ordinal));
            var unknown = mapMethod is null ? Match.Empty : Regex.Match(mapMethod.ToString(), @"_\s*=>\s*new Guid\(""[^""]+""\),?\s*//\s*default\s*(\w+)");
            if (unknown.Success)
                facts.Add(Fact("cdc-unknown-level", ClassificationArea.Pipeline, "Missing or unknown level", ClassificationState.NeedsDecision,
                    $"A missing, null or unknown value (null, negative, 4+, non-numeric) is mapped to \"{unknown.Groups[1].Value}\" (level 0) with a warning log. No authoritative rule for unknown levels was found (reject, quarantine or default) — a decision is needed; BirkNext does not assume fail-closed.",
                    [Loc(mapper.Code, mapMethod!)]));
        }
        return new CdcResult(facts, deserializer, guard.Type?.Identifier.Text, eventType, payloadField, constant, constantValue, guardFirst, mapper.Code is not null, guarded, deleteFromBefore, deletesDiscarded);
    }

    private static List<ClassificationStageEvidence> PipelineStages(CdcResult cdc, List<ClassificationLevel> levels) =>
    [
        new() { Stage = ClassificationPipelineStage.BiRK, Title = ClassificationLabels.Stage(ClassificationPipelineStage.BiRK), Source = ClassificationState.NotApplicable,
            SourceDetail = "BiRK source filtering of Kode 6/7 (documented as the primary layer) is outside the analyzed source.", RuntimeDetail = "BiRK data is not read." },
        new() { Stage = ClassificationPipelineStage.Debezium, Title = "Debezium", Source = cdc.MapperReadsPayload ? ClassificationState.SourceVerified : ClassificationState.NotTested,
            SourceDetail = cdc.MapperReadsPayload ? $"The Barn payload carries \"{cdc.PayloadField}\" (read by the mapper)." : "The payload field was not found.", RuntimeDetail = "No CDC event is consumed by BirkNext." },
        new() { Stage = ClassificationPipelineStage.EventHub, Title = "Event Hub", Source = ClassificationState.NotApplicable, SourceDetail = "Transport only; see Integrations for Event Hub evidence.", RuntimeDetail = "No event is read." },
        new() { Stage = ClassificationPipelineStage.PersonAdapterDeserialization, Title = ClassificationLabels.Stage(ClassificationPipelineStage.PersonAdapterDeserialization),
            Source = cdc.Deserializer is null ? ClassificationState.NotFound : cdc.ConstantGuardInput ? ClassificationState.IssueDetected : ClassificationState.SourceVerified,
            SourceDetail = cdc.Deserializer is { } d ? $"{d.Type}.{d.Method}: {(cdc.ConstantGuardInput ? $"sets the classification to the constant {cdc.ConstantValue}" : "reads the classification from the payload")}." : "Not found." },
        new() { Stage = ClassificationPipelineStage.GuardInput, Title = ClassificationLabels.Stage(ClassificationPipelineStage.GuardInput),
            Source = cdc.ConstantGuardInput ? ClassificationState.IssueDetected : cdc.Deserializer is null ? ClassificationState.NotTested : ClassificationState.SourceVerified,
            SourceDetail = cdc.ConstantGuardInput ? $"Expected: the row's level (2 or 3 for graded children). Source path: {cdc.ConstantValue} for every event." : "The row's level." },
        new() { Stage = ClassificationPipelineStage.Guard, Title = ClassificationLabels.Stage(ClassificationPipelineStage.Guard),
            Source = cdc.GuardedLevels.Count > 0 ? ClassificationState.SourceVerified : ClassificationState.NotFound,
            SourceDetail = cdc.GuardedLevels.Count > 0 ? $"Implemented: rejects level {string.Join("/", cdc.GuardedLevels)}{(cdc.GuardBeforeMapping ? ", before mapping" : "")}. {(cdc.ConstantGuardInput ? "Unreachable for real events while its input is constant." : "")}".Trim() : "Not found." },
        new() { Stage = ClassificationPipelineStage.PersonService, Title = ClassificationLabels.Stage(ClassificationPipelineStage.PersonService), Source = ClassificationState.Partial,
            SourceDetail = $"The mapper forwards the payload level as the child's classification ({string.Join(", ", levels.Select(l => $"{l.Nivaa}={l.Verdi}"))}); the Person module applies its own graded-access rules (see authorization)." },
    ];

    // ── Person module ───────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ClassificationFact> AccessPaths(List<Code> production)
    {
        var methods = Methods(production).ToList();
        // Profile service: anti-disclosure, child-specific grant, fail-closed read log.
        var profile = methods.FirstOrDefault(m => m.Method.ToString() is var t && t.Contains("PersonNotFoundException", StringComparison.Ordinal) && t.Contains("SeGradertBarn", StringComparison.Ordinal) && t.Contains("Leselogg", StringComparison.OrdinalIgnoreCase));
        if (profile.Method is not null)
        {
            var text = profile.Method.ToString();
            var both = Regex.Matches(text, @"throw new PersonNotFoundException").Count >= 2;
            var childSpecific = Regex.IsMatch(text, @"EvaluerOperasjon\([^)]*""Person:SeGradertBarn""\s*,\s*\w+");
            var publish = text.IndexOf("PublishAsync", StringComparison.Ordinal);
            var returns = text.LastIndexOf("return ", StringComparison.Ordinal);
            var failClosed = publish > 0 && publish < returns && !profile.Method.DescendantNodes().OfType<TryStatementSyntax>().Any(t => t.Block.ToString().Contains("PublishAsync", StringComparison.Ordinal));
            var name = $"{(profile.Method.Parent as TypeDeclarationSyntax)?.Identifier.Text}.{profile.Method.Identifier.Text}";
            yield return Fact("access-profile-antidisclosure", ClassificationArea.DirectAccess, "Unauthorized classified child is indistinguishable from nonexistent", both ? ClassificationState.SourceVerified : ClassificationState.Warning,
                both ? $"{name} throws PersonNotFoundException both when the child does not exist and when it requires graded access and the caller lacks Person:SeGradertBarn (404-not-403 by design). Not-found is the expected result for an unauthorized caller." : $"{name}: the not-found and unauthorized paths differ.",
                [Loc(profile.Code, profile.Method)], ClassificationTestType.Negative);
            yield return Fact("access-child-specific", ClassificationArea.ChildAccess, "Graded access is evaluated per child", childSpecific ? ClassificationState.SourceVerified : ClassificationState.Warning,
                childSpecific ? $"{name} evaluates Person:SeGradertBarn with the child's id (not only a global role), fail-closed through the authorization client." : "The graded-access evaluation does not pass the child id.", [Loc(profile.Code, profile.Method)]);
            yield return Fact("readlog-fail-closed", ClassificationArea.ReadLogging, "Read logging is fail-closed", failClosed ? ClassificationState.SourceVerified : ClassificationState.IssueDetected,
                failClosed ? $"{name} awaits the LeseloggHendelse publish before returning data and does not catch its failure: no profile is returned without a published read-log entry." : "The read-log publish is not on the path before data is returned, or its failure is caught.",
                [Loc(profile.Code, profile.Method)], ClassificationTestType.NonFunctional);
        }
        // Repository: profile query excludes classified children for everyone.
        var profileQuery = methods.FirstOrDefault(m => m.Method.Identifier.Text.Contains("Profil", StringComparison.Ordinal) && Regex.IsMatch(m.Method.ToString(), @"Where\([^)]*!\s*\w+\.SikkerhetsnivaaType\.KreverGradertTilgang"));
        if (profileQuery.Method is not null)
            yield return Fact("access-profile-query-excludes", ClassificationArea.DirectAccess, "Profile query excludes every graded child", ClassificationState.NeedsDecision,
                $"{(profileQuery.Method.Parent as TypeDeclarationSyntax)?.Identifier.Text}.{profileQuery.Method.Identifier.Text} filters out children with KreverGradertTilgang for every caller, so the service's Person:SeGradertBarn check is unreachable and an authorized graded user also gets not-found (also for the revision log, which uses the same query). Unauthorized callers are protected; the authorized positive control cannot pass. This matches the documented phase decision that Kode 6/7 children are not included yet, but contradicts search, which returns granted graded children.",
                [Loc(profileQuery.Code, profileQuery.Method)], ClassificationTestType.Functional);
        // Search: in-query filter before count.
        var search = methods.FirstOrDefault(m => m.Method.ToString() is var t && t.Contains("grantedChildIds", StringComparison.Ordinal) && t.Contains("CountAsync", StringComparison.Ordinal));
        if (search.Method is not null)
        {
            var text = search.Method.ToString();
            var filter = Regex.Match(text, @"Where\([^;]*KreverGradertTilgang\s*\|\|\s*grantedChildIds\.Contains");
            var count = text.IndexOf("CountAsync", StringComparison.Ordinal);
            var ok = filter.Success && filter.Index < count;
            yield return Fact("search-filter", ClassificationArea.Search, "Search returns graded children only with a grant", filter.Success ? ClassificationState.SourceVerified : ClassificationState.IssueDetected,
                filter.Success ? "The repository query keeps a graded child only when its id is in the caller's granted ids — in SQL, never post-filtered." : "No in-query graded-access filter was found in the search query.",
                [Loc(search.Code, search.Method)], ClassificationTestType.Negative);
            yield return Fact("search-count", ClassificationArea.Search, "Total count is computed after the security filter", ok ? ClassificationState.SourceVerified : ClassificationState.IssueDetected,
                ok ? "totaltAntall and pagination (harFlere) come from the already filtered query, so an unauthorized caller cannot infer hidden graded children from the count." : "The count is computed before the security filter — hidden children could be inferred from it.",
                [Loc(search.Code, search.Method)], ClassificationTestType.Negative);
        }
        // Grants for search come from every child-specific role.
        var grants = methods.FirstOrDefault(m => m.Method.Identifier.Text == "HentGradertBarntilganger" && m.Method.Body is not null);
        if (grants.Method is not null && Regex.IsMatch(grants.Method.ToString(), @"\.Barn\.Select\(\w+\s*=>\s*\w+\.BarnId\)"))
            yield return Fact("search-grant-source", ClassificationArea.ChildAccess, "Search treats any child-specific role as graded access", ClassificationState.Warning,
                "The granted ids for search are every child in the caller's child-specific access list (any role or emergency access), while the profile evaluates Person:SeGradertBarn for the child. The two predicates can differ.",
                [Loc(grants.Code, grants.Method)]);
        var client = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(t => (Code: c, Type: t))).FirstOrDefault(t => t.Type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "EvaluerOperasjon" && m.Body is not null) && !t.Type.Identifier.Text.StartsWith("Dev", StringComparison.Ordinal));
        if (client.Type is not null)
        {
            var text = client.Type.ToString();
            var failClosed = text.Contains("?? false", StringComparison.Ordinal) && Regex.IsMatch(text, @"catch \(Exception[^)]*\)\s*\{[^}]*throw new");
            yield return Fact("access-authz-fail-closed", ClassificationArea.ChildAccess, "Authorization client fails closed", failClosed ? ClassificationState.SourceVerified : ClassificationState.Warning,
                failClosed ? $"{client.Type.Identifier.Text}: a missing answer is 'not allowed' and any failure throws — access is never granted by error." : "Fail-closed handling of authorization errors was not recognised.", [Loc(client.Code, client.Type)]);
        }
        var cache = production.FirstOrDefault(c => c.Project?.Contains("Person", StringComparison.Ordinal) == true && Regex.IsMatch(c.Text, @"IMemoryCache|IDistributedCache|AddOutputCache|ResponseCache|\bOutputCache\b"));
        yield return Fact("cache-server", ClassificationArea.Caching, "Server-side response cache", cache is null ? ClassificationState.NotApplicable : ClassificationState.Warning,
            cache is null ? "No response or data cache in the Person module source, so no cached classified response can outlive an access change there. Browser/client caches are not assessed from source." : $"A cache is used in {cache.Path}: review whether classified responses can be served after an access change.",
            cache is null ? null : [new SourceLocation(cache.Path, 1)], ClassificationTestType.NonFunctional);
    }

    private static IEnumerable<ClassificationFact> Resolvers(List<Code> production)
    {
        var resolvers = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(t => t.AttributeLists.ToString().Contains("ExtendObjectType(\"Query\")", StringComparison.Ordinal))
            .SelectMany(t => t.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword))).Select(m => (Code: c, Type: t, Method: m)))).ToList();
        foreach (var (c, t, m) in resolvers)
        {
            var text = m.ToString();
            var operations = Regex.Matches(text, @"KrevOperasjon\(""([^""]+)""").Select(x => x.Groups[1].Value).Distinct().ToList();
            var touchesChild = Regex.IsMatch(text, @"barnRegistreringId|eksternId|BarnRegistrering|repository\.", RegexOptions.IgnoreCase);
            var field = char.ToLowerInvariant(m.Identifier.Text[0]) + m.Identifier.Text[1..].Replace("Async", "");
            if (operations.Count == 0 && touchesChild)
                yield return Fact($"graphql-{field}", ClassificationArea.GraphQL, $"GraphQL {field} has no authorization check", ClassificationState.IssueDetected,
                    $"{t.Identifier.Text}.{m.Identifier.Text} reads child data without any KrevOperasjon call{(Regex.Match(text, @"//\s*(.+)") is { Success: true } cm ? $" (\"// {cm.Groups[1].Value.Trim()}\")" : "")}, and the lookup it calls applies no classification filter. It answers whether a registration with a given id exists — also for a graded child — and returns its registration id. The endpoint has no global authorization policy, so reachability decides who can ask.",
                    [Loc(c, m)], ClassificationTestType.Negative);
            else if (operations.Count > 0 && touchesChild)
                yield return Fact($"graphql-{field}", ClassificationArea.GraphQL, $"GraphQL {field} authorization", ClassificationState.SourceVerified,
                    $"Requires {string.Join(" + ", operations)}{(text.Contains("PersonNotFoundException", StringComparison.Ordinal) && text.Contains("return null", StringComparison.Ordinal) ? "; not-found maps to null (anti-disclosure)" : "")}. Server-side, independent of any UI hiding.",
                    [Loc(c, m)], ClassificationTestType.Negative);
            if (text.Contains("Revisjonslogg", StringComparison.Ordinal) && text.Contains("throw new PersonNotFoundException", StringComparison.Ordinal) && !text.Contains("catch (PersonNotFoundException", StringComparison.Ordinal))
                yield return Fact("audit-log-access", ClassificationArea.AuditAccess, "Revision log: graded check and response shape", ClassificationState.Warning,
                    $"{t.Identifier.Text} requires Person:SeRevisjonslogg and, for a graded child, Person:SeGradertBarn. A nonexistent child returns null, while a denied graded child throws PersonNotFoundException (surfaced as a GraphQL error) — the two responses could differ. Today the profile query it relies on excludes graded children, so both return null.",
                    [Loc(c, m)], ClassificationTestType.Negative);
        }
        var map = production.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\.MapGraphQL\(\)\s*;"));
        if (map is not null && !production.Any(c => c.Text.Contains("FallbackPolicy", StringComparison.Ordinal)))
            yield return Fact("graphql-endpoint-auth", ClassificationArea.GraphQL, "GraphQL endpoint has no global authorization", ClassificationState.Warning,
                "MapGraphQL() is mapped without RequireAuthorization and there is no fallback policy: every resolver must enforce its own checks, so one resolver without a check is reachable by any caller that reaches the endpoint.",
                [new SourceLocation(map.Path, ScimSourceAnalyzer.Line(map.Text, "MapGraphQL"))]);
    }

    private static IEnumerable<ClassificationFact> GrantsAndEmergency(List<Code> production, List<SourceFile> documents)
    {
        var commented = production.Select(c => (Code: c, Match: Regex.Match(c.Text, @"//\s*public\s+async\s+Task<[^>]+>\s+(Tildel\w*Async)"))).FirstOrDefault(x => x.Match.Success);
        var live = Methods(production).FirstOrDefault(m => m.Method.Identifier.Text.StartsWith("Tildel", StringComparison.Ordinal) && m.Method.Identifier.Text.EndsWith("Async", StringComparison.Ordinal) && m.Method.Body is not null && (m.Method.Parent as TypeDeclarationSyntax)?.Identifier.Text.EndsWith("Service", StringComparison.Ordinal) == true);
        yield return Fact("grant-implementation", ClassificationArea.Grants, "Grant graded access", live.Method is not null ? ClassificationState.SourceVerified : commented.Code is not null ? ClassificationState.DocumentedOnly : ClassificationState.NotFound,
            live.Method is not null ? $"{live.Method.Identifier.Text} is implemented." : commented.Code is not null ? $"{commented.Match.Groups[1].Value} is commented out in the service (only its design remains); granting graded access is not available functionality in the analyzed source." : "No grant operation was found.",
            live.Method is not null ? [Loc(live.Code, live.Method)] : commented.Code is not null ? [Loc(commented.Code, commented.Match.Index)] : null);
        var revoke = Methods(production).FirstOrDefault(m => Regex.IsMatch(m.Method.Identifier.Text, @"^(Tilbakekall|Trekk|Fjern|Revoke)\w*Gradert|^\w*Gradert\w*(Tilbakekall|Revoke|Fjern)", RegexOptions.IgnoreCase));
        yield return Fact("revoke-implementation", ClassificationArea.Grants, "Revoke graded access", revoke.Method is not null ? ClassificationState.SourceVerified : ClassificationState.NotFound,
            revoke.Method is not null ? $"{revoke.Method.Identifier.Text} is implemented." : "No revoke operation for graded access was found in the analyzed source.", revoke.Method is null ? null : [Loc(revoke.Code, revoke.Method)]);
        var emergency = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => Regex.IsMatch(t.Identifier.Text, @"Nodtilgang|N[øo]dtilgang|EmergencyAccess", RegexOptions.IgnoreCase)).Select(t => (Code: c, Type: t))).ToList();
        var documented = documents.Any(d => Regex.IsMatch(d.Content, @"nødtilgang|emergency access", RegexOptions.IgnoreCase));
        yield return Fact("emergency-access", ClassificationArea.EmergencyAccess, "Emergency access (nødtilgang)", emergency.Count > 0 ? ClassificationState.SourceVerified : documented ? ClassificationState.DocumentedOnly : ClassificationState.NotFound,
            emergency.Count > 0 ? $"Types in source: {string.Join(", ", emergency.Select(e => e.Type.Identifier.Text).Distinct().Take(6))} ({string.Join(", ", emergency.Select(e => e.Code.Project).Distinct())}). Whether written reason, expiry, notification and after-review are enforced is not assessed here."
                : documented ? "Described in the documentation only; no implementation was found in the analyzed source." : "Not found.",
            emergency.Select(e => Loc(e.Code, e.Type)));
    }

    private static IEnumerable<ClassificationFact> PrivacyAndObservability(List<Code> production)
    {
        var relevant = production.Where(c => c.Project is { } p && (p.Contains("Person", StringComparison.Ordinal))).ToList();
        var templates = relevant.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => Name(i).StartsWith("Log", StringComparison.Ordinal) && i.Expression.ToString().Contains("ogger", StringComparison.Ordinal))
                .Select(i => (Code: c, Node: (SyntaxNode)i, Template: i.ArgumentList.Arguments.Select(a => Literal(a.Expression)).OfType<string>().FirstOrDefault() ?? "")))
            .Concat(relevant.SelectMany(c => c.Root.DescendantNodes().OfType<AttributeSyntax>().Where(a => a.Name.ToString() == "LoggerMessage")
                .Select(a => (Code: c, Node: (SyntaxNode)a, Template: Regex.Match(a.ToString(), @"Message\s*=\s*""([^""]+)""").Groups[1].Value))))
            .ToList();
        var pii = templates.Where(t => Regex.IsMatch(t.Template, @"\{(Navn|Name|Foedselsnummer|Fnr|Personnummer|Adresse|Address)\}", RegexOptions.IgnoreCase)).ToList();
        var classified = templates.Where(t => Regex.IsMatch(t.Template, @"\{(Sikkerhetsniv\w*|Nivaa)\}", RegexOptions.IgnoreCase) && Regex.IsMatch(t.Template, @"\{(BirkId|BarnRegistreringId|PersonFK)\}", RegexOptions.IgnoreCase)).ToList();
        yield return Fact("privacy-logging", ClassificationArea.Privacy, "Logging of personal data and classification",
            pii.Count > 0 ? ClassificationState.IssueDetected : classified.Count > 0 ? ClassificationState.Warning : ClassificationState.SourceVerified,
            (pii.Count > 0 ? $"Templates carry personal data: {string.Join("; ", pii.Select(t => t.Template))}. " : "No log template carries a name, national id or address. ")
            + (classified.Count > 0 ? $"{classified.Count} template(s) tie a child identifier to its classification level (e.g. \"{classified[0].Template}\") — a log reader learns which child is graded." : "")
            + " Runtime log content is not inspected.",
            pii.Concat(classified).Select(t => Loc(t.Code, t.Node)), ClassificationTestType.NonFunctional);
        var telemetry = relevant.FirstOrDefault(c => Regex.IsMatch(c.Text, @"RecordRequestBody|IncludeRequestBody|EnrichWithHttpRequestMessage|SetTag\(""(fnr|navn|foedselsnummer)", RegexOptions.IgnoreCase));
        yield return Fact("privacy-telemetry", ClassificationArea.Privacy, "Telemetry payload capture (source)", telemetry is null ? ClassificationState.SourceVerified : ClassificationState.Warning,
            telemetry is null ? "No request-body capture or personal-data tag is configured in the analyzed telemetry setup. Runtime telemetry content: Not available." : $"Request payloads or personal-data tags are configured in {telemetry.Path}.",
            telemetry is null ? null : [new SourceLocation(telemetry.Path, 1)], ClassificationTestType.NonFunctional);
    }

    // ── Repository tests ────────────────────────────────────────────────────────────────────────────────────────────

    private static List<RepositoryTestCoverage> TestCoverage(List<Code> tests)
    {
        var methods = tests.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "Fact" or "Theory"))
            .Select(m => (Name: $"{(m.Parent as ClassDeclarationSyntax)?.Identifier.Text}.{m.Identifier.Text}", Text: m.ToString(), File: c.Text))).ToList();
        RepositoryTestCoverage Scenario(string scenario, Func<(string Name, string Text, string File), bool> match, Func<(string Name, string Text, string File), bool>? unitOnly = null, string note = "")
        {
            var found = methods.Where(match).ToList();
            var unit = found.Count > 0 && unitOnly is not null && found.All(unitOnly);
            return new RepositoryTestCoverage
            {
                Scenario = scenario, State = found.Count == 0 ? RepositoryTestCoverageState.Missing : unit ? RepositoryTestCoverageState.UnitOnly : RepositoryTestCoverageState.Present,
                Tests = found.Select(f => f.Name).Take(5).ToList(), Note = found.Count == 0 ? "No test in the analyzed repository." : unit ? note : "",
            };
        }
        // A test that never feeds a raw payload (EventBody / BinaryData / Deserialize) exercises a directly constructed event only.
        bool DirectEvent((string Name, string Text, string File) m) => !Regex.IsMatch(m.File, @"EventBody|BinaryData|Deserialize\(");
        return
        [
            Scenario("SecurityClassificationGuard rejects level 2/3", m => m.Name.Contains("Guard", StringComparison.Ordinal) && Regex.IsMatch(m.Name, @"Rejected|TwoOrThree|Kode"), DirectEvent,
                "Guard unit coverage: the tests construct the CDC event directly with the level, so they prove the guard logic — not that the production deserializer delivers the level."),
            Scenario("Router rejects a Kode 6/7 event", m => m.Name.Contains("Kode6Event", StringComparison.Ordinal) || m.Name.Contains("Kode7Event", StringComparison.Ordinal), DirectEvent,
                "Router coverage with a directly constructed event (MakeEvent(level: …)); the production deserializer is not exercised."),
            Scenario("Raw Debezium payload → production deserializer → guard", m => (m.Text.Contains("Deserialize", StringComparison.Ordinal) || m.Text.Contains("EventBody", StringComparison.Ordinal)) && m.Text.Contains("Sikkerhetsniv", StringComparison.Ordinal)),
            Scenario("Mapper maps the payload level", m => m.Name.Contains("Mapper", StringComparison.Ordinal) && m.Name.Contains("Sikkerhetsniv", StringComparison.Ordinal)),
            Scenario("Profile: unauthorized graded child → not found", m => Regex.IsMatch(m.Name, @"HentBarnProfil.*(Kode[67]|Gradert).*(Null|NotFound)")),
            Scenario("Profile: authorized graded user sees the child", m => Regex.IsMatch(m.Name, @"HentBarnProfil.*(MedGrant|WithGrant|Authorized|Autorisert)")),
            Scenario("Search: graded child absent without a grant", m => Regex.IsMatch(m.Name, @"SoekBarn.*(NoGrant|WithNoGrants|OnlyNonClassified|Absent)")),
            Scenario("Search: graded child present with a grant", m => Regex.IsMatch(m.Name, @"SoekBarn.*WithGrant.*(Visible|Present)")),
            Scenario("Search: total count does not reveal hidden children", m => Regex.IsMatch(m.Name, @"(TotaltAntall|TotalCount).*(Hidden|Classified|Gradert|Kode)|(Kode|Gradert).*(TotaltAntall|TotalCount)")),
            Scenario("Read log: no data when publishing fails", m => Regex.IsMatch(m.Name, @"Leselogg|PublishFails|WhenPublishFails")),
            Scenario("Level-change event is emitted (SikkerhetsnivaaEndret)", m => Regex.IsMatch(m.Name, @"Sikkerhetsniv\w*Endret")),
            Scenario("Access changes after a level change (0 → 2, 2 → 0)", m => Regex.IsMatch(m.Name, @"(Endret|Change|Oppgrader|Nedgrader)\w*(Skjul|Hidden|Synlig|Visible|Access|Tilgang)|(Kode[67]|Gradert)\w*Til\w*(Ingen|Nivaa0)")),
            Scenario("Unknown / invalid level", m => Regex.IsMatch(m.Name, @"Unknown.*(Sikkerhet|Nivaa|Level)|Invalid(Level|Nivaa)")),
        ];
    }

    private static List<ProposedRegressionTest> Proposed(string workerType, string method, string guardType, string eventType, string field)
    {
        string Test(string name, int level, string op) => """
            [Fact]
            public void __NAME__()
            {
                // Raw Debezium envelope with the BiRK column name exactly as in the source ("__FIELD__"); no __EVENT__ is constructed by hand.
                const string record = "{\"BarnPK\":1,\"BirkID\":\"B024-0001\",\"__FIELD__\":__LEVEL__}";
                var json = "{\"payload\":{\"op\":\"__OP__\",\"source\":{\"table\":\"Barn\"},\"before\":" + __BEFORE__ + ",\"after\":" + __AFTER__ + "}}";
                var deserialize = typeof(__WORKER__).GetMethod("__METHOD__", BindingFlags.NonPublic | BindingFlags.Static)!;
                var cdcEvent = (__EVENT__)deserialize.Invoke(null, [BinaryData.FromString(json)])!;

                Assert.Equal(__LEVEL__, cdcEvent.Sikkerhetsnivaa);   // fails while the production path sets a constant
                var guard = new __GUARD__(Substitute.For<IAlertService>(), NullLogger<__GUARD__>.Instance);
                Assert.Equal(GuardResult.Rejected, guard.Evaluate(cdcEvent));
            }
            """.Replace("__NAME__", name).Replace("__FIELD__", field).Replace("__EVENT__", eventType).Replace("__LEVEL__", level.ToString())
            .Replace("__OP__", op).Replace("__BEFORE__", op == "d" ? "record" : "\"null\"").Replace("__AFTER__", op == "d" ? "\"null\"" : "record")
            .Replace("__WORKER__", workerType).Replace("__METHOD__", method).Replace("__GUARD__", guardType);
        return
        [
            new() { Name = "RawDebeziumLevel2_ReachesSecurityClassificationGuard", Purpose = "Level 2 in a raw create event reaches the guard through the production deserializer.", Code = Test("RawDebeziumLevel2_ReachesSecurityClassificationGuard", 2, "c") },
            new() { Name = "RawDebeziumLevel3_ReachesSecurityClassificationGuard", Purpose = "Level 3 in a raw update event reaches the guard.", Code = Test("RawDebeziumLevel3_ReachesSecurityClassificationGuard", 3, "u") },
            new() { Name = "RawDebeziumDeleteLevel3_ReadsClassificationFromBefore", Purpose = "A delete carries the level in \"before\"; it must not default to 0.", Code = Test("RawDebeziumDeleteLevel3_ReadsClassificationFromBefore", 3, "d") },
        ];
    }
}
