using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using HotChocolate.Language;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Source Analysis → Contracts. Reusable source contract evidence with typed adapters per format: OpenAPI (JSON via the existing extractor,
/// YAML via the subset reader), GraphQL SDL and operation documents (Hot Chocolate parser, as API Quality Review uses), AsyncAPI, JSON Schema,
/// protobuf, message contracts already found by the integration-path analyzer (reused, not re-parsed) and generated-client configuration.
/// A contract in source is a design artifact: it does not prove the deployed API or message flow matches it. Producer/consumer links are
/// made only from project ownership and framework evidence; a contract in a shared/docs folder has no producer.
/// </summary>
internal sealed class ContractAnalyzer : ISourceEvidenceDomainAnalyzer
{
    public const int Version = 1;
    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.Contracts, "Contract and schema analyzer", Version, 1,
        ["OpenAPI", "GraphQL SDL", "GraphQL operations", "AsyncAPI", "JSON Schema", "Protobuf", "Message contracts (C#)", "Generated clients"], [],
        ["Contracts", "Operations", "Types and fields (required/optional)", "Producer/consumer hints", "Contract drift classes"]);

    private const int MaxTypes = 200;
    private const int MaxOperations = 400;

    public void Failed(SourceEvidenceContext context, string reason) => context.Contracts = context.Envelope(new ContractEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.Contracts, Version);

    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        context.Capabilities.AddRange([
            new(SourceEvidenceDomain.Contracts, "OpenAPI 3 (JSON)", DomainSupport.Supported, "Operations and component schemas through the same extractor API Quality Review uses."),
            new(SourceEvidenceDomain.Contracts, "OpenAPI (YAML) / Swagger 2", DomainSupport.Partial, "Paths, methods and component schemas via the YAML subset reader; $ref chains are not resolved."),
            new(SourceEvidenceDomain.Contracts, "GraphQL SDL and operations", DomainSupport.Supported, "Parsed with the Hot Chocolate GraphQL parser; type extensions merged."),
            new(SourceEvidenceDomain.Contracts, "AsyncAPI", DomainSupport.Partial, "Channels and operations; message payload schemas are not resolved."),
            new(SourceEvidenceDomain.Contracts, "JSON Schema", DomainSupport.Partial, "Top-level properties and required fields; $ref/allOf are not resolved."),
            new(SourceEvidenceDomain.Contracts, "Protobuf", DomainSupport.Partial, "Messages, fields and services by pattern."),
            new(SourceEvidenceDomain.Contracts, "Message contracts (C#)", DomainSupport.Partial, "Event classes found by the integration-path analyzer (implementation contracts, not formal schemas)."),
            new(SourceEvidenceDomain.Contracts, "Generated clients", DomainSupport.Partial, "Strawberry Shake, NSwag, OpenAPI Generator and OpenApiReference configuration."),
        ]);
        var contracts = new List<SourceContract>();
        var diagnostics = new List<SourceDomainDiagnostic>();
        var extractor = new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance);
        foreach (var file in context.Files.Where(f => f.Role is SourceFileRole.Contract or SourceFileRole.Test && f.Technology is "OpenAPI" or "AsyncAPI" or "JSON Schema" or "GraphQL" or "Protobuf"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var contract = file.Technology switch
                {
                    "OpenAPI" => OpenApi(file, extractor),
                    "AsyncAPI" => AsyncApi(file),
                    "JSON Schema" => JsonSchema(file),
                    "GraphQL" => GraphQl(file, diagnostics),
                    _ => Protobuf(file),
                };
                if (contract is not null) contracts.Add(Attribute(context, file, contract));
            }
            catch (Exception ex) when (ex is JsonException or SyntaxException or FormatException)
            { diagnostics.Add(new("Parse error", $"{file.Technology} document could not be parsed.", SourceEvidenceRedaction.SafePath(file.Path))); }
        }

        // Message contracts already found by the integration-path analyzer of this snapshot (reused; no second C# scan).
        foreach (var e in context.IntegrationPath?.Events ?? [])
        {
            var project = context.Input.Projects.FirstOrDefault(p => p.Name == e.Project || p.Path == e.Project);
            var direct = project is null ? null : context.Architecture?.Components.FirstOrDefault(c => c.SourceProject == project.Path);
            // A library's event is produced by the component that includes it — only when exactly one does; a shared library is not guessed between.
            var including = direct is not null || project is null ? [] : context.Architecture?.Components.Where(c => c.IncludedLibraries.Contains(project.Name)).ToList() ?? [];
            var component = direct ?? (including.Count == 1 ? including[0] : null);
            contracts.Add(new SourceContract
            {
                Id = $"message:{SourceEvidenceRedaction.SafePath(e.EventType)}", Type = SourceContractType.MessageContract, Name = SourceEvidenceRedaction.SafePath(e.EventType), File = SourceEvidenceRedaction.SafePath(e.Location.File),
                Line = e.Location.Line, Format = "csharp", Producer = component?.Name,
                ProducerBasis = direct is not null ? "Event created in this component's source (integration path)"
                    : component is not null ? $"Event defined in library {SourceEvidenceRedaction.SafePath(project!.Name)}, included only by this component"
                    : including.Count > 1 ? $"Event defined in a library included by {including.Count} components — producer not identified" : "Producer not identified",
                Operations = e.Topics.Select(t => new ContractOperation($"publish {SourceEvidenceRedaction.SafePath(t)}", "publish", SourceEvidenceRedaction.SafePath(t), [])).ToList(),
                Types = [new ContractTypeShape(SourceEvidenceRedaction.SafePath(e.EventType), "message", e.Fields.Select(f => new ContractField(SourceEvidenceRedaction.SafePath(f.Name), SourceEvidenceRedaction.SafePath(f.Type), false)).ToList())],
                EvidenceState = ArchitectureEvidenceState.StronglySupported, ParseSupport = DomainSupport.Partial,
                Limitations = [$"Implementation contract (C# class); formal schema: {e.FormalSchema}. Requiredness is not inferred from C#."],
            });
        }

        contracts.AddRange(GeneratedClients(context));

        context.Contracts = context.Envelope(new ContractEvidence
        {
            Status = contracts.Count == 0 ? diagnostics.Count > 0 ? SourceDomainStatus.Partial : SourceDomainStatus.NotDetected
                : contracts.All(c => c.ParseSupport == DomainSupport.Supported) && diagnostics.Count == 0 ? SourceDomainStatus.Complete : SourceDomainStatus.Partial,
            StatusReason = contracts.Count == 0 ? "No source contract detected. The API may still have a contract generated at runtime (e.g. served OpenAPI or GraphQL introspection)." : null,
            Technologies = [.. contracts.Select(c => SourceDomainText.Label(c.Type)).Distinct().Order(StringComparer.Ordinal)],
            Contracts = [.. contracts.OrderBy(c => c.Type).ThenBy(c => c.File, StringComparer.Ordinal)], Diagnostics = diagnostics,
            Limitations = [SourceDomainText.SourceBoundary, "A contract in source does not prove the deployed API or message flow is compatible with it; runtime schemas stay primary in API Quality Review.",
                "Breaking-change classification needs compatibility rules; changes are classified, never judged here."],
        }, SourceEvidenceDomain.Contracts, Version);
    }

    /// <summary>Producer/consumer by ownership: a contract inside an API project is produced by it (StronglySupported when Architecture shows the
    /// matching interface), inside a client project it is a consumer copy; outside every project it has no producer (Inferred hints only).</summary>
    private static SourceContract Attribute(SourceEvidenceContext context, EvidenceFile file, SourceContract contract)
    {
        var project = context.ProjectOf(file.Path);
        var component = context.ComponentOf(file.Path);
        var limitations = new List<string>(contract.Limitations);
        if (file.Role == SourceFileRole.Test || project?.IsTest == true)
            return contract with { ConsumerHints = ["Developer contract-test snapshot"], ProducerBasis = "Test project copy — not a producer", EvidenceState = ArchitectureEvidenceState.Inferred };
        var client = project is not null && (project.HasPackage("StrawberryShake") || project.HasPackage("NSwag") || project.HasPackage("Refitter") || project.HasPackage("Microsoft.Extensions.ApiDescription.Client"));
        var graphQlServer = project?.HasPackage("HotChocolate") == true;
        if (component is not null && client && !(contract.Type is SourceContractType.GraphQlSchema && graphQlServer))
            return contract with { ConsumerHints = [component.Name], ProducerBasis = "Client-side copy in a consuming component", EvidenceState = ArchitectureEvidenceState.StronglySupported };
        if (component is not null)
        {
            var served = contract.Type switch
            {
                SourceContractType.GraphQlSchema => context.Architecture?.Interfaces.Any(i => i.ComponentId == component.Id && i.Type.Contains("GraphQL", StringComparison.OrdinalIgnoreCase)) == true,
                SourceContractType.OpenApi => context.Architecture?.Interfaces.Any(i => i.ComponentId == component.Id && i.Type.Contains("REST", StringComparison.OrdinalIgnoreCase)) == true,
                _ => false,
            };
            return contract with { Producer = component.Name, ProducerBasis = served ? "Inside the component's project, which exposes the matching interface" : "Inside the component's project",
                EvidenceState = served ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Inferred };
        }
        // Outside every project: a module folder with exactly one component exposing the matching interface is a hint, never a confirmation.
        var module = file.Path.Contains('/') ? file.Path[..file.Path.IndexOf('/')] : "";
        var candidates = context.Architecture?.Components.Where(c => c.Module == module && context.Architecture.Interfaces.Any(i => i.ComponentId == c.Id
            && (contract.Type == SourceContractType.GraphQlSchema ? i.Type.Contains("GraphQL", StringComparison.OrdinalIgnoreCase) : contract.Type == SourceContractType.OpenApi && i.Type.Contains("REST", StringComparison.OrdinalIgnoreCase)))).ToList() ?? [];
        var docs = Regex.IsMatch(file.Path, @"(?i)(^|/)(docs?|specs?|autodoc|contracts?)(/|$)");
        return contract with
        {
            Producer = candidates.Count == 1 ? candidates[0].Name : null,
            ProducerBasis = candidates.Count == 1 ? $"Same module folder as the only component exposing this interface ({(docs ? "documentation/specification copy" : "shared folder")})"
                : docs ? "Documentation/specification copy — no owning project" : "No owning project",
            EvidenceState = ArchitectureEvidenceState.Inferred,
        };
    }

    private static SourceContract Base(EvidenceFile file, SourceContractType type, string format) => new()
    {
        Id = $"{type.ToString().ToLowerInvariant()}:{SourceEvidenceRedaction.SafePath(file.Path)}", Type = type, Name = SourceEvidenceRedaction.SafePath(System.IO.Path.GetFileNameWithoutExtension(file.Path)),
        File = SourceEvidenceRedaction.SafePath(file.Path), Line = 1, Format = format,
    };

    // ── OpenAPI ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    private static SourceContract? OpenApi(EvidenceFile file, OpenApiExtractor extractor)
    {
        if (file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var result = extractor.Extract(file.Content);
            if (result.Success && result.Contract is { } c)
            {
                using var doc = JsonDocument.Parse(file.Content);
                var version = doc.RootElement.TryGetProperty("info", out var info) && info.TryGetProperty("version", out var v) ? v.GetString() : null;
                return Base(file, SourceContractType.OpenApi, "json") with
                {
                    Name = SourceEvidenceRedaction.SafePath(c.Name), Version = version is null ? null : SourceEvidenceRedaction.SafePath(version),
                    Operations = c.Operations.Take(MaxOperations).Select(o => new ContractOperation($"{o.Method.ToUpperInvariant()} {SourceEvidenceRedaction.SafePath(o.Path)}", o.Method.ToUpperInvariant(), SourceEvidenceRedaction.SafePath(o.Path), [])).ToList(),
                    Types = c.Schemas.Take(MaxTypes).Select(s => new ContractTypeShape(SourceEvidenceRedaction.SafePath(s.Name), s.Type,
                        s.Properties.Select(p => new ContractField(SourceEvidenceRedaction.SafePath(p.Name), SourceEvidenceRedaction.SafePath(p.Type), p.Required || s.Required.Contains(p.Name))).ToList())).ToList(),
                    ParseSupport = DomainSupport.Supported,
                };
            }
            // Swagger 2 or an extractor failure: fall back to the generic reader below (Partial).
            using var json = JsonDocument.Parse(file.Content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return OpenApiTree(file, JsonTree(json.RootElement, 1), "json");
        }
        var yaml = MiniYaml.Parse(file.Content);
        return yaml is null ? null : OpenApiTree(file, yaml, "yaml");
    }

    private static SourceContract OpenApiTree(EvidenceFile file, YamlNode root, string format)
    {
        var operations = new List<ContractOperation>();
        foreach (var (path, item) in root["paths"]?.Entries ?? [])
            foreach (var method in Methods.Where(m => item.Has(m)))
                operations.Add(new ContractOperation($"{method.ToUpperInvariant()} {SourceEvidenceRedaction.SafePath(path)}", method.ToUpperInvariant(), SourceEvidenceRedaction.SafePath(path),
                    item[method]!.List("parameters").Select(p => p.Str("name")).OfType<string>().Select(SourceEvidenceRedaction.SafePath).ToList()));
        var schemas = root["components"]?["schemas"] ?? root["definitions"];
        var types = (schemas?.Entries ?? []).Take(MaxTypes).Select(e =>
        {
            var required = e.Value.Strings("required").ToHashSet(StringComparer.Ordinal);
            return new ContractTypeShape(SourceEvidenceRedaction.SafePath(e.Key), e.Value.Str("type") ?? "object",
                (e.Value["properties"]?.Entries ?? []).Select(p => new ContractField(SourceEvidenceRedaction.SafePath(p.Key), SourceEvidenceRedaction.SafePath(p.Value.Str("type") ?? (p.Value.Str("$ref") is { } r ? r.Split('/').Last() : "object")), required.Contains(p.Key))).ToList());
        }).ToList();
        return Base(file, SourceContractType.OpenApi, format) with
        {
            Name = SourceEvidenceRedaction.SafePath(root["info"]?.Str("title") ?? System.IO.Path.GetFileNameWithoutExtension(file.Path)),
            Version = root["info"]?.Str("version") is { } v ? SourceEvidenceRedaction.SafePath(v) : null,
            Operations = operations.Take(MaxOperations).ToList(), Types = types, ParseSupport = DomainSupport.Partial,
            Limitations = [root.Has("swagger") ? "Swagger 2 document read generically." : "YAML OpenAPI read by the subset reader; $ref chains are not resolved."],
        };
    }

    /// <summary>JSON as the same tree shape the YAML reader produces, so one OpenAPI/AsyncAPI walker serves both formats.</summary>
    private static YamlNode JsonTree(JsonElement e, int line)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new YamlNode { Kind = YamlKind.Map, Line = line };
                foreach (var p in e.EnumerateObject()) map.Entries.Add(new(p.Name, JsonTree(p.Value, line)));
                return map;
            case JsonValueKind.Array:
                var seq = new YamlNode { Kind = YamlKind.Seq, Line = line };
                foreach (var item in e.EnumerateArray()) seq.Items.Add(JsonTree(item, line));
                return seq;
            case JsonValueKind.String: return YamlNode.Scalar(e.GetString(), line);
            case JsonValueKind.Null: return YamlNode.Scalar(null, line);
            default: return YamlNode.Scalar(e.GetRawText(), line);
        }
    }

    private static YamlNode? Tree(EvidenceFile file)
    {
        if (!file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return MiniYaml.Parse(file.Content);
        using var json = JsonDocument.Parse(file.Content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return JsonTree(json.RootElement, 1);
    }

    private static SourceContract? AsyncApi(EvidenceFile file)
    {
        if (Tree(file) is not { } root) return null;
        var operations = new List<ContractOperation>();
        foreach (var (channel, item) in root["channels"]?.Entries ?? [])
            foreach (var kind in new[] { "publish", "subscribe" }.Where(item.Has))
                operations.Add(new ContractOperation($"{kind} {SourceEvidenceRedaction.SafePath(channel)}", kind, SourceEvidenceRedaction.SafePath(channel), []));
        foreach (var (name, op) in root["operations"]?.Entries ?? [])
            operations.Add(new ContractOperation($"{op.Str("action") ?? "operation"} {SourceEvidenceRedaction.SafePath(name)}", op.Str("action") ?? "operation", op["channel"]?.Str("$ref") is { } r ? SourceEvidenceRedaction.SafePath(r.Split('/').Last()) : null, []));
        if (operations.Count == 0) operations.AddRange((root["channels"]?.Entries ?? []).Select(c => new ContractOperation($"channel {SourceEvidenceRedaction.SafePath(c.Key)}", "channel", SourceEvidenceRedaction.SafePath(c.Key), [])));
        return Base(file, SourceContractType.AsyncApi, file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml") with
        {
            Name = SourceEvidenceRedaction.SafePath(root["info"]?.Str("title") ?? System.IO.Path.GetFileNameWithoutExtension(file.Path)), Version = root.Str("asyncapi") is { } v ? $"AsyncAPI {SourceEvidenceRedaction.SafePath(v)}" : null,
            Operations = operations.Take(MaxOperations).ToList(), ParseSupport = DomainSupport.Partial, Limitations = ["Message payload schemas are not resolved."],
        };
    }

    private static SourceContract? JsonSchema(EvidenceFile file)
    {
        if (Tree(file) is not { } root) return null;
        var required = root.Strings("required").ToHashSet(StringComparer.Ordinal);
        var name = root.Str("title") ?? System.IO.Path.GetFileNameWithoutExtension(file.Path);
        return Base(file, SourceContractType.JsonSchema, "json") with
        {
            Name = SourceEvidenceRedaction.SafePath(name), ParseSupport = DomainSupport.Partial, Limitations = ["$ref, allOf/oneOf and nested definitions are not resolved."],
            Types = [new ContractTypeShape(SourceEvidenceRedaction.SafePath(name), root.Str("type") ?? "object",
                (root["properties"]?.Entries ?? []).Select(p => new ContractField(SourceEvidenceRedaction.SafePath(p.Key), SourceEvidenceRedaction.SafePath(p.Value.Str("type") ?? "object"), required.Contains(p.Key))).ToList())],
        };
    }

    // ── GraphQL ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static SourceContract? GraphQl(EvidenceFile file, List<SourceDomainDiagnostic> diagnostics)
    {
        var document = Utf8GraphQLParser.Parse(file.Content);
        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        var fragments = document.Definitions.OfType<FragmentDefinitionNode>().Count();
        var typeSystem = document.Definitions.Where(d => d is ITypeSystemDefinitionNode or ITypeSystemExtensionNode).ToList();
        if (operations.Count > 0 && typeSystem.Count == 0)
            return Base(file, SourceContractType.GraphQlOperations, "graphql") with
            {
                Operations = operations.Take(MaxOperations).Select(o => new ContractOperation($"{o.Operation.ToString().ToLowerInvariant()} {o.Name?.Value ?? "(anonymous)"}", o.Operation.ToString().ToLowerInvariant(), null,
                    o.VariableDefinitions.Select(v => "$" + v.Variable.Name.Value).ToList())).ToList(),
                ParseSupport = DomainSupport.Supported, Limitations = fragments > 0 ? [$"{fragments} fragment(s) not expanded."] : [],
            };
        if (typeSystem.Count == 0) return null;
        var schema = GraphQlSdlSchema.FromSdl(file.Content, out var error);
        var types = new List<ContractTypeShape>();
        var ops = new List<ContractOperation>();
        if (schema is not null)
        {
            foreach (var t in schema.Types.Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal)).Take(MaxTypes))
            {
                var fields = t.Kind == "INPUT_OBJECT" ? t.InputFields ?? [] : t.Fields;
                types.Add(new ContractTypeShape(t.Name, t.Kind.ToLowerInvariant(), fields.Select(f => new ContractField(f.Name, TypeText(f.Type), f.Type?.Kind == "NON_NULL")).ToList()));
            }
            foreach (var (root, kind) in new[] { (schema.QueryTypeName, "query"), (schema.MutationTypeName, "mutation"), (schema.SubscriptionTypeName, "subscription") })
                if (root is not null && schema.Types.FirstOrDefault(t => t.Name == root) is { } rootType)
                    ops.AddRange(rootType.Fields.Select(f => new ContractOperation($"{kind} {f.Name}", kind, null, f.Arguments.Select(a => a.Name).ToList())));
        }
        else
        {
            // No query root (e.g. a schema split across files or client-side extensions only): list the declared types, Partial.
            foreach (var d in typeSystem.OfType<ComplexTypeDefinitionNodeBase>().Take(MaxTypes))
                types.Add(new ContractTypeShape(d.Name.Value, d is ObjectTypeExtensionNode or InterfaceTypeExtensionNode ? "extension" : "object",
                    d.Fields.Select(f => new ContractField(f.Name.Value, f.Type.ToString(), f.Type is NonNullTypeNode)).ToList()));
            foreach (var ext in typeSystem.OfType<ObjectTypeExtensionNode>().Where(x => x.Name.Value is "Query" or "Mutation" or "Subscription"))
                ops.AddRange(ext.Fields.Select(f => new ContractOperation($"{ext.Name.Value.ToLowerInvariant()} {f.Name.Value}", ext.Name.Value.ToLowerInvariant(), null, f.Arguments.Select(a => a.Name.Value).ToList())));
        }
        return Base(file, SourceContractType.GraphQlSchema, "graphql") with
        {
            Operations = ops.Take(MaxOperations).ToList(), Types = types, ParseSupport = schema is null ? DomainSupport.Partial : DomainSupport.Supported,
            Limitations = schema is null ? [$"Partial schema document: {SourceEvidenceRedaction.SafePath(error ?? "no query root")}"] : [],
        };
    }

    private static string TypeText(GraphQlTypeRef? type) => type is null ? "" : type.Kind switch
    {
        "NON_NULL" => TypeText(type.OfType) + "!",
        "LIST" => "[" + TypeText(type.OfType) + "]",
        _ => type.Name ?? "",
    };

    // ── Protobuf (pattern-based) ────────────────────────────────────────────────────────────────────────────────────────

    private static SourceContract Protobuf(EvidenceFile file)
    {
        var text = Regex.Replace(file.Content, @"//[^\n]*|/\*[\s\S]*?\*/", "");
        var types = Regex.Matches(text, @"\bmessage\s+(\w+)\s*\{([^{}]*)\}").Select(m => new ContractTypeShape(m.Groups[1].Value, "message",
            Regex.Matches(m.Groups[2].Value, @"(?m)^\s*(optional\s+|repeated\s+|required\s+)?([\w.]+)\s+(\w+)\s*=\s*\d+").Select(f =>
                new ContractField(f.Groups[3].Value, (f.Groups[1].Value.Trim() == "repeated" ? "repeated " : "") + f.Groups[2].Value, f.Groups[1].Value.Trim() == "required")).ToList())).Take(MaxTypes).ToList();
        var operations = Regex.Matches(text, @"\bservice\s+(\w+)\s*\{([\s\S]*?)\n\}").SelectMany(s => Regex.Matches(s.Groups[2].Value, @"\brpc\s+(\w+)\s*\(\s*(stream\s+)?([\w.]+)\s*\)")
            .Select(r => new ContractOperation($"rpc {s.Groups[1].Value}.{r.Groups[1].Value}", "rpc", null, [r.Groups[3].Value]))).Take(MaxOperations).ToList();
        var package = Regex.Match(text, @"\bpackage\s+([\w.]+)\s*;").Groups[1].Value;
        return Base(file, SourceContractType.Protobuf, "proto") with
        {
            Name = package.Length > 0 ? package : Base(file, SourceContractType.Protobuf, "proto").Name, Types = types, Operations = operations, ParseSupport = DomainSupport.Partial,
            Limitations = ["Nested messages, oneofs and imports are not resolved."],
        };
    }

    // ── Generated clients ───────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<SourceContract> GeneratedClients(SourceEvidenceContext context)
    {
        foreach (var file in context.Files.Where(f => f.Name is ".graphqlrc.json" or "nswag.json" or "openapitools.json" || f.Name.EndsWith(".nswag", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".refitter", StringComparison.OrdinalIgnoreCase)))
        {
            var component = context.ComponentOf(file.Path);
            var generator = file.Name == ".graphqlrc.json" ? "Strawberry Shake (GraphQL client)" : file.Name == "openapitools.json" ? "OpenAPI Generator" : file.Name.EndsWith(".refitter", StringComparison.OrdinalIgnoreCase) ? "Refitter" : "NSwag";
            yield return new SourceContract
            {
                Id = $"client:{SourceEvidenceRedaction.SafePath(file.Path)}", Type = SourceContractType.GeneratedClient, Name = generator, File = SourceEvidenceRedaction.SafePath(file.Path), Line = 1, Format = "json",
                ConsumerHints = component is null ? [] : [component.Name], ProducerBasis = "Client generator configuration — the generated client consumes a contract",
                EvidenceState = component is null ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Confirmed, ParseSupport = DomainSupport.Partial,
                Limitations = ["A generated client existing in source does not prove it is compatible with the deployed API."],
            };
        }
        foreach (var project in context.Input.Projects.Where(p => !p.IsTest))
        {
            var csproj = context.Files.FirstOrDefault(f => f.Path == project.Path);
            if (csproj is null) continue;
            foreach (Match m in Regex.Matches(csproj.Content, @"<OpenApiReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase))
            {
                var component = context.Architecture?.Components.FirstOrDefault(c => c.SourceProject == project.Path);
                yield return new SourceContract
                {
                    Id = $"client:{SourceEvidenceRedaction.SafePath(project.Path)}#{SourceEvidenceRedaction.SafePath(m.Groups[1].Value)}", Type = SourceContractType.GeneratedClient, Name = $"OpenApiReference {SourceEvidenceRedaction.SafePath(System.IO.Path.GetFileName(m.Groups[1].Value))}",
                    File = SourceEvidenceRedaction.SafePath(project.Path), Line = csproj.Line(m.Index), Format = "msbuild", ConsumerHints = component is null ? [] : [component.Name],
                    ProducerBasis = "OpenApiReference in project file — a generated client consumes this contract", EvidenceState = ArchitectureEvidenceState.Confirmed, ParseSupport = DomainSupport.Partial,
                };
            }
        }
    }
}
