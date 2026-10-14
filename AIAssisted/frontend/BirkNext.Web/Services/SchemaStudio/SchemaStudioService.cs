using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;

namespace BirkNext.Web.Services.SchemaStudio;

public sealed record SchemaFile(string Name, string Text, string Fingerprint, string? TargetNamespace,
    IReadOnlyList<SchemaLink> Links, IReadOnlyList<SchemaDeclaration> Declarations);
public sealed record SchemaLink(string Kind, string? Namespace, string? Location, string? ResolvedFile);
public sealed record SchemaDeclaration(string Kind, string Name, string? Type, string? BaseType, string Path,
    string SourceFile, string? Compositor, string? Documentation, IReadOnlyList<SchemaFacet> Facets, IReadOnlyList<SchemaElement> Children);
public sealed record SchemaElement(string Name, string? Type, string MinOccurs, string MaxOccurs, string SourceFile,
    string? Compositor, string? Documentation, IReadOnlyList<SchemaElement> Children, IReadOnlyList<SchemaFacet> Facets);
public sealed record SchemaFacet(string Kind, string Value);
public sealed record SchemaReference(string Kind, string Name, string SourceFile, string TargetFile, string? Path);
public sealed record EffectiveMember(string Name, string? Type, string MinOccurs, string MaxOccurs, string DeclaredIn,
    string? InheritedFrom, string? TypeDeclaredIn, string? Compositor, IReadOnlyList<SchemaFacet> Facets,
    IReadOnlyList<EffectiveMember> Children, string NamespaceUri, string? CompositorGroup);
public sealed record SchemaTestCase(string Id, string Path, string Rule, string Value, bool ExpectedValid, string SourceFile);
public sealed record SchemaTestResult(string TestId, string Path, string Rule, bool ExpectedValid, string Actual,
    string Outcome, string Diagnostic, string Xml);
public sealed record SchemaPackage(string Id, string Name, DateTimeOffset ImportedAt, IReadOnlyList<SchemaFile> Files,
    string Fingerprint, string Validation, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<SchemaReference> References, IReadOnlyList<EffectiveMember> EffectiveSchema,
    IReadOnlyList<SchemaTestCase> GeneratedTests, bool ReferencesTruncated);

/// <summary>Generic, browser-local XSD package workspace. Imported XSD is treated as data, never fetched or executed.</summary>
public sealed class SchemaStudioService
{
    public const int MaxFiles = 100;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxPackageBytes = 20 * 1024 * 1024;
    public const int MaxSessionBytes = 50 * 1024 * 1024;
    public const int MaxPackageVersions = 20;
    public const int MaxRelationships = 1000;
    public const int MaxSchemaNodes = 20_000;
    public const int MaxNestingDepth = 64;
    private readonly List<SchemaPackage> _packages = [];
    public IReadOnlyList<SchemaPackage> Packages => _packages;

