using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace BirkNext.Schemas;

// Shared deterministic XSD capability: one package model, dependency resolver, effective schema and validator used by Schema Studio
// (browser), generic Test Data and Contract Evolution (backend). Linked into both projects; it never fetches anything.

public enum SchemaDependencyStatus { Resolved, Unresolved, Circular, Unsupported, External, NamespaceMismatch }

/// <summary>One xs:import, xs:include or xs:redefine edge. <see cref="ResolvedFile"/> is the package file it resolved to, never a guess.</summary>
public sealed record SchemaLink(string Kind, string? Namespace, string? Location, string? ResolvedFile,
    SchemaDependencyStatus? Status = null, string? Reason = null)
{
    public SchemaDependencyStatus EffectiveStatus => Status ?? (ResolvedFile is null ? SchemaDependencyStatus.Unresolved : SchemaDependencyStatus.Resolved);
}

public sealed record SchemaFile(string Name, string Text, string Fingerprint, string? TargetNamespace,
    IReadOnlyList<SchemaLink> Links, IReadOnlyList<SchemaDeclaration> Declarations, string? Version = null);
public sealed record SchemaDeclaration(string Kind, string Name, string? Type, string? BaseType, string Path,
    string SourceFile, string? Compositor, string? Documentation, IReadOnlyList<SchemaFacet> Facets, IReadOnlyList<SchemaElement> Children);
public sealed record SchemaElement(string Name, string? Type, string MinOccurs, string MaxOccurs, string SourceFile,
    string? Compositor, string? Documentation, IReadOnlyList<SchemaElement> Children, IReadOnlyList<SchemaFacet> Facets);
/// <summary>A restriction facet. <see cref="Documentation"/> is the facet's own xs:documentation (for example on one enumeration value).</summary>
public sealed record SchemaFacet(string Kind, string Value, string? Documentation = null);
public sealed record SchemaReference(string Kind, string Name, string SourceFile, string TargetFile, string? Path);
public sealed record EffectiveAttribute(string Name, string? Type, string Use, string DeclaredIn, string? TypeDeclaredIn,
    IReadOnlyList<SchemaFacet> Facets, string? DefaultValue, string? FixedValue, string NamespaceUri, string? InheritedFrom = null,
    string? BaseType = null, string? Documentation = null, string? QualifiedType = null);
/// <summary>
/// One member of the effective (inheritance- and reference-resolved) schema. <see cref="DeclaredIn"/>, <see cref="TypeDeclaredIn"/> and
/// <see cref="InheritedFrom"/> are provenance; <see cref="QualifiedType"/> is the namespace-resolved type ("{ns}local"), prefix-independent.
/// </summary>
public sealed record EffectiveMember(string Name, string? Type, string MinOccurs, string MaxOccurs, string DeclaredIn,
    string? InheritedFrom, string? TypeDeclaredIn, string? Compositor, IReadOnlyList<SchemaFacet> Facets,
    IReadOnlyList<EffectiveMember> Children, string NamespaceUri, string? CompositorGroup,
    string? DefaultValue = null, string? FixedValue = null, bool Nillable = false,
    IReadOnlyList<EffectiveAttribute>? Attributes = null, string? BaseType = null,
    string? Documentation = null, string? TypeDocumentation = null, string? QualifiedType = null)
{
    /// <summary>True when the member sits inside an xs:choice at any compositor level (a real xs:choice, never a naming convention).</summary>
    public bool InChoice => Compositor == "choice" || (CompositorGroup ?? "").Contains("/choice#", StringComparison.Ordinal);
}
/// <summary>A type, element or attribute reference that no file in the package declares. Reported, never guessed.</summary>
public sealed record SchemaUnresolvedReference(string Kind, string QualifiedName, string UsedIn, string? Path);
/// <summary>Stable schema identity: content fingerprint plus the primary file's namespace and declared xs:schema/@version.</summary>
public sealed record SchemaIdentity(string Fingerprint, string? TargetNamespace, string? Version, string? PrimaryFile);
/// <summary>A global simple type with its resolved base, for consumers that compare named restrictions.</summary>
public sealed record SchemaSimpleTypeFact(string QualifiedName, string? QualifiedBase, IReadOnlyList<SchemaFacet> Facets, string DeclaredIn, string? Documentation);

/// <summary>Result of analysing one package. <see cref="Validation"/> is "Valid", "Invalid" or "Partial" (a dependency is unavailable).</summary>
public sealed record XmlSchemaAnalysis(IReadOnlyList<SchemaFile> Files, string Fingerprint, string Validation, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<SchemaReference> References, bool ReferencesTruncated, IReadOnlyList<EffectiveMember> EffectiveSchema, SchemaIdentity Identity,
    IReadOnlyList<SchemaUnresolvedReference> UnresolvedReferences);

public enum XmlSchemaValidationStatus { Valid, Invalid, SchemaUnavailable, DependencyUnresolved, NotEvaluated }

/// <summary>Instance validation outcome. <see cref="Message"/> is human-readable; <see cref="Issues"/> never echo instance values.</summary>
public sealed record XmlSchemaValidationResult(XmlSchemaValidationStatus Status, string Message, IReadOnlyList<string> Issues);

/// <summary>
/// Deterministic XSD package analysis and validation. Schemas and instances are data: DTDs are prohibited, entities are not expanded, and
/// imports/includes/redefines resolve only to files inside the package (no file system, no network). Inputs are size, node and depth bounded.
/// </summary>
public static class XmlSchemaPackageAnalyzer
{
    public const string XsNamespace = "http://www.w3.org/2001/XMLSchema";
    public const int MaxFiles = 100;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxPackageBytes = 20 * 1024 * 1024;
    public const int MaxRelationships = 1000;
    public const int MaxSchemaNodes = 20_000;
    public const int MaxXmlNodes = 50_000;
    public const int MaxNestingDepth = 64;
    private const int MaxUnresolvedReferences = 200;

