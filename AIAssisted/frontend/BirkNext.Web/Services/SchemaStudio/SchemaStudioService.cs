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
public sealed record EffectiveAttribute(string Name, string? Type, string Use, string DeclaredIn, string? TypeDeclaredIn,
    IReadOnlyList<SchemaFacet> Facets, string? DefaultValue, string? FixedValue, string NamespaceUri, string? InheritedFrom = null,
    string? BaseType = null);
public sealed record EffectiveMember(string Name, string? Type, string MinOccurs, string MaxOccurs, string DeclaredIn,
    string? InheritedFrom, string? TypeDeclaredIn, string? Compositor, IReadOnlyList<SchemaFacet> Facets,
    IReadOnlyList<EffectiveMember> Children, string NamespaceUri, string? CompositorGroup,
    string? DefaultValue = null, string? FixedValue = null, bool Nillable = false,
    IReadOnlyList<EffectiveAttribute>? Attributes = null, string? BaseType = null);
public sealed record SchemaTestCase(string Id, string Path, string Rule, string Value, bool ExpectedValid, string SourceFile);
public sealed record SchemaTestResult(string TestId, string Path, string Rule, bool ExpectedValid, string Actual,
    string Outcome, string Diagnostic, string Xml, string BaselineXml = "", string BaselineId = "");
public sealed record SchemaProvenance(string SourceKind, string SourceArchiveName, Guid? SourceSnapshotId,
    string? SourceSnapshotFingerprint, DateTimeOffset? SourceAnalyzedAt, string? SelectedContractPath, string ContractFingerprint,
    bool SourceCaptureComplete = true, int OmittedSourceFiles = 0, string? ProjectImportId = null, string? SourceProjectName = null);
