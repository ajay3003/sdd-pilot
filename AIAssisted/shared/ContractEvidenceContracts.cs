using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.Schemas;
using BirkNext.SourceDomains;

namespace BirkNext.ContractSchemaEvidence;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared Contract & Schema Evidence. Providers (the shared XSD package engine, the Source Analysis contract reader, OpenAPI, JSON Schema,
// GraphQL SDL, Test Pack reference metadata) produce one normalized model — SourceContract / XmlSchemaEvidence — stored once per immutable
// source snapshot. Every consumer (Schema Studio, Source Analysis, Integrations/IQR, Topology, AQR, Test Data, Test Coverage, Impact,
// traceability, documentation) reads these queries instead of re-parsing. States stay apart:
// Declared ≠ Selected ≠ Bound ≠ Validated payload ≠ Runtime-observed ≠ Compatible ≠ Accepted downstream.
// A file-name match never confirms a relationship and the newest version is never chosen because it is newest.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractCapabilitySupport { Supported, Partial, NotSupported, SeparateEvidence }

/// <summary>What one contract evidence provider can do, per capability — detection is not parsing, parsing is not semantic structure,
/// and runtime validation is always separate evidence.</summary>
public sealed record ContractEvidenceProviderDescriptor(string Key, string Name, IReadOnlyList<string> Formats, string Owner,
    ContractCapabilitySupport Detection, ContractCapabilitySupport Parse, ContractCapabilitySupport ReferenceResolution, ContractCapabilitySupport SemanticStructure,
    ContractCapabilitySupport Comparison, ContractCapabilitySupport TestGeneration, ContractCapabilitySupport RuntimeValidation, string Proves, string CannotProve);

public static class ContractEvidenceProviders
{
    private const ContractCapabilitySupport S = ContractCapabilitySupport.Supported, P = ContractCapabilitySupport.Partial,
        N = ContractCapabilitySupport.NotSupported, X = ContractCapabilitySupport.SeparateEvidence;

    public static IReadOnlyList<ContractEvidenceProviderDescriptor> All { get; } =
    [
        new("xsd-package", "XSD package (compiled)", ["XML Schema bundle (xs:import / xs:include / xs:redefine)"], "Shared XSD engine (Schema Studio, Test Data, Contract Evolution)",
            S, S, S, S, S, S, X,
            "Compiled package: imports/includes resolved inside the package, QName-resolved effective structure (refs, extensions, restrictions), facets, cycles, unresolved references, declared version.",
            "Remote schema locations are never fetched. Instance validation is separate test evidence; it never marks the contract verified."),
        new("xsd-source", "XSD in source snapshot", ["XML Schema"], "Source Analysis",
            S, P, P, P, S, N, X,
            "Per file: target namespace, declared version, global and nested elements, occurrences, facets, named types, imports/includes (file presence in the snapshot), code references.",
            "Referenced types are not expanded and the package is not compiled; open the package in Schema Studio for the effective structure."),
        new("openapi", "OpenAPI", ["OpenAPI 3.x (JSON)", "OpenAPI 3.x (YAML, heuristic)", "Swagger 2 (heuristic)"], "Source Analysis · API Quality Review",
            S, S, P, P, S, N, X,
            "Title, info.version (the document's version — not the application or deployment version), servers, operations, parameters, request/response schemas, content types, security schemes.",
            "Runtime endpoint behaviour; declared security is not verified authentication. Server values never overwrite Target Environment configuration."),
        new("json-schema", "JSON Schema", ["JSON Schema"], "Source Analysis · Integrations",
            S, P, N, P, S, N, X,
            "Top-level properties, required, types and enums.",
            "$ref, oneOf/anyOf/allOf and nested definitions are not resolved."),
        new("graphql-sdl", "GraphQL schema (static)", ["GraphQL SDL"], "Source Analysis · API Quality Review",
            S, S, S, S, S, N, X,
            "Types, fields, arguments, queries, mutations, subscriptions and directives from SDL.",
            "Runtime introspection is a separate evidence source; a static schema does not prove the served schema."),
        new("graphql-introspection", "GraphQL introspection (runtime)", ["Introspection result"], "API Quality Review",
            N, S, N, S, S, N, X,
            "The schema a running endpoint served when introspection was allowed.",
            "Not source evidence; unavailable introspection is not a missing schema."),
        new("asyncapi", "AsyncAPI", ["AsyncAPI"], "Source Analysis",
            S, P, N, N, P, N, X,
            "Channels and operations.",
            "Payload schemas are not resolved; no message validation."),
        new("protobuf", "Protobuf", ["Protocol Buffers"], "Source Analysis",
            S, P, N, P, P, N, X,
            "Messages, fields and services by pattern.",
            "Imports and options are not compiled."),
        new("test-pack-reference", "Test Pack reference metadata", ["Reference facts (data type, content type, task)"], "Integration templates",
            N, S, N, N, N, N, X,
            "Safe declared metadata: application route, data type, content type, process task, token provider.",
            "Not a Source Analysis snapshot, not an IQR source binding and not runtime evidence; it never proves the consumer schema or authorization setup."),
    ];

    public static ContractEvidenceProviderDescriptor Get(string key) => All.First(p => p.Key == key);

    public static string ProviderFor(SourceContractType type) => type switch
    {
        SourceContractType.XmlSchema => "xsd-source",
        SourceContractType.OpenApi => "openapi",
        SourceContractType.JsonSchema => "json-schema",
        SourceContractType.GraphQlSchema or SourceContractType.GraphQlOperations => "graphql-sdl",
        SourceContractType.AsyncApi => "asyncapi",
        SourceContractType.Protobuf => "protobuf",
        _ => "source-analysis",
    };

    public static string Label(ContractCapabilitySupport support) => support switch
    {
        ContractCapabilitySupport.Supported => "Supported",
        ContractCapabilitySupport.Partial => "Partial",
        ContractCapabilitySupport.SeparateEvidence => "Separate evidence",
        _ => "Not supported",
    };
}

// ── Normalized facts ────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractParseStatus { Parsed, Partial, Failed, ReferenceUnresolved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractUsageKind { PublishedApi, MessageSchema, ReusableLibrary, ValidationSchema, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractEntryPointState { SingleMessageRoot, MultipleCandidateRoots, ReusableDefinitions, NotApplicable }

/// <summary>Where a fact comes from: snapshot, archive, file and line, format, document identity, namespace/version, element/operation path,
/// provider and analyzer version, and the evidence state. Every normalized fact carries one.</summary>
public sealed record ContractProvenance(Guid SnapshotId, string Source, string File, int Line, string Format, string DocumentId, string? Namespace,
    string? Version, string? Path, string Provider, int AnalyzerVersion, string EvidenceState);

/// <summary>One snapshot's stored contract evidence. Read, never re-parsed or mutated.</summary>
public sealed record ContractEvidenceSource(Guid SnapshotId, string Source, DateTimeOffset AnalyzedAt, ContractEvidence Evidence);

public sealed record ContractEntryPoint(ContractEntryPointState State, IReadOnlyList<string> Roots, string Text);
public sealed record ContractVersionFact(string Value, string Kind);
public sealed record ContractReferenceFact(string Kind, string? Namespace, string? Location, bool Resolved, ContractProvenance Provenance);
public sealed record ContractOperationFact(string Name, string Kind, string? Path, IReadOnlyList<string> Parameters, ContractProvenance Provenance);
public sealed record ContractFieldFact(string Path, string Name, string? Type, bool Required, bool Repeating, IReadOnlyList<string> AllowedValues, string? Pattern,
    string? Minimum, string? Maximum, string? MinLength, string? MaxLength, string? Documentation, ContractProvenance Provenance);
public sealed record ContractConstraintFact(string Path, string Kind, string Value, string PlainText, ContractProvenance Provenance);

/// <summary>A stored contract with its normalized interpretation. The <see cref="Contract"/> is the stored source evidence itself.</summary>
public sealed record ContractFact(SourceContract Contract, ContractProvenance Provenance, ContractParseStatus Parse, ContractUsageKind Usage,
    ContractEntryPoint EntryPoint, IReadOnlyList<ContractVersionFact> Versions, IReadOnlyList<ContractReferenceFact> UnresolvedReferences)
{
    public string Id => Contract.Id;
    public string Name => Contract.Name;
}

public sealed record ContractFormatCount(string Format, int Count);
public sealed record ContractSummary(int Total, IReadOnlyList<ContractFormatCount> Formats, int Parsed, int Partial, int Failed, int NeedsReview,
    int UnresolvedReferences, int VersionGroups);

/// <summary>Several versions of one contract identity in the given sources — all kept; none is selected because it is newest.</summary>
public sealed record ContractVersionGroup(string Identity, IReadOnlyList<ContractFact> Members, string Text);

public static class ContractEvidenceQueries
{
    private static readonly Regex VersionToken = new(@"(?:^|[._\-])v?(\d+(?:\.\d+){1,3})(?=$|[._\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Label(SourceContractType type) => type switch
    {
        SourceContractType.XmlSchema => "XSD",
        SourceContractType.OpenApi => "OpenAPI",
        SourceContractType.JsonSchema => "JSON Schema",
        SourceContractType.GraphQlSchema => "GraphQL schema",
        SourceContractType.GraphQlOperations => "GraphQL operations",
        SourceContractType.AsyncApi => "AsyncAPI",
        SourceContractType.Protobuf => "Protobuf",
        SourceContractType.MessageContract => "Message contract",
        _ => "Generated client",
    };

    public static string Label(ContractParseStatus status) => status switch
    {
        ContractParseStatus.Parsed => "Parsed",
        ContractParseStatus.Partial => "Partial",
        ContractParseStatus.ReferenceUnresolved => "Reference unresolved",
        _ => "Failed",
    };

    public static string Label(ContractUsageKind usage) => usage switch
    {
        ContractUsageKind.PublishedApi => "Published API",
        ContractUsageKind.MessageSchema => "Message schema",
        ContractUsageKind.ReusableLibrary => "Reusable library schema",
        ContractUsageKind.ValidationSchema => "Validation schema",
        _ => "Unknown usage",
    };

    /// <summary>Every stored contract of a snapshot, normalized. Generated clients are kept (they are evidence) but are not contracts of their own.</summary>
    public static IReadOnlyList<ContractFact> GetContracts(ContractEvidenceSource source) =>
        [.. source.Evidence.Contracts.Select(c => Fact(source, c))];

    public static ContractFact Fact(ContractEvidenceSource source, SourceContract c)
    {
        var provenance = Provenance(source.SnapshotId, source.Source, source.Evidence.AnalyzerVersion, c);
        var unresolved = (c.XmlSchema?.Dependencies ?? []).Where(d => !d.ResolvedInSnapshot)
            .Select(d => new ContractReferenceFact(d.Kind, d.Namespace, d.SchemaLocation, false, provenance with { Path = d.SchemaLocation })).ToList();
        var parse = c.ParseSupport == DomainSupport.Unsupported ? ContractParseStatus.Failed
            : unresolved.Count > 0 ? ContractParseStatus.ReferenceUnresolved
            : c.ParseSupport == DomainSupport.Partial ? ContractParseStatus.Partial : ContractParseStatus.Parsed;
        var entry = EntryPoint(c);
        return new(c, provenance, parse, Usage(c, entry), entry, Versions(c), unresolved);
    }

    public static ContractProvenance Provenance(Guid snapshotId, string source, int analyzerVersion, SourceContract c) =>
        new(snapshotId, source, c.File, c.Line, c.Format, c.Id, c.XmlSchema?.TargetNamespace, c.Version, null, ContractEvidenceProviders.ProviderFor(c.Type),
            analyzerVersion, c.EvidenceState.ToString());

    /// <summary>Decision-first counts from the stored evidence. A parse failure is a separate diagnostic (the contract is not in the list);
    /// it is counted, and one failure never hides the other contracts.</summary>
    public static ContractSummary Summary(ContractEvidenceSource source)
    {
        var facts = GetContracts(source).Where(f => f.Contract.Type != SourceContractType.GeneratedClient).ToList();
        var failed = source.Evidence.Diagnostics.Count(d => d.Kind == "Parse error");
        return new(facts.Count + failed,
            [.. facts.GroupBy(f => Label(f.Contract.Type)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new ContractFormatCount(g.Key, g.Count()))],
            facts.Count(f => f.Parse == ContractParseStatus.Parsed), facts.Count(f => f.Parse is ContractParseStatus.Partial or ContractParseStatus.ReferenceUnresolved), failed,
            facts.Count(NeedsReview), facts.Sum(f => f.UnresolvedReferences.Count), VersionGroups(facts).Count);
    }

    /// <summary>Review is needed for an unresolved reference, several candidate message roots or a partial parse — normal technical
    /// complexity, never a defect.</summary>
    public static bool NeedsReview(ContractFact f) =>
        f.UnresolvedReferences.Count > 0 || f.EntryPoint.State == ContractEntryPointState.MultipleCandidateRoots || f.Parse == ContractParseStatus.Partial;

    /// <summary>Message entry point of an XML Schema from its global elements: one is the entry point; several are candidates that need
    /// selection (none is chosen); none is a reusable definitions library — not a broken schema.</summary>
    public static ContractEntryPoint EntryPoint(SourceContract c)
    {
        if (c.XmlSchema is not { } xsd) return new(ContractEntryPointState.NotApplicable, [], c.Operations.Count > 0 ? $"{c.Operations.Count} operation(s)" : "No message entry point applies");
        var roots = xsd.Elements.Where(e => !e.Path.Contains('/')).Select(e => e.Path).Distinct(StringComparer.Ordinal).ToList();
        return roots.Count switch
        {
            0 => new(ContractEntryPointState.ReusableDefinitions, [], "Reusable schema definitions · no message entry point detected"),
            1 => new(ContractEntryPointState.SingleMessageRoot, roots, $"Message entry point: {roots[0]}"),
            _ => new(ContractEntryPointState.MultipleCandidateRoots, roots, $"{roots.Count} candidate message roots · needs selection"),
        };
    }

    /// <summary>Usage only where source evidence supports it: an API description format, a schema loaded by validator code, a schema with a
    /// message root or a definitions library; otherwise Unknown usage.</summary>
    public static ContractUsageKind Usage(SourceContract c, ContractEntryPoint entry) => c.Type switch
    {
        SourceContractType.OpenApi or SourceContractType.GraphQlSchema or SourceContractType.AsyncApi => ContractUsageKind.PublishedApi,
        SourceContractType.MessageContract => ContractUsageKind.MessageSchema,
        SourceContractType.XmlSchema or SourceContractType.JsonSchema when c.CodeReferences.Any(r => r.Kind == "Validator") => ContractUsageKind.ValidationSchema,
        SourceContractType.XmlSchema when entry.State == ContractEntryPointState.ReusableDefinitions => ContractUsageKind.ReusableLibrary,
        SourceContractType.XmlSchema when entry.State == ContractEntryPointState.SingleMessageRoot => ContractUsageKind.MessageSchema,
        _ => ContractUsageKind.Unknown,
    };

    /// <summary>Every version concept the source states, each with its own meaning — never collapsed into one "version".</summary>
    public static IReadOnlyList<ContractVersionFact> Versions(SourceContract c)
    {
        var result = new List<ContractVersionFact>();
        if (c.Type == SourceContractType.OpenApi && c.Version is { } info) result.Add(new(info, "OpenAPI info.version (document version — not the application or deployment version)"));
        else if (c.XmlSchema?.ExplicitVersion is { } declared) result.Add(new(declared, "xs:schema version attribute"));
        else if (c.Version is { } other) result.Add(new(other, "Declared contract version"));
        if (FileVersion(c.File) is { } file && result.All(r => r.Value != file)) result.Add(new(file, "Version in file name"));
        return result;
    }

    public static string? FileVersion(string path)
    {
        var m = VersionToken.Match(System.IO.Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex UnderscoreVersion = new(@"(?:^|_)v(\d+(?:_\d+){1,3})(?=$|_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The version a declared name states ("…_v2_0_0_altinn" → 2.0.0, "….v2.0.1.flat" → 2.0.1), or null. Only what the name says.</summary>
    public static string? NameVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (FileVersion(name) is { } dotted) return dotted;
        var m = UnderscoreVersion.Match(name);
        return m.Success ? m.Groups[1].Value.Replace('_', '.') : null;
    }

    /// <summary>Contract identity beyond the file name: format, namespace without version segments and the contract's own name (OpenAPI title,
    /// schema name) without version tokens. Many services' "openapi.yaml" files are different contracts, not versions of one.</summary>
    public static string Identity(SourceContract c)
    {
        var stem = VersionToken.Replace(c.Name.Length > 0 ? c.Name : System.IO.Path.GetFileNameWithoutExtension(c.File), "").Trim('.', '_', '-', ' ').ToLowerInvariant();
        var ns = Regex.Replace(c.XmlSchema?.TargetNamespace ?? "", @"[/:]v?\d+(\.\d+)*", "", RegexOptions.CultureInvariant).ToLowerInvariant();
        return $"{c.Type}|{ns}|{stem}";
    }

    /// <summary>Groups of several versions of the same contract identity. Members are ordered by path, never by version or date: nothing
    /// is selected automatically.</summary>
    public static IReadOnlyList<ContractVersionGroup> VersionGroups(IEnumerable<ContractFact> facts) =>
        [.. facts.Where(f => f.Contract.Type is SourceContractType.XmlSchema or SourceContractType.JsonSchema or SourceContractType.OpenApi)
            .GroupBy(f => Identity(f.Contract))
            .Where(g => g.Select(f => f.Versions.FirstOrDefault()?.Value ?? f.Contract.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => new ContractVersionGroup(g.Key, [.. g.OrderBy(f => f.Contract.File, StringComparer.Ordinal)],
                $"{g.Count()} versions ({string.Join(", ", g.Select(f => f.Versions.FirstOrDefault()?.Value ?? "unversioned").Distinct())}) · no version is selected automatically"))];

    public static IReadOnlyList<ContractOperationFact> GetOperations(ContractFact fact) =>
        [.. fact.Contract.Operations.Select(o => new ContractOperationFact(o.Name, o.Kind, o.Path, o.Parameters, fact.Provenance with { Path = o.Path ?? o.Name }))];

    /// <summary>Fields with their declared rules: XSD elements (required = minOccurs &gt; 0 outside a choice) or the types of an API/JSON contract.</summary>
    public static IReadOnlyList<ContractFieldFact> GetFields(ContractFact fact)
    {
        if (fact.Contract.XmlSchema is { } xsd)
            return [.. xsd.Elements.Select(e =>
            {
                string? Facet(params string[] kinds) => e.Restrictions.FirstOrDefault(r => kinds.Contains(r.Facet))?.Value;
                return new ContractFieldFact(e.Path, e.Path.Split('/').Last(), e.Type, e.MinOccurs > 0 && !e.Compositor.Contains("choice", StringComparison.Ordinal),
                    e.MaxOccurs == "unbounded" || (decimal.TryParse(e.MaxOccurs, NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && max > 1),
                    [.. e.Restrictions.Where(r => r.Facet == "enumeration").Select(r => r.Value)], Facet("pattern"), Facet("minInclusive", "minExclusive"),
                    Facet("maxInclusive", "maxExclusive"), Facet("minLength", "length"), Facet("maxLength", "length"), e.Documentation, fact.Provenance with { Path = e.Path });
            })];
        return [.. fact.Contract.Types.SelectMany(t => t.Fields.Select(f => new ContractFieldFact($"{t.Name}.{f.Name}", f.Name, f.Type, f.Required, f.Type.EndsWith("[]", StringComparison.Ordinal),
            [], null, null, null, null, null, null, fact.Provenance with { Path = $"{t.Name}.{f.Name}" })))];
    }

    /// <summary>Declared rules in plain language first ("Required field", "Allowed values"), the schema term second.</summary>
    public static IReadOnlyList<ContractConstraintFact> GetConstraints(ContractFact fact) =>
        [.. GetFields(fact).SelectMany(f => new[]
        {
            f.Required ? new ContractConstraintFact(f.Path, "Required", "minOccurs ≥ 1", "Required field", f.Provenance) : null,
            f.AllowedValues.Count > 0 ? new ContractConstraintFact(f.Path, "Allowed values", string.Join(", ", f.AllowedValues), $"Allowed values: {string.Join(", ", f.AllowedValues)}", f.Provenance) : null,
            f.Pattern is { } p ? new ContractConstraintFact(f.Path, "Pattern", p, "Must match a declared pattern", f.Provenance) : null,
            f.Minimum is not null || f.Maximum is not null ? new ContractConstraintFact(f.Path, "Range", $"{f.Minimum ?? "…"}..{f.Maximum ?? "…"}", $"Value between {f.Minimum ?? "no minimum"} and {f.Maximum ?? "no maximum"}", f.Provenance) : null,
            f.MinLength is not null || f.MaxLength is not null ? new ContractConstraintFact(f.Path, "Length", $"{f.MinLength ?? "0"}..{f.MaxLength ?? "…"}", $"Length between {f.MinLength ?? "0"} and {f.MaxLength ?? "no maximum"}", f.Provenance) : null,
        }.OfType<ContractConstraintFact>())];

    /// <summary>The contract's dependency edges: imports/includes/redefines (resolved or not) and named-type base relationships.</summary>
    public static IReadOnlyList<ContractReferenceFact> GetDependencies(ContractFact fact) =>
        [.. (fact.Contract.XmlSchema?.Dependencies ?? []).Select(d => new ContractReferenceFact(d.Kind, d.Namespace, d.SchemaLocation, d.ResolvedInSnapshot, fact.Provenance with { Path = d.SchemaLocation })),
         .. (fact.Contract.XmlSchema?.Types ?? []).Where(t => t.BaseType is not null && !t.BaseType.StartsWith("{http://www.w3.org/2001/XMLSchema}", StringComparison.Ordinal))
            .Select(t => new ContractReferenceFact("extends", null, t.BaseType, true, fact.Provenance with { Path = t.QualifiedName }))];
}

// ── XSD provider projection ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Projects the shared compiled XSD engine's package analysis into the stored contract shape (<see cref="XmlSchemaEvidence"/>), so a
/// package opened in Schema Studio and a contract stored by Source Analysis are compared by the same engine. Names are namespace-URI
/// qualified ("{uri}local"): a prefix change alone is not a contract change. Recursion is cut by the engine and bounded here.
/// </summary>
public static class XmlSchemaContractProjection
{
    private const int MaxDepth = 24, MaxElements = 5000;

    public static SourceContract FromPackage(string name, IReadOnlyList<SchemaFile> files, IReadOnlyList<EffectiveMember> effective, SchemaIdentity? identity = null)
    {
        var primary = identity?.PrimaryFile ?? XmlSchemaPackageAnalyzer.PackageRoots(files).FirstOrDefault()?.Name ?? files.FirstOrDefault()?.Name ?? name;
        var elements = new List<XmlSchemaElementEvidence>();
        void Walk(EffectiveMember m, string parent, int depth)
        {
            if (depth > MaxDepth || elements.Count >= MaxElements) return;
            var path = parent.Length == 0 ? m.Name : $"{parent}/{m.Name}";
            var min = decimal.TryParse(m.MinOccurs, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 1m;
            elements.Add(new(path, $"{{{m.NamespaceUri}}}{m.Name}", m.QualifiedType ?? m.Type, min, m.MaxOccurs, m.Nillable, m.DefaultValue, m.FixedValue, m.Documentation,
                [.. m.Facets.Select(f => new XmlSchemaRestrictionEvidence(f.Kind, f.Value))], m.InChoice ? "choice" : m.Compositor ?? "sequence"));
            foreach (var child in m.Children) Walk(child, path, depth + 1);
        }
        foreach (var root in effective) Walk(root, "", 0);
        var types = files.SelectMany(f => f.Declarations.Where(d => d.Kind is "complexType" or "simpleType")
            .Select(d => new XmlSchemaTypeEvidence($"{{{f.TargetNamespace ?? ""}}}{d.Name}", d.Kind, d.BaseType, [.. d.Facets.Select(x => new XmlSchemaRestrictionEvidence(x.Kind, x.Value))]))).ToList();
        var dependencies = files.SelectMany(f => f.Links.Select(l => new XmlSchemaDependencyEvidence(l.Kind switch { "Imported" => "import", "Included" => "include", "Redefined" => "redefine", _ => l.Kind.ToLowerInvariant() },
            l.Namespace, l.Location, l.EffectiveStatus is SchemaDependencyStatus.Resolved or SchemaDependencyStatus.Circular))).ToList();
        var primaryFile = files.FirstOrDefault(f => f.Name == primary);
        return new SourceContract
        {
            Id = $"xsd:{primary}", Type = SourceContractType.XmlSchema, Name = name, Version = identity?.Version ?? primaryFile?.Version, File = primary, Line = 1, Format = "xml",
            XmlSchema = new(identity?.TargetNamespace ?? primaryFile?.TargetNamespace, identity?.Version ?? primaryFile?.Version,
                identity?.Version is null ? "No explicit xs:schema version attribute." : "Explicit xs:schema version attribute.", elements, [], types, dependencies),
            ParseSupport = DomainSupport.Supported,
            Limitations = ["Projected from the compiled XSD package (shared XSD engine). Remote schema locations are never fetched."],
        };
    }
}

// ── Comparison (one engine for Schema Studio Compare and Impact Analysis) ───────────────────────────────────────────────

/// <summary>One normalized contract change with a cautious classification: Potential breaking change, Non-breaking candidate or Review required.
/// Never "breaking" as a verdict: context (direction, conditional semantics) may change the effect.</summary>
public sealed record ContractChangeFact(SourceEvidenceChangeKind Kind, string Area, string Path, string Detail, ContractChangeClass Class, string Classification);

public static class ContractComparison
{
    public const string PotentialBreaking = "Potential breaking change", NonBreakingCandidate = "Non-breaking candidate", ReviewRequired = "Review required";

    public static IReadOnlyList<ContractChangeFact> Compare(SourceContract before, SourceContract after) =>
        [.. SourceEvidenceDiff.CompareContract(before, after).Select(c => new ContractChangeFact(c.Kind, c.Area, c.Key.Contains('|') ? c.Key[(c.Key.IndexOf('|') + 1)..] : c.Key,
            c.Detail, c.ContractChange, Classify(c.ContractChange, c.CompatibilityConcern)))];

    public static string Classify(ContractChangeClass change, ContractCompatibilityConcern? concern) => concern switch
    {
        ContractCompatibilityConcern.PotentiallyBreaking => PotentialBreaking,
        ContractCompatibilityConcern.PotentiallyCompatible => NonBreakingCandidate,
        ContractCompatibilityConcern.NeedsReview => ReviewRequired,
        _ => change switch
        {
            ContractChangeClass.OperationRemoved or ContractChangeClass.FieldRemoved or ContractChangeClass.FieldAddedRequired or ContractChangeClass.TypeRemoved or ContractChangeClass.EnumValueRemoved => PotentialBreaking,
            ContractChangeClass.OperationAdded or ContractChangeClass.FieldAddedOptional or ContractChangeClass.TypeAdded or ContractChangeClass.EnumValueAdded => NonBreakingCandidate,
            _ => ReviewRequired,
        },
    };
}

// ── Producer / consumer relationships ───────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractRelationshipReviewState { NotAssessed, Candidate, StrongCandidate, NeedsReview, Confirmed, Ambiguous, NoMatchingEvidence, Bound, RuntimeObserved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractMatchStrength { ExplicitUsage, Strong, Supporting, Weak }

/// <summary>What the producer side declares (e.g. a Test Pack reference or an OpenAPI request body): origin, data type, content type, root,
/// namespace, version, task. A declaration, never the consumer's schema.</summary>
public sealed record ContractDeclaration(string Origin, string? Name = null, string? DataType = null, string? ContentType = null, string? Namespace = null,
    string? Version = null, string? Task = null);

public sealed record ContractMatch(string Kind, string Text, ContractMatchStrength Strength);
public sealed record ContractCandidateAssessment(string ContractId, string Name, string File, string? Version, Guid SnapshotId, IReadOnlyList<ContractMatch> Matches,
    ContractRelationshipReviewState State, string Label);

/// <summary>Relationship review between a producer declaration and consumer schema candidates. <see cref="RuntimeValidation"/> is always
/// separate: source evidence never proves that the downstream system accepts a payload.</summary>
public sealed record ContractRelationshipAssessment(ContractDeclaration? Producer, IReadOnlyList<ContractCandidateAssessment> Candidates,
    ContractRelationshipReviewState State, string Label, string Reason, IReadOnlyList<string> Differences, string RuntimeValidation = ContractRelationships.RuntimeNotAssessed);

public static class ContractRelationships
{
    public const string RuntimeNotAssessed = "Not assessed — source contract evidence is not runtime validation";

    public static string Label(ContractRelationshipReviewState state) => state switch
    {
        ContractRelationshipReviewState.Candidate => "Candidate · needs review",
        ContractRelationshipReviewState.StrongCandidate => "Strong candidate · needs review",
        ContractRelationshipReviewState.NeedsReview => "Needs review",
        ContractRelationshipReviewState.Confirmed => "Confirmed source usage",
        ContractRelationshipReviewState.Ambiguous => "Ambiguous · needs selection",
        ContractRelationshipReviewState.NoMatchingEvidence => "No matching evidence",
        ContractRelationshipReviewState.Bound => "Bound by explicit selection",
        ContractRelationshipReviewState.RuntimeObserved => "Runtime observed",
        _ => "Not assessed",
    };

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9æøå]", "");

    /// <summary>
    /// Assesses consumer schema candidates against a producer declaration. Exact root = data type and exact namespace are strong; a matching
    /// content type only supports; a file-name resemblance is weak and alone only ever yields a Candidate. Confirmed source usage needs a
    /// validator/handler reference to the file plus a strong identity match — and is still not runtime validation. Several strong candidates
    /// are Ambiguous (never "highest version wins"); an explicit selection is Bound, not Confirmed.
    /// </summary>
    public static ContractRelationshipAssessment Assess(ContractDeclaration? producer, IEnumerable<ContractFact> consumers, string? selectedContractId = null)
    {
        var assessed = new List<ContractCandidateAssessment>();
        var differences = new List<string>();
        foreach (var c in consumers)
        {
            var matches = new List<ContractMatch>();
            var roots = c.EntryPoint.Roots;
            if (producer?.DataType is { Length: > 0 } dt && roots.Any(r => string.Equals(r, dt, StringComparison.OrdinalIgnoreCase)))
                matches.Add(new("root", $"Message root {dt} matches the declared data type", ContractMatchStrength.Strong));
            if (producer?.Namespace is { Length: > 0 } ns && string.Equals(ns, c.Contract.XmlSchema?.TargetNamespace, StringComparison.Ordinal))
                matches.Add(new("namespace", $"Target namespace {ns} matches exactly", ContractMatchStrength.Strong));
            if (producer?.ContentType is { } ct && (ct.Contains("xml", StringComparison.OrdinalIgnoreCase) && c.Contract.Type == SourceContractType.XmlSchema
                || ct.Contains("json", StringComparison.OrdinalIgnoreCase) && c.Contract.Type == SourceContractType.JsonSchema))
                matches.Add(new("content-type", $"{ct} fits a {ContractEvidenceQueries.Label(c.Contract.Type)}", ContractMatchStrength.Supporting));
            foreach (var r in c.Contract.CodeReferences.Where(r => r.Kind is "Validator" or "Configuration reference" or "Embedded resource"))
                matches.Add(new("source-usage", $"{r.Kind}: {r.File}:{r.Line}", r.Kind == "Validator" ? ContractMatchStrength.ExplicitUsage : ContractMatchStrength.Strong));
            if (producer?.DataType is { Length: > 2 } name && !matches.Any(m => m.Kind == "root") && Norm(c.Contract.Name).Contains(Norm(name), StringComparison.Ordinal))
                matches.Add(new("file-name", $"File name resembles {name} (low confidence)", ContractMatchStrength.Weak));
            if (c.Id == selectedContractId)
                matches.Add(new("selection", "Selected explicitly for this integration", ContractMatchStrength.Strong));
            if (matches.All(m => m.Strength == ContractMatchStrength.Supporting)) continue;

            var identity = matches.Any(m => m.Kind is "root" or "namespace");
            var state = c.Id == selectedContractId ? ContractRelationshipReviewState.Bound
                : identity && matches.Any(m => m.Strength == ContractMatchStrength.ExplicitUsage) ? ContractRelationshipReviewState.Confirmed
                : matches.Any(m => m.Strength is ContractMatchStrength.Strong or ContractMatchStrength.ExplicitUsage) ? ContractRelationshipReviewState.StrongCandidate
                : ContractRelationshipReviewState.Candidate;
            var version = c.Versions.FirstOrDefault()?.Value;
            assessed.Add(new(c.Id, c.Name, c.Contract.File, version, c.Provenance.SnapshotId, matches, state, Label(state)));
            if (producer?.Version is { } pv && version is not null && !string.Equals(pv, version, StringComparison.OrdinalIgnoreCase))
                differences.Add($"Version difference: producer {pv}, consumer candidate {c.Name} {version} · review required");
            if (producer?.DataType is { Length: > 0 } declared && matches.Any(m => m.Kind == "namespace") && !matches.Any(m => m.Kind == "root") && roots.Count > 0)
                differences.Add($"Contract evidence difference: reference data type {declared}, consumer schema root {string.Join(", ", roots)} · review required");
        }
        var ordered = assessed.OrderBy(a => a.State switch
        {
            ContractRelationshipReviewState.Bound => 0, ContractRelationshipReviewState.Confirmed => 1, ContractRelationshipReviewState.StrongCandidate => 2, _ => 3,
        }).ThenBy(a => a.File, StringComparer.Ordinal).ToList();
        var top = ordered.Where(a => a.State is ContractRelationshipReviewState.Confirmed or ContractRelationshipReviewState.StrongCandidate).ToList();
        var (overall, reason) = producer is null && ordered.Count == 0 ? (ContractRelationshipReviewState.NotAssessed, "No producer declaration to compare with.")
            : ordered.Count == 0 ? (ContractRelationshipReviewState.NoMatchingEvidence, "No consumer schema has matching root, namespace, content or source-usage evidence.")
            : ordered.Any(a => a.State == ContractRelationshipReviewState.Bound) ? (ContractRelationshipReviewState.Bound, "A consumer contract was selected explicitly. Selection is not runtime acceptance.")
            : top.Count > 1 ? (ContractRelationshipReviewState.Ambiguous, $"{top.Count} candidates are equally supported; none is chosen (never the newest by version or date).")
            : top.Count == 1 && top[0].State == ContractRelationshipReviewState.Confirmed ? (ContractRelationshipReviewState.Confirmed, "Source code loads or validates this schema and its identity matches the declaration.")
            : top.Count == 1 ? (ContractRelationshipReviewState.StrongCandidate, "Matching schema evidence; source usage does not confirm it yet.")
            : (ContractRelationshipReviewState.Candidate, "Only weak evidence (such as a similar file name); a name match never confirms a relationship.");
        return new(producer, ordered, overall, Label(overall), reason, differences.Distinct().ToList());
    }

    /// <summary>Impact confidence from relationship evidence; no breakage is ever claimed.</summary>
    public static string ImpactConfidence(ContractRelationshipReviewState state) => state switch
    {
        ContractRelationshipReviewState.Confirmed or ContractRelationshipReviewState.Bound => "High-confidence impact",
        ContractRelationshipReviewState.StrongCandidate or ContractRelationshipReviewState.Ambiguous => "Likely impact",
        _ => "Possible impact",
    };
}

// ── Tests, traceability, documentation, evidence graph ──────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestEvidenceState { GeneratedCandidate, Selected, Executed, Passed, Failed }

public sealed record ContractTestReference(string Name, string? File);
public sealed record ContractTestLink(string Test, string? File, string Element, string Basis, string Confidence);

public static class ContractTestCoverage
{
    public static string Label(ContractTestEvidenceState state) => state switch
    {
        ContractTestEvidenceState.GeneratedCandidate => "Generated candidate (not executed)",
        ContractTestEvidenceState.Selected => "Selected (not executed)",
        ContractTestEvidenceState.Executed => "Executed",
        ContractTestEvidenceState.Passed => "Passed",
        _ => "Failed",
    };

    /// <summary>Tests that exercise a contract's operations or message roots: a test file referencing the contract file is direct evidence;
    /// a test name mentioning an operation path or root element is a candidate link. Neither says the test ran.</summary>
    public static IReadOnlyList<ContractTestLink> Map(ContractFact fact, IEnumerable<ContractTestReference> tests)
    {
        var testFiles = fact.Contract.CodeReferences.Where(r => r.Kind == "Test code reference").Select(r => r.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elements = fact.Contract.Operations.Select(o => o.Path ?? o.Name).Concat(fact.EntryPoint.Roots).Where(e => e.Length >= 4).Distinct(StringComparer.Ordinal).ToList();
        var links = new List<ContractTestLink>();
        foreach (var t in tests)
        {
            if (t.File is not null && testFiles.Contains(t.File)) links.Add(new(t.Name, t.File, fact.Name, "Test code references the contract file", "Direct"));
            foreach (var e in elements.Where(e => t.Name.Contains(e.Trim('/').Split('/').Last(), StringComparison.OrdinalIgnoreCase)))
                links.Add(new(t.Name, t.File, e, $"Test name mentions {e}", "Candidate"));
        }
        return links;
    }
}

public sealed record ContractRequirementEvidence(string Control, string Implementation, string Test, string Note);

public static class ContractTraceability
{
    /// <summary>Contract evidence relevant to a requirement: a contract exists, an operation/field implements it, a schema rule validates it.
    /// Implementation and test evidence stay separate; test evidence is "Not assessed" unless a mapped test exists.</summary>
    public static IReadOnlyList<ContractRequirementEvidence> For(IReadOnlyList<ContractFact> facts, string requirement, IReadOnlyList<ContractTestReference>? tests = null)
    {
        var text = requirement.ToLowerInvariant();
        var result = new List<ContractRequirementEvidence>();
        string Tested(ContractFact f) => tests is { Count: > 0 } && ContractTestCoverage.Map(f, tests).Count > 0 ? $"{ContractTestCoverage.Map(f, tests).Count} test link(s)" : "Not assessed";
        foreach (var f in facts)
        {
            foreach (var op in f.Contract.Operations.Where(o => (o.Path ?? o.Name).Split('/', ' ', '{', '}').Any(s => s.Length >= 4 && text.Contains(s.ToLowerInvariant()))))
                result.Add(new("Implemented by API operation", $"{op.Name} in {f.Name}", Tested(f), "Declared in the contract; runtime behaviour is separate evidence."));
            foreach (var field in ContractEvidenceQueries.GetFields(f).Where(x => x.Name.Length >= 4 && text.Contains(x.Name.ToLowerInvariant())).Take(5))
                result.Add(new(ContractEvidenceQueries.GetConstraints(f).Any(c => c.Path == field.Path) ? "Validated by schema constraint" : "Implemented by contract field",
                    $"{field.Path} in {f.Name}", Tested(f), "A schema rule constrains the payload; it does not prove a payload was validated."));
        }
        if (result.Count == 0 && (text.Contains("schema") || text.Contains("contract") || text.Contains("xsd") || text.Contains("openapi")))
            result.Add(new("Contract exists", facts.Count == 0 ? "No contract in the selected source" : $"{facts.Count} contract(s): {string.Join(", ", facts.Take(5).Select(f => f.Name))}",
                "Not assessed", "Existence only; not compatibility or runtime acceptance."));
        return result;
    }
}

public static class ContractDocumentation
{
    /// <summary>A Markdown contract summary from normalized evidence — overview, structure, operations, rule counts, versions, unresolved
    /// references and limitations. Raw schema text is never copied.</summary>
    public static string Markdown(ContractEvidenceSource source, IReadOnlyList<ContractFact> facts)
    {
        var summary = ContractEvidenceQueries.Summary(source);
        var lines = new List<string>
        {
            $"## Contracts — {source.Source}", "",
            $"Source evidence analyzed {source.AnalyzedAt:yyyy-MM-dd HH:mm} UTC. Declared contracts only: runtime behaviour and downstream acceptance are not verified.", "",
            $"{summary.Total} contract(s) · {string.Join(" · ", summary.Formats.Select(f => $"{f.Format} {f.Count}"))} · parsed {summary.Parsed} · partial {summary.Partial} · failed {summary.Failed} · needs review {summary.NeedsReview}", "",
        };
        foreach (var f in facts.Where(f => f.Contract.Type != SourceContractType.GeneratedClient))
        {
            lines.Add($"### {f.Name}{(f.Versions.FirstOrDefault() is { } v ? $" {v.Value}" : "")}");
            lines.Add($"- {ContractEvidenceQueries.Label(f.Contract.Type)} · {ContractEvidenceQueries.Label(f.Usage)} · {ContractEvidenceQueries.Label(f.Parse)} · `{f.Contract.File}`");
            if (f.EntryPoint.State != ContractEntryPointState.NotApplicable) lines.Add($"- {f.EntryPoint.Text}");
            if (f.Contract.Operations.Count > 0) lines.Add($"- Operations: {string.Join(", ", f.Contract.Operations.Take(20).Select(o => o.Name))}{(f.Contract.Operations.Count > 20 ? $" (+{f.Contract.Operations.Count - 20})" : "")}");
            var constraints = ContractEvidenceQueries.GetConstraints(f);
            if (constraints.Count > 0) lines.Add($"- Rules: {string.Join(", ", constraints.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"))}");
            foreach (var u in f.UnresolvedReferences) lines.Add($"- Unresolved reference: {u.Kind} {u.Location ?? u.Namespace}");
            lines.Add("");
        }
        foreach (var g in ContractEvidenceQueries.VersionGroups(facts)) lines.Add($"- Versions: {g.Text}");
        return string.Join("\n", lines).TrimEnd() + "\n";
    }
}

public sealed record ContractGraphNode(string Id, string Kind, string Label);
public sealed record ContractGraphEdge(string From, string To, string Kind, string Evidence);

/// <summary>Contract contributions to the evidence graph (contract, operation, component, test; exposes/validates/tests/references). Stored
/// centrally; each feature renders only the subset it needs — there is no default global graph.</summary>
public static class ContractEvidenceGraph
{
    public static (IReadOnlyList<ContractGraphNode> Nodes, IReadOnlyList<ContractGraphEdge> Edges) For(IEnumerable<ContractFact> facts)
    {
        var nodes = new Dictionary<string, ContractGraphNode>(StringComparer.Ordinal);
        var edges = new List<ContractGraphEdge>();
        foreach (var f in facts)
        {
            nodes[f.Id] = new(f.Id, "Contract", f.Name);
            if (f.Contract.Producer is { } producer) { nodes[$"component:{producer}"] = new($"component:{producer}", "Component", producer); edges.Add(new($"component:{producer}", f.Id, "exposes", f.Contract.ProducerBasis ?? "Project ownership")); }
            foreach (var op in f.Contract.Operations.Take(200)) { var id = $"{f.Id}#{op.Name}"; nodes[id] = new(id, "Operation", op.Name); edges.Add(new(f.Id, id, "declares", $"{f.Contract.File}")); }
            foreach (var r in f.Contract.CodeReferences)
            {
                var id = r.Kind == "Test code reference" ? $"test:{r.File}" : $"file:{r.File}";
                nodes[id] = new(id, r.Kind == "Test code reference" ? "Test" : r.Component is null ? "Source file" : "Component", r.Component ?? r.File);
                edges.Add(new(id, f.Id, r.Kind switch { "Validator" => "validates", "Test code reference" => "tests", _ => "references" }, $"{r.File}:{r.Line}"));
            }
        }
        return ([.. nodes.Values], edges);
    }
}