    /// <summary>Parses, links and compiles a package of XSD files keyed by safe relative path.</summary>
    public static XmlSchemaAnalysis Analyze(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count is 0 or > MaxFiles) throw new InvalidDataException($"Choose between 1 and {MaxFiles} XSD files.");
        if (files.Any(f => !SafeName(f.Key) || !f.Key.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only XSD files with safe relative filenames are accepted.");
        if (files.Any(f => Encoding.UTF8.GetByteCount(f.Value) > MaxFileBytes) || files.Sum(f => Encoding.UTF8.GetByteCount(f.Value)) > MaxPackageBytes)
            throw new InvalidDataException("The XSD package exceeds the allowed size.");

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.Keys)
            if (!names.TryAdd(name.Replace('\\', '/'), name)) throw new InvalidDataException("Package contains duplicate schema paths.");
        var parsed = new List<SchemaFile>();
        foreach (var (name, text) in files)
        {
            var document = LoadSafe(text);
            var root = document.Root;
            if (root?.Name != XName.Get("schema", XsNamespace))
                throw new InvalidDataException($"{name} is not an XML Schema document.");
            if (document.Descendants().Take(MaxSchemaNodes + 1).Count() > MaxSchemaNodes)
                throw new InvalidDataException($"{name} exceeds the {MaxSchemaNodes} node analysis limit.");
            var links = root.Elements().Where(e => e.Name.Namespace == root.Name.Namespace && e.Name.LocalName is "import" or "include" or "redefine")
                .Select(e => Link(name, e, names)).ToArray();
            var declarations = root.Elements().Where(e => e.Name.Namespace == root.Name.Namespace && e.Name.LocalName is "element" or "attribute" or "simpleType" or "complexType")
                .Select(e => Declaration(e, name, "/" + (string?)e.Attribute("name"))).ToArray();
            parsed.Add(new(name, text, Fingerprint(text), (string?)root.Attribute("targetNamespace"), links, declarations, (string?)root.Attribute("version")));
        }
        parsed = ClassifyLinks(parsed);
        var missing = parsed.SelectMany(f => f.Links.Where(l => l.ResolvedFile is null)
            .Select(l => $"{f.Name}: unresolved {l.Kind.ToLowerInvariant()} '{l.Location ?? l.Namespace ?? "(no location)"}'.")).ToList();
        var fingerprint = PackageFingerprint(parsed);
        var diagnostics = new List<string>(missing);
        string? compileError = null;
        var validation = missing.Count > 0 ? "Partial" : Compile(parsed, out compileError);
        if (validation == "Invalid" && compileError is not null) diagnostics.Add(compileError);
        var references = BuildReferences(parsed, out var referencesTruncated);
        var effective = BuildEffectiveSchema(parsed);
        var roots = PackageRoots(parsed);
        var primary = roots.Count == 1 ? roots[0] : null;
        return new(parsed, fingerprint, validation, diagnostics, references, referencesTruncated, effective,
            new SchemaIdentity(fingerprint, primary?.TargetNamespace, primary?.Version, primary?.Name), UnresolvedReferences(parsed));
    }

    /// <summary>Returns only the selected file and its transitive package-local include/import/redefine dependencies.</summary>
    public static IReadOnlyDictionary<string, string> DependencyClosure(IReadOnlyDictionary<string, string> files, string contractPath)
    {
        if (files.Count is 0 or > MaxFiles || files.Any(file => !SafeName(file.Key) || !file.Key.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The Source Analysis snapshot contains an invalid or oversized XSD file set.");
        var byPath = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (!byPath.TryAdd(file.Key.Replace('\\', '/'), file))
                throw new InvalidDataException("The Source Analysis snapshot contains duplicate XSD paths.");
        var start = contractPath.Replace('\\', '/');
        if (!SafeName(start) || !byPath.ContainsKey(start))
            throw new InvalidDataException("The selected XSD contract is not present in the Source Analysis snapshot.");

        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(start);
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            if (!visited.Add(path) || !byPath.TryGetValue(path, out var file)) continue;
            if (visited.Count > MaxFiles) throw new InvalidDataException($"The linked XSD package exceeds the {MaxFiles} file limit.");
            var bytes = Encoding.UTF8.GetByteCount(file.Value);
            if (bytes > MaxFileBytes || (totalBytes += bytes) > MaxPackageBytes)
                throw new InvalidDataException("The linked XSD package exceeds the allowed file or package size.");
            selected.Add(file.Key, file.Value);
            var root = LoadSafe(file.Value).Root;
            if (root?.Name != XName.Get("schema", XsNamespace))
                throw new InvalidDataException($"{file.Key} is not an XML Schema document.");
            foreach (var link in root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace && element.Name.LocalName is "include" or "import" or "redefine"))
            {
                var location = (string?)link.Attribute("schemaLocation");
                if (location is null) continue;
                var dependency = ResolvePackageLinkName(file.Key, location);
                if (dependency is not null && byPath.ContainsKey(dependency)) pending.Push(dependency);
            }
        }
        return selected;
    }

