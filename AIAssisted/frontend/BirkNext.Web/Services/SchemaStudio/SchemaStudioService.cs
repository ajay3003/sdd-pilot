using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

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
    bool GeneratedTestsTruncated = false, SchemaIdentity? Identity = null, IReadOnlyList<SchemaUnresolvedReference>? UnresolvedReferences = null);

/// <summary>
/// Schema Studio's browser session over the shared XSD capability (<see cref="XmlSchemaPackageAnalyzer"/>): package versions, XML generation and
/// mutation cases. Parsing, dependency resolution, the effective schema and validation are the shared, backend-reusable implementation.
/// Imported XSD is treated as data, never fetched or executed.
/// </summary>
public sealed class SchemaStudioService
{
    public const int MaxFiles = XmlSchemaPackageAnalyzer.MaxFiles;
    public const int MaxFileBytes = XmlSchemaPackageAnalyzer.MaxFileBytes;
    public const int MaxPackageBytes = XmlSchemaPackageAnalyzer.MaxPackageBytes;
    public const int MaxSessionBytes = 50 * 1024 * 1024;
    public const int MaxPackageVersions = 20;
    public const int MaxRelationships = XmlSchemaPackageAnalyzer.MaxRelationships;
    public const int MaxSchemaNodes = XmlSchemaPackageAnalyzer.MaxSchemaNodes;
    public const int MaxXmlNodes = XmlSchemaPackageAnalyzer.MaxXmlNodes;
    public const int MaxGeneratedXmlNodes = 50_000;
    public const int MaxGeneratedBoundaryCharacters = 4096;
    public const int MaxNestingDepth = XmlSchemaPackageAnalyzer.MaxNestingDepth;
    private readonly List<SchemaPackage> _packages = [];
    public IReadOnlyList<SchemaPackage> Packages => _packages;

    public SchemaPackage Import(IReadOnlyDictionary<string, string> files, string packageName, SchemaProvenance? provenance = null)
    {
        if (_packages.Count >= MaxPackageVersions) throw new InvalidDataException($"This session is limited to {MaxPackageVersions} imported package versions. Remove a version before importing another.");
        if (files.Count > 0 && files.Count <= MaxFiles && files.All(f => Encoding.UTF8.GetByteCount(f.Value) <= MaxFileBytes)
            && _packages.Sum(p => p.Files.Sum(f => Encoding.UTF8.GetByteCount(f.Text))) + files.Sum(f => Encoding.UTF8.GetByteCount(f.Value)) > MaxSessionBytes)
            throw new InvalidDataException("Schema Studio retains at most 50 MB of XSD text per browser session. Remove older versions first.");

        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        var diagnostics = analysis.Diagnostics.ToList();
        var validation = analysis.Validation;
        if (provenance is { SourceCaptureComplete: false })
        {
            diagnostics.Add($"Source Analysis stored only a bounded subset of XSD files; {provenance.OmittedSourceFiles} file(s) were omitted from this snapshot-bound package.");
            validation = "Partial";
        }
        var tests = BuildTests(analysis.EffectiveSchema, out var generatedTestsTruncated);
        var package = new SchemaPackage(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(packageName), DateTimeOffset.UtcNow, analysis.Files,
            analysis.Fingerprint, validation, diagnostics, analysis.References, analysis.EffectiveSchema, tests, analysis.ReferencesTruncated,
            provenance is null ? null : provenance with { ContractFingerprint = analysis.Files.FirstOrDefault(f => f.Name == provenance.SelectedContractPath)?.Fingerprint ?? provenance.ContractFingerprint },
            generatedTestsTruncated, analysis.Identity, analysis.UnresolvedReferences);
        _packages.Insert(0, package);
        return package;
    }

    public void Remove(string id) => _packages.RemoveAll(p => p.Id == id);

    /// <summary>Returns only the selected contract and its transitive package-local include/import dependencies.</summary>
    public static IReadOnlyDictionary<string, string> ContractPackageFiles(IReadOnlyDictionary<string, string> files, string contractPath) =>
        XmlSchemaPackageAnalyzer.DependencyClosure(files, contractPath);

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

    /// <summary>Validates through the shared validator; the text format is what Schema Studio has always shown.</summary>
    public static string ValidateXml(SchemaPackage package, string fileName, string xml) => Validate(package, fileName, xml).Message;

    /// <summary>Structured validation result from the shared validator (status, message and value-free issues).</summary>
    public static XmlSchemaValidationResult Validate(SchemaPackage package, string? fileName, string xml, string? expectedRoot = null) =>
        XmlSchemaPackageAnalyzer.ValidateXml(package.Files, package.Validation, fileName, xml, expectedRoot);

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

    public static string GenerateProfileXml(SchemaPackage package, string rootName, IReadOnlyDictionary<string, string> profileValues, SchemaGenerationProfile? profile = null)
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
        SchemaGenerationProfile? profile = null, XmlGenerationBudget? budget = null)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException("Generated XML exceeds the schema nesting limit.");
        budget ??= new XmlGenerationBudget();
        budget.AddElement(member.Name, member.NamespaceUri);
        var element = new XElement(XName.Get(member.Name, member.NamespaceUri));
        var attributeOnlyComplexType = member.Type is null && member.Attributes is { Count: > 0 };
        if (member.Children.Count == 0 && !attributeOnlyComplexType)
        {
            if (profileValues is not null && profileValues.TryGetValue(path, out var profileValue)
                && profileValue == SchemaGenerationProfilesService.NilToken)
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
            if (profile?.Kind == SchemaGenerationProfileKind.MaximumComplexity) return pair.Value[random!.Next(pair.Value.Length)];
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
            if (child.MinOccurs != "0" || profile?.Kind == SchemaGenerationProfileKind.MaximumComplexity || hasExplicitProfileValue)
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
                if (configuredCount is null && profile?.Kind == SchemaGenerationProfileKind.MaximumComplexity)
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

    private static string? LocalName(string? value) => XmlSchemaPackageAnalyzer.LocalName(value);
    private static bool SafeName(string name) => XmlSchemaPackageAnalyzer.SafeName(name);
    private static string Fingerprint(string text) => XmlSchemaPackageAnalyzer.Fingerprint(text);
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? "";
}
