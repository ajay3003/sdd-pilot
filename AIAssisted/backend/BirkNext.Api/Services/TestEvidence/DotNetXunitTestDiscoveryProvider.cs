using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Sdd;
using BirkNext.TestEvidence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.TestEvidence;

/// <summary>
/// Source test discovery for .NET/xUnit (<c>test.discovery.dotnet.xunit</c>). Runs once at Source Analysis upload over the archive already in memory and
/// answers only "what tests exist in source": test projects (from project metadata, never folder names), [Fact]/[Theory] methods, traits, declared
/// skips/data rows and explicit requirement/acceptance-criterion references. It never claims a test ran. NUnit/MSTest projects are reported with
/// discovery unsupported. Generic: no repository-, project- or filename-specific rules.
/// </summary>
public static class DotNetXunitTestDiscoveryProvider
{
    public const int Version = 1;
    private const int MaxDefinitions = 20_000;
    private const int MaxReferencesPerTest = 50;
    private const int MaxTrxConfiguration = 200;

    /// <summary>Trait names whose value is an explicit requirement / acceptance-criterion identifier (a convention, not a project rule).</summary>
    private static readonly HashSet<string> RequirementTraitNames = new(StringComparer.OrdinalIgnoreCase) { "Requirement", "Requirements", "RequirementId", "Req", "Specification", "Spec", "UserStory", "Story" };
    private static readonly HashSet<string> AcceptanceTraitNames = new(StringComparer.OrdinalIgnoreCase) { "AcceptanceCriterion", "AcceptanceCriteria", "AC", "Scenario", "AcceptanceScenario" };
    private static readonly string[] TestPackagePrefixes = ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest", "Microsoft.Testing.", "coverlet.", "bunit", "Testcontainers",
        "Microsoft.AspNetCore.Mvc.Testing", "Shouldly", "NSubstitute", "Moq", "FluentAssertions", "AwesomeAssertions", "WireMock.Net", "Microsoft.Playwright", "Verify."];

    private sealed record ProjectInfo(string Name, string Path, string Directory, SourceTestProject Project);

    public static SourceTestInventory Discover(Guid snapshotId, string snapshotFingerprint, string? repositoryName, IqrSourceArchiveReader.Workspace workspace, CancellationToken ct = default)
    {
        var limitations = new List<string>();
        var props = workspace.Files.Where(f => Path.GetFileName(f.Path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)).ToList();
        var projects = new List<ProjectInfo>();
        foreach (var file in workspace.Files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var directory = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
            var xml = Load(file.Content);
            if (xml is null) { limitations.Add($"Project not parsed: {Safe(file.Path)}."); continue; }
            // Directory.Build.props in the project's directory or an ancestor contributes package references and properties.
            var inherited = props.Where(p => directory.StartsWith(p.Path[..(p.Path.LastIndexOf('/') + 1)], StringComparison.Ordinal) || !p.Path.Contains('/'))
                .Select(p => Load(p.Content)).OfType<XDocument>().ToList();
            var documents = inherited.Append(xml).ToList();
            var packages = documents.SelectMany(d => d.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value).OfType<string>().Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var isTestProperty = documents.SelectMany(d => d.Descendants().Where(e => e.Name.LocalName == "IsTestProject")).Select(e => e.Value.Trim()).LastOrDefault();
            if (isTestProperty?.Equals("false", StringComparison.OrdinalIgnoreCase) == true) continue;
            var framework = Framework(packages);
            var isTest = isTestProperty?.Equals("true", StringComparison.OrdinalIgnoreCase) == true || framework.Name is not null
                || packages.Contains("Microsoft.NET.Test.Sdk", StringComparer.OrdinalIgnoreCase);
            if (!isTest) continue;
            var name = Path.GetFileNameWithoutExtension(file.Path);
            var (kind, confidence, basis) = ProjectKind(name, packages);
            projects.Add(new ProjectInfo(name, file.Path, directory, new SourceTestProject
            {
                Name = Safe(name), Path = Safe(file.Path), Framework = framework.Name, FrameworkPackage = framework.Package,
                Packages = packages.Where(p => TestPackagePrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Select(Safe).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                TrxReportConfigured = packages.Contains("Microsoft.Testing.Extensions.TrxReport", StringComparer.OrdinalIgnoreCase),
                Kind = kind, KindConfidence = confidence, KindBasis = basis, DiscoverySupported = framework.Name == "xUnit",
            }));
        }

        foreach (var unsupported in projects.Where(p => p.Project.Framework is "NUnit" or "MSTest").GroupBy(p => p.Project.Framework))
            limitations.Add($"{unsupported.Key} source test discovery is not implemented ({unsupported.Count()} project(s)); TRX results of these tests import without source correlation.");
        if (projects.Any(p => p.Project.Framework is null))
            limitations.Add("Some test projects reference only the test SDK; their framework is unknown and their tests are not discovered.");

        var definitions = new List<SourceTestDefinition>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var partial = false;
        foreach (var file in workspace.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var owner = projects.Where(p => file.Path.StartsWith(p.Directory, StringComparison.Ordinal)).OrderByDescending(p => p.Directory.Length).FirstOrDefault();
            if (owner is null || !owner.Project.DiscoverySupported) continue;
            if (!file.Content.Contains("Fact", StringComparison.Ordinal) && !file.Content.Contains("Theory", StringComparison.Ordinal)) continue;
            var root = Parse(WolverineSourceAnalyzer.BlankPrimaryConstructors(file.Content), ct);
            if (root.ContainsDiagnostics)
            {
                // The bundled parser predates C# 12 collection expressions; error recovery can drop every member after one. Rewriting simple
                // single-line ones to array creation on the same line keeps members and line numbers (syntax only — nothing is compiled).
                var rewritten = Parse(NeutralizeCollectionExpressions(WolverineSourceAnalyzer.BlankPrimaryConstructors(file.Content)), ct);
                if (rewritten.GetDiagnostics().Count() < root.GetDiagnostics().Count()) root = rewritten;
            }
            if (root.ContainsDiagnostics) { partial = true; limitations.Add($"Partial syntax analysis: {Safe(file.Path)}; tests in it may be incomplete."); }
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (definitions.Count >= MaxDefinitions) { partial = true; break; }
                var attributes = method.AttributeLists.SelectMany(a => a.Attributes).ToList();
                var testAttribute = attributes.FirstOrDefault(a => AttributeName(a) is "Fact" or "Theory");
                if (testAttribute is null) continue;
                var types = method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().ToList();
                if (types.Count == 0) continue;
                var definition = Define(file.Path, owner, repositoryName, method, testAttribute, attributes, types, counts);
                definitions.Add(definition);
            }
            if (definitions.Count >= MaxDefinitions) { limitations.Add($"Test definition limit reached ({MaxDefinitions:N0}); further tests are not discovered."); break; }
        }

        var byProject = definitions.GroupBy(d => d.Project).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var finalProjects = projects.Select(p => p.Project with { DefinitionCount = byProject.GetValueOrDefault(p.Project.Name) }).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        limitations.Add("Syntax-only discovery: custom attributes derived from FactAttribute, inherited test methods and MemberData/ClassData rows are not resolved.");
        limitations.Add("A discovered test is source evidence only. Whether it ran and passed comes from imported execution results.");
        var status = finalProjects.Count == 0 ? SourceTestDiscoveryStatus.NoTestProjects
            : finalProjects.All(p => !p.DiscoverySupported) ? SourceTestDiscoveryStatus.Unsupported
            : partial || finalProjects.Any(p => !p.DiscoverySupported) ? SourceTestDiscoveryStatus.Partial : SourceTestDiscoveryStatus.Complete;
        return new SourceTestInventory
        {
            ProviderVersion = Version, SnapshotId = snapshotId, SnapshotFingerprint = snapshotFingerprint, RepositoryName = repositoryName is null ? null : Safe(repositoryName),
            Status = status, Projects = finalProjects, Definitions = definitions, TrxConfiguration = TrxConfiguration(workspace, finalProjects),
            Limitations = limitations.Distinct().ToList(),
        };
    }

    private static SourceTestDefinition Define(string path, ProjectInfo owner, string? repository, MethodDeclarationSyntax method, AttributeSyntax testAttribute,
        List<AttributeSyntax> attributes, List<TypeDeclarationSyntax> types, Dictionary<string, int> counts)
    {
        var ns = string.Join('.', method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
        var className = (ns.Length > 0 ? ns + "." : "") + string.Join('+', types.Select(t => t.Identifier.ValueText));
        var fqn = className + "." + method.Identifier.ValueText;
        var identity = $"{repository ?? ""}::{owner.Name}::{fqn}";
        // Two methods with the same name in one class (overloads) share an identity; the ordinal keeps both definitions and correlation reports ambiguity.
        var ordinal = counts[identity] = counts.GetValueOrDefault(identity) + 1;
        var traits = attributes.Where(a => AttributeName(a) == "Trait").Select(a => Trait(a, "Method"))
            .Concat(types.SelectMany(t => t.AttributeLists.SelectMany(l => l.Attributes)).Where(a => AttributeName(a) == "Trait").Select(a => Trait(a, "Class")))
            .OfType<SourceTestTrait>().Distinct().ToList();
        var categories = traits.Where(t => t.Name.Equals("Category", StringComparison.OrdinalIgnoreCase)).Select(t => t.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var declared = categories.Select(c => c.ToLowerInvariant() switch { "unit" => TestKind.Unit, "integration" => TestKind.Integration, "contract" => TestKind.Contract, _ => (TestKind?)null })
            .FirstOrDefault(k => k is not null);
        var kind = declared ?? owner.Project.Kind;
        var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new SourceTestDefinition
        {
            TestDefinitionId = Hash(identity + (ordinal > 1 ? $"#{ordinal}" : "")), StableIdentity = Safe(identity) + (ordinal > 1 ? $"#{ordinal}" : ""),
            Project = Safe(owner.Name), FilePath = Safe(path), Line = line, Namespace = ns.Length == 0 ? null : Safe(ns),
            ClassName = Safe(className), MethodName = Safe(method.Identifier.ValueText), FullyQualifiedName = Safe(fqn),
            DisplayName = Named(testAttribute, "DisplayName") is { Length: > 0 } display ? Bound(display, 300) : null,
            Framework = "xUnit", DefinitionKind = AttributeName(testAttribute) == "Theory" ? TestDefinitionKind.Theory : TestDefinitionKind.Fact,
            Kind = kind, KindConfidence = declared is not null ? TestEvidenceConfidence.Confirmed : owner.Project.KindConfidence,
            KindBasis = declared is not null ? $"Declared category '{categories.First(c => c.Equals(declared.ToString(), StringComparison.OrdinalIgnoreCase))}'" : owner.Project.KindBasis,
            SkipDeclared = Named(testAttribute, "Skip") is not null,
            InlineDataRows = attributes.Count(a => AttributeName(a) == "InlineData"),
            HasDynamicData = attributes.Any(a => AttributeName(a) is "MemberData" or "ClassData"),
            Traits = traits, Categories = categories,
            References = References(method, types[^1], traits),
            SourceFingerprint = Hash(method.ToFullString().Replace("\r\n", "\n")),
        };
    }

    /// <summary>Explicit identifiers tied to one test, strongest evidence per identifier. Only identifiers within this method's own syntax, its traits
    /// or its class can become references; a mention in a helper or another method never links this test.</summary>
    private static List<SourceTestReference> References(MethodDeclarationSyntax method, TypeDeclarationSyntax type, List<SourceTestTrait> traits)
    {
        var found = new Dictionary<string, SourceTestReference>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, TestReferenceKind kind, TestEvidenceConfidence confidence, string basis, int line)
        {
            if (found.Count >= MaxReferencesPerTest && !found.ContainsKey(id)) return;
            if (found.TryGetValue(id, out var existing) && existing.Confidence <= confidence) return;
            found[id] = new SourceTestReference(Safe(id), kind, confidence, basis, line);
        }
        static TestReferenceKind KindOf(string id) => id.StartsWith("AC-", StringComparison.OrdinalIgnoreCase) ? TestReferenceKind.AcceptanceCriterion : TestReferenceKind.Requirement;
        int LineOf(SyntaxTrivia t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var methodLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        foreach (var trait in traits)
        {
            var isAcceptance = AcceptanceTraitNames.Contains(trait.Name);
            if (!isAcceptance && !RequirementTraitNames.Contains(trait.Name)) continue;
            foreach (var id in RequirementReferenceParser.Extract(trait.Value))
                Add(id, isAcceptance ? TestReferenceKind.AcceptanceCriterion : KindOf(id), TestEvidenceConfidence.Confirmed, $"[Trait(\"{trait.Name}\")] on the test {trait.Scope.ToLowerInvariant()}", methodLine);
        }

        var bodyStart = method.Body?.SpanStart ?? method.ExpressionBody?.SpanStart ?? method.Span.End;
        foreach (var trivia in method.GetLeadingTrivia().Concat(method.DescendantTrivia(descendIntoTrivia: true)).Where(IsComment).Distinct())
        {
            var text = CommentText(trivia);
            var inBody = trivia.SpanStart >= bodyStart;
            var structured = RequirementReferenceParser.StartsWithReference(text);
            var documentation = trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
            var (confidence, basis) = documentation && !inBody ? (TestEvidenceConfidence.StronglySupported, "Documentation comment of the test method")
                : structured ? (TestEvidenceConfidence.StronglySupported, inBody ? "Structured comment in the test body" : "Structured comment on the test method")
                : (TestEvidenceConfidence.Inferred, inBody ? "Mentioned in a comment in the test body" : "Mentioned in a comment on the test method");
            foreach (var id in RequirementReferenceParser.Extract(text, requireUpperCase: true)) Add(id, KindOf(id), confidence, basis, LineOf(trivia));
        }
        // Only the body: attribute arguments (skip reasons, display names, inline data) are not statements about what the test verifies.
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        foreach (var literal in (body?.DescendantNodes() ?? []).OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            foreach (var id in RequirementReferenceParser.Extract(literal.Token.ValueText, requireUpperCase: true))
                Add(id, KindOf(id), TestEvidenceConfidence.Inferred, "Mentioned in a string in the test body (assertion message or test data)", literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        foreach (var interpolated in (body?.DescendantNodes() ?? []).OfType<InterpolatedStringTextSyntax>())
            foreach (var id in RequirementReferenceParser.Extract(interpolated.TextToken.ValueText, requireUpperCase: true))
                Add(id, KindOf(id), TestEvidenceConfidence.Inferred, "Mentioned in a string in the test body (assertion message or test data)", interpolated.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        // The class's own comments/documentation describe all its tests; per-test scope cannot be established, so they stay candidates.
        foreach (var trivia in type.GetLeadingTrivia().Where(IsComment))
            foreach (var id in RequirementReferenceParser.Extract(CommentText(trivia), requireUpperCase: true))
                Add(id, KindOf(id), TestEvidenceConfidence.Inferred, "Mentioned in the test class comment (applies to the class, not established per test)", LineOf(trivia));
        return found.Values.OrderBy(r => r.Confidence).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static CompilationUnitSyntax Parse(string text, CancellationToken ct) =>
        CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct).GetCompilationUnitRoot(ct);

    /// <summary>"[a, b]" in expression position (after '(', ',', '=', '=>', ':', 'return' or 'yield return') → "new[]{a, b}" on the same line. Attributes and
    /// indexers are never in those positions. Only simple, single-line, non-nested forms; anything else stays a stated partial-parse limitation.</summary>
    internal static string NeutralizeCollectionExpressions(string text) =>
        Regex.Replace(text, @"(?<=(?:[(,=:]|=>|\breturn|\byield\s+return)\s*)\[(?<items>[^\[\]\r\n;{}]*)\]", m => "new[]{" + m.Groups["items"].Value + "}");

    private static bool IsComment(SyntaxTrivia t) => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static string CommentText(SyntaxTrivia trivia)
    {
        var text = trivia.ToFullString();
        text = Regex.Replace(text, @"(?m)^\s*(///|//|/\*+|\*+/?)", " ");
        text = Regex.Replace(text, "<[^>]+>", " ");
        return text.Trim();
    }

    private static (string? Name, string? Package) Framework(List<string> packages)
    {
        string? Has(params string[] names) => names.FirstOrDefault(n => packages.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (Has("xunit.v3", "xunit.v3.core", "xunit.v3.extensibility.core") is { } v3) return ("xUnit", v3);
        if (Has("xunit", "xunit.core", "xunit.extensibility.core") is { } v2) return ("xUnit", v2);
        if (Has("NUnit") is { } nunit) return ("NUnit", nunit);
        if (Has("MSTest", "MSTest.TestFramework") is { } mstest) return ("MSTest", mstest);
        return (null, null);
    }

    /// <summary>Project-level kind: bUnit marks component tests (rendered components, not a browser); otherwise the last meaningful project-name token.
    /// Testcontainers or Mvc.Testing alone do not make a project an integration test of a runtime environment.</summary>
    private static (TestKind, TestEvidenceConfidence, string) ProjectKind(string name, List<string> packages)
    {
        if (packages.Any(p => p.Equals("bunit", StringComparison.OrdinalIgnoreCase) || p.StartsWith("bunit.", StringComparison.OrdinalIgnoreCase)))
            return (TestKind.FrontendComponent, TestEvidenceConfidence.StronglySupported, "bUnit component-test package (rendered components, not browser E2E)");
        var tokens = name.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && tokens[^1] is "Tests" or "Test" or "Testing") tokens.RemoveAt(tokens.Count - 1);
        var last = tokens[^1];
        var kind = last switch
        {
            _ when Regex.IsMatch(last, "^(Unit|UnitTests?)$", RegexOptions.IgnoreCase) => TestKind.Unit,
            _ when Regex.IsMatch(last, "^(Integration|IntegrationTests?)$", RegexOptions.IgnoreCase) => TestKind.Integration,
            _ when Regex.IsMatch(last, "^(Contract|Contracts|ContractTests?)$", RegexOptions.IgnoreCase) => TestKind.Contract,
            _ => TestKind.Unknown,
        };
        return kind == TestKind.Unknown
            ? (TestKind.Unknown, TestEvidenceConfidence.Unresolved, "Project name and packages do not establish a test kind")
            : (kind, TestEvidenceConfidence.StronglySupported, $"Project name token '{Safe(last)}'");
    }

    private static List<TrxConfigurationEvidence> TrxConfiguration(IqrSourceArchiveReader.Workspace workspace, List<SourceTestProject> projects)
    {
        var evidence = projects.Where(p => p.TrxReportConfigured)
            .Select(p => new TrxConfigurationEvidence(p.Path, 1, "TrxReportPackage", "Microsoft.Testing.Extensions.TrxReport referenced (TRX can be generated)")).ToList();
        foreach (var file in (workspace.ConfigurationFiles ?? []).Where(f => f.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = file.Content.Split('\n');
            for (var i = 0; i < lines.Length && evidence.Count < MaxTrxConfiguration; i++)
            {
                var line = lines[i];
                if (line.Contains("--report-trx", StringComparison.Ordinal))
                    evidence.Add(new(Safe(file.Path), i + 1, "TrxGeneration", "dotnet test --report-trx (Microsoft.Testing.Platform)"));
                else if (Regex.IsMatch(line, @"--logger\s+[""']?trx", RegexOptions.IgnoreCase))
                    evidence.Add(new(Safe(file.Path), i + 1, "TrxGeneration", "dotnet test --logger trx (VSTest)"));
                if (line.Contains("PublishTestResults@", StringComparison.Ordinal))
                {
                    var window = string.Join('\n', lines.Skip(i).Take(8));
                    var trx = Regex.IsMatch(window, @"testResultsFiles:\s*['""]?[^\n]*\.trx", RegexOptions.IgnoreCase) || Regex.IsMatch(window, @"testResultsFormat:\s*['""]?VSTest", RegexOptions.IgnoreCase);
                    evidence.Add(new(Safe(file.Path), i + 1, "TrxPublication", trx ? "PublishTestResults publishes TRX results to the pipeline" : "PublishTestResults (format not TRX or not stated)"));
                }
            }
        }
        return evidence.Take(MaxTrxConfiguration).ToList();
    }

    private static SourceTestTrait? Trait(AttributeSyntax attribute, string scope)
    {
        var args = attribute.ArgumentList?.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).ToList();
        return args is { Count: 2 } ? new SourceTestTrait(Bound(args[0].Token.ValueText, 100), Bound(args[1].Token.ValueText, 200), scope) : null;
    }

    private static string? Named(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)?.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)
            ? l.Token.ValueText : attribute.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.ValueText == name) == true ? "" : null;

    private static string AttributeName(AttributeSyntax a)
    {
        var name = a.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static XDocument? Load(string content)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException) { return null; }
    }

    private static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);
    private static string Bound(string value, int max) => TestEvidenceText.Bound(value, max);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