    /// <summary>
    /// Validates one XML instance against the compiled package. A package with an unavailable dependency is never evaluated (no guessing),
    /// and <paramref name="expectedRoot"/>, when given, must match the instance root's local name.
    /// </summary>
    public static XmlSchemaValidationResult ValidateXml(IReadOnlyList<SchemaFile> files, string packageValidation, string? schemaFileName, string xml,
        string? expectedRoot = null)
    {
        const string unavailable = "Not evaluated: schema file or linked dependency is unavailable.";
        if (Encoding.UTF8.GetByteCount(xml) > MaxPackageBytes)
            return new(XmlSchemaValidationStatus.NotEvaluated, "Not evaluated: XML instance exceeds the 20 MB validation limit.", []);
        if (files.Count == 0 || schemaFileName is not null && !files.Any(f => string.Equals(f.Name, schemaFileName, StringComparison.OrdinalIgnoreCase)))
            return new(XmlSchemaValidationStatus.SchemaUnavailable, unavailable, []);
        if (packageValidation == "Partial")
            return new(XmlSchemaValidationStatus.DependencyUnresolved, unavailable,
                [.. files.SelectMany(f => f.Links.Where(l => l.ResolvedFile is null).Select(l => $"{f.Name}: {l.Kind.ToLowerInvariant()} '{l.Location ?? l.Namespace ?? "(no location)"}' is not resolved."))]);
        try
        {
            var set = CompiledSet(files);
            XDocument document;
            try { document = ParseBoundedXml(xml); }
            catch (InvalidDataException e) { return new(XmlSchemaValidationStatus.NotEvaluated, "Not evaluated: " + e.Message, []); }
            catch (XmlException e)
            {
                var issue = "XML is not well formed: " + SafeIssue(e.Message);
                return new(XmlSchemaValidationStatus.Invalid, "Invalid against this XSD: " + issue, [issue]);
            }
            if (document.Root is null || !set.GlobalElements.Contains(new XmlQualifiedName(document.Root.Name.LocalName, document.Root.Name.NamespaceName)))
            {
                var issue = $"no global element is declared for root '{document.Root?.Name.ToString() ?? "(missing)"}'.";
                return new(XmlSchemaValidationStatus.Invalid, "Invalid against this XSD: " + issue, [issue]);
            }
            if (expectedRoot is not null && !string.Equals(document.Root.Name.LocalName, expectedRoot, StringComparison.Ordinal))
            {
                var issue = $"root '{document.Root.Name.LocalName}' is not the selected root '{expectedRoot}'.";
                return new(XmlSchemaValidationStatus.Invalid, "Invalid against this XSD: " + issue, [issue]);
            }
            var errors = new List<string>();
            document.Validate(set, (_, e) => { if (errors.Count < 50) errors.Add(SafeIssue(e.Message)); }, true);
            return errors.Count == 0
                ? new(XmlSchemaValidationStatus.Valid, "Valid against this XSD.", [])
                : new(XmlSchemaValidationStatus.Invalid, "Invalid against this XSD: " + string.Join(" ", errors.Take(3)), errors);
        }
        catch (XmlException e) { return new(XmlSchemaValidationStatus.NotEvaluated, "Not evaluated: XML parser error: " + SafeIssue(e.Message), []); }
        catch (XmlSchemaValidationException e) { return new(XmlSchemaValidationStatus.Invalid, "Invalid against this XSD: " + SafeIssue(e.Message), [SafeIssue(e.Message)]); }
        catch (XmlSchemaException e) { return new(XmlSchemaValidationStatus.NotEvaluated, "Not evaluated: schema validation error: " + SafeIssue(e.Message), []); }
        catch (InvalidOperationException e) { return new(XmlSchemaValidationStatus.NotEvaluated, "Not evaluated: " + SafeIssue(e.Message), []); }
    }

