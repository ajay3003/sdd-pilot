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
    /// <summary>How deep a page's composed components are followed (page → component → component …), and how many per page.</summary>
    public const int MaxCompositionDepth = 3;
    public const int MaxComposedComponents = 12;

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
    [GeneratedRegex(@"^(?<lib>Radzen|Mud|Fluent|Telerik|Sf)(?<base>[A-Z]\w*)$")] private static partial Regex LibraryTag();
    [GeneratedRegex(@"\b(clear|reset|nullstill|t[øo]m|t[øo]mme)\b|clear|reset", RegexOptions.IgnoreCase)] private static partial Regex ClearIntent();
    [GeneratedRegex(@"search|s[øo]k|\bfinn\b|\bfind\b|lookup|oppslag|query", RegexOptions.IgnoreCase)] private static partial Regex SearchIntent();
    [GeneratedRegex(@"filte?r", RegexOptions.IgnoreCase)] private static partial Regex FilterIntent();
    [GeneratedRegex(@"NavigateTo\(\s*(?<interp>\$)?@?""(?<route>/[^""]*)""")] private static partial Regex NavigateCall();
    [GeneratedRegex(@"\b(?<m>[A-Z]\w*)\s*\([^()]*\)\s*(?:=>|\{)")] private static partial Regex MethodHeader();
    [GeneratedRegex(@"delete|remove|slett|fjern", RegexOptions.IgnoreCase)] private static partial Regex DeleteIntent();
    [GeneratedRegex(@"save|lagre|submit|\bsend|registrer|create|opprett|update|oppdater|godkjenn|approve", RegexOptions.IgnoreCase)] private static partial Regex SaveIntent();
    [GeneratedRegex(@"\bopen|[åa]pne|\bvis\b|\bshow|detail|detalj|\bview", RegexOptions.IgnoreCase)] private static partial Regex OpenIntent();
    [GeneratedRegex(@"ingen treff|ingen resultat|fant ingen|fant ikke|ikke funnet|ingen (?:personer|barn|elever|brukere|data)|no results?\b|no matches|nothing found|not found|no records|no data", RegexOptions.IgnoreCase)] private static partial Regex EmptyText();
    [GeneratedRegex(@"\bfeil\b|noe gikk galt|kunne ikke|something went wrong|\berror\b|failed", RegexOptions.IgnoreCase)] private static partial Regex ErrorText();
    [GeneratedRegex(@"laster|s[øo]ker\.\.\.|s[øo]ker…|loading|searching", RegexOptions.IgnoreCase)] private static partial Regex LoadingText();

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

    /// <summary>Component-library controls by the name after the library prefix (RadzenTextBox → TextBox, MudTextField → TextField). A library
    /// component renders real DOM controls; which element and role is the library's business, so locators stay label/text/test-id based.</summary>
    private static readonly Dictionary<string, (SourceUiControlKind Kind, string? Role, string? InputType)> LibraryControls = new(StringComparer.Ordinal)
    {
        ["Button"] = (SourceUiControlKind.Button, "button", null), ["IconButton"] = (SourceUiControlKind.Button, "button", null), ["SplitButton"] = (SourceUiControlKind.Button, "button", null),
        ["TextBox"] = (SourceUiControlKind.TextInput, "textbox", "text"), ["TextField"] = (SourceUiControlKind.TextInput, "textbox", "text"), ["Search"] = (SourceUiControlKind.TextInput, "searchbox", "search"),
        ["AutoComplete"] = (SourceUiControlKind.TextInput, "combobox", "text"), ["Autocomplete"] = (SourceUiControlKind.TextInput, "combobox", "text"), ["Mask"] = (SourceUiControlKind.TextInput, "textbox", "text"),
        ["MaskedTextBox"] = (SourceUiControlKind.TextInput, "textbox", "text"), ["Password"] = (SourceUiControlKind.TextInput, "textbox", "password"), ["PasswordBox"] = (SourceUiControlKind.TextInput, "textbox", "password"),
        ["TextArea"] = (SourceUiControlKind.TextArea, "textbox", null),
        ["Numeric"] = (SourceUiControlKind.NumberInput, "spinbutton", "number"), ["NumericField"] = (SourceUiControlKind.NumberInput, "spinbutton", "number"),
        ["NumberField"] = (SourceUiControlKind.NumberInput, "spinbutton", "number"), ["NumericTextBox"] = (SourceUiControlKind.NumberInput, "spinbutton", "number"),
        ["DatePicker"] = (SourceUiControlKind.DateInput, "textbox", "date"), ["DateTimePicker"] = (SourceUiControlKind.DateInput, "textbox", "date"),
        ["DropDown"] = (SourceUiControlKind.Select, "combobox", null), ["DropDownList"] = (SourceUiControlKind.Select, "combobox", null), ["Select"] = (SourceUiControlKind.Select, "combobox", null),
        ["Combobox"] = (SourceUiControlKind.Select, "combobox", null), ["ComboBox"] = (SourceUiControlKind.Select, "combobox", null),
        ["CheckBox"] = (SourceUiControlKind.Checkbox, "checkbox", "checkbox"), ["Checkbox"] = (SourceUiControlKind.Checkbox, "checkbox", "checkbox"), ["Switch"] = (SourceUiControlKind.Checkbox, "switch", "checkbox"),
        ["DataGrid"] = (SourceUiControlKind.ResultList, null, null), ["Grid"] = (SourceUiControlKind.ResultList, null, null), ["Table"] = (SourceUiControlKind.ResultList, null, null),
        ["DataList"] = (SourceUiControlKind.ResultList, null, null), ["SimpleTable"] = (SourceUiControlKind.ResultList, null, null),
        ["Upload"] = (SourceUiControlKind.FileInput, null, "file"), ["FileUpload"] = (SourceUiControlKind.FileInput, null, "file"), ["FileInput"] = (SourceUiControlKind.FileInput, null, "file"),
        ["Link"] = (SourceUiControlKind.Link, "link", null), ["NavLink"] = (SourceUiControlKind.Link, "link", null),
    };

    private static readonly HashSet<string> FormTags = new(StringComparer.Ordinal) { "EditForm", "form", "RadzenTemplateForm", "MudForm", "FluentEditForm", "TelerikForm", "SfDataForm" };

    private static string? LibraryName(string prefix) => prefix switch
    {
        "Radzen" => "Radzen", "Mud" => "MudBlazor", "Fluent" => "FluentUI", "Telerik" => "Telerik", "Sf" => "Syncfusion", _ => null,
    };

    /// <summary>What a control is for, from its own words (handler, text, label, binding, test id, id). Clear before Search ("ClearSearch"),
    /// Delete before Save. A hint for suggestions only.</summary>
    internal static SourceUiIntent IntentOf(SourceUiControl c)
    {
        var words = string.Join(" ", new[] { c.Handler, c.Text, c.Label, c.Binding, c.TestId, c.ElementId }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (words.Length == 0) return SourceUiIntent.None;
        if (ClearIntent().IsMatch(words)) return SourceUiIntent.Clear;
        if (SearchIntent().IsMatch(words)) return SourceUiIntent.Search;
        if (FilterIntent().IsMatch(words)) return SourceUiIntent.Filter;
        if (DeleteIntent().IsMatch(words)) return SourceUiIntent.Delete;
        if (SaveIntent().IsMatch(words)) return SourceUiIntent.Save;
        if (OpenIntent().IsMatch(words)) return SourceUiIntent.Open;
        return SourceUiIntent.None;
    }

    private static string? Expr(string? value)
    {
        var v = value?.Trim().Trim('@').Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.StartsWith('(') && v.EndsWith(')')) v = v[1..^1].Trim();
        return v.Length is 0 or > 120 ? null : v;
    }

    private static string? Handler(string? value)
    {
        var v = Expr(value);
        if (v is null) return null;
        // "() => SearchAsync()" / "e => Select(e)" / "SearchAsync": the method name is the fact; a lambda body is never evaluated.
        // "e => _query = e.Value?.ToString()" / "() => _q = string.Empty" assigns a field: "set _query", not the last call in the body.
        if (Regex.Match(v, @"=>\s*(?<target>[A-Za-z_][\w.]*)\s*=(?!=)") is { Success: true } assignment) return $"set {assignment.Groups["target"].Value}";
        var m = Regex.Match(v, @"(?:=>\s*)?(?:await\s+)?(?<name>[A-Za-z_][\w.]*)\s*(?:\(|$)");
        return m.Success ? m.Groups["name"].Value : v;
    }

    /// <summary>An icon-font ligature ("close", "arrow_back", "search") is what the markup says, not the button's accessible name.</summary>
    private static string? ButtonName(string? text, string? ariaLabel) =>
        text is not null && ariaLabel is not null && Regex.IsMatch(text, "^[a-z]+(?:_[a-z]+)*$") ? ariaLabel : text ?? ariaLabel;

    /// <summary>Model constraints from DataAnnotations: class → property → constraint facts.</summary>
    public sealed record PropertyRules(bool Required, int? MinLength, int? MaxLength, string? RangeMin, string? RangeMax, string? Pattern, string? RequiredMessage, string? ConstraintMessage, string? InputType);

    public static SourceUiSurface Extract(IReadOnlyList<SourceFile>? uiFiles, IReadOnlyList<SourceFile>? codeFiles, int skipped = 0)
    {
        if (uiFiles is null || uiFiles.Count == 0)
            return new SourceUiSurface { FilesSkipped = skipped, Limitations = ["No Razor, CSHTML or HTML file was read from this archive."] };
        var models = ModelRules(codeFiles ?? []);
        var codeBehind = (codeFiles ?? []).Where(f => f.Path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Path[..^3], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Content, StringComparer.OrdinalIgnoreCase);
        var pages = new List<SourceUiPage>();
        var failed = new List<string>();
        foreach (var file in uiFiles.OrderBy(f => f.Path, StringComparer.Ordinal).Take(MaxPages))
        {
            // One file the extractor cannot read never erases the facts of every other page: it is named as a limitation instead.
            try { pages.Add(Page(file, codeBehind.GetValueOrDefault(file.Path), models)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add($"{file.Path.Replace('\\', '/')}({ex.GetType().Name})"); }
        }
        pages = Compose(pages);
        var limitations = new List<string>
        {
            "Facts from UI source only: routes, controls, labels and test ids are candidates until the Edge Companion verifies them on the attended page.",
            "Razor expressions are not evaluated. A component used by a page is followed when its file is in this archive (bounded depth); component-library controls (Radzen, MudBlazor, FluentUI, Telerik, Syncfusion) are recognised by name and parameters.",
        };
        if (uiFiles.Count > MaxPages) limitations.Add($"Only the first {MaxPages} UI files were analysed.");
        if (skipped > 0) limitations.Add($"{skipped} UI file(s) were not read (size, binary content or the file limit).");
        if (failed.Count > 0) limitations.Add($"{failed.Count} UI file(s) could not be analysed: {string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? ", …" : "")}.");
        return new SourceUiSurface { Pages = pages, FilesRead = uiFiles.Count, FilesSkipped = skipped, Limitations = limitations };
    }

    /// <summary>
    /// Pages compose components: /person renders &lt;PersonSearchComponent /&gt;, whose input and search button are the page's real behaviour. A
    /// routed page takes the controls, forms and messages of every component it uses whose .razor file is in the archive, followed to
    /// <see cref="MaxCompositionDepth"/> and at most <see cref="MaxComposedComponents"/> per page; each component once per page (cycle-safe).
    /// A component used inside @if/@foreach makes its controls conditional/repeated. Two files with the same component name are ambiguous and
    /// are not composed. Component parameters are not evaluated.
    /// </summary>
    internal static List<SourceUiPage> Compose(List<SourceUiPage> pages)
    {
        var components = pages.Where(p => p.Routes.Count == 0 && p.File.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => System.IO.Path.GetFileNameWithoutExtension(p.File), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (components.Count == 0) return pages;
        return [.. pages.Select(p => p.Routes.Count == 0 || p.ComponentUses.Count == 0 ? p : ComposePage(p, components))];
    }

    private static SourceUiPage ComposePage(SourceUiPage page, Dictionary<string, List<SourceUiPage>> components)
    {
        var controls = page.Controls.ToList();
        var forms = page.Forms.ToList();
        var messages = page.Messages.ToList();
        var limitations = page.Limitations.ToList();
        var composed = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { page.File };
        void Visit(SourceUiPage host, int depth, bool conditional, bool repeated)
        {
            foreach (var use in host.ComponentUses)
            {
                if (!components.TryGetValue(use.Component, out var matches)) continue;
                if (matches.Count > 1)
                {
                    limitations.Add($"<{use.Component}> matches {matches.Count} component files: not composed (ambiguous).");
                    continue;
                }
                var component = matches[0];
                if (!visited.Add(component.File)) continue;
                if (depth >= MaxCompositionDepth || composed.Count >= MaxComposedComponents)
                {
                    limitations.Add($"<{use.Component}> was not followed (composition limit: depth {MaxCompositionDepth}, {MaxComposedComponents} components).");
                    continue;
                }
                composed.Add(component.File);
                var prefix = System.IO.Path.GetFileNameWithoutExtension(component.File);
                var c = conditional || use.Conditional;
                var r = repeated || use.Repeated;
                forms.AddRange(component.Forms.Select(f => f with { Id = $"{prefix}.{f.Id}" }));
                foreach (var control in component.Controls)
                {
                    if (controls.Count >= MaxControlsPerPage) break;
                    controls.Add(control with
                    {
                        SourceFile = control.SourceFile ?? component.File, FormId = control.FormId is null ? null : $"{prefix}.{control.FormId}",
                        Conditional = control.Conditional || c, Repeated = control.Repeated || r,
                    });
                }
                messages.AddRange(component.Messages.Select(m => m with { Conditional = m.Conditional || c }));
                Visit(component, depth + 1, c, r);
            }
        }
        Visit(page, 0, false, false);
        if (composed.Count == 0) return page;
        limitations.Insert(0, $"Includes {composed.Count} composed component(s): {string.Join(", ", composed.Select(System.IO.Path.GetFileNameWithoutExtension))}. Their parameters are not evaluated.");
        return page with { Controls = controls, Forms = forms, Messages = messages, ComposedFrom = composed, Limitations = limitations };
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
        var navigations = new List<SourceUiNavigation>();
        if (CodeBlock().Match(text) is { Success: true } cb)
        {
            code = text[cb.Index..]; text = text[..cb.Index];
            navigations.AddRange(Navigations(code, path, LineOf(file.Content, cb.Index) - 1));
        }
        if (codeBehind is not null) navigations.AddRange(Navigations(codeBehind, path + ".cs", 0));
        code += "\n" + (codeBehind ?? "");
        var limitations = new List<string>();
        var routes = PageDirective().Matches(text).Select(m => m.Groups["route"].Value).Distinct().ToList();
        // A Blazor host page (it loads _framework/blazor.*.js) is the shell every @page renders into, not a page of its own: opening
        // /index.html only hands the path to the Blazor router, so it gets no route and no "page opens" suggestion.
        // Also the .NET 9 fingerprinted form: _framework/blazor.webassembly#[.{fingerprint}].js.
        var blazorHost = Regex.IsMatch(text, @"_framework/blazor\.(?:webassembly|web|server)\b", RegexOptions.IgnoreCase);
        if (blazorHost) limitations.Add("Blazor host page: the shell the @page components render into, not a navigable page.");
        if (routes.Count == 0 && !blazorHost && path.Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
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
        var uses = new List<SourceUiComponentUse>();
        var names = new Dictionary<int, string>();
        var validators = new List<(string Component, string Tag, string? Text, int? Max)>();
        string? currentForm = null;
        string? formModelType = null;
        string? title = Regex.Match(markup, @"<PageTitle>(?<t>[^<]*)</PageTitle>") is { Success: true } pt ? Static(pt.Groups["t"].Value) : null;
        var tags = Tag().Matches(markup).ToList();
        var messageFacts = new List<SourceUiMessage>();
        for (var i = 0; i < tags.Count && controls.Count < MaxControlsPerPage; i++)
        {
            var t = tags[i];
            var tag = t.Groups["tag"].Value;
            if (t.Groups["close"].Success)
            {
                if (FormTags.Contains(tag)) currentForm = null;
                continue;
            }
            // First occurrence wins, as in the browser. A Razor expression with nested quotes in a value (class="@(x ? "a" : "")") is
            // split by the attribute pattern into fragments that can repeat a name; those fragments must never abort the page.
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attr().Matches(t.Groups["attrs"].Value))
                attrs.TryAdd(a.Groups["name"].Value, a.Groups["v"].Success ? a.Groups["v"].Value : "");
            string? A(string name) => attrs.TryGetValue(name, out var v) ? v : null;
            var line = LineOf(text, t.Index);
            var (conditional, repeated) = regions(t.Index);
            string? Inner() => t.Groups["self"].Success ? null : Regex.Match(markup[(t.Index + t.Length)..], $@"^(?<inner>.*?)</{Regex.Escape(tag)}>", RegexOptions.Singleline) is { Success: true } m
                ? Static(Regex.Replace(m.Groups["inner"].Value, "<[^>]+>", " ")) : null;
            var testId = A("data-testid") is { } tid && !tid.Contains('@') ? tid : null;
            var elementId = A("id") is { } eid && !eid.Contains('@') ? eid : null;
            if (FormTags.Contains(tag))
            {
                currentForm = $"form{forms.Count + 1}";
                var modelExpr = (A("Model") ?? A("EditContext") ?? A("Data") ?? "").Trim('@', '(', ')', ' ');
                formModelType = fieldTypes.GetValueOrDefault(modelExpr.Split('.')[0]);
                forms.Add(new SourceUiForm(currentForm, formModelType, Handler(A("OnValidSubmit") ?? A("OnSubmit") ?? A("@onsubmit") ?? A("Submit")),
                    Regex.IsMatch(markup, @"<DataAnnotationsValidator\b") || Regex.IsMatch(markup, @"<(?:Radzen\w*Validator|MudForm)\b"), line));
                continue;
            }
            // Radzen validators name the control they validate (Component="Name"): applied to that control after the loop.
            if (tag is "RadzenRequiredValidator" or "RadzenLengthValidator" && A("Component") is { } validated)
            {
                validators.Add((validated, tag, Static(A("Text")), int.TryParse(A("Max"), out var vmax) ? vmax : null));
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
                    Handler = Handler(A("@oninput") ?? A("@onchange") ?? A("ValueChanged") ?? A("@onclick")), KeyHandler = Handler(A("@onkeyup") ?? A("@onkeydown") ?? A("@onkeypress")),
                    DisabledWhen = Expr(A("disabled")) is { } dis && dis != "false" ? dis : null,
                };
            }
            else if (LibraryTag().Match(tag) is { Success: true } lib && LibraryControls.TryGetValue(lib.Groups["base"].Value, out var lc))
            {
                var library = LibraryName(lib.Groups["lib"].Value);
                var isButton = lc.Kind == SourceUiControlKind.Button;
                var submit = isButton && Regex.IsMatch(A("ButtonType") ?? A("Type") ?? "", @"Submit", RegexOptions.IgnoreCase);
                var inputType = lc.InputType == "text" && Regex.IsMatch(A("InputType") ?? A("Type") ?? "", "Password", RegexOptions.IgnoreCase) ? "password" : lc.InputType;
                var ariaLabel = Static(A("aria-label") ?? A("AriaLabel"));
                control = new SourceUiControl
                {
                    Kind = submit ? SourceUiControlKind.SubmitButton : lc.Kind, Role = lc.Role, InputType = rules?.InputType ?? inputType, Binding = member, Library = library,
                    Text = isButton || lc.Kind is SourceUiControlKind.Link ? ButtonName(Static(A("Text")) ?? Inner(), ariaLabel) : lc.Kind == SourceUiControlKind.ResultList ? tag : null,
                    Label = isButton ? ariaLabel ?? Static(A("Title")) : label ?? Static(A("Label")) ?? ariaLabel ?? Static(A("Placeholder")),
                    Handler = Handler(A("Click") ?? A("OnClick") ?? A("@onclick") ?? A("Change") ?? A("ValueChanged") ?? A("@oninput") ?? A("@onchange")),
                    KeyHandler = Handler(A("@onkeyup") ?? A("@onkeydown") ?? A("@onkeypress") ?? A("OnKeyUp") ?? A("OnKeyDown")),
                    Href = lc.Kind == SourceUiControlKind.Link && (A("Path") ?? A("Href") ?? A("href")) is { } lh && !lh.Contains('@') ? lh : null,
                    Required = string.Equals(A("Required"), "true", StringComparison.OrdinalIgnoreCase) || rules?.Required == true,
                    MaxLength = int.TryParse(A("MaxLength") ?? A("maxlength"), out var lmx) ? lmx : rules?.MaxLength, MinLength = rules?.MinLength,
                    RangeMin = A("Min") ?? rules?.RangeMin, RangeMax = A("Max") ?? rules?.RangeMax, Pattern = rules?.Pattern, ErrorMessage = rules?.RequiredMessage, ConstraintMessage = rules?.ConstraintMessage,
                    DisabledWhen = Expr(A("Disabled")) is { } ld && ld != "false" ? ld : null,
                };
                if (A("Name") is { } nm && !nm.Contains('@')) names[controls.Count] = nm;
                // A grid's empty text is the page's own empty-result message.
                if (lc.Kind == SourceUiControlKind.ResultList && Static(A("EmptyText") ?? A("NoRecordsText") ?? A("NoDataText")) is { } empty)
                    messageFacts.Add(new SourceUiMessage(SourceUiMessageKind.EmptyResult, empty, path, line, conditional));
            }
            else if (tag is "QuickGrid" || (tag is "table" && Regex.IsMatch(markup[(t.Index + t.Length)..].Split("</table>")[0], @"@foreach\b")))
                control = new SourceUiControl { Kind = SourceUiControlKind.ResultList, Text = tag };
            else if (tag is "button")
            {
                var type = (A("type") ?? (currentForm is not null ? "submit" : "button")).ToLowerInvariant();
                control = new SourceUiControl
                {
                    Kind = type == "submit" ? SourceUiControlKind.SubmitButton : SourceUiControlKind.Button, Role = "button", Text = ButtonName(Inner(), Static(A("aria-label"))),
                    Handler = Handler(A("@onclick")), Label = Static(A("aria-label")), DisabledWhen = Expr(A("disabled")) is { } bd && bd != "false" ? bd : null,
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
            else if (char.IsUpper(tag[0]) && !tag.Contains('.') && !LibraryTag().IsMatch(tag) && tag is not ("DataAnnotationsValidator" or "PageTitle" or "label"))
            {
                // A component this file uses: composed later when its .razor file is in the archive.
                // Event callbacks bound to a host method (OnChildSelected="@NavigateToChild"): names only, never evaluated.
                var callbacks = attrs.Where(a => a.Key.StartsWith("On", StringComparison.Ordinal) && Handler(a.Value) is { } h && Regex.IsMatch(h, @"^[A-Za-z_]\w*$"))
                    .Select(a => new SourceUiCallback(a.Key, Handler(a.Value)!)).Take(10).ToList();
                if (uses.Count < 100 && !uses.Any(u => u.Component == tag && u.Line == line)) uses.Add(new SourceUiComponentUse(tag, line, conditional, repeated) { Callbacks = callbacks });
                if (currentForm is not null)
                {
                    control = new SourceUiControl { Kind = SourceUiControlKind.Component, Text = tag };
                    limitations.Add($"<{tag}> at line {line} is a component: its rendered controls are not known from this file alone.");
                }
            }
            if (control is null) continue;
            control = control with { Intent = IntentOf(control) };
            controls.Add(control with
            {
                Id = $"{System.IO.Path.GetFileNameWithoutExtension(path)}:{line}:{controls.Count + 1}", TestId = testId, ElementId = elementId, FormId = currentForm, Line = line,
                Conditional = conditional, Repeated = repeated, Confidence = testId is not null ? "High (test id in source)" : control.Label is not null || control.Text is not null ? "Medium (label or text)" : "Low",
            });
        }
        foreach (var (component, vtag, vtext, vmax) in validators)
        {
            var index = names.FirstOrDefault(n => n.Value == component).Key;
            if (!names.ContainsValue(component) || index >= controls.Count) continue;
            controls[index] = vtag == "RadzenRequiredValidator"
                ? controls[index] with { Required = true, ErrorMessage = vtext ?? controls[index].ErrorMessage }
                : controls[index] with { MaxLength = vmax ?? controls[index].MaxLength, ConstraintMessage = vtext ?? controls[index].ConstraintMessage };
        }
        // Static text that names an empty result, an error or a loading state (shown conditionally in practice). Text only, no markup.
        foreach (Match m in Regex.Matches(markup, @">(?<t>[^<>@{}]{3,120})<"))
        {
            var said = Static(m.Groups["t"].Value);
            if (said is null) continue;
            var kind = EmptyText().IsMatch(said) ? SourceUiMessageKind.EmptyResult : ErrorText().IsMatch(said) ? SourceUiMessageKind.Error : LoadingText().IsMatch(said) ? SourceUiMessageKind.Loading : (SourceUiMessageKind?)null;
            if (kind is { } k && messageFacts.Count < 20) messageFacts.Add(new SourceUiMessage(k, said, path, LineOf(text, m.Index), regions(m.Index).Conditional));
        }
        if (controls.Count >= MaxControlsPerPage) limitations.Add($"Only the first {MaxControlsPerPage} controls of this file were recorded.");
        if (routes.Count == 0) limitations.Add("No @page route: a component, not directly navigable.");
        var resultNavigations = uses.SelectMany(u => u.Callbacks.Select(c => (u, c)))
            .SelectMany(x => navigations.Where(n => n.Handler == x.c.Handler).Select(n => new SourceUiResultNavigation(x.u.Component, x.c.Parameter, n.Handler, n.Route, n.File, n.Line)))
            .DistinctBy(r => (r.Component, r.Parameter, r.Route)).Take(20).ToList();
        return new SourceUiPage
        {
            Navigations = navigations, ResultNavigations = resultNavigations,
            File = path, Module = path.Contains('/') ? path[..path.IndexOf('/')] : "", Routes = routes, Title = title, RequiresAuthorization = auth.Success, Roles = roles,
            Forms = forms, Controls = controls, RequirementRefs = requirements, Limitations = limitations, ComponentUses = uses, Messages = messageFacts,
        };
    }

    /// <summary>
    /// Methods that call <c>NavigateTo("/…")</c>, with the route as written: an interpolation in a path segment stays a template
    /// (<c>/person/{childId}</c>); a query string, or an interpolation that builds one, is cut off. The enclosing method is the nearest method
    /// header before the call (approximate: C# is not parsed).
    /// </summary>
    internal static List<SourceUiNavigation> Navigations(string code, string file, int lineOffset)
    {
        var list = new List<SourceUiNavigation>();
        var headers = MethodHeader().Matches(code).Where(m => m.Groups["m"].Value is not ("NavigateTo" or "InvokeAsync")).ToList();
        foreach (Match call in NavigateCall().Matches(code))
        {
            var route = call.Groups["route"].Value;
            var cut = route.IndexOf('?');
            if (cut >= 0) route = route[..cut];
            var interpolated = call.Groups["interp"].Success;
            // "/admin/audit-log{BuildQueryString(…)}": an interpolation that does not start a path segment builds the query, not the path.
            if (interpolated && Regex.Match(route, @"(?<!/)\{") is { Success: true } brace) route = route[..brace.Index];
            if (!interpolated) route = route.Replace("{", "").Replace("}", "");
            if (route.Length == 0) continue;
            var owner = headers.LastOrDefault(h => h.Index < call.Index)?.Groups["m"].Value;
            if (owner is null || list.Count >= 20) continue;
            list.Add(new SourceUiNavigation(owner, route.Length > 1 ? route.TrimEnd('/') : route, file, lineOffset + LineOf(code, call.Index)));
        }
        return list;
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