public sealed record SchemaPackage(string Id, string Name, DateTimeOffset ImportedAt, IReadOnlyList<SchemaFile> Files,
    string Fingerprint, string Validation, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<SchemaReference> References, IReadOnlyList<EffectiveMember> EffectiveSchema,
    IReadOnlyList<SchemaTestCase> GeneratedTests, bool ReferencesTruncated, SchemaProvenance? Provenance = null,
    bool GeneratedTestsTruncated = false);

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
    public const int MaxXmlNodes = 50_000;
    public const int MaxGeneratedXmlNodes = 50_000;
    public const int MaxGeneratedBoundaryCharacters = 4096;
    public const int MaxNestingDepth = 64;
    private readonly List<SchemaPackage> _packages = [];
    public IReadOnlyList<SchemaPackage> Packages => _packages;

    public SchemaPackage Import(IReadOnlyDictionary<string, string> files, string packageName, SchemaProvenance? provenance = null)
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
            var declarations = root.Elements().Where(e => e.Name.Namespace == root.Name.Namespace && e.Name.LocalName is "element" or "attribute" or "simpleType" or "complexType")
                .Select(e => Declaration(e, name, "/" + (string?)e.Attribute("name"))).ToArray();
            parsed.Add(new(name, text, Fingerprint(text), ns, links, declarations));
        }
        var missing = parsed.SelectMany(f => f.Links.Where(l => l.ResolvedFile is null).Select(l => $"{f.Name}: unresolved {l.Kind.ToLowerInvariant()} '{l.Location ?? l.Namespace ?? "(no location)"}'.")).ToList();
        if (provenance is { SourceCaptureComplete: false })
            missing.Add($"Source Analysis stored only a bounded subset of XSD files; {provenance.OmittedSourceFiles} file(s) were omitted from this snapshot-bound package.");
        var id = Guid.NewGuid().ToString("N");
        var fingerprint = Fingerprint(string.Join("\n", parsed.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + ":" + f.Fingerprint)));
        var validation = missing.Count > 0 ? "Partial" : Validate(parsed);
        var references = BuildReferences(parsed, out var referencesTruncated);
        var effective = BuildEffectiveSchema(parsed);
        var tests = BuildTests(effective, out var generatedTestsTruncated);
        var package = new SchemaPackage(id, Path.GetFileNameWithoutExtension(packageName), DateTimeOffset.UtcNow, parsed,
            fingerprint, validation, missing, references, effective, tests, referencesTruncated,
            provenance is null ? null : provenance with { ContractFingerprint = parsed.FirstOrDefault(f => f.Name == provenance.SelectedContractPath)?.Fingerprint ?? provenance.ContractFingerprint },
            generatedTestsTruncated);
        _packages.Insert(0, package);
        return package;
    }

    public void Remove(string id) => _packages.RemoveAll(p => p.Id == id);

    /// <summary>Returns only the selected contract and its transitive package-local include/import dependencies.</summary>
    public static IReadOnlyDictionary<string, string> ContractPackageFiles(IReadOnlyDictionary<string, string> files, string contractPath)
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
            var document = LoadSafe(file.Value);
            var root = document.Root;
            if (root?.Name != XName.Get("schema", "http://www.w3.org/2001/XMLSchema"))
                throw new InvalidDataException($"{file.Key} is not an XML Schema document.");
            foreach (var link in root.Elements().Where(element => element.Name == XName.Get("include", root.Name.NamespaceName)
                || element.Name == XName.Get("import", root.Name.NamespaceName)))
            {
                var location = (string?)link.Attribute("schemaLocation");
                if (location is null) continue;
                var dependency = ResolvePackageLinkName(file.Key, location);
                if (dependency is not null && byPath.ContainsKey(dependency)) pending.Push(dependency);
            }
        }
        return selected;
    }

    public static IReadOnlyDictionary<string, string> ReadZip(Stream stream)
    {
        using var buffer = new MemoryStream();
        var zipChunk = new byte[8192];
        while (true)
        {
            var read = stream.Read(zipChunk, 0, zipChunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > MaxPackageBytes) throw new InvalidDataException("ZIP package exceeds 20 MB.");
            buffer.Write(zipChunk, 0, read);
        }
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
            foreach (var file in PackageRoots(package.Files))
            {
                using var schemaReader = XmlReader.Create(new StringReader(file.Text), ReaderSettings(), SchemaUri(file.Name));
                set.Add(null, schemaReader);
            }
            set.Compile();
            XDocument document;
            try { document = ParseBoundedXml(xml); }
            catch (InvalidDataException e) { return "Not evaluated: " + e.Message; }
            catch (XmlException e) { return "Invalid against this XSD: XML is not well formed: " + SafeDiagnostic(e.Message); }
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

    public static string GenerateProfileXml(SchemaPackage package, string rootName, IReadOnlyDictionary<string, string> profileValues, TestDataProfile? profile = null)
    {
        var rootSchema = package.EffectiveSchema.FirstOrDefault(root => root.Name == rootName)
            ?? throw new InvalidDataException("The selected global root is not present in the effective schema.");
        var budget = new XmlGenerationBudget();
        var root = BuildInstanceElement(rootSchema, package, profileValues, "/" + rootSchema.Name, 0, profile, budget);
        return SerializeGeneratedXml(root);
    }

    public static string GenerateTestXml(SchemaPackage package, SchemaTestCase test, IReadOnlyDictionary<string, string>? profileValues = null)
    {
        if (package.EffectiveSchema.Count == 0) return "";
        var pathParts = test.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var schemaRoot = package.EffectiveSchema.FirstOrDefault(r => pathParts.Length == 0 || r.Name == pathParts[0]) ?? package.EffectiveSchema[0];
        var budget = new XmlGenerationBudget();
        var root = BuildInstanceElement(schemaRoot, package, profileValues, "/" + schemaRoot.Name, 0, null, budget);
        if (test.Rule == "malformed XML") return SerializeGeneratedXml(root) + "<broken";
        if (test.Rule == "wrong namespace")
        {
            root.Name = XName.Get(root.Name.LocalName, test.Value);
        }
        else if (test.Rule == "wrong namespace prefix binding")
        {
            root.Name = XName.Get(root.Name.LocalName, test.Value);
            root.SetAttributeValue(XNamespace.Xmlns + "ss", test.Value);
        }
        else if (test.Rule == "missing namespace") root.Name = XName.Get(root.Name.LocalName, "");
        else if (test.Rule == "wrong element namespace")
        {
            var target = FindInstanceElement(root, pathParts.Skip(1));
            if (target is not null) target.Name = XName.Get(target.Name.LocalName, test.Value);
        }
        else if (test.Rule == "missing parent element") FindInstanceElement(root, pathParts.Skip(1))?.Remove();
        else if (test.Rule == "wrong element order")
        {
            var parent = FindInstanceElement(root, pathParts.Skip(1));
            if (parent is not null)
            {
                var reversed = parent.Elements().Reverse().ToArray();
                parent.RemoveNodes();
                foreach (var child in reversed) parent.Add(child);
            }
        }
        else if (test.Rule == "choice with multiple branches")
        {
            var parent = FindInstanceElement(root, pathParts.Skip(1));
            var parentModel = FindEffectiveMember(schemaRoot, pathParts.Skip(1));
            if (parent is not null && parentModel is not null)
                foreach (var branch in test.Value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var model = parentModel.Children.FirstOrDefault(m => m.Name == branch);
                    if (model is not null) parent.Add(BuildInstanceElement(model, package, profileValues, test.Path + "/" + model.Name, 0, null, budget));
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
                parent.Add(BuildInstanceElement(branch, package, profileValues, test.Path, 0, null, budget));
            }
        }
        else
        {
            if (test.Path.Contains("/@", StringComparison.Ordinal))
            {
                var attributePath = test.Path.Split("/@", StringSplitOptions.None);
                if (attributePath.Length == 2)
                {
                    var ownerParts = attributePath[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1);
                    var ownerMember = FindEffectiveMember(schemaRoot, ownerParts);
                    var attribute = ownerMember?.Attributes?.FirstOrDefault(candidate => candidate.Name == attributePath[1]);
                    var instance = FindInstanceElement(root, ownerParts);
                    var instanceAttribute = attribute is null ? null : instance?.Attribute(XName.Get(attribute.Name, attribute.NamespaceUri));
                    if (test.Rule == "missing required attribute") instanceAttribute?.Remove();
                    else if (test.Rule == "wrong datatype" && instanceAttribute is not null) instanceAttribute.Value = test.Value;
                }
                return SerializeGeneratedXml(root);
            }
            var element = FindInstanceElement(root, pathParts.Skip(1));
            if (test.Rule == "missing required element") element?.Remove();
            else if (test.Rule == "empty required value" && element is not null) element.Value = "";
            else if (test.Rule == "maxOccurs + 1" && element is not null && int.TryParse(test.Value, out var occurrences))
            {
                var count = Math.Max(0, occurrences - (element.Parent?.Elements(element.Name).Count() ?? 0));
                var parent = element.Parent;
                for (var i = 0; i < count; i++)
                {
                    budget.AddSubtree(element);
                    parent?.Add(new XElement(element));
                }
            }
            else if (test.Rule == "minOccurs - 1" && element is not null && int.TryParse(test.Value, out var minimumCount))
            {
                var siblings = element.Parent?.Elements(element.Name).ToArray() ?? [];
                foreach (var extra in siblings.Skip(Math.Clamp(minimumCount, 0, 100))) extra.Remove();
            }
            else if (element is not null && test.Rule is not "optional element omitted")
                element.Value = test.Value;
        }
        return SerializeGeneratedXml(root);
    }

    private static string SafeDiagnostic(string value) => value.Length > 300 ? value[..300] : value;

    private static string SerializeGeneratedXml(XElement root)
    {
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(xml) > MaxPackageBytes)
            throw new InvalidDataException("Generated XML exceeds the 20 MB generation limit.");
        return xml;
    }

    private static XDocument ParseBoundedXml(string xml)
    {
        static XmlReaderSettings Settings() => new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPackageBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
        };

        using (var scan = XmlReader.Create(new StringReader(xml), Settings()))
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

        using var reader = XmlReader.Create(new StringReader(xml), Settings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    public static IReadOnlyList<SchemaTestResult> RunGeneratedTests(SchemaPackage package,
        IReadOnlyDictionary<string, string>? profileValues = null, int maxTests = 100)
    {
        var results = new List<SchemaTestResult>();
        var baselines = new Dictionary<string, (string Xml, string Validation, string Id)>(StringComparer.Ordinal);
        var baselineShown = new HashSet<string>(StringComparer.Ordinal);
        long baselineBytes = 0;
        foreach (var test in package.GeneratedTests.Take(Math.Clamp(maxTests, 1, 500)))
        {
            var rootName = test.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var schemaRoot = package.EffectiveSchema.FirstOrDefault(root => root.Name == rootName);
            if (schemaRoot is null)
            {
                results.Add(new(test.Id, test.Path, test.Rule, test.ExpectedValid, "Not evaluated", "Not evaluated",
                    "The schema root for this case is unavailable.", ""));
                continue;
            }
            if (!baselines.TryGetValue(schemaRoot.Name, out var baseline))
            {
                try
                {
                    var baselineXml = GenerateProfileXml(package, schemaRoot.Name, profileValues ?? new Dictionary<string, string>());
                    baselineBytes += Encoding.UTF8.GetByteCount(baselineXml);
                    if (baselineBytes > MaxPackageBytes)
                        throw new InvalidDataException("The generated test run exceeds the 20 MB valid-baseline limit.");
                    baseline = (baselineXml, ValidateXml(package, schemaRoot.DeclaredIn, baselineXml),
                        schemaRoot.Name + ":" + Fingerprint(baselineXml)[..12]);
                    baselines[schemaRoot.Name] = baseline;
                }
                catch (InvalidDataException e)
                {
                    baseline = ("", "Not evaluated: " + SafeDiagnostic(e.Message), schemaRoot.Name + ":unverified");
                    baselines[schemaRoot.Name] = baseline;
                }
            }
            if (!baseline.Validation.StartsWith("Valid against", StringComparison.Ordinal))
            {
                results.Add(new(test.Id, test.Path, test.Rule, test.ExpectedValid, "Not evaluated", "Not evaluated",
                    "A valid XML baseline could not be established. " + SafeDiagnostic(baseline.Validation), "", "", baseline.Id));
                continue;
            }
            var includeBaseline = baselineShown.Add(schemaRoot.Name);
            string xml;
            string validation;
            try
            {
                xml = GenerateTestXml(package, test, profileValues);
                validation = ValidateXml(package, test.SourceFile, xml);
            }
            catch (InvalidDataException e)
            {
                results.Add(new(test.Id, test.Path, test.Rule, test.ExpectedValid, "Not evaluated", "Not evaluated", SafeDiagnostic(e.Message),
                    "", includeBaseline ? baseline.Xml : "", baseline.Id));
                continue;
            }
            var actual = validation.StartsWith("Valid against", StringComparison.Ordinal) ? "Valid"
                : validation.StartsWith("Invalid against", StringComparison.Ordinal) ? "Invalid" : "Not evaluated";
            var outcome = actual == "Not evaluated" ? "Not evaluated"
                : (actual == "Valid") == test.ExpectedValid ? "Pass" : "Fail";
            results.Add(new(test.Id, test.Path, test.Rule, test.ExpectedValid, actual, outcome, validation, xml,
                includeBaseline ? baseline.Xml : "", baseline.Id));
        }
        return results;
    }

    private static XElement BuildInstanceElement(EffectiveMember member, SchemaPackage package, IReadOnlyDictionary<string, string>? profileValues, string path, int depth,
        TestDataProfile? profile = null, XmlGenerationBudget? budget = null)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException("Generated XML exceeds the schema nesting limit.");
        budget ??= new XmlGenerationBudget();
        budget.AddElement(member.Name, member.NamespaceUri);
        var element = new XElement(XName.Get(member.Name, member.NamespaceUri));
        var attributeOnlyComplexType = member.Type is null && member.Attributes is { Count: > 0 };
        if (member.Children.Count == 0 && !attributeOnlyComplexType)
        {
            if (profileValues is not null && profileValues.TryGetValue(path, out var profileValue)
                && profileValue == TestDataProfilesService.NilToken)
            {
                if (!member.Nillable) throw new InvalidDataException($"Element {path} is not nillable in this schema.");
                budget.AddAttribute("nil", "true", "http://www.w3.org/2001/XMLSchema-instance");
                element.SetAttributeValue(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"), "true");
                return element;
            }
            var value = profileValues is not null && profileValues.TryGetValue(path, out profileValue)
                ? profileValue
                : member.FixedValue ?? member.DefaultValue ?? DefaultValue(member);
            budget.AddValue(value);
            element.Value = value;
        }
        foreach (var attribute in member.Attributes ?? [])
        {
            var attributePath = path + "/@" + attribute.Name;
            var configuredValue = "";
            var explicitlyConfigured = profileValues is not null && profileValues.TryGetValue(attributePath, out configuredValue);
            if (attribute.Use != "required" && attribute.DefaultValue is null && attribute.FixedValue is null && !explicitlyConfigured) continue;
            var model = new EffectiveMember(attribute.Name, attribute.Type, "1", "1", attribute.DeclaredIn, attribute.InheritedFrom,
                attribute.TypeDeclaredIn, null, attribute.Facets, [], attribute.NamespaceUri, null, attribute.DefaultValue, attribute.FixedValue,
                BaseType: attribute.BaseType);
            var value = explicitlyConfigured ? configuredValue! : attribute.FixedValue ?? attribute.DefaultValue ?? DefaultValue(model);
            budget.AddAttribute(attribute.Name, value, attribute.NamespaceUri);
            element.SetAttributeValue(XName.Get(attribute.Name, attribute.NamespaceUri), value);
        }
        if (member.Children.Count == 0) return element;
        var selectedChoices = new HashSet<string?>(StringComparer.Ordinal);
        var random = profile is null ? null : new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Seed + "|" + path)), 0) & int.MaxValue);
        var choiceGroups = member.Children.Where(c => c.Compositor == "choice").GroupBy(c => c.CompositorGroup ?? "").ToDictionary(g => g.Key, g => g.ToArray());
        var selectedChoiceBranches = choiceGroups.ToDictionary(pair => pair.Key, pair =>
        {
            if (profile?.Kind == TestDataProfileKind.MaximumComplexity) return pair.Value[random!.Next(pair.Value.Length)];
            var configured = profile?.Rules.Select(rule => rule.ElementPath).FirstOrDefault(rulePath =>
                pair.Value.Any(branch => rulePath.Equals(path + "/" + branch.Name, StringComparison.Ordinal)
                    || rulePath.StartsWith(path + "/" + branch.Name + "/", StringComparison.Ordinal)));
            return configured is null ? pair.Value[0] : pair.Value.FirstOrDefault(branch =>
                configured.Equals(path + "/" + branch.Name, StringComparison.Ordinal)
                    || configured.StartsWith(path + "/" + branch.Name + "/", StringComparison.Ordinal)) ?? pair.Value[0];
        });
        var selected = new List<EffectiveMember>();
        foreach (var child in member.Children)
        {
            if (child.Compositor == "choice")
            {
                if (!selectedChoices.Add(child.CompositorGroup)) continue;
                if (selectedChoiceBranches is not null && selectedChoiceBranches.TryGetValue(child.CompositorGroup ?? "", out var selectedBranch)) selected.Add(selectedBranch);
                else selected.Add(child);
                continue;
            }
            selected.Add(child);
        }
        foreach (var child in selected)
        {
            var pathChild = path + "/" + child.Name;
            var hasExplicitProfileValue = profileValues?.Keys.Any(valuePath => valuePath.Equals(pathChild, StringComparison.Ordinal)
                || valuePath.StartsWith(pathChild + "/", StringComparison.Ordinal)) == true;
            if (child.MinOccurs != "0" || profile?.Kind == TestDataProfileKind.MaximumComplexity || hasExplicitProfileValue)
            {
                var minimum = int.TryParse(child.MinOccurs, out var parsedMinimum) ? parsedMinimum : 1;
                if (minimum > 100) throw new InvalidDataException($"Element {pathChild} requires more than the 100 occurrence generation limit.");
                var count = Math.Clamp(minimum, 1, 100);
                var configuredCount = profile?.Rules.FirstOrDefault(rule => string.Equals(rule.ElementPath, pathChild, StringComparison.Ordinal))?.OccurrenceCount;
                if (configuredCount is { } requestedCount)
                {
                    var maximum = child.MaxOccurs == "unbounded" ? 100
                        : int.TryParse(child.MaxOccurs, out var parsedMaximum) ? parsedMaximum : 1;
                    if (requestedCount < minimum || requestedCount > maximum || requestedCount > 100)
                        throw new InvalidDataException($"Occurrence count for {pathChild} must be between {minimum} and {Math.Min(maximum, 100)} for this schema.");
                    count = requestedCount;
                }
                if (configuredCount is null && profile?.Kind == TestDataProfileKind.MaximumComplexity)
                {
                    var complexityLimit = Math.Max(count, 3);
                    count = child.MaxOccurs == "unbounded" ? complexityLimit : int.TryParse(child.MaxOccurs, out var maximum)
                        ? Math.Clamp(maximum, count, complexityLimit) : count;
                }
                for (var i = 0; i < count; i++) element.Add(BuildInstanceElement(child, package, profileValues, pathChild, depth + 1, profile, budget));
            }
        }
        return element;
    }

    private sealed class XmlGenerationBudget
    {
        private int _nodes;
        private long _estimatedBytes;

        public void AddElement(string name, string ns)
        {
            _nodes++;
            AddEstimatedBytes(Encoding.UTF8.GetByteCount(name + ns) * 6L + 16);
            CheckNodes();
        }

        public void AddAttribute(string name, string value, string ns = "")
        {
            _nodes++;
            AddEstimatedBytes(Encoding.UTF8.GetByteCount(name + ns + value) * 6L + 8);
            CheckNodes();
        }

        public void AddValue(string value)
        {
            _nodes++;
            AddEstimatedBytes(Encoding.UTF8.GetByteCount(value) * 6L);
            CheckNodes();
        }

        public void AddSubtree(XElement element)
        {
            foreach (var item in element.DescendantsAndSelf())
            {
                AddElement(item.Name.LocalName, item.Name.NamespaceName);
                foreach (var attribute in item.Attributes()) AddAttribute(attribute.Name.LocalName, attribute.Value);
                foreach (var value in item.Nodes().OfType<XText>()) AddValue(value.Value);
            }
        }

        private void CheckNodes()
        {
            if (_nodes > MaxGeneratedXmlNodes) throw new InvalidDataException($"Generated XML exceeds the {MaxGeneratedXmlNodes} node generation limit.");
        }

        private void AddEstimatedBytes(long count)
        {
            _estimatedBytes += count;
            if (_estimatedBytes > MaxPackageBytes) throw new InvalidDataException("Generated XML exceeds the 20 MB generation limit.");
        }
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
        return LocalName(member.BaseType ?? member.Type) switch
        {
            "boolean" => "true",
            "integer" or "int" or "long" or "short" or "byte" or "decimal" or "positiveInteger" or "nonNegativeInteger"
                or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" => "1",
            "negativeInteger" => "-1", "nonPositiveInteger" => "0", "float" or "double" => "1.5",
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
            foreach (var file in PackageRoots(files)) { using var reader = XmlReader.Create(new StringReader(file.Text), ReaderSettings(), SchemaUri(file.Name)); set.Add(null, reader); }
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
                    var referenceKind = attr == "ref" && node.Name.LocalName == "attribute" ? "attribute" : null;
                    var target = files.FirstOrDefault(f => f.TargetNamespace == qname.Namespace && f.Declarations.Any(d => d.Name == qname.Name
                        && (referenceKind is not null ? d.Kind == referenceKind : d.Kind is "simpleType" or "complexType" or "element")));
                    if (target is not null && !ReferenceEquals(target, file))
                    {
                        var relation = attr == "base" ? "extends" : attr == "ref" ? referenceKind == "attribute" ? "references attribute" : "references element" : "uses type";
                        var reference = new SchemaReference(relation, qname.Name, file.Name, target.Name, ElementPath(node));
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
                var rootType = (string?)root.Attribute("type") ?? root.Descendants(xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value;
                var typeFile = FindTypeFile(files, file, rootType, root);
                var facets = ExtractFacets(root).Concat(ReferencedSimpleTypeFacets(root, files)).Distinct().ToArray();
                var attributes = ResolveElementAttributes(root, file, files, schemas, new HashSet<string>(StringComparer.Ordinal), 0);
                result.Add(new((string)root.Attribute("name")!, rootType, "1", "1", file.Name, null,
                    typeFile?.Name, null, facets, members, file.TargetNamespace ?? "", null,
                    (string?)root.Attribute("default"), (string?)root.Attribute("fixed"), (string?)root.Attribute("nillable") == "true", attributes,
                    ResolveSimplePrimitiveBase(root, rootType, files)));
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
                    .Select(attribute => attribute with { InheritedFrom = LocalName(baseAttribute.Value) }));
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
                    var global = docs[candidate.Name].Root!.Elements(xs + "attribute")
                        .FirstOrDefault(a => (string?)a.Attribute("name") == qname.Name);
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
            var facets = ExtractFacets(attribute).Concat(ReferencedSimpleTypeFacets(declaration, files)).Distinct().ToArray();
            if (attributeNamespace.Length == 0) attributeNamespace = AttributeNamespace(attribute, typeFile ?? owner);
            result.RemoveAll(existing => existing.Name == name && existing.NamespaceUri == attributeNamespace);
            result.Add(new(name, lexicalType, (string?)attribute.Attribute("use") ?? "optional", declarationFile.Name,
                typeSource?.Name, facets, (string?)attribute.Attribute("default") ?? (string?)declaration.Attribute("default"),
                (string?)attribute.Attribute("fixed") ?? (string?)declaration.Attribute("fixed"), attributeNamespace, null,
                ResolveSimplePrimitiveBase(declaration, lexicalType, files)));
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
                    ?? child.Descendants(xs + "restriction").FirstOrDefault()?.Attribute("base")?.Value;
                var source = FindElementFile(files, owner, child, name);
                result.Add(new(name, type, (string?)child.Attribute("minOccurs") ?? "1", (string?)child.Attribute("maxOccurs") ?? "1", source,
                    null, FindTypeFile(files, owner, type, child)?.Name, groupKind,
                    ExtractFacets(child).Concat(ReferencedSimpleTypeFacets(child, files)).Distinct().ToArray(),
                    ResolveElementMembers(child, owner, files, docs, stack, depth + 1), ElementNamespace(child, owner, files, source), groupId,
                    (string?)child.Attribute("default"), (string?)child.Attribute("fixed"), (string?)child.Attribute("nillable") == "true",
                    ResolveElementAttributes(child, owner, files, docs, stack, depth + 1), ResolveSimplePrimitiveBase(child, type, files)));
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
        var current = lexical;
        var currentContext = element;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var facets = new List<SchemaFacet>();
        for (var depth = 0; depth < 32; depth++)
        {
            var qname = ResolveQName(currentContext, current);
            if (qname.Namespace == "http://www.w3.org/2001/XMLSchema" || !visited.Add(qname.Namespace + "#" + qname.Name)) break;
            var file = files.FirstOrDefault(candidate => candidate.TargetNamespace == qname.Namespace);
            if (file is null) break;
            var doc = LoadSafe(file.Text);
            var xs = doc.Root!.Name.Namespace;
            var simple = doc.Root.Elements(xs + "simpleType").FirstOrDefault(t => (string?)t.Attribute("name") == qname.Name);
            if (simple is null) break;
            facets.AddRange(ExtractFacets(simple));
            var restriction = simple.Element(xs + "restriction");
            if (restriction?.Attribute("base") is not { } baseAttribute) break;
            current = baseAttribute.Value;
            currentContext = restriction;
        }
        return facets.Distinct().ToArray();
    }

    private static string? ResolveSimplePrimitiveBase(XElement context, string? lexicalType, IReadOnlyList<SchemaFile> files)
    {
        if (string.IsNullOrWhiteSpace(lexicalType)) return null;
        var current = lexicalType;
        var currentContext = context;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 32; depth++)
        {
            var qname = ResolveQName(currentContext, current);
            if (qname.Namespace == "http://www.w3.org/2001/XMLSchema") return "xs:" + qname.Name;
            var identity = qname.Namespace + "#" + qname.Name;
            if (!visited.Add(identity)) return current;
            var typeFile = files.FirstOrDefault(file => file.TargetNamespace == qname.Namespace);
            if (typeFile is null) return current;
            var document = LoadSafe(typeFile.Text);
            var xs = document.Root!.Name.Namespace;
            var simpleType = document.Root.Elements(xs + "simpleType").FirstOrDefault(type => (string?)type.Attribute("name") == qname.Name);
            var restriction = simpleType?.Element(xs + "restriction");
            if (restriction?.Attribute("base") is not { } baseAttribute) return current;
            current = baseAttribute.Value;
            currentContext = restriction;
        }
        return current;
    }

    private const int MaxGeneratedTestCases = 5000;

    private static IReadOnlyList<SchemaTestCase> BuildTests(IReadOnlyList<EffectiveMember> roots, out bool truncated)
    {
        var result = new List<SchemaTestCase>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var didTruncate = false;
        foreach (var root in roots)
        {
            if (didTruncate) break;
            var rootPath = "/" + root.Name;
            Add("wrong-namespace:" + root.Name, rootPath, "wrong namespace", "urn:schema-studio:wrong-namespace", false, root.DeclaredIn);
            Add("wrong-prefix-binding:" + root.Name, rootPath, "wrong namespace prefix binding", "urn:schema-studio:wrong-prefix-binding", false, root.DeclaredIn);
            Add("missing-namespace:" + root.Name, rootPath, "missing namespace", "", false, root.DeclaredIn);
            Add("malformed-xml:" + root.Name, rootPath, "malformed XML", "(not well formed)", false, root.DeclaredIn);
            foreach (var attribute in root.Attributes ?? [])
            {
                if (didTruncate) break;
                AddAttributeTests(attribute, rootPath);
            }
            Visit(root.Children, rootPath);
        }
        truncated = didTruncate;
        return result;

        void Visit(IReadOnlyList<EffectiveMember> members, string parentPath)
        {
            if (didTruncate) return;
            foreach (var sequence in members.Where(m => m.Compositor == "sequence").GroupBy(m => m.CompositorGroup))
            {
                if (didTruncate) return;
                if (sequence.Count() > 1) Add("order-invalid:" + parentPath + ":" + sequence.Key, parentPath, "wrong element order", "reverse sequence", false, sequence.First().DeclaredIn);
            }
            foreach (var choice in members.Where(m => m.Compositor == "choice").GroupBy(m => m.CompositorGroup))
            {
                if (didTruncate) return;
                if (choice.Count() > 1)
                {
                    foreach (var branch in choice)
                    {
                        if (didTruncate) return;
                        Add("choice-branch:" + parentPath + "/" + branch.Name, parentPath + "/" + branch.Name, "choice branch", branch.Name, true, branch.DeclaredIn);
                    }
                    Add("choice-multiple:" + choice.Key, parentPath, "choice with multiple branches", string.Join("|", choice.Select(m => m.Name)), false, choice.First().DeclaredIn);
                }
            }
            foreach (var member in members)
            {
                if (didTruncate) return;
                var path = parentPath + "/" + member.Name;
                foreach (var attribute in member.Attributes ?? [])
                {
                    if (didTruncate) return;
                    AddAttributeTests(attribute, path);
                }
                if (member.MinOccurs != "0") Add("required:" + path, path, "missing required element", "(omitted)", false, member.DeclaredIn);
                else Add("optional-omitted:" + path, path, "optional element omitted", "(omitted)", true, member.DeclaredIn);
                var materialized = member.MinOccurs != "0" && (member.Compositor != "choice" || members.FirstOrDefault(m => m.Compositor == "choice" && m.CompositorGroup == member.CompositorGroup) == member);
                if (materialized && member.Children.Count > 0) Add("parent-missing:" + path, path, "missing parent element", "(parent omitted)", false, member.DeclaredIn);
                if (materialized) Add("wrong-member-namespace:" + path, path, "wrong element namespace", "urn:schema-studio:wrong-namespace", false, member.DeclaredIn);
                if (materialized && (LocalName(member.Type) != "string" || member.Facets.Any(f => f.Kind == "minLength" && int.TryParse(f.Value, out var length) && length > 0)))
                    Add("empty-required:" + path, path, "empty required value", "(empty)", false, member.DeclaredIn);
                if (int.TryParse(member.MinOccurs, out var minimum) && minimum > 1) Add("occurs-underflow:" + path, path, "minOccurs - 1", (minimum - 1).ToString(), false, member.DeclaredIn);
                if (member.MaxOccurs != "1" && member.MaxOccurs != "unbounded" && int.TryParse(member.MaxOccurs, out var max))
                    Add("occurs-overflow:" + path, path, "maxOccurs + 1", (max + 1).ToString(), false, member.DeclaredIn);
                foreach (var facet in member.Facets)
                {
                    if (didTruncate) return;
                    if (facet.Kind == "enumeration")
                    {
                        Add("enum-valid:" + path + ":" + facet.Value, path, "known enumeration", facet.Value, true, member.DeclaredIn);
                        Add("enum-invalid:" + path, path, "unknown enumeration", "__unknown_value__", false, member.DeclaredIn);
                    }
                    if (facet.Kind == "length" && int.TryParse(facet.Value, out var exactLength) && exactLength >= 0)
                    {
                        if (exactLength <= MaxGeneratedBoundaryCharacters)
                            Add("length-boundary:" + path + facet.Kind, path, "length boundary", new string('a', exactLength), true, member.DeclaredIn);
                        if (exactLength < MaxGeneratedBoundaryCharacters)
                            Add("length-outside:" + path + facet.Kind, path, "length outside boundary", new string('a', exactLength + 1), false, member.DeclaredIn);
                    }
                    if (facet.Kind == "minLength" && int.TryParse(facet.Value, out var lower) && lower >= 0)
                    {
                        if (lower <= MaxGeneratedBoundaryCharacters)
                            Add("length-boundary:" + path + facet.Kind, path, "minLength boundary", new string('a', lower), true, member.DeclaredIn);
                        if (lower > 0 && lower - 1 <= MaxGeneratedBoundaryCharacters)
                            Add("length-outside:" + path + facet.Kind, path, "below minLength", new string('a', lower - 1), false, member.DeclaredIn);
                    }
                    if (facet.Kind == "maxLength" && int.TryParse(facet.Value, out var upper) && upper >= 0)
                    {
                        if (upper <= MaxGeneratedBoundaryCharacters)
                            Add("length-boundary:" + path + facet.Kind, path, "maxLength boundary", new string('a', upper), true, member.DeclaredIn);
                        if (upper < MaxGeneratedBoundaryCharacters)
                            Add("length-outside:" + path + facet.Kind, path, "above maxLength", new string('a', upper + 1), false, member.DeclaredIn);
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
                if (HasLexicalTypeMutation(LocalName(member.BaseType) ?? typeName))
                    Add("wrong-type:" + path, path, "wrong datatype", "not-a-valid-" + typeName, false, member.DeclaredIn);
                Visit(member.Children, path);
            }
        }
        void AddAttributeTests(EffectiveAttribute attribute, string ownerPath)
        {
            var path = ownerPath + "/@" + attribute.Name;
            if (attribute.Use == "required")
                Add("required-attribute:" + path, path, "missing required attribute", "(omitted)", false, attribute.DeclaredIn);
            var typeName = LocalName(attribute.Type);
            if (HasLexicalTypeMutation(LocalName(attribute.BaseType) ?? typeName))
                Add("wrong-attribute-type:" + path, path, "wrong datatype", "not-a-valid-" + typeName, false, attribute.DeclaredIn);
        }
        void Add(string id, string path, string rule, string value, bool expected, string source)
        {
            if (didTruncate) return;
            var stableId = StableTestId(id);
            if (identifiers.Contains(stableId)) return;
            if (result.Count >= MaxGeneratedTestCases) { didTruncate = true; return; }
            identifiers.Add(stableId);
            result.Add(new(stableId, path, rule, value, expected, source));
        }
    }

    private static bool HasLexicalTypeMutation(string? typeName) => typeName is
        "decimal" or "float" or "double" or "integer" or "int" or "long" or "short" or "byte"
        or "positiveInteger" or "nonNegativeInteger" or "negativeInteger" or "nonPositiveInteger"
        or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte"
        or "date" or "dateTime" or "time" or "boolean";

    private static string StableTestId(string identity) => "xsd-" + Fingerprint(identity)[..24];

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
    private static string AttributeNamespace(XElement attribute, SchemaFile owner)
    {
        if ((string?)attribute.Attribute("ref") is { } reference) return ResolveQName(attribute, reference).Namespace;
        var schema = LoadSafe(owner.Text).Root!;
        var qualified = (string?)attribute.Attribute("form") switch
        {
            "qualified" => true,
            "unqualified" => false,
            _ => (string?)schema.Attribute("attributeFormDefault") == "qualified"
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
    private static IReadOnlyList<SchemaFile> PackageRoots(IReadOnlyList<SchemaFile> files)
    {
        var referenced = files.SelectMany(file => file.Links).Where(link => link.ResolvedFile is not null)
            .Select(link => link.ResolvedFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = files.Where(file => !referenced.Contains(file.Name)).ToArray();
        return roots.Length == 0 ? files : roots;
    }
    private static string SchemaUri(string name) => "schema://package/" + string.Join('/',
        name.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
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
            var name = Uri.UnescapeDataString(absoluteUri.AbsolutePath.TrimStart('/')).Replace('\\', '/');
            var file = files.SingleOrDefault(f => string.Equals(f.Name.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
            if (file is null) throw new XmlException("A linked XSD is not available in the imported package.");
            return new MemoryStream(Encoding.UTF8.GetBytes(file.Text), writable: false);
        }
    }
}