    /// <summary>Validates an instance against a package given as raw files (backend consumers such as Test Data).</summary>
    public static XmlSchemaValidationResult ValidateXml(IReadOnlyDictionary<string, string> files, string xml, string? expectedRoot = null)
    {
        XmlSchemaAnalysis analysis;
        try { analysis = Analyze(files); }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            return new(XmlSchemaValidationStatus.SchemaUnavailable, "Not evaluated: the schema package could not be read: " + SafeIssue(e.Message), []);
        }
        return ValidateXml(analysis.Files, analysis.Validation, null, xml, expectedRoot);
    }

    /// <summary>Global simple types with namespace-resolved bases and their own facets.</summary>
    public static IReadOnlyList<SchemaSimpleTypeFact> SimpleTypes(IReadOnlyList<SchemaFile> files)
    {
        var result = new List<SchemaSimpleTypeFact>();
        foreach (var file in files)
        {
            var root = LoadSafe(file.Text).Root!;
            var xs = root.Name.Namespace;
            foreach (var type in root.Elements(xs + "simpleType").Where(t => t.Attribute("name") is not null))
            {
                var restriction = type.Element(xs + "restriction");
                var baseName = (string?)restriction?.Attribute("base");
                var qualifiedBase = baseName is null ? null : QualifiedName(ResolveQName(restriction!, baseName));
                result.Add(new($"{{{file.TargetNamespace ?? ""}}}{(string)type.Attribute("name")!}", qualifiedBase,
                    restriction is null ? [] : FacetsOf(restriction), file.Name, Documentation(type)));
            }
        }
        return result;
    }

    /// <summary>Stable SHA-256 over the sorted file names and per-file fingerprints (unchanged from Schema Studio's original package identity).</summary>
    public static string PackageFingerprint(IEnumerable<SchemaFile> files) =>
        Fingerprint(string.Join("\n", files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + ":" + f.Fingerprint)));

    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('/') && !System.IO.Path.IsPathRooted(name) && !name.Contains(':')
        && name.Split(['/', '\\']).All(p => p.Length > 0 && p != "." && p != "..");

    public static string? LocalName(string? value) => value?.Split(':').LastOrDefault();

    /// <summary>The XML Schema built-in type a member resolves to ("xs:string"), or the last resolvable named type.</summary>
    public static string? BuiltInType(EffectiveMember member) => member.BaseType ?? member.Type;

    /// <summary>Resolves a package-relative schemaLocation; null for absolute, rooted or package-escaping locations.</summary>
    public static string? ResolvePackageLinkName(string sourceName, string location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Contains(':') || location.StartsWith('/') || location.StartsWith('\\')) return null;
        var parts = (System.IO.Path.GetDirectoryName(sourceName)?.Replace('\\', '/') ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in location.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            if (segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    public static XDocument LoadSafe(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), ReaderSettings(MaxFileBytes));
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Parses an instance with node, depth and size bounds before building the document.</summary>
    public static XDocument ParseBoundedXml(string xml)
    {
        using (var scan = XmlReader.Create(new StringReader(xml), ReaderSettings(MaxPackageBytes)))
        {
            var nodes = 0;
            while (scan.Read())
            {
                nodes += 1 + scan.AttributeCount;
                if (nodes > MaxXmlNodes) throw new InvalidDataException($"XML exceeds the {MaxXmlNodes} node validation limit.");
                if (scan.NodeType == XmlNodeType.Element && scan.Depth + 1 > MaxNestingDepth)
                    throw new InvalidDataException($"XML exceeds the {MaxNestingDepth} level nesting limit.");
            }
        }
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings(MaxPackageBytes));
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Validator messages without instance values (for example "The value '12345678901' is invalid" loses the value).</summary>
    /// <remarks>Plain character scanning, not a timed regex: the result never depends on machine load.</remarks>
    public static string SafeIssue(string message)
    {
        var builder = new System.Text.StringBuilder(message.Length);
        for (var i = 0; i < message.Length; i++)
        {
            // "value '...'" (any case, word boundary before, whitespace before the quote) → "value".
            if ((i == 0 || !char.IsLetterOrDigit(message[i - 1]) && message[i - 1] != '_') && i + 5 <= message.Length
                && string.Compare(message, i, "value", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var j = i + 5;
                while (j < message.Length && char.IsWhiteSpace(message[j])) j++;
                var close = j > i + 5 && j < message.Length && message[j] == '\'' ? message.IndexOf('\'', j + 1) : -1;
                if (close > 0) { builder.Append("value"); i = close; continue; }
            }
            // Runs of six or more digits → "[number]".
            if (char.IsAsciiDigit(message[i]))
            {
                var end = i;
                while (end < message.Length && char.IsAsciiDigit(message[end])) end++;
                builder.Append(end - i >= 6 ? "[number]" : message[i..end]);
                i = end - 1;
                continue;
            }
            builder.Append(message[i]);
        }
        var safe = builder.ToString();
        return safe.Length > 300 ? safe[..300] : safe;
    }

    private static XmlReaderSettings ReaderSettings(int maxCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxCharacters, MaxCharactersFromEntities = 0,
    };

    private static SchemaLink Link(string source, XElement element, IReadOnlyDictionary<string, string> names)
    {
        var kind = element.Name.LocalName switch { "import" => "Imported", "include" => "Included", _ => "Redefined" };
        var location = (string?)element.Attribute("schemaLocation");
        var ns = (string?)element.Attribute("namespace");
        if (string.IsNullOrWhiteSpace(location))
            return new(kind, ns, location, null, SchemaDependencyStatus.Unresolved, "No schemaLocation: a namespace-only reference is not resolved inside the package.");
        if (Uri.TryCreate(location, UriKind.Absolute, out var absolute) && !absolute.IsFile || location.Contains("://", StringComparison.Ordinal))
            return new(kind, ns, location, null, SchemaDependencyStatus.External, "Absolute schema locations are never fetched; add the file to the package instead.");
        var packagePath = ResolvePackageLinkName(source, location);
        if (packagePath is null)
            return new(kind, ns, location, null, SchemaDependencyStatus.Unresolved, "The location is absolute, rooted or escapes the package; it is not followed.");
        return names.TryGetValue(packagePath, out var found)
            ? new(kind, ns, location, found)
            : new(kind, ns, location, null, SchemaDependencyStatus.Unresolved, "The referenced file is not in this package.");
    }

    // Namespace mismatch, redefine support level and cycles are decided once every file's target namespace is known.
    private static List<SchemaFile> ClassifyLinks(List<SchemaFile> files)
    {
        var byName = files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var links = files.ToDictionary(f => f.Name, f => f.Links.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var current = links[file.Name];
            for (var i = 0; i < current.Length; i++)
            {
                var link = current[i];
                if (link.ResolvedFile is null || !byName.TryGetValue(link.ResolvedFile, out var target)) continue;
                if (link.Kind == "Imported" && !string.Equals(link.Namespace ?? "", target.TargetNamespace ?? "", StringComparison.Ordinal))
                    current[i] = link with { Status = SchemaDependencyStatus.NamespaceMismatch,
                        Reason = $"Imported namespace '{link.Namespace ?? "(none)"}' differs from the target namespace '{target.TargetNamespace ?? "(none)"}' of {target.Name}." };
                else if (link.Kind is "Included" or "Redefined" && target.TargetNamespace is not null
                    && !string.Equals(target.TargetNamespace, file.TargetNamespace, StringComparison.Ordinal))
                    current[i] = link with { Status = SchemaDependencyStatus.NamespaceMismatch,
                        Reason = $"{target.Name} has target namespace '{target.TargetNamespace}', not '{file.TargetNamespace ?? "(none)"}' of the including schema." };
                else if (link.Kind == "Redefined")
                    current[i] = link with { Status = SchemaDependencyStatus.Unsupported,
                        Reason = "xs:redefine is applied by the validator, but the readable effective model does not apply redefined components." };
            }
        }

        // Depth-first over resolved edges: an edge back to a file on the current path closes a cycle. The edge is kept, marked Circular.
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name)
        {
            state[name] = 1;
            var current = links[name];
            for (var i = 0; i < current.Length; i++)
            {
                var target = current[i].ResolvedFile;
                if (target is null || !links.ContainsKey(target)) continue;
                var seen = state.GetValueOrDefault(target);
                if (seen == 1)
                {
                    if (current[i].Status is null)
                        current[i] = current[i] with { Status = SchemaDependencyStatus.Circular, Reason = $"Circular dependency detected: {target} is already on the dependency path." };
                }
                else if (seen == 0) Visit(target);
            }
            state[name] = 2;
        }
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            if (state.GetValueOrDefault(file.Name) == 0) Visit(file.Name);
        return [.. files.Select(f => f with { Links = links[f.Name] })];
    }

    private static string Compile(IReadOnlyList<SchemaFile> files, out string? error)
    {
        error = null;
        try { CompiledSet(files); return "Valid"; }
        catch (XmlSchemaException e) { error = "Schema compilation failed: " + SafeIssue(e.Message); return "Invalid"; }
        catch (XmlException e) { error = "Schema compilation failed: " + SafeIssue(e.Message); return "Invalid"; }
        catch (InvalidOperationException) { return "Partial"; }
    }

    private static XmlSchemaSet CompiledSet(IReadOnlyList<SchemaFile> files)
    {
        var set = new XmlSchemaSet { XmlResolver = new PackageSchemaResolver(files) };
        foreach (var file in PackageRoots(files))
        {
            using var reader = XmlReader.Create(new StringReader(file.Text), ReaderSettings(MaxFileBytes), SchemaUri(file.Name));
            set.Add(null, reader);
        }
        set.Compile();
        return set;
    }

    private static SchemaDeclaration Declaration(XElement x, string source, string path)
    {
        var kind = x.Name.LocalName;
        var name = (string?)x.Attribute("name") ?? (string?)x.Attribute("ref") ?? "(anonymous)";
        var restriction = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "restriction");
        var facets = restriction is null ? [] : FacetsOf(restriction);
        var children = x.Descendants().Where(e => e.Name.LocalName == "element" && e.Parent?.Name.LocalName is "sequence" or "choice" or "all")
            .Select(e => Element(e, source, 1)).ToArray();
        var baseType = x.Descendants().FirstOrDefault(e => e.Name.LocalName is "extension" or "restriction")?.Attribute("base")?.Value;
        var compositor = x.Descendants().FirstOrDefault(e => e.Name.LocalName is "sequence" or "choice" or "all")?.Name.LocalName;
        return new(kind, name, (string?)x.Attribute("type"), baseType, path, source, compositor, Documentation(x), facets, children);
    }

    private static SchemaElement Element(XElement e, string source, int depth)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException($"XSD element nesting exceeds {MaxNestingDepth} levels.");
        return new((string?)e.Attribute("name") ?? (string?)e.Attribute("ref") ?? "(anonymous)",
            (string?)e.Attribute("type"), (string?)e.Attribute("minOccurs") ?? "1", (string?)e.Attribute("maxOccurs") ?? "1", source,
            e.Elements().FirstOrDefault(c => c.Name.LocalName is "sequence" or "choice" or "all")?.Name.LocalName,
            Documentation(e),
            e.Elements().Where(c => c.Name.LocalName is "sequence" or "choice" or "all").SelectMany(c => c.Elements().Where(n => n.Name.LocalName == "element").Select(n => Element(n, source, depth + 1))).ToArray(),
            OwnFacets(e));
    }

    /// <summary>The node's own xs:annotation/xs:documentation text, trimmed but otherwise verbatim (source language preserved).</summary>
    private static string? Documentation(XElement node)
    {
        var xs = XNamespace.Get(XsNamespace);
        var texts = node.Elements(xs + "annotation").Elements(xs + "documentation").Select(d => d.Value.Trim()).Where(t => t.Length > 0).ToArray();
        return texts.Length == 0 ? null : string.Join("\n\n", texts);
    }

    private static readonly HashSet<string> FacetNames = ["enumeration", "length", "minLength", "maxLength", "pattern", "minInclusive", "maxInclusive",
        "minExclusive", "maxExclusive", "totalDigits", "fractionDigits", "whiteSpace"];

    private static IReadOnlyList<SchemaFacet> FacetsOf(XElement scope) => scope.Descendants()
        .Where(f => f.Name.NamespaceName == XsNamespace && FacetNames.Contains(f.Name.LocalName))
        .Select(f => new SchemaFacet(f.Name.LocalName, (string?)f.Attribute("value") ?? "", Documentation(f))).ToArray();

    // Facets that belong to this element or attribute itself (its inline simple type or simple content), never those of nested children.
    private static IReadOnlyList<SchemaFacet> OwnFacets(XElement node)
    {
        var xs = XNamespace.Get(XsNamespace);
        var scope = node.Element(xs + "simpleType") ?? node.Element(xs + "complexType")?.Element(xs + "simpleContent");
        return scope is null ? [] : FacetsOf(scope).Where(f => f.Kind != "whiteSpace").ToArray();
    }

    private static IReadOnlyList<SchemaReference> BuildReferences(IReadOnlyList<SchemaFile> files, out bool truncated)
    {
        var result = new List<SchemaReference>();
        var seen = new HashSet<SchemaReference>();
        truncated = false;
        foreach (var file in files)
        {
            var document = LoadSafe(file.Text);
            var ns = document.Root?.Name.Namespace ?? XNamespace.Get(XsNamespace);
            foreach (var node in document.Descendants().Where(e => e.Name == ns + "element" || e.Name == ns + "attribute" || e.Name == ns + "extension" || e.Name == ns + "restriction"))
            {
                foreach (var attr in new[] { "type", "base", "ref" })
                {
                    var lexical = (string?)node.Attribute(attr);
                    if (string.IsNullOrWhiteSpace(lexical)) continue;
                    var qname = ResolveQName(node, lexical);
                    var referenceKind = attr == "ref" && node.Name.LocalName == "attribute" ? "attribute" : null;
                    var target = files.FirstOrDefault(f => f.TargetNamespace == qname.Namespace && f.Declarations.Any(d => d.Name == qname.Name
                        && (referenceKind is not null ? d.Kind == referenceKind : d.Kind is "simpleType" or "complexType" or "element")));
                    if (target is not null && !ReferenceEquals(target, file))
                    {
                        var relation = attr == "base" ? "extends" : attr == "ref" ? referenceKind == "attribute" ? "references attribute" : "references element" : "uses type";
                        var reference = new SchemaReference(relation, qname.Name, file.Name, target.Name, ElementPath(node));
                        if (!seen.Add(reference)) continue;
                        if (result.Count == MaxRelationships) { truncated = true; return result; }
                        result.Add(reference);
                    }
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<SchemaUnresolvedReference> UnresolvedReferences(IReadOnlyList<SchemaFile> files)
    {
        var result = new List<SchemaUnresolvedReference>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var file in files)
        {
            var root = LoadSafe(file.Text).Root!;
            var xs = root.Name.Namespace;
            foreach (var node in root.Descendants().Where(e => e.Name == xs + "element" || e.Name == xs + "attribute" || e.Name == xs + "extension" || e.Name == xs + "restriction"))
                foreach (var attr in new[] { "type", "base", "ref" })
                {
                    var lexical = (string?)node.Attribute(attr);
                    if (string.IsNullOrWhiteSpace(lexical)) continue;
                    var qname = ResolveQName(node, lexical);
                    if (qname.Namespace == XsNamespace) continue;
                    var kinds = attr == "ref" ? node.Name.LocalName == "attribute" ? new[] { "attribute" } : ["element"] : ["simpleType", "complexType"];
                    if (files.Any(f => (f.TargetNamespace ?? "") == qname.Namespace && f.Declarations.Any(d => d.Name == qname.Name && kinds.Contains(d.Kind)))) continue;
                    var kind = attr == "ref" ? kinds[0] : attr == "base" ? "base type" : "type";
                    if (!seen.Add((kind, QualifiedName(qname), file.Name))) continue;
                    result.Add(new(kind, QualifiedName(qname), file.Name, ElementPath(node)));
                    if (result.Count >= MaxUnresolvedReferences) return result;
                }
        }
        return result;
    }

    private static IReadOnlyList<EffectiveMember> BuildEffectiveSchema(IReadOnlyList<SchemaFile> files)
    {
        var schemas = files.ToDictionary(f => f.Name, f => LoadSafe(f.Text), StringComparer.OrdinalIgnoreCase);
        var result = new List<EffectiveMember>();
        foreach (var file in files)
        {
            var doc = schemas[file.Name];
            var xs = doc.Root?.Name.Namespace ?? XNamespace.Get(XsNamespace);
            foreach (var root in doc.Root!.Elements(xs + "element").Where(e => e.Attribute("name") is not null))
            {
                var members = ResolveElementMembers(root, file, files, schemas, new HashSet<string>(StringComparer.Ordinal), 0);
                var rootType = (string?)root.Attribute("type") ?? root.Descendants(xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value;
                var typeFile = FindTypeFile(files, file, rootType, root);
                var facets = OwnFacets(root).Concat(ReferencedSimpleTypeFacets(root, files, schemas)).Distinct().ToArray();
                var attributes = ResolveElementAttributes(root, file, files, schemas, new HashSet<string>(StringComparer.Ordinal), 0);
                result.Add(new((string)root.Attribute("name")!, rootType, "1", "1", file.Name, null,
                    typeFile?.Name, null, facets, members, file.TargetNamespace ?? "", null,
                    (string?)root.Attribute("default"), (string?)root.Attribute("fixed"), (string?)root.Attribute("nillable") == "true", attributes,
                    ResolveSimplePrimitiveBase(root, rootType, files, schemas), Documentation(root), TypeDocumentation(root, rootType, files, schemas),
                    QualifiedType(root, (string?)root.Attribute("type"))));
            }
        }
        return result;
    }

    private static IReadOnlyList<EffectiveMember> ResolveElementMembers(XElement element, SchemaFile owner, IReadOnlyList<SchemaFile> files,
        IReadOnlyDictionary<string, XDocument> docs, HashSet<string> stack, int depth)
    {
        if (depth >= 32) return [];
        var xs = element.Name.Namespace;
        var typeName = (string?)element.Attribute("type");
        var typeFile = FindTypeFile(files, owner, typeName, element);
        var type = typeFile is null ? null : docs[typeFile.Name].Root!.Elements(xs + "complexType").FirstOrDefault(t => (string?)t.Attribute("name") == LocalName(typeName));
        var inline = element.Element(xs + "complexType");
        var complex = inline ?? type;
        if (complex is null) return [];
        var identity = (typeFile?.Name ?? owner.Name) + ":" + ((string?)complex.Attribute("name") ?? (string?)element.Attribute("name"));
        if (!stack.Add(identity)) return [];
        var output = new List<EffectiveMember>();
        var content = complex.Elements().FirstOrDefault(e => e.Name == xs + "complexContent" || e.Name == xs + "simpleContent");
        var extension = content?.Elements().FirstOrDefault(e => e.Name == xs + "extension" || e.Name == xs + "restriction");
        // Only xs:extension inherits the base content model; an xs:restriction restates the members it keeps.
        if (extension?.Name == xs + "extension" && extension.Attribute("base") is { } baseAttr)
        {
            var baseFile = FindTypeFile(files, typeFile ?? owner, baseAttr.Value, extension);
            if (baseFile is not null)
            {
                var baseType = docs[baseFile.Name].Root!.Elements(xs + "complexType").FirstOrDefault(t => (string?)t.Attribute("name") == LocalName(baseAttr.Value));
                if (baseType is not null)
                {
                    var baseQName = ResolveQName(extension, baseAttr.Value);
                    var synthetic = new XElement(xs + "element", new XAttribute("name", "(base)"),
                        new XAttribute("type", "base:" + baseQName.Name), new XAttribute(XNamespace.Xmlns + "base", baseQName.Namespace));
                    var baseMembers = ResolveElementMembers(synthetic, baseFile, files, docs, stack, depth + 1);
                    output.AddRange(baseMembers.Select(m => m with { InheritedFrom = m.InheritedFrom ?? LocalName(baseAttr.Value) }));
                }
            }
        }
        var memberContainer = extension ?? complex;
        foreach (var group in memberContainer.Elements().Where(e => e.Name == xs + "sequence" || e.Name == xs + "choice" || e.Name == xs + "all"))
            output.AddRange(ReadMembers(group, typeFile ?? owner, files, docs, stack, depth + 1, group.Name.LocalName,
                ElementPath(group) + "/" + group.Name.LocalName + "#" + (group.ElementsBeforeSelf().Count(e => e.Name.LocalName == group.Name.LocalName) + 1)));
        stack.Remove(identity);
        return output;
    }

    private static IReadOnlyList<EffectiveAttribute> ResolveElementAttributes(XElement element, SchemaFile owner, IReadOnlyList<SchemaFile> files,
        IReadOnlyDictionary<string, XDocument> docs, HashSet<string> stack, int depth)
    {
        if (depth >= 32) return [];
        var xs = element.Name.Namespace;
        var typeName = (string?)element.Attribute("type");
        var typeFile = FindTypeFile(files, owner, typeName, element);
        var complex = element.Element(xs + "complexType") ?? (typeFile is null ? null
            : docs[typeFile.Name].Root!.Elements(xs + "complexType").FirstOrDefault(t => (string?)t.Attribute("name") == LocalName(typeName)));
        if (complex is null) return [];
        var identity = (typeFile?.Name ?? owner.Name) + ":attributes:" + ((string?)complex.Attribute("name") ?? (string?)element.Attribute("name"));
        if (!stack.Add(identity)) return [];

        var result = new List<EffectiveAttribute>();
        var content = complex.Elements().FirstOrDefault(e => e.Name == xs + "complexContent" || e.Name == xs + "simpleContent");
        var extension = content?.Elements().FirstOrDefault(e => e.Name == xs + "extension" || e.Name == xs + "restriction");
        if (extension?.Attribute("base") is { } baseAttribute)
        {
            var baseFile = FindTypeFile(files, typeFile ?? owner, baseAttribute.Value, extension);
            var baseType = baseFile is null ? null : docs[baseFile.Name].Root!.Elements(xs + "complexType")
                .FirstOrDefault(t => (string?)t.Attribute("name") == LocalName(baseAttribute.Value));
            if (baseFile is not null && baseType is not null)
            {
                var baseQName = ResolveQName(extension, baseAttribute.Value);
                var synthetic = new XElement(xs + "element", new XAttribute("name", "(base)"),
                    new XAttribute("type", "base:" + baseQName.Name), new XAttribute(XNamespace.Xmlns + "base", baseQName.Namespace));
                result.AddRange(ResolveElementAttributes(synthetic, baseFile, files, docs, stack, depth + 1)
                    .Select(attribute => attribute with { InheritedFrom = attribute.InheritedFrom ?? LocalName(baseAttribute.Value) }));
            }
        }

        var container = extension ?? complex;
        foreach (var attribute in container.Elements(xs + "attribute"))
        {
            var declaration = attribute;
            var declarationFile = typeFile ?? owner;
            var attributeNamespace = "";
            if ((string?)attribute.Attribute("ref") is { } reference)
            {
                var qname = ResolveQName(attribute, reference);
                attributeNamespace = qname.Namespace;
                foreach (var candidate in files.Where(f => f.TargetNamespace == qname.Namespace))
                {
                    var global = docs[candidate.Name].Root!.Elements(xs + "attribute").FirstOrDefault(a => (string?)a.Attribute("name") == qname.Name);
                    if (global is null) continue;
                    declaration = global;
                    declarationFile = candidate;
                    break;
                }
            }

            var name = (string?)declaration.Attribute("name") ?? LocalName((string?)attribute.Attribute("ref")) ?? "(anonymous)";
            var lexicalType = (string?)attribute.Attribute("type") ?? (string?)declaration.Attribute("type")
                ?? attribute.Descendants(xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value;
            var typeSource = FindTypeFile(files, declarationFile, lexicalType, declaration);
            var facets = OwnFacets(declaration).Concat(ReferencedSimpleTypeFacets(declaration, files, docs)).Distinct().ToArray();
            if (attributeNamespace.Length == 0) attributeNamespace = AttributeNamespace(attribute, typeFile ?? owner, docs);
            result.RemoveAll(existing => existing.Name == name && existing.NamespaceUri == attributeNamespace);
            result.Add(new(name, lexicalType, (string?)attribute.Attribute("use") ?? "optional", declarationFile.Name,
                typeSource?.Name, facets, (string?)attribute.Attribute("default") ?? (string?)declaration.Attribute("default"),
                (string?)attribute.Attribute("fixed") ?? (string?)declaration.Attribute("fixed"), attributeNamespace, null,
                ResolveSimplePrimitiveBase(declaration, lexicalType, files, docs), Documentation(attribute) ?? Documentation(declaration),
                QualifiedType(declaration, (string?)attribute.Attribute("type") ?? (string?)declaration.Attribute("type"))));
        }
        stack.Remove(identity);
        return result;
    }

    private static IReadOnlyList<EffectiveMember> ReadMembers(XElement compositor, SchemaFile owner, IReadOnlyList<SchemaFile> files,
        IReadOnlyDictionary<string, XDocument> docs, HashSet<string> stack, int depth, string groupKind, string groupId)
    {
        if (depth >= 32) return [];
        var xs = compositor.Name.Namespace;
        var result = new List<EffectiveMember>();
        foreach (var child in compositor.Elements())
        {
            if (child.Name == xs + "element")
            {
                var name = (string?)child.Attribute("name") ?? LocalName((string?)child.Attribute("ref")) ?? "(anonymous)";
                var type = (string?)child.Attribute("type")
                    ?? child.Element(xs + "simpleType")?.Element(xs + "restriction")?.Attribute("base")?.Value;
                var source = FindElementFile(files, owner, child, name);
                result.Add(new(name, type, (string?)child.Attribute("minOccurs") ?? "1", (string?)child.Attribute("maxOccurs") ?? "1", source,
                    null, FindTypeFile(files, owner, type, child)?.Name, groupKind,
                    OwnFacets(child).Concat(ReferencedSimpleTypeFacets(child, files, docs)).Distinct().ToArray(),
                    ResolveElementMembers(child, owner, files, docs, stack, depth + 1), ElementNamespace(child, owner, files, source, docs), groupId,
                    (string?)child.Attribute("default"), (string?)child.Attribute("fixed"), (string?)child.Attribute("nillable") == "true",
                    ResolveElementAttributes(child, owner, files, docs, stack, depth + 1), ResolveSimplePrimitiveBase(child, type, files, docs),
                    Documentation(child), TypeDocumentation(child, (string?)child.Attribute("type"), files, docs),
                    QualifiedType(child, (string?)child.Attribute("type"))));
            }
            else if (child.Name == xs + "sequence" || child.Name == xs + "choice" || child.Name == xs + "all")
                result.AddRange(ReadMembers(child, owner, files, docs, stack, depth + 1, child.Name.LocalName,
                    groupId + "/" + child.Name.LocalName + "#" + (child.ElementsBeforeSelf().Count(e => e.Name.LocalName == child.Name.LocalName) + 1)));
        }
        return result;
    }

    // The global simple or complex type a lexical QName names, looked up in the file that actually declares it (includes share a namespace).
    private static XElement? FindNamedType(XElement context, string? lexical, IReadOnlyList<SchemaFile> files, IReadOnlyDictionary<string, XDocument> docs, string kind)
    {
        if (string.IsNullOrWhiteSpace(lexical)) return null;
        var qname = ResolveQName(context, lexical);
        if (qname.Namespace == XsNamespace) return null;
        var file = files.FirstOrDefault(f => (f.TargetNamespace ?? "") == qname.Namespace && f.Declarations.Any(d => d.Kind == kind && d.Name == qname.Name));
        if (file is null) return null;
        var root = docs[file.Name].Root!;
        return root.Elements(root.Name.Namespace + kind).FirstOrDefault(t => (string?)t.Attribute("name") == qname.Name);
    }

    private static string? TypeDocumentation(XElement context, string? lexical, IReadOnlyList<SchemaFile> files, IReadOnlyDictionary<string, XDocument> docs) =>
        (FindNamedType(context, lexical, files, docs, "simpleType") ?? FindNamedType(context, lexical, files, docs, "complexType")) is { } type ? Documentation(type) : null;

    private static IReadOnlyList<SchemaFacet> ReferencedSimpleTypeFacets(XElement element, IReadOnlyList<SchemaFile> files, IReadOnlyDictionary<string, XDocument> docs)
    {
        var current = (string?)element.Attribute("type");
        var currentContext = element;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var facets = new List<SchemaFacet>();
        for (var depth = 0; depth < 32 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var qname = ResolveQName(currentContext, current);
            if (qname.Namespace == XsNamespace || !visited.Add(qname.Namespace + "#" + qname.Name)) break;
            var simple = FindNamedType(currentContext, current, files, docs, "simpleType");
            if (simple is null) break;
            var restriction = simple.Element(simple.Name.Namespace + "restriction");
            if (restriction is null) break;
            facets.AddRange(FacetsOf(restriction).Where(f => f.Kind != "whiteSpace"));
            if (restriction.Attribute("base") is not { } baseAttribute) break;
            current = baseAttribute.Value;
            currentContext = restriction;
        }
        return facets.Distinct().ToArray();
    }

    private static string? ResolveSimplePrimitiveBase(XElement context, string? lexicalType, IReadOnlyList<SchemaFile> files, IReadOnlyDictionary<string, XDocument> docs)
    {
        if (string.IsNullOrWhiteSpace(lexicalType)) return null;
        var current = lexicalType;
        var currentContext = context;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 32; depth++)
        {
            var qname = ResolveQName(currentContext, current);
            if (qname.Namespace == XsNamespace) return "xs:" + qname.Name;
            if (!visited.Add(qname.Namespace + "#" + qname.Name)) return current;
            var restriction = FindNamedType(currentContext, current, files, docs, "simpleType")?.Element(XNamespace.Get(XsNamespace) + "restriction");
            if (restriction?.Attribute("base") is not { } baseAttribute) return current;
            current = baseAttribute.Value;
            currentContext = restriction;
        }
        return current;
    }

    private static string? QualifiedType(XElement context, string? lexical) =>
        string.IsNullOrWhiteSpace(lexical) ? null : QualifiedName(ResolveQName(context, lexical));

    private static string QualifiedName((string Namespace, string Name) qname) => $"{{{qname.Namespace}}}{qname.Name}";

    public static (string Namespace, string Name) ResolveQName(XElement node, string lexical)
    {
        var parts = lexical.Split(':', 2);
        var ns = parts.Length == 2 ? node.GetNamespaceOfPrefix(parts[0])?.NamespaceName : node.GetDefaultNamespace().NamespaceName;
        return (ns ?? "", parts[^1]);
    }

    private static SchemaFile? FindTypeFile(IReadOnlyList<SchemaFile> files, SchemaFile owner, string? lexical, XElement? context = null)
    {
        if (string.IsNullOrWhiteSpace(lexical)) return null;
        var q = ResolveQName(context ?? LoadSafe(owner.Text).Root!, lexical);
        return files.FirstOrDefault(f => f.TargetNamespace == q.Namespace && f.Declarations.Any(d => d.Name == q.Name && d.Kind is "complexType" or "simpleType"));
    }

    private static string FindElementFile(IReadOnlyList<SchemaFile> files, SchemaFile owner, XElement element, string name)
    {
        var reference = (string?)element.Attribute("ref");
        if (reference is null) return owner.Name;
        var q = ResolveQName(element, reference);
        return files.FirstOrDefault(f => f.TargetNamespace == q.Namespace && f.Declarations.Any(d => d.Kind == "element" && d.Name == q.Name))?.Name ?? owner.Name;
    }

    private static string ElementNamespace(XElement element, SchemaFile owner, IReadOnlyList<SchemaFile> files, string declaredIn, IReadOnlyDictionary<string, XDocument> docs)
    {
        if (element.Attribute("ref") is not null) return files.FirstOrDefault(f => f.Name == declaredIn)?.TargetNamespace ?? "";
        var schema = docs[owner.Name].Root!;
        var qualified = (string?)element.Attribute("form") switch
        {
            "qualified" => true,
            "unqualified" => false,
            _ => (string?)schema.Attribute("elementFormDefault") == "qualified",
        };
        return qualified ? owner.TargetNamespace ?? "" : "";
    }

    private static string AttributeNamespace(XElement attribute, SchemaFile owner, IReadOnlyDictionary<string, XDocument> docs)
    {
        if ((string?)attribute.Attribute("ref") is { } reference) return ResolveQName(attribute, reference).Namespace;
        var schema = docs[owner.Name].Root!;
        var qualified = (string?)attribute.Attribute("form") switch
        {
            "qualified" => true,
            "unqualified" => false,
            _ => (string?)schema.Attribute("attributeFormDefault") == "qualified",
        };
        return qualified ? owner.TargetNamespace ?? "" : "";
    }

    /// <summary>Files no other package file references: the entry points compiled into the schema set.</summary>
    public static IReadOnlyList<SchemaFile> PackageRoots(IReadOnlyList<SchemaFile> files)
    {
        var referenced = files.SelectMany(file => file.Links).Where(link => link.ResolvedFile is not null)
            .Select(link => link.ResolvedFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = files.Where(file => !referenced.Contains(file.Name)).ToArray();
        return roots.Length == 0 ? files : roots;
    }

    private static string SchemaUri(string name) => "schema://package/" + string.Join('/', name.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    private static string ElementPath(XElement e) => "/" + string.Join("/", e.AncestorsAndSelf().Where(a => a.Name.LocalName == "element").Reverse()
        .Select(a => (string?)a.Attribute("name") ?? (string?)a.Attribute("ref") ?? "(anonymous)"));

    /// <summary>Serves only files of this package under the synthetic schema://package/ authority; everything else is refused.</summary>
    private sealed class PackageSchemaResolver(IReadOnlyList<SchemaFile> files) : XmlResolver
    {
        public override System.Net.ICredentials? Credentials { set { } }
        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
        {
            if (string.IsNullOrWhiteSpace(relativeUri) || Uri.TryCreate(relativeUri, UriKind.Absolute, out var absolute) && absolute.Scheme != "schema")
                throw new XmlException("Only schemas from the imported package can be resolved.");
            var resolved = base.ResolveUri(baseUri ?? new Uri("schema://package/"), relativeUri);
            if (resolved.Scheme != "schema") throw new XmlException("Remote schema resolution is disabled.");
            return resolved;
        }
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (absoluteUri.Scheme != "schema" || absoluteUri.Host != "package") throw new XmlException("Remote schema resolution is disabled.");
            var name = Uri.UnescapeDataString(absoluteUri.AbsolutePath.TrimStart('/')).Replace('\\', '/');
            var file = files.SingleOrDefault(f => string.Equals(f.Name.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
            if (file is null) throw new XmlException("A linked XSD is not available in the imported package.");
            return new MemoryStream(Encoding.UTF8.GetBytes(file.Text), writable: false);
        }
    }
}
