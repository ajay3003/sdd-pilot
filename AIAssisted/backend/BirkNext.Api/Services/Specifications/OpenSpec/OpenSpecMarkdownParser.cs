using System.Text.RegularExpressions;
using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// Reads the canonical OpenSpec Markdown structure (built-in spec-driven schema), line by line, with line numbers, and never invents content:
/// <code>
/// ## Requirements                     (current spec)       ## ADDED|MODIFIED|REMOVED|RENAMED Requirements   (delta spec)
/// ### Requirement: &lt;name&gt;
/// &lt;normative text (SHALL / MUST)&gt;
/// #### Scenario: &lt;name&gt;
/// - **GIVEN** … / - **WHEN** … / - **THEN** … / - **AND** …
/// </code>
/// Requirements need no FR identifier; scenarios need no user story. Headings inside fenced code are text. Everything it cannot place is a
/// diagnostic with file and line (orphaned scenario, unsupported heading, step outside a scenario, malformed delta …) — nothing is dropped silently.
/// </summary>
public static class OpenSpecMarkdownParser
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Heading = new(@"^(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant, Budget);
    private static readonly Regex RequirementHeading = new(@"^Requirement\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
    private static readonly Regex ScenarioHeading = new(@"^Scenario\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
    private static readonly Regex DeltaSection = new(@"^(ADDED|MODIFIED|REMOVED|RENAMED)\s+Requirements?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
    private static readonly Regex Step = new(@"^\s*(?:[-*+]|\d{1,3}[.)])\s+(?:\*\*|__)?(GIVEN|WHEN|THEN|AND|BUT)\b(?:\*\*|__)?\s*:?\s*(?:\*\*|__)?\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
    private static readonly Regex Rename = new(@"^\s*[-*]?\s*(?:\*\*)?(FROM|TO)(?:\*\*)?\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
    private static readonly Regex Normative = new(@"\b(SHALL|MUST)\b", RegexOptions.CultureInvariant, Budget);

    public sealed record Diagnostic(string Code, OpenSpecDiagnosticSeverity Severity, string Message, int? Line);

    public sealed class ParsedScenario
    {
        public string Name { get; init; } = "";
        public int Line { get; init; }
        public List<string> Given { get; } = [];
        public List<string> When { get; } = [];
        public List<string> Then { get; } = [];
        public List<string> Steps { get; } = [];
        public List<string> Raw { get; } = [];
        internal List<string>? Last;
        internal bool WarnedUnsupportedLine;
    }

    public sealed class ParsedRequirement
    {
        public string Name { get; init; } = "";
        public int Line { get; init; }
        public int EndLine { get; set; }
        public OpenSpecDeltaOperation? Operation { get; init; }
        public List<string> Body { get; } = [];
        public List<ParsedScenario> Scenarios { get; } = [];
        public List<string> Reason { get; } = [];
        public string Statement => string.Join(' ', Body.Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    public sealed record ParsedRename(string From, string To, int Line);

    public sealed class ParsedDocument
    {
        public string? Title { get; set; }
        public List<ParsedRequirement> Requirements { get; } = [];
        public List<ParsedRename> Renames { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];
        public int OrphanScenarios { get; set; }
        public int ScenarioHeadings { get; set; }
        public int RequirementHeadings { get; set; }
        /// <summary>Requirement headings not taken into the result (no name, inside a delta section of a baseline, malformed delta), each with a diagnostic.</summary>
        public int ExcludedRequirements { get; set; }
        /// <summary>Scenario headings under an excluded requirement (excluded with it) or without a name.</summary>
        public int ExcludedScenarios { get; set; }
    }

    /// <param name="delta">True for a change's delta specification (requirements must be inside ADDED/MODIFIED/REMOVED/RENAMED sections).</param>
    public static ParsedDocument Parse(string content, bool delta, CancellationToken ct = default)
    {
        var doc = new ParsedDocument();
        var lines = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fence = (string?)null;
        string? section = null;                 // H2 text
        OpenSpecDeltaOperation? operation = null;
        var deltaInBaseline = false;
        ParsedRequirement? requirement = null;
        ParsedScenario? scenario = null;
        var ignoring = false;                   // under an unsupported H4 inside a requirement: lines are not the requirement's text
        var excluded = false;                   // inside a requirement block that is not taken (its scenarios are excluded with it)
        string? pendingFrom = null; var pendingFromLine = 0;
        var stepWarned = false;

        void Close(int line)
        {
            if (requirement is not null) { requirement.EndLine = Math.Max(requirement.Line, line - 1); }
            requirement = null; scenario = null; ignoring = false; excluded = false;
        }
        void Warn(string code, OpenSpecDiagnosticSeverity severity, string message, int line) => doc.Diagnostics.Add(new(code, severity, message, line));

        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            var number = i + 1;
            var line = lines[i];
            if (line.Length > OpenSpecLimits.MaxLineChars)
            {
                Warn("LINE_TOO_LONG", OpenSpecDiagnosticSeverity.Warning, $"Linjen er lengre enn {OpenSpecLimits.MaxLineChars} tegn og er ikke tolket.", number);
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[..3];
                fence = fence is null ? marker : fence == marker ? null : fence;
                AddText(line);
                continue;
            }
            if (fence is not null) { AddText(line); continue; }

            var h = line.Length > 0 && line[0] == '#' ? Heading.Match(line) : Match.Empty;
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                var text = h.Groups[2].Value.Trim();
                if (level == 1) { Close(number); doc.Title ??= text; section = null; operation = null; deltaInBaseline = false; continue; }
                if (level == 2)
                {
                    Close(number);
                    section = text; operation = null; deltaInBaseline = false;
                    var d = DeltaSection.Match(text);
                    if (d.Success)
                    {
                        if (delta) operation = Enum.Parse<OpenSpecDeltaOperation>(d.Groups[1].Value, ignoreCase: true);
                        else
                        {
                            deltaInBaseline = true;
                            Warn("MALFORMED_DELTA_IN_BASELINE", OpenSpecDiagnosticSeverity.Error,
                                $"«## {text}» er en delta-seksjon, men dokumentet er en gjeldende spesifikasjon. Kravene i seksjonen er ikke tatt med i gjeldende grunnlag.", number);
                        }
                    }
                    else if (delta && RequirementsSection(text))
                        Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, "«## Requirements» i en endrings-spesifikasjon: forventet ADDED/MODIFIED/REMOVED/RENAMED Requirements.", number);
                    continue;
                }
                if (level == 3)
                {
                    var r = RequirementHeading.Match(text);
                    if (r.Success)
                    {
                        Close(number);
                        doc.RequirementHeadings++;
                        var name = OpenSpecIdentity.NormalizeName(r.Groups[1].Value);
                        if (name.Length == 0)
                        {
                            Warn("MISSING_REQUIREMENT_NAME", OpenSpecDiagnosticSeverity.Error, "«### Requirement:» uten navn. Kravet er ikke tatt med.", number);
                            requirement = null; ignoring = true; excluded = true; doc.ExcludedRequirements++; continue;
                        }
                        if (deltaInBaseline) { ignoring = true; excluded = true; doc.ExcludedRequirements++; continue; }
                        if (delta && operation is null)
                        {
                            Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"Kravet «{name}» står utenfor en ADDED/MODIFIED/REMOVED-seksjon og er ikke tolket.", number);
                            ignoring = true; excluded = true; doc.ExcludedRequirements++; continue;
                        }
                        if (delta && operation == OpenSpecDeltaOperation.Renamed)
                        {
                            Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"Kravoverskrift «{name}» i RENAMED-seksjonen: forventet FROM/TO-linjer.", number);
                            ignoring = true; excluded = true; doc.ExcludedRequirements++; continue;
                        }
                        if (!delta && section is not null && !RequirementsSection(section))
                            Warn("REQUIREMENT_OUTSIDE_REQUIREMENTS_SECTION", OpenSpecDiagnosticSeverity.Info, $"Kravet «{name}» står under «## {section}», ikke «## Requirements». Det er tatt med.", number);
                        requirement = new ParsedRequirement { Name = name, Line = number, Operation = operation };
                        doc.Requirements.Add(requirement);
                        continue;
                    }
                    if (requirement is not null || (section is not null && (RequirementsSection(section) || operation is not null)))
                    {
                        Close(number);
                        Warn("UNSUPPORTED_HEADING", OpenSpecDiagnosticSeverity.Warning, $"Overskriften «### {text}» er ikke «### Requirement: …»; innholdet under den er ikke tolket som krav.", number);
                        ignoring = true;
                    }
                    continue;
                }
                if (level == 4)
                {
                    var s = ScenarioHeading.Match(text);
                    if (s.Success)
                    {
                        doc.ScenarioHeadings++;
                        var name = OpenSpecIdentity.NormalizeName(s.Groups[1].Value);
                        if (requirement is null && excluded)
                        {
                            doc.ExcludedScenarios++;
                            scenario = null; ignoring = true; continue;
                        }
                        if (requirement is null)
                        {
                            doc.OrphanScenarios++;
                            Warn("ORPHAN_SCENARIO", OpenSpecDiagnosticSeverity.Error, $"Scenarioet «{name}» står ikke under et «### Requirement:» og er ikke koblet til noe krav.", number);
                            scenario = null; ignoring = true; continue;
                        }
                        if (name.Length == 0)
                        {
                            Warn("MISSING_SCENARIO_NAME", OpenSpecDiagnosticSeverity.Error, $"«#### Scenario:» uten navn under kravet «{requirement.Name}». Scenarioet er ikke tatt med.", number);
                            doc.ExcludedScenarios++;
                            scenario = null; ignoring = true; continue;
                        }
                        ignoring = false;
                        scenario = new ParsedScenario { Name = name, Line = number };
                        requirement.Scenarios.Add(scenario);
                        continue;
                    }
                    if (requirement is not null)
                    {
                        scenario = null; ignoring = true;
                        Warn("UNSUPPORTED_HEADING", OpenSpecDiagnosticSeverity.Warning, $"Overskriften «#### {text}» under kravet «{requirement.Name}» er ikke «#### Scenario: …» og er ikke tolket.", number);
                    }
                    continue;
                }
                // H5/H6 inside a requirement: text of the current block (kept as written).
                AddText(line);
                continue;
            }

            if (delta && operation == OpenSpecDeltaOperation.Renamed && requirement is null)
            {
                var m = Rename.Match(line);
                if (m.Success)
                {
                    var value = RenameTarget(m.Groups[2].Value);
                    if (m.Groups[1].Value.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pendingFrom is not null) Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"RENAMED: «FROM: {pendingFrom}» mangler TO.", pendingFromLine);
                        pendingFrom = value; pendingFromLine = number;
                    }
                    else if (pendingFrom is null) Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"RENAMED: «TO: {value}» uten FROM.", number);
                    else if (value.Length == 0 || pendingFrom.Length == 0) { Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, "RENAMED: tomt kravnavn.", number); pendingFrom = null; }
                    else { doc.Renames.Add(new(pendingFrom, value, pendingFromLine)); pendingFrom = null; }
                }
                continue;
            }
            AddText(line);
        }
        Close(lines.Length + 1);
        if (pendingFrom is not null) Warn("MALFORMED_DELTA", OpenSpecDiagnosticSeverity.Error, $"RENAMED: «FROM: {pendingFrom}» mangler TO.", pendingFromLine);
        return doc;

        void AddText(string line)
        {
            if (ignoring || requirement is null)
            {
                if (!ignoring && requirement is null && !stepWarned && line.Length > 0 && line[0] != '#' && Step.IsMatch(line) && fence is null)
                {
                    stepWarned = true;
                    doc.Diagnostics.Add(new("STEPS_OUTSIDE_SCENARIO", OpenSpecDiagnosticSeverity.Warning, "Steg (GIVEN/WHEN/THEN) står utenfor et scenario og er ikke tolket.", null));
                }
                return;
            }
            if (scenario is null)
            {
                if (requirement.Operation == OpenSpecDeltaOperation.Removed)
                {
                    if (line.Trim().Length > 0) requirement.Reason.Add(line.Trim());
                    return;
                }
                requirement.Body.Add(line);
                return;
            }
            if (fence is not null || line.Trim().Length == 0) { if (line.Trim().Length > 0) scenario.Raw.Add(line); return; }
            var m = Step.Match(line);
            if (m.Success)
            {
                if (scenario.Steps.Count >= OpenSpecLimits.MaxStepsPerScenario) return;
                var keyword = m.Groups[1].Value.ToUpperInvariant();
                var text = m.Groups[2].Value.Trim().TrimEnd('*', '_').Trim();
                scenario.Steps.Add($"{keyword} {text}".Trim());
                scenario.Raw.Add(line);
                var target = keyword switch { "GIVEN" => scenario.Given, "WHEN" => scenario.When, "THEN" => scenario.Then, _ => scenario.Last };
                if (target is null)
                {
                    doc.Diagnostics.Add(new("STEP_KEYWORD_WITHOUT_CONTEXT", OpenSpecDiagnosticSeverity.Warning,
                        $"«{keyword}» i scenarioet «{scenario.Name}» står før GIVEN/WHEN/THEN; steget er beholdt som tekst, men ikke plassert.", null));
                    return;
                }
                if (text.Length > 0) target.Add(text);
                scenario.Last = target;
                return;
            }
            // An indented line continues the previous step; anything else is text the scenario grammar does not cover.
            if (char.IsWhiteSpace(line[0]) && scenario.Last is { Count: > 0 } last)
            {
                last[^1] = $"{last[^1]} {line.Trim()}";
                scenario.Raw.Add(line);
                return;
            }
            scenario.Raw.Add(line);
            if (!scenario.WarnedUnsupportedLine)
            {
                scenario.WarnedUnsupportedLine = true;
                doc.Diagnostics.Add(new("UNSUPPORTED_SCENARIO_LINE", OpenSpecDiagnosticSeverity.Warning,
                    $"Scenarioet «{scenario.Name}» har tekst som ikke er GIVEN/WHEN/THEN/AND-steg; teksten er beholdt i kilden, men ikke tolket som steg.", scenario.Line));
            }
        }
    }

    public static bool HasNormativeKeyword(string statement) => Normative.IsMatch(statement);

    private static bool RequirementsSection(string h2) => h2.Trim().Equals("Requirements", StringComparison.OrdinalIgnoreCase);

    /// <summary>«`### Requirement: Old name`» → «Old name».</summary>
    private static string RenameTarget(string value)
    {
        var v = value.Trim().Trim('`').Trim();
        if (v.StartsWith("###", StringComparison.Ordinal)) v = v.TrimStart('#').Trim();
        var r = RequirementHeading.Match(v);
        return OpenSpecIdentity.NormalizeName(r.Success ? r.Groups[1].Value : v);
    }
}
