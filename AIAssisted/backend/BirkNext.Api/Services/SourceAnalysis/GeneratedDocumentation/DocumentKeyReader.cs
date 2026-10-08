using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Structured keys from a document's tables, headings and code spans. Each reader keys on recognised column names or heading shapes; a value
/// outside such a structure is never read. Shared by generated documentation (at upload) and authored artifacts (at diagnostic time).
/// </summary>
internal static class DocumentKeyReader
{
    private const string ResourceColumn = @"^(resource|resource name|topic|topics|queue|queues|subscription|subscriptions|event ?hubs?|hub|hub name|consumer ?group|channel|destination|entity name)$";
    private const string ResourceProperty = @"^(topic|queue|subscription|event ?hub|event ?hub name|hub|consumer ?group|channel|destination|entity name)$";

    /// <summary>"METHOD /path" from Method+Path tables and inline "GET /x" spans; "ROUTE /path" from route/page tables (UI routes).</summary>
    public static IEnumerable<string> Routes(DocumentStructure s)
    {
        foreach (var table in s.Tables)
        {
            var method = DocumentStructure.Column(table, @"^(method|verb|http method)$");
            var path = DocumentStructure.Column(table, @"^(path|route|endpoint|url|uri|route template)$");
            if (method >= 0 && path >= 0)
            {
                foreach (var row in table.Rows)
                {
                    var m = DocumentStructure.Plain(DocumentStructure.Cell(row, method)).ToUpperInvariant();
                    var p = DocumentStructure.CodeSpans(DocumentStructure.Cell(row, path)).FirstOrDefault() ?? DocumentStructure.Plain(DocumentStructure.Cell(row, path));
                    if (Regex.IsMatch(m, "^(GET|POST|PUT|DELETE|PATCH)$") && p.StartsWith('/')) yield return StructuredKeys.Route(m, p);
                }
                continue;
            }
            var route = DocumentStructure.Column(table, @"^(route|page route|page|url)$");
            if (route < 0) continue;
            foreach (var row in table.Rows)
            {
                var value = DocumentStructure.CodeSpans(DocumentStructure.Cell(row, route)).FirstOrDefault() ?? DocumentStructure.Plain(DocumentStructure.Cell(row, route));
                var inline = Regex.Match(value, @"^(GET|POST|PUT|DELETE|PATCH)\s+(/\S*)$", RegexOptions.IgnoreCase);
                if (inline.Success) yield return StructuredKeys.Route(inline.Groups[1].Value, inline.Groups[2].Value);
                else if (value.StartsWith('/') && !value.Contains(' ')) yield return "ROUTE " + StructuredKeys.Route("X", value)[2..];
            }
        }
        foreach (var span in DocumentStructure.CodeSpans(s.Text))
            foreach (var route in StructuredKeys.InlineRoutes(span)) yield return route;
    }

    /// <summary>Messaging resource names from resource columns (column style) and from property/value tables whose property names a resource.</summary>
    public static IEnumerable<string> MessagingResources(DocumentStructure s)
    {
        foreach (var table in s.Tables)
        {
            var columns = table.Headers.Select((h, i) => (Header: DocumentStructure.Plain(h), i)).Where(x => Regex.IsMatch(x.Header, ResourceColumn, RegexOptions.IgnoreCase)).Select(x => x.i).ToList();
            var property = DocumentStructure.Column(table, @"^(property|field|attribute|setting|key|name)$");
            var value = DocumentStructure.Column(table, @"^(value|values)$");
            if (property >= 0 && value >= 0 && table.Headers.Count == 2)
            {
                foreach (var row in table.Rows.Where(r => Regex.IsMatch(DocumentStructure.Plain(DocumentStructure.Cell(r, property)), ResourceProperty, RegexOptions.IgnoreCase)))
                    foreach (var id in DocumentStructure.Identifiers(DocumentStructure.Cell(row, value)).SelectMany(EntityPath).Where(IsResourceName)) yield return id;
                continue;
            }
            foreach (var column in columns)
                foreach (var row in table.Rows)
                    foreach (var id in DocumentStructure.Identifiers(DocumentStructure.Cell(row, column)).SelectMany(EntityPath).Where(IsResourceName)) yield return id;
        }
    }