    public SchemaPackage Import(IReadOnlyDictionary<string, string> files, string packageName)
    {
        if (_packages.Count >= MaxPackageVersions) throw new InvalidDataException($"This session is limited to {MaxPackageVersions} imported package versions. Remove a version before importing another.");
        if (files.Count is 0 or > MaxFiles) throw new InvalidDataException($"Choose between 1 and {MaxFiles} XSD files.");
        if (files.Any(f => !SafeName(f.Key) || !f.Key.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only XSD files with safe relative filenames are accepted.");
        if (files.Any(f => Encoding.UTF8.GetByteCount(f.Value) > MaxFileBytes) || files.Sum(f => Encoding.UTF8.GetByteCount(f.Value)) > MaxPackageBytes)
            throw new InvalidDataException("The XSD package exceeds the allowed size.");
        if (_packages.Sum(p => p.Files.Sum(f => Encoding.UTF8.GetByteCount(f.Text))) + files.Sum(f => Encoding.UTF8.GetByteCount(f.Value)) > MaxSessionBytes)
            throw new InvalidDataException("Schema Studio retains at most 50 MB of XSD text per browser session. Remove older versions first.");

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.Keys)
            if (!names.TryAdd(name.Replace('\\', '/'), name)) throw new InvalidDataException("Package contains duplicate schema paths.");
        var parsed = new List<SchemaFile>();
        foreach (var (name, text) in files)
        {
            var document = LoadSafe(text);
            var root = document.Root;
            if (root?.Name != XName.Get("schema", "http://www.w3.org/2001/XMLSchema"))
                throw new InvalidDataException($"{name} is not an XML Schema document.");
            if (document.Descendants().Take(MaxSchemaNodes + 1).Count() > MaxSchemaNodes)
                throw new InvalidDataException($"{name} exceeds the {MaxSchemaNodes} node analysis limit.");
            var ns = (string?)root.Attribute("targetNamespace");
            var links = root.Elements().Where(e => e.Name == XName.Get("import", root.Name.NamespaceName) || e.Name == XName.Get("include", root.Name.NamespaceName))
                .Select(e =>
                {
                    var loc = (string?)e.Attribute("schemaLocation");
                    var packagePath = loc is null ? null : ResolvePackageLinkName(name, loc);
                    var resolved = packagePath is not null && names.TryGetValue(packagePath, out var found) ? found : null;
                    return new SchemaLink(e.Name.LocalName == "import" ? "Imported" : "Included", (string?)e.Attribute("namespace"), loc, resolved);
                }).ToArray();
            var declarations = root.Elements().Where(e => e.Name.Namespace == root.Name.Namespace && e.Name.LocalName is "element" or "simpleType" or "complexType")
                .Select(e => Declaration(e, name, "/" + (string?)e.Attribute("name"))).ToArray();
            parsed.Add(new(name, text, Fingerprint(text), ns, links, declarations));
        }
        var missing = parsed.SelectMany(f => f.Links.Where(l => l.ResolvedFile is null).Select(l => $"{f.Name}: unresolved {l.Kind.ToLowerInvariant()} '{l.Location ?? l.Namespace ?? "(no location)"}'.")).ToArray();
        var id = Guid.NewGuid().ToString("N");
        var fingerprint = Fingerprint(string.Join("\n", parsed.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + ":" + f.Fingerprint)));
        var validation = missing.Length > 0 ? "Partial" : Validate(parsed);
        var references = BuildReferences(parsed, out var referencesTruncated);
        var effective = BuildEffectiveSchema(parsed);
        var tests = BuildTests(effective);
        var package = new SchemaPackage(id, Path.GetFileNameWithoutExtension(packageName), DateTimeOffset.UtcNow, parsed,
            fingerprint, validation, missing, references, effective, tests, referencesTruncated);
        _packages.Insert(0, package);
        return package;
    }

    public void Remove(string id) => _packages.RemoveAll(p => p.Id == id);

