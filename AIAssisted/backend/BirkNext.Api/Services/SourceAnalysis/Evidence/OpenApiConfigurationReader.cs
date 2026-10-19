using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → configuration values an OpenAPI / Swagger document declares, under generic semantic keys: identity (info), servers by
/// environment role (from the server's own description, never from list order), the application route shared by the paths, security schemes,
/// token providers an exchange operation enumerates, endpoint roles, request schemas, and the data-type and process-task tables a generated
/// API description carries. Examples (<c>example</c>/<c>examples</c>) and descriptions of parameters are documentation and are never read as
/// configuration. Everything here is declared in source: not configured, not runtime verified.
/// </summary>
internal static class OpenApiConfigurationReader
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];
    private static readonly HashSet<string> GenericSegments = new(StringComparer.OrdinalIgnoreCase) { "api", "rest", "services", "service", "public", "internal", "graphql", "swagger", "openapi" };

    public static IEnumerable<SourceConfigurationValue> Read(string artifact, YamlNode root)
    {
        var path = SourceEvidenceRedaction.SafePath(artifact);
        var title = root["info"]?.Str("title") is { } t ? SourceEvidenceRedaction.SafePath(t) : null;
        SourceConfigurationValue Value(string key, string? value, SourceConfigurationGroup group, string location, string basis,
            ArchitectureEvidenceState confidence = ArchitectureEvidenceState.Confirmed, SourceEnvironmentRole environment = SourceEnvironmentRole.Any, Dictionary<string, string>? attributes = null) => new()
        {
            SemanticKey = key, Value = value, Group = group, Environment = environment, EvidenceKind = SourceConfigurationEvidenceKind.OpenApi, Artifact = path,
            Location = location, Basis = basis, LogicalSource = title, Confidence = confidence, Attributes = attributes ?? [],
        };
        var values = new List<SourceConfigurationValue>();

        // ── Identity ──
        if (title is not null) values.Add(Value(SourceConfigurationKeys.ApplicationDisplayName, title, SourceConfigurationGroup.Application, "info.title", "API title"));
        if (root["info"]?.Str("version") is { } version)
            values.Add(Value(SourceConfigurationKeys.ApplicationVersion, SourceEvidenceRedaction.SafePath(version), SourceConfigurationGroup.Application, "info.version", "API version"));

        // ── Servers: the role comes from the server's description (or an unmistakable loopback host), never from its position ──
        var servers = root.List("servers").ToArray();
        for (var index = 0; index < servers.Length; index++)
        {
            if (servers[index].Str("url") is not { } raw || SafeUrl(raw) is not { } url) continue;
            var description = servers[index].Str("description");
            var (role, basis, confidence) = Role(description, url);
            values.Add(Value(SourceConfigurationKeys.Server(role), url, SourceConfigurationGroup.Environment, $"servers[{index}]", basis, confidence, role));
        }

        // ── Paths: route, endpoint roles, token providers, request schemas, payload parts ──
        var operations = (root["paths"]?.Entries ?? []).SelectMany(entry => Methods.Where(method => entry.Value.Has(method))
            .Select(method => (Path: entry.Key, Method: method, Node: entry.Value[method]!))).ToArray();
        var route = Route(operations.Select(operation => operation.Path).Distinct(StringComparer.Ordinal).ToArray(), out var routeBasis);
        if (route is not null)
        {
            var corroborated = values.Any(item => item.SemanticKey.StartsWith(SourceConfigurationKeys.EnvironmentServer, StringComparison.Ordinal)
                && Uri.TryCreate(item.Value, UriKind.Absolute, out var server) && server.Host.Split('.')[0].Equals(route.Split('/')[0], StringComparison.OrdinalIgnoreCase));
            values.Add(Value(SourceConfigurationKeys.ApplicationRoute, route, SourceConfigurationGroup.Application, "paths",
                routeBasis + (corroborated ? "; the first route segment is also the first label of a declared server host" : ""),
                corroborated ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Inferred));
        }
        foreach (var (operationPath, method, node) in operations)
        {
            var label = $"{method.ToUpperInvariant()} {SourceEvidenceRedaction.SafePath(operationPath)}";
            var location = $"paths.{SourceEvidenceRedaction.SafePath(operationPath)}.{method}";
            if (EndpointRole(method, operationPath) is { } role)
                values.Add(Value(role.Key, label, SourceConfigurationGroup.ApiEndpoints, location, role.Basis,
                    environment: role.Key == SourceConfigurationKeys.EndpointLocalTestToken ? SourceEnvironmentRole.Local : SourceEnvironmentRole.Any));

            // Token providers: the enumerated values of a provider parameter of a token-exchange operation (an enum is a declared set, not an example).
            if (Regex.IsMatch(operationPath, @"(?i)(exchange|token)"))
                foreach (var (parameter, index) in node.List("parameters").Select((parameter, index) => (parameter, index)))
                    if (parameter.Str("name") is { } name && Regex.IsMatch(name, @"(?i)provider"))
                        foreach (var provider in parameter["schema"]?.Strings("enum") ?? [])
                            values.Add(Value(SourceConfigurationKeys.AuthenticationTokenProvider, SourceEvidenceRedaction.SafePath(provider), SourceConfigurationGroup.Authentication,
                                $"{location}.parameters[{index}].schema.enum", $"enumerated values of parameter \"{SourceEvidenceRedaction.SafePath(name)}\" of {label}"));

            // Request schema of a data write: the contract the payload must satisfy.
            if (method is "post" or "put" && Regex.IsMatch(operationPath, @"(?i)/data(/|$)"))
                foreach (var (mediaType, content) in node["requestBody"]?["content"]?.Entries ?? [])
                    if (content["schema"]?.Str("$ref") is { } reference)
                        values.Add(Value(SourceConfigurationKeys.ContractSchemaReference, SourceEvidenceRedaction.SafePath(reference.Split('/').Last()), SourceConfigurationGroup.Contract,
                            $"{location}.requestBody.content.{SourceEvidenceRedaction.SafePath(mediaType)}.schema", $"request schema of {label}",
                            attributes: new() { ["MediaType"] = SourceEvidenceRedaction.SafePath(mediaType), ["Operation"] = label }));
        }

        // ── Security schemes (OpenAPI 3 components.securitySchemes or Swagger 2 securityDefinitions) ──
        var schemes = root["components"]?["securitySchemes"] ?? root["securityDefinitions"];
        foreach (var (name, scheme) in schemes?.Entries ?? [])
        {
            var type = scheme.Str("type") ?? "";
            var described = type.ToLowerInvariant() switch
            {
                // Each part is a safe label on its own; together "Bearer JWT" would look like a credential to the redactor.
                "http" => $"{SourceEvidenceRedaction.SafePath(scheme.Str("scheme") ?? "HTTP")}{(scheme.Str("bearerFormat") is { } format ? $" {SourceEvidenceRedaction.SafePath(format)}" : "")}",
                "apikey" => $"API key ({scheme.Str("in") ?? "?"} {scheme.Str("name") ?? ""})".Replace(" )", ")", StringComparison.Ordinal),
                "oauth2" => "OAuth 2.0",
                "openidconnect" => "OpenID Connect",
                "basic" => "Basic",
                _ => type,
            };
            values.Add(Value(SourceConfigurationKeys.AuthenticationScheme, type.Equals("http", StringComparison.OrdinalIgnoreCase) ? described : SourceEvidenceRedaction.SafePath(described), SourceConfigurationGroup.Authentication,
                $"{(root["components"]?["securitySchemes"] is not null ? "components.securitySchemes" : "securityDefinitions")}.{SourceEvidenceRedaction.SafePath(name)}",
                "security scheme", attributes: new() { ["Name"] = SourceEvidenceRedaction.SafePath(name), ["Type"] = SourceEvidenceRedaction.SafePath(type) }));
        }

        // ── Tables in the API description: data types and process tasks, recognized by their column headers ──
        var dataTypes = new List<(string Id, string Type, string Mime, string Task)>();
        foreach (var table in Tables(root["info"]?.Str("description") ?? ""))
        {
            int Column(string header) => Array.FindIndex(table.Headers, item => item.Equals(header, StringComparison.OrdinalIgnoreCase));
            string Cell(string[] row, int column) => column >= 0 && column < row.Length ? row[column] : "";
            var dataTypeId = Column("DataTypeId");
            if (dataTypeId >= 0)
            {
                foreach (var row in table.Rows.Where(row => Cell(row, dataTypeId) != ""))
                {
                    var (id, kind, mime, task) = (SourceEvidenceRedaction.SafePath(Cell(row, dataTypeId)), SourceEvidenceRedaction.SafePath(Cell(row, Column("Type"))),
                        SourceEvidenceRedaction.SafePath(Cell(row, Column("MimeTypes"))), SourceEvidenceRedaction.SafePath(Cell(row, Column("TaskId"))));
                    dataTypes.Add((id, kind, mime, task));
                    var attributes = new Dictionary<string, string> { ["Type"] = kind, ["MimeTypes"] = mime };
                    if (task != "" && task != "-") attributes["TaskId"] = task;
                    if (Cell(row, Column("Allowed number")) is { Length: > 0 } allowed) attributes["AllowedNumber"] = SourceEvidenceRedaction.SafePath(allowed);
                    values.Add(Value(SourceConfigurationKeys.ApplicationDataType, id, SourceConfigurationGroup.Application, "info.description (data types table)", "data types table",
                        ArchitectureEvidenceState.StronglySupported, attributes: attributes));
                }
                continue;
            }
            var taskId = Column("TaskId");
            var taskName = Column("Name");
            if (taskId < 0 || taskName < 0) continue;
            foreach (var row in table.Rows.Where(row => Cell(row, taskId) != ""))
            {
                var (id, name, kind) = (SourceEvidenceRedaction.SafePath(Cell(row, taskId)), SourceEvidenceRedaction.SafePath(Cell(row, taskName)), SourceEvidenceRedaction.SafePath(Cell(row, Column("Type"))));
                values.Add(Value(SourceConfigurationKeys.ApplicationTaskId, id, SourceConfigurationGroup.Application, "info.description (process tasks table)", "process tasks table",
                    ArchitectureEvidenceState.StronglySupported, attributes: new() { ["Name"] = name, ["Type"] = kind }));
                if (name != "")
                    values.Add(Value(SourceConfigurationKeys.ApplicationTaskName, name, SourceConfigurationGroup.Application, "info.description (process tasks table)",
                        $"name of process task {id}", ArchitectureEvidenceState.StronglySupported, attributes: new() { ["TaskId"] = id }));
            }
        }
        // The one form data type (a "FormData" row) and its content type. Several form data types: none is chosen.
        var forms = dataTypes.Where(item => item.Type.StartsWith("FormData", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forms.Length == 1)
        {
            var form = forms[0];
            values.Add(Value(SourceConfigurationKeys.ApplicationFormDataType, form.Id, SourceConfigurationGroup.Application, "info.description (data types table)",
                $"the only data type of type {form.Type}", ArchitectureEvidenceState.StronglySupported,
                attributes: form.Task is "" or "-" ? new() { ["MimeTypes"] = form.Mime } : new() { ["MimeTypes"] = form.Mime, ["TaskId"] = form.Task }));
            if (form.Mime.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [var single])
                values.Add(Value(SourceConfigurationKeys.PayloadContentType, single, SourceConfigurationGroup.Payload, "info.description (data types table)",
                    $"MIME type of data type {form.Id}", ArchitectureEvidenceState.StronglySupported, attributes: new() { ["DataType"] = form.Id }));
        }
        // Structural confirmation: a multipart part named after a declared data type, with its declared content type.
        var declared = dataTypes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (operationPath, method, node) in operations)
            foreach (var (mediaType, content) in node["requestBody"]?["content"]?.Entries ?? [])
                foreach (var (part, encoding) in content["encoding"]?.Entries ?? [])
                    if (declared.Contains(SourceEvidenceRedaction.SafePath(part)) && encoding.Str("contentType") is { } contentType && forms.Any(form => form.Id == SourceEvidenceRedaction.SafePath(part)))
                        values.Add(Value(SourceConfigurationKeys.PayloadContentType, SourceEvidenceRedaction.SafePath(contentType), SourceConfigurationGroup.Payload,
                            $"paths.{SourceEvidenceRedaction.SafePath(operationPath)}.{method}.requestBody.content.{SourceEvidenceRedaction.SafePath(mediaType)}.encoding.{SourceEvidenceRedaction.SafePath(part)}",
                            $"content type of multipart part {SourceEvidenceRedaction.SafePath(part)}", attributes: new() { ["DataType"] = SourceEvidenceRedaction.SafePath(part) }));
        return values;
    }

    /// <summary>The environment role a server's own description states; a loopback host is Local by its address. Ambiguous → Unclassified.</summary>
    internal static (SourceEnvironmentRole Role, string Basis, ArchitectureEvidenceState Confidence) Role(string? description, string url)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var text = description.ToLowerInvariant();
            var roles = new List<SourceEnvironmentRole>();
            if (Regex.IsMatch(text, @"\b(local|localhost|development|dev)\b")) roles.Add(SourceEnvironmentRole.Local);
            if (Regex.IsMatch(text, @"\b(test|tt\d*|qa|staging|stage|acceptance|uat|at\d+|preprod)\b")) roles.Add(SourceEnvironmentRole.Test);
            if (Regex.IsMatch(text, @"\b(prod|production|live)\b")) roles.Add(SourceEnvironmentRole.Production);
            var basis = $"server description \"{SourceEvidenceRedaction.SafePath(description.Trim())}\"";
            return roles.Count == 1 ? (roles[0], basis, ArchitectureEvidenceState.StronglySupported) : (SourceEnvironmentRole.Unclassified, basis + (roles.Count > 1 ? " names several environments" : " names no environment"), ArchitectureEvidenceState.Unresolved);
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            ? (SourceEnvironmentRole.Local, "loopback server address", ArchitectureEvidenceState.StronglySupported)
            : (SourceEnvironmentRole.Unclassified, "server without a description; its role is not inferred from its position", ArchitectureEvidenceState.Unresolved);
    }

    /// <summary>The first two literal path segments a majority of paths share ("org/app"), unless they are generic API segments or templates.</summary>
    internal static string? Route(IReadOnlyList<string> paths, out string basis)
    {
        basis = "";
        if (paths.Count == 0) return null;
        var best = paths.Select(path => path.Trim('/').Split('/'))
            .Where(segments => segments.Length >= 3 && segments.Take(2).All(segment => segment.Length > 0 && !segment.Contains('{') && !GenericSegments.Contains(segment) && !Regex.IsMatch(segment, @"^v\d+$")))
            .GroupBy(segments => $"{segments[0]}/{segments[1]}", StringComparer.Ordinal).OrderByDescending(group => group.Count()).FirstOrDefault();
        if (best is null || best.Count() * 2 <= paths.Count) return null;
        basis = $"{best.Count()} of {paths.Count} paths start with /{SourceEvidenceRedaction.SafePath(best.Key)}/";
        return SourceEvidenceRedaction.SafePath(best.Key);
    }

    private static (string Key, string Basis)? EndpointRole(string method, string path)
    {
        var p = path.TrimEnd('/');
        return (method, p) switch
        {
            ("post", _) when Regex.IsMatch(p, @"/instances$") => (SourceConfigurationKeys.EndpointInstanceCreate, "POST on an instances collection"),
            ("get", _) when Regex.IsMatch(p, @"/instances/\{[^}]+\}/\{[^}]+\}$") => (SourceConfigurationKeys.EndpointInstanceRead, "GET of one instance"),
            ("put", _) when Regex.IsMatch(p, @"/process/next$") => (SourceConfigurationKeys.EndpointProcessNext, "PUT process/next"),
            ("get", _) when Regex.IsMatch(p, @"/validate$") => (SourceConfigurationKeys.EndpointValidate, "GET validate"),
            ("get", _) when Regex.IsMatch(p, @"(?i)/applicationmetadata$") => (SourceConfigurationKeys.EndpointApplicationMetadata, "GET applicationmetadata"),
            ("get", _) when Regex.IsMatch(p, @"/options/\{[^}]+\}$") => (SourceConfigurationKeys.EndpointOptions, "GET options"),
            ("get", _) when Regex.IsMatch(p, @"(?i)/exchange/\{[^}]+\}$") => (SourceConfigurationKeys.EndpointAuthenticationExchange, "GET token exchange"),
            ("get", _) when Regex.IsMatch(p, @"(?i)test\w*token") => (SourceConfigurationKeys.EndpointLocalTestToken, "test token operation (local test environments only)"),
            ("post" or "put" or "patch", _) when Regex.IsMatch(p, @"/data(/|$)") => (SourceConfigurationKeys.EndpointDataWrite, $"{method.ToUpperInvariant()} on instance data"),
            _ => null,
        };
    }

    /// <summary>scheme://host[:port][/path] only; a URL with credentials or a query is not a safe configuration value and is skipped.</summary>
    private static string? SafeUrl(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0) return null;
        return SourceEvidenceRedaction.SafePath(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    internal sealed record MarkdownTable(string[] Headers, List<string[]> Rows);

    /// <summary>Markdown pipe tables (header row, separator row, data rows) in a description.</summary>
    internal static IEnumerable<MarkdownTable> Tables(string text)
    {
        var lines = text.Replace("\\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim()).ToArray();
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith('|') || !Regex.IsMatch(lines[i + 1], @"^\|[\s:|-]+\|?$")) continue;
            var table = new MarkdownTable(Cells(lines[i]), []);
            var j = i + 2;
            for (; j < lines.Length && lines[j].StartsWith('|'); j++) table.Rows.Add(Cells(lines[j]));
            yield return table;
            i = j - 1;
        }
    }

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
}