    /// <summary>An entity path "topic/Subscriptions/sub" names two resources.</summary>
    private static IEnumerable<string> EntityPath(string value)
    {
        var parts = Regex.Split(value, "/subscriptions/", RegexOptions.IgnoreCase);
        return parts.Length == 2 ? parts : [value];
    }

    private static bool IsResourceName(string value) =>
        !value.StartsWith('$') && !value.Contains(':') && !Regex.IsMatch(value, @"(?i)\.(json|ya?ml|xml|md|cs|csproj|txt|config)$") && !Regex.IsMatch(value, @"^(INBOUND|OUTBOUND|in|out|topic|queue|subscription|n/?a|none|yes|no)$", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(value, @"^[A-Z][a-z]+([A-Z][a-z0-9]+)+$") /* PascalCase type names are message types, not resources */;

    /// <summary>Entity groups ("EntityClass|table_name") from entity headings ("### Entity: `Order` → table `orders`", "### Orders (Order)") and entity/table columns.</summary>
    public static IEnumerable<string> Entities(DocumentStructure s)
    {
        foreach (var heading in s.Headings.Where(h => h.Level >= 2))
        {
            var text = heading.Text;
            var plain = DocumentStructure.Plain(text);
            var underEntities = heading.Parent is { } parent && Regex.IsMatch(parent, @"(?i)\b(entit(y|ies)|tables?|schema|model)\b");
            var entityHeading = Regex.IsMatch(plain, @"(?i)^(entity|table)\b");
            if (!entityHeading && !underEntities) continue;
            if (Regex.IsMatch(plain, @"(?i)^(migrations?|relationships?|overview|indexes|enums?\b|dbcontext|notes?|constraints)")
                || Regex.IsMatch(plain, @"(?i)^(entities|entity|tables|table|schema|model|data model|domain model|value objects?)$")) continue;
            var spans = DocumentStructure.CodeSpans(text).Where(DocumentStructure.IsIdentifier).ToList();
            if (spans.Count == 0)
            {
                var m = Regex.Match(Regex.Replace(plain, @"(?i)^(entity|table)\s*:?\s*", ""), @"^([\p{L}_][\w.]*)(?:\s*\(([\p{L}_][\w.]*)\))?\s*$");
                if (!m.Success) continue;
                spans = new[] { m.Groups[1].Value, m.Groups[2].Value }.Where(v => v.Length > 0).ToList();
            }
            yield return string.Join("|", spans.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        foreach (var table in s.Tables)
        {
            var column = DocumentStructure.Column(table, @"^(entity|entities|table|table name|entity class)$");
            if (column < 0 || DocumentStructure.Column(table, @"^(from|to)$") >= 0) continue;
            foreach (var row in table.Rows)
                foreach (var id in DocumentStructure.Identifiers(DocumentStructure.Cell(row, column))) yield return id;
        }
    }

    public static IEnumerable<string> Relationships(DocumentStructure s)
    {
        foreach (var table in s.Tables)
        {
            var from = DocumentStructure.Column(table, @"^(from|parent|principal)$");
            var to = DocumentStructure.Column(table, @"^(to|child|dependent)$");
            if (from < 0 || to < 0) continue;
            foreach (var row in table.Rows)
            {
                var a = DocumentStructure.Identifiers(DocumentStructure.Cell(row, from)).FirstOrDefault();
                var b = DocumentStructure.Identifiers(DocumentStructure.Cell(row, to)).FirstOrDefault();
                if (a is not null && b is not null) yield return $"{a}->{b}";
            }
        }
    }

    /// <summary>Configuration keys from key columns ("Key", "Setting", "Configuration key" …) when the value is a hierarchical key (":" or "__").</summary>
    public static IEnumerable<string> ConfigurationKeys(DocumentStructure s)
    {
        foreach (var table in s.Tables)
        {
            var column = DocumentStructure.Column(table, @"^(key|keys|setting|settings|configuration key|config key|configuration|environment variable|variable|section|name)$");
            if (column < 0 || DocumentStructure.Column(table, @"^(method|route|path)$") >= 0) continue;
            foreach (var row in table.Rows)
                foreach (var id in DocumentStructure.CodeSpans(DocumentStructure.Cell(row, column)).DefaultIfEmpty(DocumentStructure.Plain(DocumentStructure.Cell(row, column))))
                    if (Regex.IsMatch(id, @"^[A-Za-z][\w.-]*((:|__)[\w.\[\]-]+)+$") && !id.Contains("://")) yield return id;
        }
    }

    /// <summary>Dotted component/project identifiers ("Shop.Orders.Api") in code spans, table cells and diagram labels.</summary>
    public static IEnumerable<string> Components(DocumentStructure s)
    {
        var pattern = new Regex(@"(?<![\w.])([A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,6})(?![\w(])");
        foreach (var span in DocumentStructure.CodeSpans(s.Text)) if (pattern.Match(span) is { Success: true } m && m.Value == span) yield return span;
        foreach (var table in s.Tables) foreach (var row in table.Rows) foreach (var cell in row)
            foreach (var id in DocumentStructure.Identifiers(cell)) if (pattern.Match(id) is { Success: true } m && m.Value == id) yield return id;
        foreach (var fence in s.Fences.Where(f => f.Language.StartsWith("mermaid", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(fence.Body, @"[\[(""]\s*([A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,6})\s*[\])""<]")) yield return m.Groups[1].Value;
    }
}

/// <summary>HTTP routes declared by attributes in C# source: controller [Route] + [HttpGet(…)] (with [controller] resolved) and endpoint
/// attributes with absolute routes (e.g. [WolverineGet("/x")]). Minimal-API routes come from the Architecture analysis facts.</summary>
internal static class SourceRouteReader
{
    private static readonly Regex Class = new(@"((?:\[(?:[^\[\]\r\n]|\[[^\]\r\n]*\])*\]\s*)*)(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex Http = new(@"\[Http(Get|Post|Put|Delete|Patch)(?:\(\s*""([^""]*)""[^)]*\))?\]", RegexOptions.Compiled);
    private static readonly Regex Endpoint = new(@"\[\w*(Get|Post|Put|Delete|Patch)\(\s*""(/[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Route = new(@"\[Route\(\s*""([^""]*)""", RegexOptions.Compiled);

    public static IEnumerable<string> AttributeRoutes(string code)
    {
        if (string.IsNullOrEmpty(code)) yield break;
        var classes = Class.Matches(code).ToList();
        for (var i = 0; i < classes.Count; i++)
        {
            var cls = classes[i];
            var start = cls.Index + cls.Length;
            var end = i + 1 < classes.Count ? classes[i + 1].Index : code.Length;
            var body = code[start..end];
            var name = cls.Groups[2].Value;
            var prefix = Route.Match(cls.Groups[1].Value) is { Success: true } r ? r.Groups[1].Value : "";
            var controller = name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^"Controller".Length] : name;
            prefix = prefix.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase);
            foreach (Match m in Http.Matches(body))
            {
                var template = m.Groups[2].Success ? m.Groups[2].Value : "";
                var path = template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal) ? template.TrimStart('~') : $"{prefix}/{template}";
                yield return StructuredKeys.Route(m.Groups[1].Value, path.Replace("[action]", "{}", StringComparison.OrdinalIgnoreCase));
            }
        }
        foreach (Match m in Endpoint.Matches(code))
            if (!m.Value.StartsWith("[Http", StringComparison.Ordinal)) yield return StructuredKeys.Route(m.Groups[1].Value, m.Groups[2].Value);
    }
}