    public static IReadOnlyDictionary<string, string> ReadZip(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length > MaxPackageBytes) throw new InvalidDataException("ZIP package exceeds 20 MB.");
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        if (archive.Entries.Count > 500 || archive.Entries.Any(e => !SafeName(e.FullName.TrimEnd('/', '\\'))))
            throw new InvalidDataException("ZIP contains too many entries or an unsafe path.");
        var xsdEntries = archive.Entries.Where(e => e.FullName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (xsdEntries.Length is 0 or > MaxFiles) throw new InvalidDataException($"ZIP must contain between 1 and {MaxFiles} XSD files.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in xsdEntries)
        {
            if (!SafeName(entry.FullName) || entry.Length > MaxFileBytes) throw new InvalidDataException("ZIP contains an unsafe path or oversized XSD.");
            expanded += entry.Length;
            if (expanded > MaxPackageBytes) throw new InvalidDataException("Expanded XSD package exceeds 20 MB.");
            using var input = entry.Open(); using var output = new MemoryStream();
            var chunk = new byte[8192];
            while (true) { var read = input.Read(chunk, 0, chunk.Length); if (read == 0) break; if (output.Length + read > MaxFileBytes) throw new InvalidDataException("Expanded XSD exceeds 2 MB."); output.Write(chunk, 0, read); }
            if (!result.TryAdd(entry.FullName.Replace('\\', '/'), new UTF8Encoding(false, true).GetString(output.ToArray())))
                throw new InvalidDataException("ZIP contains duplicate XSD paths.");
        }
        return result;
    }

    public static byte[] ExportZip(SchemaPackage package)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in package.Files)
            {
                if (!SafeName(file.Name)) throw new InvalidDataException("Package contains an unsafe filename.");
                var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Text);
            }
        return output.ToArray();
    }

    public static string ValidateXml(SchemaPackage package, string fileName, string xml)
    {
        if (Encoding.UTF8.GetByteCount(xml) > MaxPackageBytes) return "Not evaluated: XML instance exceeds the 20 MB validation limit.";
        var schema = package.Files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (schema is null || package.Validation == "Partial") return "Not evaluated: schema file or linked dependency is unavailable.";
        try
        {
            var set = new XmlSchemaSet { XmlResolver = new PackageSchemaResolver(package.Files) };
            foreach (var file in package.Files)
            {
                using var schemaReader = XmlReader.Create(new StringReader(file.Text), ReaderSettings(), "schema://package/" + Uri.EscapeDataString(file.Name));
                set.Add(null, schemaReader);
            }
            set.Compile();
            var document = XDocument.Parse(xml, LoadOptions.None);
            if (document.Root is null || !set.GlobalElements.Contains(new XmlQualifiedName(document.Root.Name.LocalName, document.Root.Name.NamespaceName)))
                return $"Invalid against this XSD: no global element is declared for root '{document.Root?.Name ?? "(missing)"}'.";
            var errors = new List<string>();
            document.Validate(set, (_, e) => errors.Add(e.Message), true);
            return errors.Count == 0 ? "Valid against this XSD." : "Invalid against this XSD: " + string.Join(" ", errors.Take(3));
        }
        catch (XmlException e) { return "Not evaluated: XML parser error: " + e.Message; }
        catch (XmlSchemaValidationException e) { return "Invalid against this XSD: " + e.Message; }
        catch (XmlSchemaException e) { return "Not evaluated: schema validation error: " + e.Message; }
        catch (InvalidOperationException e) { return "Not evaluated: " + e.Message; }
    }

    public static string GenerateMinimumXml(SchemaFile file)
    {
        var root = file.Declarations.FirstOrDefault(d => d.Kind == "element");
        if (root is null) return "<!-- No global root element was found. -->";
        var ns = file.TargetNamespace;
        var elementName = root.Name;
        var qname = string.IsNullOrWhiteSpace(ns) ? elementName : $"tns:{elementName}";
        var content = string.Concat(root.Children.Where(e => e.MinOccurs != "0").Select(e => BuildElement(e)));
        return string.IsNullOrWhiteSpace(ns)
            ? $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<{qname}>{content}</{qname}>"
            : $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<{qname} xmlns:tns=\"{Escape(ns)}\" xmlns=\"{Escape(ns)}\">{content}</{qname}>";
    }

    public static string GenerateTestXml(SchemaPackage package, SchemaTestCase test, IReadOnlyDictionary<string, string>? profileValues = null)
    {
        if (package.EffectiveSchema.Count == 0) return "";
        var pathParts = test.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var schemaRoot = package.EffectiveSchema.FirstOrDefault(r => pathParts.Length == 0 || r.Name == pathParts[0]) ?? package.EffectiveSchema[0];
        var root = BuildInstanceElement(schemaRoot, package, profileValues, "/" + schemaRoot.Name, 0);
        if (test.Rule == "wrong namespace")
        {
            root.Name = XName.Get(root.Name.LocalName, test.Value);
        }
        else if (test.Rule == "choice with multiple branches")
        {
            var parent = FindInstanceElement(root, pathParts.Skip(1));
            var parentModel = FindEffectiveMember(schemaRoot, pathParts.Skip(1));
            if (parent is not null && parentModel is not null)
                foreach (var branch in test.Value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var model = parentModel.Children.FirstOrDefault(m => m.Name == branch);
                    if (model is not null) parent.Add(BuildInstanceElement(model, package, profileValues, test.Path + "/" + model.Name, 0));
                }
        }
        else if (test.Rule == "choice branch")
        {
            var parentParts = pathParts.Skip(1).Take(Math.Max(0, pathParts.Length - 2));
            var parent = FindInstanceElement(root, parentParts);
            var parentModel = FindEffectiveMember(schemaRoot, parentParts);
            var branch = parentModel?.Children.FirstOrDefault(m => m.Name == test.Value && m.Compositor == "choice");
            if (parent is not null && parentModel is not null && branch is not null)
            {
                var groupNames = parentModel.Children.Where(m => m.CompositorGroup == branch.CompositorGroup).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
                parent.Elements().Where(e => groupNames.Contains(e.Name.LocalName)).Remove();
                parent.Add(BuildInstanceElement(branch, package, profileValues, test.Path, 0));
            }
        }
        else
        {
            var element = FindInstanceElement(root, pathParts.Skip(1));
            if (test.Rule == "missing required element") element?.Remove();
            else if (test.Rule == "maxOccurs + 1" && element is not null && int.TryParse(test.Value, out var occurrences))
            {
                var count = Math.Clamp(occurrences - (element.Parent?.Elements(element.Name).Count() ?? 0), 0, 100);
                for (var i = 0; i < count; i++) element.Parent?.Add(new XElement(element));
            }
            else if (test.Rule == "minOccurs - 1" && element is not null && int.TryParse(test.Value, out var minimumCount))
            {
                var siblings = element.Parent?.Elements(element.Name).ToArray() ?? [];
                foreach (var extra in siblings.Skip(Math.Clamp(minimumCount, 0, 100))) extra.Remove();
            }
            else if (element is not null && test.Rule is not "optional element omitted")
                element.Value = test.Value;
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<SchemaTestResult> RunGeneratedTests(SchemaPackage package,
        IReadOnlyDictionary<string, string>? profileValues = null, int maxTests = 100)
    {
        var results = new List<SchemaTestResult>();
        foreach (var test in package.GeneratedTests.Take(Math.Clamp(maxTests, 1, 500)))
        {
            var xml = GenerateTestXml(package, test, profileValues);
            var validation = ValidateXml(package, test.SourceFile, xml);
            var actual = validation.StartsWith("Valid against", StringComparison.Ordinal) ? "Valid"
                : validation.StartsWith("Invalid against", StringComparison.Ordinal) ? "Invalid" : "Not evaluated";
            var outcome = actual == "Not evaluated" ? "Not evaluated"
                : (actual == "Valid") == test.ExpectedValid ? "Pass" : "Fail";
            results.Add(new(test.Id, test.Path, test.Rule, test.ExpectedValid, actual, outcome, validation, xml));
        }
        return results;
    }

    private static XElement BuildInstanceElement(EffectiveMember member, SchemaPackage package, IReadOnlyDictionary<string, string>? profileValues, string path, int depth)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException("Generated XML exceeds the schema nesting limit.");
        var element = new XElement(XName.Get(member.Name, member.NamespaceUri));
        if (member.Children.Count == 0)
        {
            var value = profileValues is not null && profileValues.TryGetValue(path, out var profileValue) ? profileValue : DefaultValue(member);
            element.Value = value;
            return element;
        }
        var selectedChoices = new HashSet<string?>(StringComparer.Ordinal);
        var selected = new List<EffectiveMember>();
        foreach (var child in member.Children)
        {
            if (child.Compositor == "choice")
            {
                if (!selectedChoices.Add(child.CompositorGroup)) continue;
            }
            selected.Add(child);
        }
        foreach (var child in selected)
        {
            var pathChild = path + "/" + child.Name;
            if (child.MinOccurs != "0")
            {
                var count = int.TryParse(child.MinOccurs, out var minimum) ? Math.Clamp(minimum, 1, 100) : 1;
                for (var i = 0; i < count; i++) element.Add(BuildInstanceElement(child, package, profileValues, pathChild, depth + 1));
            }
        }
        return element;
    }

    private static string DefaultValue(EffectiveMember member)
    {
        if (member.Facets.FirstOrDefault(f => f.Kind == "enumeration") is { } enumeration) return enumeration.Value;
        var patterns = member.Facets.Where(f => f.Kind == "pattern").ToArray();
        if (patterns.Length > 0)
        {
            var candidate = new[] { "A", "AB", "ABC", "0", "01", "123", "a", "abc", "test", "AA", "A1", "12", "_", "-" }
                .FirstOrDefault(value => patterns.All(p => PatternMatches(p.Value, value)) && SatisfiesSimpleFacets(member.Facets, value));
            if (candidate is not null) return candidate;
        }
        if (member.Facets.FirstOrDefault(f => f.Kind is "length" or "minLength") is { } length && int.TryParse(length.Value, out var size)) return new string('a', Math.Clamp(size, 0, 512));
        var min = member.Facets.FirstOrDefault(f => f.Kind is "minInclusive" or "minExclusive");
        var max = member.Facets.FirstOrDefault(f => f.Kind is "maxInclusive" or "maxExclusive");
        if (min is not null && max is not null && decimal.TryParse(min.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var minValue)
            && decimal.TryParse(max.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var maxValue))
            return ((minValue + maxValue) / 2m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (min is not null && decimal.TryParse(min.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out minValue))
            return (minValue + (min.Kind == "minExclusive" ? 0.01m : 0m)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (max is not null && decimal.TryParse(max.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out maxValue))
            return (maxValue - (max.Kind == "maxExclusive" ? 0.01m : 0m)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return LocalName(member.Type) switch
        {
            "boolean" => "true", "integer" or "int" or "long" or "short" or "byte" or "decimal" or "positiveInteger" or "nonNegativeInteger" => "1",
            "date" => "2000-01-01", "dateTime" => "2000-01-01T00:00:00Z", "time" => "00:00:00", _ => "value"
        };
    }

    private static bool PatternMatches(string pattern, string value)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(20)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool SatisfiesSimpleFacets(IReadOnlyList<SchemaFacet> facets, string value)
    {
        var enumerations = facets.Where(f => f.Kind == "enumeration").ToArray();
        if (enumerations.Length > 0 && !enumerations.Any(f => f.Value == value)) return false;
        if (facets.FirstOrDefault(f => f.Kind is "length" or "minLength") is { } min && int.TryParse(min.Value, out var lower) && value.Length < lower) return false;
        if (facets.FirstOrDefault(f => f.Kind is "length" or "maxLength") is { } max && int.TryParse(max.Value, out var upper) && value.Length > upper) return false;
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            foreach (var facet in facets.Where(f => f.Kind is "minInclusive" or "minExclusive" or "maxInclusive" or "maxExclusive"))
                if (decimal.TryParse(facet.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var bound)
                    && (facet.Kind == "minInclusive" && number < bound || facet.Kind == "minExclusive" && number <= bound || facet.Kind == "maxInclusive" && number > bound || facet.Kind == "maxExclusive" && number >= bound)) return false;
        }
        return true;
    }

    private static XElement? FindInstanceElement(XElement root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (part == root.Name.LocalName) continue;
            current = current.Elements().FirstOrDefault(e => e.Name.LocalName == part)!;
            if (current is null) return null;
        }
        return current;
    }

    private static EffectiveMember? FindEffectiveMember(EffectiveMember root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (part == root.Name) continue;
            current = current.Children.FirstOrDefault(c => c.Name == part)!;
            if (current is null) return null;
        }
        return current;
    }

    private static string BuildElement(SchemaElement element)
    {
        if (element.Children.Count == 0)
        {
            var value = element.Facets.FirstOrDefault(f => f.Kind == "enumeration")?.Value;
            if (value is null && element.Facets.FirstOrDefault(f => f.Kind == "minLength") is { } min && int.TryParse(min.Value, out var count))
                value = new string('a', Math.Clamp(count, 0, 64));
            value ??= "value";
            return $"<{element.Name}>{Escape(value)}</{element.Name}>";
        }
        return $"<{element.Name}>" + string.Concat(element.Children.Where(e => e.MinOccurs != "0").Select(BuildElement)) + $"</{element.Name}>";
    }

    private static string Validate(IReadOnlyList<SchemaFile> files)
    {
        try
        {
            var set = new XmlSchemaSet { XmlResolver = null };
            set.XmlResolver = new PackageSchemaResolver(files);
            foreach (var file in files) { using var reader = XmlReader.Create(new StringReader(file.Text), ReaderSettings(), "schema://package/" + Uri.EscapeDataString(file.Name)); set.Add(null, reader); }
            set.Compile();
            return "Valid";
        }
        catch (XmlSchemaException) { return "Invalid"; }
        catch (XmlException) { return "Invalid"; }
        catch (InvalidOperationException) { return "Partial"; }
    }

    private static SchemaDeclaration Declaration(XElement x, string source, string path)
    {
        var kind = x.Name.LocalName;
        var name = (string?)x.Attribute("name") ?? (string?)x.Attribute("ref") ?? "(anonymous)";
        var restriction = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "restriction");
        var facets = restriction?.Elements().Where(e => e.Name.LocalName is "enumeration" or "length" or "minLength" or "maxLength" or "pattern" or "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive" or "totalDigits" or "fractionDigits" or "whiteSpace")
            .Select(e => new SchemaFacet(e.Name.LocalName, (string?)e.Attribute("value") ?? "")).ToArray() ?? [];
        var children = x.Descendants().Where(e => e.Name.LocalName == "element" && e.Parent?.Name.LocalName is "sequence" or "choice" or "all")
            .Select(e => Element(e, source, 1)).ToArray();
        var baseType = x.Descendants().FirstOrDefault(e => e.Name.LocalName is "extension" or "restriction")?.Attribute("base")?.Value;
        var compositor = x.Descendants().FirstOrDefault(e => e.Name.LocalName is "sequence" or "choice" or "all")?.Name.LocalName;
        var documentation = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "documentation")?.Value.Trim();
        return new(kind, name, (string?)x.Attribute("type"), baseType, path, source, compositor, documentation, facets, children);
    }

    private static SchemaElement Element(XElement e, string source, int depth)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException($"XSD element nesting exceeds {MaxNestingDepth} levels.");
        return new((string?)e.Attribute("name") ?? (string?)e.Attribute("ref") ?? "(anonymous)",
        (string?)e.Attribute("type"), (string?)e.Attribute("minOccurs") ?? "1", (string?)e.Attribute("maxOccurs") ?? "1", source,
        e.Elements().FirstOrDefault(c => c.Name.LocalName is "sequence" or "choice" or "all")?.Name.LocalName,
        e.Descendants().FirstOrDefault(d => d.Name.LocalName == "documentation")?.Value.Trim(),
        e.Elements().Where(c => c.Name.LocalName is "sequence" or "choice" or "all").SelectMany(c => c.Elements().Where(n => n.Name.LocalName == "element").Select(n => Element(n, source, depth + 1))).ToArray(),
        e.Descendants().Where(f => f.Name.LocalName is "enumeration" or "length" or "minLength" or "maxLength" or "pattern" or "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive" or "totalDigits" or "fractionDigits" or "whiteSpace")
            .Select(f => new SchemaFacet(f.Name.LocalName, (string?)f.Attribute("value") ?? "")).ToArray());
    }

    private static IReadOnlyList<SchemaReference> BuildReferences(IReadOnlyList<SchemaFile> files, out bool truncated)
    {
        var result = new List<SchemaReference>();
        var seen = new HashSet<SchemaReference>();
        truncated = false;
        foreach (var file in files)
        {
            var document = LoadSafe(file.Text);
            var ns = document.Root?.Name.Namespace ?? XNamespace.Get("http://www.w3.org/2001/XMLSchema");
            foreach (var node in document.Descendants().Where(e => e.Name == ns + "element" || e.Name == ns + "attribute" || e.Name == ns + "extension" || e.Name == ns + "restriction"))
            {
                foreach (var attr in new[] { "type", "base", "ref" })
                {
                    var lexical = (string?)node.Attribute(attr);
                    if (string.IsNullOrWhiteSpace(lexical)) continue;
                    var qname = ResolveQName(node, lexical);
                    var target = files.FirstOrDefault(f => f.TargetNamespace == qname.Namespace && f.Declarations.Any(d => d.Name == qname.Name && (d.Kind is "simpleType" or "complexType" or "element")));
                    if (target is not null && !ReferenceEquals(target, file))
                    {
                        var reference = new SchemaReference(attr == "base" ? "extends" : attr == "ref" ? "references element" : "uses type", qname.Name, file.Name, target.Name, ElementPath(node));
                        if (!seen.Add(reference)) continue;
                        if (result.Count == MaxRelationships)
                        {
                            truncated = true;
                            return result;
                        }
                        result.Add(reference);
                    }
                }
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
            var xs = doc.Root?.Name.Namespace ?? XNamespace.Get("http://www.w3.org/2001/XMLSchema");
            foreach (var root in doc.Root!.Elements(xs + "element").Where(e => e.Attribute("name") is not null))
            {
                var members = ResolveElementMembers(root, file, files, schemas, new HashSet<string>(StringComparer.Ordinal), 0);
                result.Add(new((string)root.Attribute("name")!, (string?)root.Attribute("type"), "1", "1", file.Name, null,
                    FindTypeFile(files, file, (string?)root.Attribute("type"), root)?.Name, null, [], members, file.TargetNamespace ?? "", null));
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
        if (extension?.Attribute("base") is { } baseAttr)
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
                    output.AddRange(baseMembers.Select(m => m with { InheritedFrom = LocalName(baseAttr.Value) }));
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
                    ?? child.Descendants(xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value;
                var source = FindElementFile(files, owner, child, name);
                result.Add(new(name, type, (string?)child.Attribute("minOccurs") ?? "1", (string?)child.Attribute("maxOccurs") ?? "1", source,
                    null, FindTypeFile(files, owner, type, child)?.Name, groupKind,
                    ExtractFacets(child).Concat(ReferencedSimpleTypeFacets(child, files)).Distinct().ToArray(),
                    ResolveElementMembers(child, owner, files, docs, stack, depth + 1), ElementNamespace(child, owner, files, source), groupId));
            }
            else if (child.Name == xs + "sequence" || child.Name == xs + "choice" || child.Name == xs + "all")
                result.AddRange(ReadMembers(child, owner, files, docs, stack, depth + 1, child.Name.LocalName,
                    groupId + "/" + child.Name.LocalName + "#" + (child.ElementsBeforeSelf().Count(e => e.Name.LocalName == child.Name.LocalName) + 1)));
        }
        return result;
    }

    private static IReadOnlyList<SchemaFacet> ExtractFacets(XElement e) => e.Descendants().Where(f => f.Name.LocalName is "enumeration" or "length" or "minLength" or "maxLength" or "pattern" or "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive" or "totalDigits" or "fractionDigits")
        .Select(f => new SchemaFacet(f.Name.LocalName, (string?)f.Attribute("value") ?? "")).ToArray();

    private static IReadOnlyList<SchemaFacet> ReferencedSimpleTypeFacets(XElement element, IReadOnlyList<SchemaFile> files)
    {
        var lexical = (string?)element.Attribute("type");
        if (string.IsNullOrWhiteSpace(lexical)) return [];
        var qname = ResolveQName(element, lexical);
        foreach (var file in files.Where(f => f.TargetNamespace == qname.Namespace))
        {
            var doc = LoadSafe(file.Text);
            var xs = doc.Root!.Name.Namespace;
            var simple = doc.Root.Elements(xs + "simpleType").FirstOrDefault(t => (string?)t.Attribute("name") == qname.Name);
            if (simple is not null) return ExtractFacets(simple);
        }
        return [];
    }

    private static IReadOnlyList<SchemaTestCase> BuildTests(IReadOnlyList<EffectiveMember> roots)
    {
        var result = new List<SchemaTestCase>();
        foreach (var root in roots)
        {
            var rootPath = "/" + root.Name;
            Add("wrong-namespace:" + root.Name, rootPath, "wrong namespace", "urn:schema-studio:wrong-namespace", false, root.DeclaredIn);
            Visit(root.Children, rootPath);
        }
        return result.Take(5000).ToArray();

        void Visit(IReadOnlyList<EffectiveMember> members, string parentPath)
        {
            foreach (var choice in members.Where(m => m.Compositor == "choice").GroupBy(m => m.CompositorGroup))
                if (choice.Count() > 1)
                {
                    foreach (var branch in choice) Add("choice-branch:" + parentPath + "/" + branch.Name, parentPath + "/" + branch.Name, "choice branch", branch.Name, true, branch.DeclaredIn);
                    Add("choice-multiple:" + choice.Key, parentPath, "choice with multiple branches", string.Join("|", choice.Select(m => m.Name)), false, choice.First().DeclaredIn);
                }
            foreach (var member in members)
            {
                var path = parentPath + "/" + member.Name;
                if (member.MinOccurs != "0") Add("required:" + path, path, "missing required element", "(omitted)", false, member.DeclaredIn);
                else Add("optional-omitted:" + path, path, "optional element omitted", "(omitted)", true, member.DeclaredIn);
                if (int.TryParse(member.MinOccurs, out var minimum) && minimum > 1) Add("occurs-underflow:" + path, path, "minOccurs - 1", (minimum - 1).ToString(), false, member.DeclaredIn);
                if (member.MaxOccurs != "1" && member.MaxOccurs != "unbounded" && int.TryParse(member.MaxOccurs, out var max))
                    Add("occurs-overflow:" + path, path, "maxOccurs + 1", (max + 1).ToString(), false, member.DeclaredIn);
                foreach (var facet in member.Facets)
                {
                    if (facet.Kind == "enumeration")
                    {
                        Add("enum-valid:" + path + ":" + facet.Value, path, "known enumeration", facet.Value, true, member.DeclaredIn);
                        var invalidId = "enum-invalid:" + path;
                        if (result.All(t => t.Id != invalidId)) Add(invalidId, path, "unknown enumeration", "__unknown_value__", false, member.DeclaredIn);
                    }
                    if (facet.Kind is "length" or "minLength" && int.TryParse(facet.Value, out var lower))
                    {
                        Add("length-boundary:" + path + facet.Kind, path, facet.Kind + " boundary", new string('a', Math.Min(lower, 512)), true, member.DeclaredIn);
                        var invalidLength = facet.Kind == "minLength" ? Math.Max(0, lower - 1) : lower + 1;
                        Add("length-outside:" + path + facet.Kind, path, facet.Kind + " outside boundary", new string('a', Math.Min(invalidLength, 512)), false, member.DeclaredIn);
                    }
                    if (facet.Kind == "maxLength" && int.TryParse(facet.Value, out var upper))
                    {
                        Add("length-boundary:" + path, path, "maxLength boundary", new string('a', Math.Min(upper, 512)), true, member.DeclaredIn);
                        Add("length-outside:" + path, path, "above maxLength", new string('a', Math.Min(upper + 1, 512)), false, member.DeclaredIn);
                    }
                    if (facet.Kind is "minInclusive" or "minExclusive" or "maxInclusive" or "maxExclusive")
                    {
                        var inclusive = facet.Kind.EndsWith("Inclusive", StringComparison.Ordinal);
                        Add("numeric-boundary:" + path + facet.Kind, path, facet.Kind + " boundary", facet.Value, inclusive, member.DeclaredIn);
                        if (decimal.TryParse(facet.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var bound))
                        {
                            var delta = Math.Max(Math.Abs(bound) * 0.01m, 0.01m);
                            var isMinimum = facet.Kind.StartsWith("min", StringComparison.Ordinal);
                            var outside = isMinimum ? bound - delta : bound + delta;
                            Add("numeric-outside:" + path + facet.Kind, path, facet.Kind + " outside boundary", outside.ToString(System.Globalization.CultureInfo.InvariantCulture), false, member.DeclaredIn);
                            if (!inclusive) Add("numeric-inside:" + path + facet.Kind, path, facet.Kind + " adjacent valid value", (isMinimum ? bound + delta : bound - delta).ToString(System.Globalization.CultureInfo.InvariantCulture), true, member.DeclaredIn);
                        }
                    }
                    if (facet.Kind == "pattern")
                    {
                        var candidates = new[] { "A", "AB", "ABC", "0", "01", "123", "a", "abc", "test", "AA", "A1", "12", "_", "-" };
                        var validPatternValue = candidates.FirstOrDefault(value => PatternMatches(facet.Value, value) && SatisfiesSimpleFacets(member.Facets, value));
                        var invalidPatternValue = candidates.FirstOrDefault(value => !PatternMatches(facet.Value, value) && SatisfiesSimpleFacets(member.Facets, value));
                        if (validPatternValue is not null) Add("pattern-valid:" + path + facet.Value, path, "pattern match", validPatternValue, true, member.DeclaredIn);
                        if (invalidPatternValue is not null) Add("pattern-invalid:" + path + facet.Value, path, "pattern non-match", invalidPatternValue, false, member.DeclaredIn);
                    }
                }
                var typeName = LocalName(member.Type);
                if (typeName is "decimal" or "integer" or "int" or "long" or "short" or "byte" or "positiveInteger" or "nonNegativeInteger" or "date" or "dateTime" or "time" or "boolean")
                    Add("wrong-type:" + path, path, "wrong datatype", "not-a-valid-" + typeName, false, member.DeclaredIn);
                Visit(member.Children, path);
            }
        }
        void Add(string id, string path, string rule, string value, bool expected, string source) => result.Add(new(id, path, rule, value, expected, source));
    }

    private static (string Namespace, string Name) ResolveQName(XElement node, string lexical)
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
    private static string ElementNamespace(XElement element, SchemaFile owner, IReadOnlyList<SchemaFile> files, string declaredIn)
    {
        if (element.Attribute("ref") is not null) return files.FirstOrDefault(f => f.Name == declaredIn)?.TargetNamespace ?? "";
        var schema = LoadSafe(owner.Text).Root!;
        var qualified = (string?)element.Attribute("form") switch
        {
            "qualified" => true,
            "unqualified" => false,
            _ => (string?)schema.Attribute("elementFormDefault") == "qualified"
        };
        return qualified ? owner.TargetNamespace ?? "" : "";
    }
    private static string? LocalName(string? value) => value?.Split(':').LastOrDefault();
    private static string? ResolvePackageLinkName(string sourceName, string location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Contains(':') || location.StartsWith('/') || location.StartsWith('\\')) return null;
        var parts = (Path.GetDirectoryName(sourceName)?.Replace('\\', '/') ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in location.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }
    private static string ElementPath(XElement e) => "/" + string.Join("/", e.AncestorsAndSelf().Where(a => a.Name.LocalName == "element").Reverse().Select(a => (string?)a.Attribute("name") ?? (string?)a.Attribute("ref") ?? "(anonymous)"));

    private static XDocument LoadSafe(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), ReaderSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }
    private static XmlReaderSettings ReaderSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes, MaxCharactersFromEntities = 0 };
    private static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('/') && !Path.IsPathRooted(name) && !name.Contains(':') && name.Split(['/', '\\']).All(p => p.Length > 0 && p != "." && p != "..");
    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? "";

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
            var name = Uri.UnescapeDataString(absoluteUri.AbsolutePath.TrimStart('/'));
            var file = files.SingleOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? files.SingleOrDefault(f => string.Equals(Path.GetFileName(f.Name), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
            if (file is null) throw new XmlException("A linked XSD is not available in the imported package.");
            return new MemoryStream(Encoding.UTF8.GetBytes(file.Text), writable: false);
        }
    }
}
