using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Schemas;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractEvolution;

/// <summary>A flattened field of a structured contract (JSON Schema-like): path ("/" or "." separated), type, requiredness, enum values.</summary>
public sealed record ContractFieldShape(string Path, string? DataType, bool Required, IReadOnlyList<string>? EnumValues = null);

/// <summary>
/// Contract normalization and fingerprinting. Each contract type has a normalizer that turns the existing parser's output into
/// <see cref="ContractNode"/>s; nothing here parses a format itself. The fingerprint is SHA-256 over the canonical normalized form, so
/// formatting, comments and declaration-irrelevant ordering do not change it while every structural fact does.
/// </summary>
public static class ContractSnapshots
{
    /// <summary>Normalizer version: part of every fingerprint, so a normalization change is never mistaken for a contract change silently.</summary>
    public const int NormalizerVersion = 1;

    /// <summary>
    /// XSD normalizer version. 2 = the shared effective schema (inheritance, cross-file types, restrictions); an XSD fingerprint made by
    /// version 1 is therefore never equal to a version 2 one, so an old baseline shows as changed, never as silently equal.
    /// </summary>
    public const int XmlSchemaNormalizerVersion = 2;

    /// <summary>XSD: one schema file, read as a single-file package by the shared XSD capability (no DTD, no resolver, imports never fetched).</summary>
    public static ContractSnapshot FromXmlSchemaText(ContractDescriptor descriptor, string path, string xsd, DateTimeOffset capturedAt)
    {
        var name = path.Replace('\\', '/');
        return FromXmlSchemaPackage(descriptor, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = xsd }, name, capturedAt);
    }

    /// <summary>
    /// XSD package: normalizes the shared effective schema of the global elements declared in <paramref name="rootPath"/> — the same
    /// semantics Schema Studio and Test Data validation use. Inherited and imported members are expanded with their declaring file as
    /// provenance, and every package file is listed as a dependency with its fingerprint.
    /// </summary>
    public static ContractSnapshot FromXmlSchemaPackage(ContractDescriptor descriptor, IReadOnlyDictionary<string, string> files, string rootPath,
        DateTimeOffset capturedAt)
    {
        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        var rootName = rootPath.Replace('\\', '/');
        var rootFile = analysis.Files.FirstOrDefault(f => string.Equals(f.Name.Replace('\\', '/'), rootName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The root schema is not part of the package.", nameof(rootPath));
        var roots = analysis.EffectiveSchema.Where(root => string.Equals(root.DeclaredIn, rootFile.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roots.Count == 0) roots = [.. analysis.EffectiveSchema];
        var nodes = new List<ContractNode>();
        void Add(EffectiveMember member, string path, int position, bool required, string cardinality)
        {
            nodes.Add(new(path, "element", DataType(member), required, cardinality, Enumerations(member.Facets), member.NamespaceUri, position,
                Restrictions(member.Facets), member.DeclaredIn));
            foreach (var attribute in member.Attributes ?? [])
                nodes.Add(new($"{path}/@{attribute.Name}", "attribute",
                    attribute.QualifiedType ?? (attribute.BaseType is { } builtIn ? "#inline-simple:" + builtIn : "#inline-simple"),
                    attribute.Use == "required", attribute.Use == "required" ? "1..1" : "0..1", Enumerations(attribute.Facets),
                    attribute.NamespaceUri.Length == 0 ? null : attribute.NamespaceUri, 0, Restrictions(attribute.Facets), attribute.DeclaredIn));
            for (var i = 0; i < member.Children.Count && nodes.Count < 5000; i++)
            {
                var child = member.Children[i];
                Add(child, $"{path}/{child.Name}", i + 1, child.MinOccurs != "0" && !child.InChoice, $"{child.MinOccurs}..{child.MaxOccurs}");
            }
        }
        for (var i = 0; i < roots.Count; i++) Add(roots[i], roots[i].Name, i + 1, true, "1..1");
        foreach (var type in XmlSchemaPackageAnalyzer.SimpleTypes(analysis.Files))
            nodes.Add(new($"#type/{type.QualifiedName}", "simple-type", type.QualifiedBase, false, "", Enumerations(type.Facets), Namespace(type.QualifiedName), 0,
                Restrictions(type.Facets), type.DeclaredIn));
        return Create(descriptor with { Version = descriptor.Version ?? rootFile.Version }, rootFile.TargetNamespace, nodes, capturedAt,
            $"XML Schema read by the shared XSD capability ({(rootFile.Version is null ? "no explicit xs:schema version attribute" : "explicit xs:schema version attribute")}; package {analysis.Validation})")
            with { Dependencies = Dependencies(analysis, rootFile) };
    }

    /// <summary>Every package file (with fingerprint and who references it) plus every link that did not resolve, with its status.</summary>
    private static List<ContractSchemaDependency> Dependencies(XmlSchemaAnalysis analysis, SchemaFile rootFile)
    {
        var result = new List<ContractSchemaDependency>();
        foreach (var file in analysis.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var incoming = analysis.Files.SelectMany(f => f.Links.Where(l => string.Equals(l.ResolvedFile, file.Name, StringComparison.OrdinalIgnoreCase))
                .Select(l => (From: f.Name, l.Kind))).ToList();
            var kind = ReferenceEquals(file, rootFile) ? "root" : incoming.Count > 0 ? incoming[0].Kind.ToLowerInvariant() : "package";
            result.Add(new(file.Name, kind, file.TargetNamespace, file.Version, file.Fingerprint,
                incoming.Count == 0 ? null : string.Join(", ", incoming.Select(i => i.From).Distinct(StringComparer.Ordinal)), "Resolved"));
        }
        foreach (var file in analysis.Files)
            foreach (var link in file.Links.Where(l => l.ResolvedFile is null))
                result.Add(new(link.Location ?? link.Namespace ?? "(no location)", link.Kind.ToLowerInvariant(), link.Namespace, null, "", file.Name, link.EffectiveStatus.ToString()));
        return result;
    }

    private static string DataType(EffectiveMember member) => member.QualifiedType
        ?? (member.Children.Count > 0 || member.Attributes is { Count: > 0 } ? "#inline-complex"
            : member.BaseType is { } builtIn ? "#inline-simple:" + builtIn : "#inline-simple");

    /// <summary>Structured fields (a flattened JSON Schema or similar). Order is not significant for these formats.</summary>
    public static ContractSnapshot FromFields(ContractDescriptor descriptor, string? @namespace, IEnumerable<ContractFieldShape> fields, DateTimeOffset capturedAt, string evidence) =>
        Create(descriptor, @namespace, fields.Select(field => new ContractNode(NormalizePath(field.Path), "property", field.DataType, field.Required, field.Required ? "1..1" : "0..1",
            [.. (field.EnumValues ?? []).Order(StringComparer.Ordinal)], null, 0)).ToList(), capturedAt, evidence);

    /// <summary>OpenAPI: operations, their parameters and request body fields from the existing OpenAPI extractor's normalized contract.</summary>
    public static ContractSnapshot FromOpenApi(ContractDescriptor descriptor, NormalizedContract contract, DateTimeOffset capturedAt)
    {
        var nodes = new List<ContractNode>();
        foreach (var operation in contract.Operations)
        {
            var op = $"{operation.Method.ToUpperInvariant()} {operation.Path}";
            nodes.Add(new(op, "operation", null, true, "1..1", [], null, 0));
            foreach (var parameter in operation.Parameters)
                nodes.Add(new($"{op}/{parameter.Location.ToString().ToLowerInvariant()}/{parameter.Name}", "parameter", parameter.Type, parameter.Required,
                    parameter.Required ? "1..1" : "0..1", [.. (parameter.EnumValues ?? []).Order(StringComparer.Ordinal)], null, 0));
            foreach (var field in operation.RequestBody?.Fields ?? [])
                nodes.Add(new($"{op}/body/{field.Name}", "property", field.Type, field.Required, field.Required ? "1..1" : "0..1",
                    [.. (field.EnumValues ?? []).Order(StringComparer.Ordinal)], null, 0));
        }
        return Create(descriptor, null, nodes, capturedAt, "OpenAPI read by the existing OpenAPI extractor (operations, parameters, request fields)");
    }

    /// <summary>Builds the snapshot with nodes in canonical order and its fingerprint.</summary>
    public static ContractSnapshot Create(ContractDescriptor descriptor, string? @namespace, List<ContractNode> nodes, DateTimeOffset capturedAt, string evidence)
    {
        var ordered = nodes.GroupBy(node => node.Path, StringComparer.Ordinal).Select(group => group.First()).OrderBy(node => node.Path, StringComparer.Ordinal).ToList();
        return new ContractSnapshot(descriptor, @namespace, ordered, Fingerprint(descriptor.Type, descriptor.Version, @namespace, ordered), capturedAt, evidence);
    }

    /// <summary>SHA-256 (lowercase hex) of the canonical JSON of the normalized form: type, declared version, namespace and the ordered nodes.</summary>
    public static string Fingerprint(SourceContractType type, string? version, string? @namespace, IEnumerable<ContractNode> nodes)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            normalizer = type == SourceContractType.XmlSchema ? XmlSchemaNormalizerVersion : NormalizerVersion,
            type = type.ToString(),
            version = version ?? "",
            ns = @namespace ?? "",
            nodes = nodes.OrderBy(node => node.Path, StringComparer.Ordinal)
                .Select(node => node.Restrictions is { Count: > 0 } restrictions
                    ? new object?[] { node.Path, node.Kind, node.DataType, node.Required, node.Cardinality, node.EnumValues.Order(StringComparer.Ordinal).ToArray(), node.Namespace, node.Position, restrictions.Order(StringComparer.Ordinal).ToArray() }
                    : new object?[] { node.Path, node.Kind, node.DataType, node.Required, node.Cardinality, node.EnumValues.Order(StringComparer.Ordinal).ToArray(), node.Namespace, node.Position }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string NormalizePath(string path) => path.Replace('.', '/').Trim('/');

    private static List<string> Enumerations(IEnumerable<SchemaFacet> facets) =>
        [.. facets.Where(f => f.Kind == "enumeration").Select(f => f.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Non-enumeration facets (pattern, length, bounds, digits) as canonical "facet=value" strings: a format change is a structural fact.</summary>
    private static List<string> Restrictions(IEnumerable<SchemaFacet> facets) =>
        [.. facets.Where(f => f.Kind is not ("enumeration" or "whiteSpace")).Select(f => $"{f.Kind}={f.Value}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string? Namespace(string qualifiedName) =>
        qualifiedName.StartsWith('{') && qualifiedName.IndexOf('}') is var end and > 0 ? qualifiedName[1..end] : null;
}
