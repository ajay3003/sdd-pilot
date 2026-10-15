using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
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

    /// <summary>XSD: reads the schema with the canonical hardened reader (no DTD, no resolver, imports never fetched), then normalizes it.</summary>
    public static ContractSnapshot FromXmlSchemaText(ContractDescriptor descriptor, string path, string xsd, DateTimeOffset capturedAt) =>
        FromXmlSchema(descriptor, ContractAnalyzer.ReadXmlSchema(path, xsd), capturedAt);

    /// <summary>XSD: normalizes Source Analysis XML Schema evidence (elements, attributes, occurrence, types, enumerations, namespace).</summary>
    public static ContractSnapshot FromXmlSchema(ContractDescriptor descriptor, SourceContract contract, DateTimeOffset capturedAt)
    {
        var schema = contract.XmlSchema ?? throw new ArgumentException("The contract has no XML Schema evidence.", nameof(contract));
        var nodes = new List<ContractNode>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        int Next(string path)
        {
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            positions[parent] = positions.GetValueOrDefault(parent) + 1;
            return positions[parent];
        }
        foreach (var element in schema.Elements)
            nodes.Add(new(element.Path, "element", element.Type, element.MinOccurs > 0 && !element.Compositor.Contains("choice", StringComparison.Ordinal),
                $"{element.MinOccurs.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{element.MaxOccurs}", Enumerations(element.Restrictions),
                Namespace(element.QualifiedName), Next(element.Path)));
        foreach (var attribute in schema.Attributes)
            nodes.Add(new(attribute.Path, "attribute", attribute.Type, string.Equals(attribute.Use, "required", StringComparison.Ordinal),
                string.Equals(attribute.Use, "required", StringComparison.Ordinal) ? "1..1" : "0..1", Enumerations(attribute.Restrictions), Namespace(attribute.QualifiedName), 0));
        foreach (var type in schema.Types.Where(t => t.Kind == "simpleType"))
            nodes.Add(new($"#type/{type.QualifiedName}", "simple-type", type.BaseType, false, "", Enumerations(type.Restrictions), Namespace(type.QualifiedName), 0));
        return Create(descriptor with { Version = descriptor.Version ?? schema.ExplicitVersion }, schema.TargetNamespace, nodes, capturedAt,
            $"XML Schema read by the canonical schema reader ({schema.VersionEvidence})");
    }

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
            normalizer = NormalizerVersion,
            type = type.ToString(),
            version = version ?? "",
            ns = @namespace ?? "",
            nodes = nodes.OrderBy(node => node.Path, StringComparer.Ordinal)
                .Select(node => new object?[] { node.Path, node.Kind, node.DataType, node.Required, node.Cardinality, node.EnumValues.Order(StringComparer.Ordinal).ToArray(), node.Namespace, node.Position }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string NormalizePath(string path) => path.Replace('.', '/').Trim('/');

    private static List<string> Enumerations(IEnumerable<XmlSchemaRestrictionEvidence> restrictions) =>
        [.. restrictions.Where(r => r.Facet == "enumeration").Select(r => r.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string? Namespace(string qualifiedName) =>
        qualifiedName.StartsWith('{') && qualifiedName.IndexOf('}') is var end and > 0 ? qualifiedName[1..end] : null;
}
