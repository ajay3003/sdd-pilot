using System.Text.RegularExpressions;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.Sdd;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;

namespace BirkNext.Api.Services.CriticalE2E.Suggestions;

/// <summary>
/// UI-surface facts from Razor / CSHTML / HTML source, read once at Source Analysis. Facts only — routes, forms, controls, labels, test ids,
/// validation constraints (markup attributes and DataAnnotations on the bound model), handlers, requirement references written in comments.
/// The markup itself is never kept. Razor is not executed: an expression is not evaluated, a custom component is not assumed to be a DOM
/// element, and anything conditional or repeated is marked as such. Bounded: at most <see cref="MaxControlsPerPage"/> controls per file.
/// </summary>
public static partial class SourceUiExtractor
{
    public const int MaxControlsPerPage = 200;
    public const int MaxPages = 400;

    [GeneratedRegex(@"^\s*@page\s+""(?<route>[^""]+)""", RegexOptions.Multiline)] private static partial Regex PageDirective();
    [GeneratedRegex(@"<(?<close>/)?(?<tag>[A-Za-z][\w.:-]*)\b(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*?)(?<self>/)?>", RegexOptions.Singleline)] private static partial Regex Tag();
    [GeneratedRegex(@"(?<name>@?[A-Za-z_:][\w.:@-]*)\s*(?:=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+)))?", RegexOptions.Singleline)] private static partial Regex Attr();
    [GeneratedRegex(@"@\*(?<c>.*?)\*@|<!--(?<c>.*?)-->", RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"\[Authorize(?:\((?<args>[^\]]*)\))?\]")] private static partial Regex Authorize();
    [GeneratedRegex(@"Roles\s*=\s*""(?<r>[^""]+)""")] private static partial Regex Roles();
    [GeneratedRegex(@"@code\s*\{")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"(?<type>[A-Z]\w*)(?:<[^>]*>)?\??\s+(?<name>_?\w+)\s*(?:=|;|\{)")] private static partial Regex FieldDeclaration();
    [GeneratedRegex(@"class\s+(?<name>\w+)")] private static partial Regex ClassDeclaration();
    [GeneratedRegex(@"(?<attrs>(?:\[[^\]]+\]\s*)+)public\s+[\w<>\[\]?,. ]+?\s+(?<prop>\w+)\s*\{", RegexOptions.Singleline)] private static partial Regex AttributedProperty();
    [GeneratedRegex(@"@(?:if|foreach|for|while)\b|\belse\b")] private static partial Regex ControlFlow();

    private static readonly Dictionary<string, (SourceUiControlKind Kind, string? Role, string? InputType)> Builtins = new(StringComparer.Ordinal)
    {
        ["InputText"] = (SourceUiControlKind.TextInput, "textbox", "text"),
        ["InputNumber"] = (SourceUiControlKind.NumberInput, "textbox", "number"),
        ["InputDate"] = (SourceUiControlKind.DateInput, "textbox", "date"),
        ["InputSelect"] = (SourceUiControlKind.Select, "combobox", null),
        ["InputCheckbox"] = (SourceUiControlKind.Checkbox, "checkbox", "checkbox"),
        ["InputTextArea"] = (SourceUiControlKind.TextArea, "textbox", null),
        ["InputFile"] = (SourceUiControlKind.FileInput, null, "file"),
        ["textarea"] = (SourceUiControlKind.TextArea, "textbox", null),
        ["select"] = (SourceUiControlKind.Select, "combobox", null),
    };

    /// <summary>Model constraints from DataAnnotations: class → property → constraint facts.</summary>
    public sealed record PropertyRules(bool Required, int? MinLength, int? MaxLength, string? RangeMin, string? RangeMax, string? Pattern, string? RequiredMessage, string? ConstraintMessage, string? InputType);

    public static SourceUiSurface Extract(IReadOnlyList<SourceFile>? uiFiles, IReadOnlyList<SourceFile>? codeFiles, int skipped = 0)
    {
        if (uiFiles is null || uiFiles.Count == 0)
            return new SourceUiSurface { FilesSkipped = skipped, Limitations = ["No Razor, CSHTML or HTML file was read from this archive."] };
        var models = ModelRules(codeFiles ?? []);
        var codeBehind = (codeFiles ?? []).Where(f => f.Path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Path[..^3], f => f.Content, StringComparer.OrdinalIgnoreCase);
        var pages = new List<SourceUiPage>();
        foreach (var file in uiFiles.OrderBy(f => f.Path, StringComparer.Ordinal).Take(MaxPages))
            pages.Add(Page(file, codeBehind.GetValueOrDefault(file.Path), models));
        var limitations = new List<string>
        {
            "Facts from UI source only: routes, controls, labels and test ids are candidates until the Edge Companion verifies them on the attended page.",
            "Razor expressions are not evaluated; custom components are listed as components, not as DOM elements.",
        };
        if (uiFiles.Count > MaxPages) limitations.Add($"Only the first {MaxPages} UI files were analysed.");
        if (skipped > 0) limitations.Add($"{skipped} UI file(s) were not read (size, binary content or the file limit).");
        return new SourceUiSurface { Pages = pages, FilesRead = uiFiles.Count, FilesSkipped = skipped, Limitations = limitations };
    }

    internal static Dictionary<string, Dictionary<string, PropertyRules>> ModelRules(IReadOnlyList<SourceFile> codeFiles)
    {
        var result = new Dictionary<string, Dictionary<string, PropertyRules>>(StringComparer.Ordinal);
        foreach (var f in codeFiles.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && f.Content.Contains("System.ComponentModel.DataAnnotations", StringComparison.Ordinal)))
        {
            var classes = ClassDeclaration().Matches(f.Content).ToList();
            foreach (Match m in AttributedProperty().Matches(f.Content))
            {
                var owner = classes.LastOrDefault(c => c.Index < m.Index)?.Groups["name"].Value;
                if (owner is null) continue;
                var attrs = m.Groups["attrs"].Value;
                static string? Arg(string attrs, string pattern) => Regex.Match(attrs, pattern) is { Success: true } x ? x.Groups[1].Value : null;
                var stringLength = Regex.Match(attrs, @"StringLength\(\s*(\d+)(?:\s*,\s*MinimumLength\s*=\s*(\d+))?");
                var range = Regex.Match(attrs, @"Range\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)");
                var rules = new PropertyRules(
                    Required: Regex.IsMatch(attrs, @"\bRequired\b"),
                    MinLength: int.TryParse(stringLength.Success && stringLength.Groups[2].Success ? stringLength.Groups[2].Value : Arg(attrs, @"MinLength\(\s*(\d+)"), out var min) ? min : null,
                    MaxLength: int.TryParse(stringLength.Success ? stringLength.Groups[1].Value : Arg(attrs, @"MaxLength\(\s*(\d+)"), out var max) ? max : null,
                    RangeMin: range.Success ? range.Groups[1].Value : null, RangeMax: range.Success ? range.Groups[2].Value : null,
                    Pattern: Arg(attrs, @"RegularExpression\(\s*@?""([^""]*)"""),
                    // Each message belongs to its own attribute: the required message never stands in for a length or range message.
                    RequiredMessage: Arg(attrs, @"Required\([^\)]*ErrorMessage\s*=\s*""([^""]*)"""),
                    ConstraintMessage: Arg(attrs, @"(?:StringLength|MaxLength|MinLength|Range)\([^\)]*ErrorMessage\s*=\s*""([^""]*)"""),
                    InputType: Regex.IsMatch(attrs, @"\bEmailAddress\b") ? "email" : Regex.IsMatch(attrs, @"\bPhone\b") ? "tel" : null);
                if (!result.TryGetValue(owner, out var props)) result[owner] = props = new(StringComparer.Ordinal);
                props[m.Groups["prop"].Value] = rules;
            }
        }
        return result;
    }

    private static int LineOf(string text, int index) { var n = 1; for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++; return n; }

    private static string? Static(string? text)
    {
        if (text is null) return null;
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        // A Razor expression is not evaluated: text that is (or contains) one is not a static label.
        return t.Length == 0 || t.Contains('@') || t.Length > 120 ? null : System.Net.WebUtility.HtmlDecode(t);
    }

    private static SourceUiPage Page(SourceFile file, string? codeBehind, Dictionary<string, Dictionary<string, PropertyRules>> models)
    {
        var path = file.Path.Replace('\\', '/');
        var text = file.Content;
        // The @code block is C#, not markup.
        var code = "";
        if (CodeBlock().Match(text) is { Success: true } cb) { code = text[cb.Index..]; text = text[..cb.Index]; }
        code += "\n" + (codeBehind ?? "");
        var limitations = new List<string>();
        var routes = PageDirective().Matches(text).Select(m => m.Groups["route"].Value).Distinct().ToList();
        if (routes.Count == 0 && path.Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            routes.Add("/" + path[(path.IndexOf("/wwwroot/", StringComparison.OrdinalIgnoreCase) + 9)..]);
        var auth = Authorize().Match(text + "\n" + code);
        var roles = auth.Success && Roles().Match(auth.Value) is { Success: true } r ? r.Groups["r"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : [];
        var requirements = Comment().Matches(text).SelectMany(m => RequirementReferenceParser.Extract(m.Groups["c"].Value, requireUpperCase: true))
            .Where(id => Regex.IsMatch(id, @"^(FR|NFR|SC|US|UC|AC|TS|REQ|TC)-\d+$")).Distinct().ToList();
        // Binding roots → model types ("model" → "ChildForm") from field declarations in @code / code-behind.
        var fieldTypes = FieldDeclaration().Matches(code).GroupBy(m => m.Groups["name"].Value).ToDictionary(g => g.Key, g => g.First().Groups["type"].Value, StringComparer.Ordinal);
        var markup = Comment().Replace(text, m => new string(' ', m.Length));
        // Labels: <label for="id">Text</label> and wrapping labels.
        var labelsFor = Regex.Matches(markup, @"<label\b[^>]*\bfor\s*=\s*""(?<for>[^""]+)""[^>]*>(?<t>[^<]*)</label>", RegexOptions.Singleline)
            .GroupBy(m => m.Groups["for"].Value).ToDictionary(g => g.Key, g => Static(g.First().Groups["t"].Value), StringComparer.Ordinal);
        var wrapping = Regex.Matches(markup, @"<label\b(?![^>]*\bfor\s*=)[^>]*>(?<t>[^<@]*)<(?<tag>Input\w*|input|select|textarea)\b", RegexOptions.Singleline)
            .Select(m => (Index: m.Groups["tag"].Index - 1, Text: Static(m.Groups["t"].Value))).ToList();
        // Conditional/repeated regions: brace depth after @if/@foreach/… (approximate, stated as such).
        var regions = Regions(markup);

        var controls = new List<SourceUiControl>();
        var forms = new List<SourceUiForm>();
        string? currentForm = null;
        string? formModelType = null;
        string? title = Regex.Match(markup, @"<PageTitle>(?<t>[^<]*)</PageTitle>") is { Success: true } pt ? Static(pt.Groups["t"].Value) : null;
        var tags = Tag().Matches(markup).ToList();
        for (var i = 0; i < tags.Count && controls.Count < MaxControlsPerPage; i++)
        {
            var t = tags[i];
            var tag = t.Groups["tag"].Value;
            if (t.Groups["close"].Success)
            {
                if (tag is "EditForm" or "form") currentForm = null;
                continue;
            }
            var attrs = Attr().Matches(t.Groups["attrs"].Value).ToDictionary(a => a.Groups["name"].Value, a => a.Groups["v"].Success ? a.Groups["v"].Value : "", StringComparer.OrdinalIgnoreCase);
            string? A(string name) => attrs.TryGetValue(name, out var v) ? v : null;
            var line = LineOf(text, t.Index);
            var (conditional, repeated) = regions(t.Index);
            string? Inner() => t.Groups["self"].Success ? null : Regex.Match(markup[(t.Index + t.Length)..], $@"^(?<inner>.*?)</{Regex.Escape(tag)}>", RegexOptions.Singleline) is { Success: true } m
                ? Static(Regex.Replace(m.Groups["inner"].Value, "<[^>]+>", " ")) : null;
            var testId = A("data-testid") is { } tid && !tid.Contains('@') ? tid : null;
            var elementId = A("id") is { } eid && !eid.Contains('@') ? eid : null;
            if (tag is "EditForm" or "form")
            {
                currentForm = $"form{forms.Count + 1}";
                var modelExpr = (A("Model") ?? A("EditContext") ?? "").Trim('@', '(', ')', ' ');
                formModelType = fieldTypes.GetValueOrDefault(modelExpr.Split('.')[0]);
                forms.Add(new SourceUiForm(currentForm, formModelType, (A("OnValidSubmit") ?? A("OnSubmit") ?? A("@onsubmit"))?.Trim('@'),
                    Regex.IsMatch(markup, @"<DataAnnotationsValidator\b"), line));
                continue;
            }
            SourceUiControl? control = null;
            var binding = (A("@bind-Value") ?? A("@bind") ?? A("@bind-value"))?.Trim('@', '(', ')', ' ');
            var member = binding?.Split('.').LastOrDefault();
            var root = binding?.Split('.').FirstOrDefault();
            var rules = member is not null && (root is not null && fieldTypes.GetValueOrDefault(root) is { } bt ? bt : formModelType) is { } modelType
                && models.GetValueOrDefault(modelType)?.GetValueOrDefault(member) is { } pr ? pr : null;
            var label = elementId is not null ? labelsFor.GetValueOrDefault(elementId) : null;
            label ??= wrapping.FirstOrDefault(w => w.Index == t.Index).Text;
            label ??= Static(A("aria-label"));
            if (Builtins.TryGetValue(tag, out var builtin) || tag is "input")
            {
                var type = (A("type") ?? builtin.InputType ?? "text").ToLowerInvariant();
                var (kind, role) = tag is "input"
                    ? type switch
                    {
                        "checkbox" => (SourceUiControlKind.Checkbox, "checkbox"), "number" => (SourceUiControlKind.NumberInput, "textbox"), "date" => (SourceUiControlKind.DateInput, "textbox"),
                        "file" => (SourceUiControlKind.FileInput, (string?)null), "submit" => (SourceUiControlKind.SubmitButton, "button"), "button" => (SourceUiControlKind.Button, "button"),
                        "hidden" => (SourceUiControlKind.Component, (string?)null),
                        _ => (SourceUiControlKind.TextInput, "textbox"),
                    }
                    : (builtin.Kind, builtin.Role);
                if (type == "hidden") continue;
                var options = tag is "select" or "InputSelect"
                    ? Regex.Matches(markup[(t.Index + t.Length)..].Split($"</{tag}>")[0], @"<option\b[^>]*\bvalue\s*=\s*""(?<v>[^""@]*)""").Select(o => o.Groups["v"].Value).Where(v => v.Length > 0).ToList()
                    : [];
                control = new SourceUiControl
                {
                    Kind = kind, Role = role, InputType = rules?.InputType ?? (kind is SourceUiControlKind.TextInput ? type : builtin.InputType ?? type), Binding = member,
                    Required = attrs.ContainsKey("required") || rules?.Required == true,
                    MinLength = int.TryParse(A("minlength"), out var mn) ? mn : rules?.MinLength, MaxLength = int.TryParse(A("maxlength"), out var mx) ? mx : rules?.MaxLength,
                    RangeMin = A("min") ?? rules?.RangeMin, RangeMax = A("max") ?? rules?.RangeMax, Pattern = A("pattern") ?? rules?.Pattern, ErrorMessage = rules?.RequiredMessage, ConstraintMessage = rules?.ConstraintMessage,
                    Text = kind is SourceUiControlKind.SubmitButton or SourceUiControlKind.Button ? Static(A("value")) : null,
                    Label = label ?? (kind is SourceUiControlKind.SubmitButton or SourceUiControlKind.Button ? null : Static(A("placeholder"))), Options = options,
                };
            }
            else if (tag is "button")
            {
                var type = (A("type") ?? (currentForm is not null ? "submit" : "button")).ToLowerInvariant();
                control = new SourceUiControl
                {
                    Kind = type == "submit" ? SourceUiControlKind.SubmitButton : SourceUiControlKind.Button, Role = "button", Text = Inner() ?? Static(A("aria-label")),
                    Handler = A("@onclick")?.Trim('@', '(', ')', ' '), Label = Static(A("aria-label")),
                };
            }
            else if (tag is "a" or "NavLink" && A("href") is { } href)
                control = new SourceUiControl { Kind = SourceUiControlKind.Link, Role = "link", Href = href.Contains('@') ? null : href, Text = Inner() ?? Static(A("aria-label")) };
            else if (tag is "ValidationMessage")
            {
                var forExpr = A("For") ?? "";
                control = new SourceUiControl { Kind = SourceUiControlKind.ValidationMessage, Binding = Regex.Match(forExpr, @"\.(?<m>\w+)\s*\)?\s*$") is { Success: true } vm ? vm.Groups["m"].Value : null };
            }
            else if (tag is "ValidationSummary")
                control = new SourceUiControl { Kind = SourceUiControlKind.ValidationSummary };
            else if (tag is "h1")
            {
                control = new SourceUiControl { Kind = SourceUiControlKind.Heading, Role = "heading", Text = Inner() };
                title ??= control.Text;
            }
            else if (char.IsUpper(tag[0]) && currentForm is not null && !tag.Contains('.') && tag is not ("DataAnnotationsValidator" or "PageTitle" or "label"))
            {
                control = new SourceUiControl { Kind = SourceUiControlKind.Component, Text = tag };
                limitations.Add($"<{tag}> at line {line} is a component: its rendered controls are not known from this file.");
            }
            if (control is null) continue;
            controls.Add(control with
            {
                Id = $"{System.IO.Path.GetFileNameWithoutExtension(path)}:{line}:{controls.Count + 1}", TestId = testId, ElementId = elementId, FormId = currentForm, Line = line,
                Conditional = conditional, Repeated = repeated, Confidence = testId is not null ? "High (test id in source)" : control.Label is not null || control.Text is not null ? "Medium (label or text)" : "Low",
            });
        }
        if (controls.Count >= MaxControlsPerPage) limitations.Add($"Only the first {MaxControlsPerPage} controls of this file were recorded.");
        if (routes.Count == 0) limitations.Add("No @page route: a component, not directly navigable.");
        return new SourceUiPage
        {
            File = path, Module = path.Contains('/') ? path[..path.IndexOf('/')] : "", Routes = routes, Title = title, RequiresAuthorization = auth.Success, Roles = roles,
            Forms = forms, Controls = controls, RequirementRefs = requirements, Limitations = limitations,
        };
    }

    /// <summary>For an index in the markup: is it inside an @if/else (conditional) or @foreach/@for/@while (repeated) block? Brace counting,
    /// approximate by design (Razor is not parsed).</summary>
    private static Func<int, (bool Conditional, bool Repeated)> Regions(string markup)
    {
        var spans = new List<(int Start, int End, bool Repeated)>();
        foreach (Match m in ControlFlow().Matches(markup))
        {
            var open = markup.IndexOf('{', m.Index);
            if (open < 0 || open - m.Index > 200) continue;
            var depth = 0;
            var end = markup.Length;
            for (var i = open; i < markup.Length; i++)
            {
                if (markup[i] == '{') depth++;
                else if (markup[i] == '}' && --depth == 0) { end = i; break; }
            }
            spans.Add((open, end, m.Value is "@foreach" or "@for" or "@while"));
        }
        return index => (spans.Any(s => s.Start < index && index < s.End && !s.Repeated), spans.Any(s => s.Start < index && index < s.End && s.Repeated));
    }
}
