using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Path = System.IO.Path;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SourceAnalysis.TestBehavior;

/// <summary>A language-specific provider of per-test facts. Only languages whose source Source Analysis retains can have one.</summary>
public interface ITestBehaviorAnalyzer
{
    string Language { get; }
    string AnalyzerId { get; }
}

/// <summary>
/// Reads C# test methods (xUnit, NUnit, MSTest attributes) once at Source Analysis upload and records facts the Test Coverage &amp; Overlap Review
/// needs: what the test hosts or substitutes (boundary signals), what it asserts, which production types/projects it targets, skip state,
/// categories and explicit requirement identifiers. Syntax only — no semantic model, no execution. Names and status codes only; never literal
/// values. Single pass per file plus one type-name index over production files (no pairwise comparison).
/// </summary>
public sealed partial class TestBehaviorSourceAnalyzer : ITestBehaviorAnalyzer
{
    public const int Version = 1;
    public const int MaxTests = 20_000;
    public const int MaxFiles = 20_000;
    public string Language => "C#";
    public string AnalyzerId => "dotnet-roslyn-syntax";

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
        { "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "SkippableFact", "SkippableTheory", "AvaloniaFact" };
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
        { "should", "when", "given", "then", "returns", "return", "test", "tests", "async", "it", "a", "an", "the", "and", "with", "for", "is", "are", "be", "does", "do", "of", "to", "in", "on", "if", "that", "can" };
    private static readonly HashSet<string> FrameworkTypes = new(StringComparer.Ordinal)
    {
        "HttpClient", "Uri", "StringContent", "JsonContent", "List", "Dictionary", "HashSet", "Guid", "DateTime", "DateTimeOffset", "CancellationTokenSource", "MemoryStream",
        "StringBuilder", "Exception", "ArgumentException", "InvalidOperationException", "ServiceCollection", "ConfigurationBuilder", "HttpRequestMessage", "HttpResponseMessage",
        "TimeSpan", "Random", "Stopwatch", "Object", "JsonSerializerOptions", "ByteArrayContent", "FormUrlEncodedContent", "StreamContent", "Lazy", "Task", "TaskCompletionSource",
    };
    private static readonly Dictionary<string, string> StatusNames = new(StringComparer.Ordinal)
    {
        ["OK"] = "200", ["Created"] = "201", ["Accepted"] = "202", ["NoContent"] = "204", ["BadRequest"] = "400", ["Unauthorized"] = "401", ["Forbidden"] = "403",
        ["NotFound"] = "404", ["Conflict"] = "409", ["UnprocessableEntity"] = "422", ["UnprocessableContent"] = "422", ["TooManyRequests"] = "429",
        ["InternalServerError"] = "500", ["ServiceUnavailable"] = "503",
    };
    [GeneratedRegex(@"\b(?:FR|NFR|SC|REQ|AC|US)-\d{1,4}[a-z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequirementPattern();
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|_|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex NameSplit();

    private sealed record Unit(string Path, string Project, CompilationUnitSyntax Root);

    public static TestBehaviorSourceEvidence Analyze(Guid snapshotId, SourceTestInventory? inventory, IReadOnlyList<SourceFile> files, IReadOnlyList<string> allPaths, CancellationToken ct = default)
    {
        var limitations = new List<string>();
        var csprojFiles = files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var testProjects = (inventory?.Projects ?? []).Select(p => p.Path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var projectDirs = csprojFiles.Select(f => (Dir: Dir(f.Path), Name: Path.GetFileNameWithoutExtension(f.Path), Test: testProjects.Contains(f.Path.Replace('\\', '/'))))
            .OrderByDescending(p => p.Dir.Length).ToList();
        var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in csprojFiles.Where(f => testProjects.Contains(f.Path.Replace('\\', '/'))))
            references[Path.GetFileNameWithoutExtension(project.Path)] = ProjectReferences(project.Content);

        // Type-name index over production C# files: which production project declares a type (ambiguous names are dropped).
        var typeIndex = new Dictionary<string, string?>(StringComparer.Ordinal);
        var units = new List<(Unit Unit, bool Test)>();
        var csFiles = files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(MaxFiles + 1).ToList();
        var truncated = csFiles.Count > MaxFiles;
        if (truncated) { csFiles = csFiles.Take(MaxFiles).ToList(); limitations.Add($"Test analysis read the first {MaxFiles:N0} C# files."); }
        foreach (var file in csFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = file.Path.Replace('\\', '/');
            var owner = projectDirs.FirstOrDefault(p => p.Dir.Length == 0 || path.StartsWith(p.Dir + "/", StringComparison.Ordinal));
            if (owner.Name is null) continue;
            var tree = CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(file.Content), Parse, path, cancellationToken: ct);
            var unit = new Unit(path, owner.Name, tree.GetCompilationUnitRoot(ct));
            if (owner.Test) { units.Add((unit, true)); continue; }
            foreach (var type in unit.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var name = type.Identifier.ValueText;
                typeIndex[name] = typeIndex.TryGetValue(name, out var existing) && existing != owner.Name ? null : owner.Name;
            }
        }

        var byFqn = (inventory?.Definitions ?? []).GroupBy(d => d.FullyQualifiedName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var tests = new List<TestBehaviorFact>();
        foreach (var (unit, _) in units)
        {
            ct.ThrowIfCancellationRequested();
            var ns = unit.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            foreach (var cls in unit.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var methods = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.AttributeLists.SelectMany(a => a.Attributes).Any(a => TestAttributes.Contains(Short(a.Name)))).ToList();
                if (methods.Count == 0) continue;
                // Class-level setup (fixtures, fields, constructor, base types) applies to every test in the class.
                var classNodes = cls.BaseList is null ? [] : new List<SyntaxNode> { cls.BaseList };
                classNodes.AddRange(cls.Members.Where(m => m is not MethodDeclarationSyntax md || !methods.Contains(md)));
                var classSignals = classNodes.SelectMany(n => Signals(n, "class")).ToList();
                var classCategories = Categories(cls.AttributeLists);
                foreach (var method in methods)
                {
                    if (tests.Count >= MaxTests) { truncated = true; break; }
                    var fqn = $"{(ns is null ? "" : ns + ".")}{cls.Identifier.ValueText}.{method.Identifier.ValueText}";
                    byFqn.TryGetValue(fqn, out var definition);
                    var attributes = method.AttributeLists.SelectMany(a => a.Attributes).ToList();
                    var signals = Signals(method, "method").Concat(classSignals).DistinctBy(s => (s.Kind, s.Mode, s.Detail)).ToList();
                    var assertions = Assertions(method);
                    var targets = Targets(method, cls);
                    var targetProjects = targets.Select(t => t.Split('.')[0]).Select(t => typeIndex.GetValueOrDefault(t)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                    var text = method.Identifier.ValueText + " " + string.Join(" ", attributes.Select(a => a.ToString())) + " " + method.GetLeadingTrivia().ToString();
                    tests.Add(new TestBehaviorFact
                    {
                        TestId = definition?.TestDefinitionId ?? StableId(unit.Project, fqn),
                        InInventory = definition is not null,
                        Project = unit.Project, FilePath = unit.Path, Line = Line(method), ClassName = cls.Identifier.ValueText, MethodName = method.Identifier.ValueText,
                        FullyQualifiedName = fqn, Framework = Framework(attributes),
                        Skipped = definition?.SkipDeclared ?? attributes.Any(a => Short(a.Name) is "Ignore" or "Explicit" || a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.ValueText == "Skip") == true),
                        Categories = classCategories.Concat(Categories(method.AttributeLists)).Concat(definition?.Categories ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        ProductionProjects = references.GetValueOrDefault(unit.Project) ?? [],
                        TargetProjects = targetProjects,
                        Targets = targets,
                        Signals = signals,
                        Assertions = assertions,
                        RequirementReferences = RequirementPattern().Matches(text).Select(m => m.Value.ToUpperInvariant())
                            .Concat(definition?.References.Select(r => r.Id) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                        BehaviorTokens = Tokens(method.Identifier.ValueText),
                        ExpectedOutcomes = assertions.Where(a => a.Kind is TestAssertionKind.HttpStatus or TestAssertionKind.ExceptionThrown or TestAssertionKind.AuthorizationResult)
                            .Select(a => a.Detail).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    });
                }
            }
        }

        return new TestBehaviorSourceEvidence
        {
            SnapshotId = snapshotId, AnalyzerVersion = Version, TestFilesAnalyzed = units.Count, Tests = tests, ProjectReferences = references,
            Unsupported = UnsupportedFiles(allPaths), Truncated = truncated,
            Limitations = [.. limitations, "C# syntax only: no semantic model, so a type is mapped to a project by name and ambiguous names are not mapped."],
        };
    }

    // ── boundary signals ─────────────────────────────────────────────────────────────────────────────────────────────

    internal static List<TestBoundarySignal> Signals(SyntaxNode node, string scope)
    {
        var signals = new List<TestBoundarySignal>();
        void Add(BoundaryKind kind, BoundaryMode mode, string detail, SyntaxNode at) => signals.Add(new(kind, mode, detail, scope, Line(at)));
        foreach (var generic in node.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
        {
            var name = generic.Identifier.ValueText;
            var argument = generic.TypeArgumentList.Arguments.Count > 0 ? Short(generic.TypeArgumentList.Arguments[0]) : "";
            switch (name)
            {
                case "WebApplicationFactory": Add(BoundaryKind.Http, BoundaryMode.InProcess, "WebApplicationFactory", generic); break;
                case "Mock" when argument.Length > 0:
                    Add(KindOf(argument), BoundaryMode.Mocked, argument, generic); break;
                case "For" or "Fake" when argument.Length > 0 && generic.Parent is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Substitute" or "A" } }:
                    Add(KindOf(argument), BoundaryMode.Mocked, argument, generic); break;
                case "RenderComponent" or "Render": Add(BoundaryKind.Browser, BoundaryMode.InMemory, "bUnit render (no browser)", generic); break;
            }
        }
        foreach (var creation in node.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
        {
            var type = Short(creation.Type);
            if (type.StartsWith("Fake", StringComparison.Ordinal) || type.StartsWith("Stub", StringComparison.Ordinal) || type.StartsWith("InMemory", StringComparison.Ordinal))
                Add(KindOf(type), BoundaryMode.Fake, type, creation);
            else if (type is "TestServer") Add(BoundaryKind.Http, BoundaryMode.InProcess, "TestServer", creation);
            // Matched by name shape: the API itself never references messaging client types (Active CDC owns the single send path).
            else if (type.StartsWith("EventHub", StringComparison.Ordinal) && type.EndsWith("Client", StringComparison.Ordinal) || type.StartsWith("EventProcessor", StringComparison.Ordinal) && type.EndsWith("Client", StringComparison.Ordinal))
                Add(BoundaryKind.EventHub, BoundaryMode.Real, type, creation);
            else if (type.StartsWith("ServiceBus", StringComparison.Ordinal) && type.EndsWith("Client", StringComparison.Ordinal)) Add(BoundaryKind.ServiceBus, BoundaryMode.Real, type, creation);
            else if (type.EndsWith("ContainerBuilder", StringComparison.Ordinal) || type is "PostgreSqlBuilder" or "MsSqlBuilder" or "SqlEdgeBuilder" or "MySqlBuilder")
                Add(type.Contains("Azurite") ? BoundaryKind.Storage : BoundaryKind.Database, BoundaryMode.RealContainer, type, creation);
            else if (type is "ChromeDriver" or "FirefoxDriver" or "EdgeDriver") Add(BoundaryKind.Browser, BoundaryMode.Real, type, creation);
            else if (type is "BunitContext" or "TestContext" && creation.Ancestors().OfType<ClassDeclarationSyntax>().Any()) { }
        }
        foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            switch (identifier.Identifier.ValueText)
            {
                case "UseInMemoryDatabase": Add(BoundaryKind.Database, BoundaryMode.InMemory, "EF Core in-memory provider", identifier); break;
                case "UseSqlite" when identifier.Parent?.Parent?.ToString().Contains(":memory:", StringComparison.Ordinal) == true:
                    Add(BoundaryKind.Database, BoundaryMode.InMemory, "SQLite in-memory", identifier); break;
                case "UseSqlServer" or "UseNpgsql": Add(BoundaryKind.Database, BoundaryMode.Real, identifier.Identifier.ValueText, identifier); break;
                case "TrackActivity" or "InvokeMessageAndWaitAsync" or "StubAllExternalTransports": Add(BoundaryKind.Messaging, BoundaryMode.InMemory, "Wolverine in-process tracking", identifier); break;
                case "IPage" or "IBrowser" or "IPlaywright" or "IBrowserContext": Add(BoundaryKind.Browser, BoundaryMode.Real, "Playwright", identifier); break;
                case "IWebDriver": Add(BoundaryKind.Browser, BoundaryMode.Real, "Selenium", identifier); break;
                case "CreateClient" when identifier.Parent is MemberAccessExpressionSyntax: Add(BoundaryKind.Http, BoundaryMode.InProcess, "In-process HTTP client", identifier); break;
                case "BunitContext" or "TestContextWrapper": Add(BoundaryKind.Browser, BoundaryMode.InMemory, "bUnit (no browser)", identifier); break;
                case "IClassFixture" or "ICollectionFixture": break;
            }
        }
        return signals.DistinctBy(s => (s.Kind, s.Mode, s.Detail)).ToList();
    }

    /// <summary>The boundary a substituted type stands for, read from its name only.</summary>
    internal static BoundaryKind KindOf(string type)
    {
        var t = type.TrimStart('I');
        if (Contains(t, "HttpMessageHandler") || Contains(t, "HttpClient") || Contains(t, "ApiClient") || t.EndsWith("Client", StringComparison.Ordinal) && Contains(t, "Api")) return BoundaryKind.Http;
        if (Contains(t, "EventHub") || Contains(t, "EventProcessor")) return BoundaryKind.EventHub;
        if (Contains(t, "ServiceBus")) return BoundaryKind.ServiceBus;
        if (Contains(t, "MessageBus") || Contains(t, "Publisher") || Contains(t, "MessageSession") || Contains(t, "PublishEndpoint") || Contains(t, "MessageContext") || t is "Bus" || Contains(t, "Outbox"))
            return BoundaryKind.Messaging;
        if (Contains(t, "Repository") || Contains(t, "DbContext") || Contains(t, "Database") || Contains(t, "UnitOfWork") || Contains(t, "Store") && !Contains(t, "Blob")) return BoundaryKind.Database;
        if (Contains(t, "Blob") || Contains(t, "Storage") || Contains(t, "Checkpoint")) return BoundaryKind.Storage;
        if (Contains(t, "FileSystem")) return BoundaryKind.FileSystem;
        return BoundaryKind.Unknown;
    }

    // ── assertions ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static List<TestAssertionFact> Assertions(MethodDeclarationSyntax method)
    {
        var facts = new List<TestAssertionFact>();
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax i => i.Identifier.ValueText,
                GenericNameSyntax g => g.Identifier.ValueText,
                _ => "",
            };
            var receiver = invocation.Expression is MemberAccessExpressionSyntax ma ? ma.Expression.ToString() : "";
            var generic = invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax gn } ? Short(gn.TypeArgumentList.Arguments[0])
                : invocation.Expression is GenericNameSyntax g2 ? Short(g2.TypeArgumentList.Arguments[0]) : null;
            var line = Line(invocation);
            var args = invocation.ArgumentList.ToString();
            var isAssert = receiver is "Assert" || receiver.EndsWith(".Should()", StringComparison.Ordinal) || receiver.Contains("Should()", StringComparison.Ordinal)
                || name.StartsWith("Should", StringComparison.Ordinal) || receiver.StartsWith("Assert.", StringComparison.Ordinal);
            if (name is "Throws" or "ThrowsAsync" or "ThrowsAny" or "ThrowsAnyAsync" or "ThrowsException" or "ThrowsExceptionAsync" or "Throw" or "ThrowAsync" or "ThrowExactly" or "ThrowExactlyAsync")
            { facts.Add(new(TestAssertionKind.ExceptionThrown, generic ?? "Exception", line)); continue; }
            if (name is "EnsureSuccessStatusCode") { facts.Add(new(TestAssertionKind.HttpStatus, "2xx", line)); continue; }
            if (name is "Verify" or "VerifyAll" or "Received" or "MustHaveHappened" or "MustHaveHappenedOnceExactly" or "DidNotReceive" or "MustNotHaveHappened")
            {
                if (receiver is "" && name == "Verify" && method.Body?.ToString().Contains("Verifier", StringComparison.Ordinal) != true && invocation.ArgumentList.Arguments.Count == 1 && !args.Contains("=>"))
                { facts.Add(new(TestAssertionKind.Snapshot, "Verify snapshot", line)); continue; }
                var published = Regex.IsMatch(args + " " + invocation.Parent?.ToString(), @"\b(Publish|PublishAsync|Send|SendAsync|SendMessageAsync|SendMessagesAsync|ScheduleMessageAsync)\b");
                facts.Add(new(published ? TestAssertionKind.MessagePublished : TestAssertionKind.MockInvocationVerified, published ? "message sent/published" : "mock invocation", line));
                continue;
            }
            if (name is "MatchSnapshot" or "Approve" or "VerifyJson") { facts.Add(new(TestAssertionKind.Snapshot, name, line)); continue; }
            if (name is "MarkupMatches" || receiver.EndsWith("Markup", StringComparison.Ordinal) && isAssert) { facts.Add(new(TestAssertionKind.Rendered, "rendered markup", line)); continue; }
            if (!isAssert && !name.StartsWith("Should", StringComparison.Ordinal) && name is not ("Be" or "BeEquivalentTo" or "HaveCount" or "BeEmpty" or "NotBeNull" or "BeNull" or "BeTrue" or "BeFalse" or "ContainSingle" or "Contain"))
                continue;
            var statusCode = StatusIn(args + " " + receiver);
            if (statusCode is not null)
            {
                facts.Add(new(TestAssertionKind.HttpStatus, statusCode, line));
                if (statusCode is "401" or "403") facts.Add(new(TestAssertionKind.AuthorizationResult, statusCode == "401" ? "unauthenticated" : "forbidden", line));
                continue;
            }
            if (Regex.IsMatch(args + receiver, @"\b(Published|Sent|SentMessages|Envelopes|OutgoingMessages)\b")) { facts.Add(new(TestAssertionKind.MessagePublished, "published message", line)); continue; }
            var persistence = Regex.IsMatch(args + receiver, @"\b(Db|Context|Repository|dbContext|context|db|repository)\b|\.Set<|SaveChanges|FindAsync|Count\(");
            facts.Add(name switch
            {
                "Single" or "Empty" or "NotEmpty" or "HaveCount" or "ContainSingle" or "BeEmpty" when persistence => new(TestAssertionKind.RecordCount, "record count", line),
                "NotNull" or "NotBeNull" when persistence => new(TestAssertionKind.RecordPersisted, "record exists", line),
                "NotNull" or "NotBeNull" or "NotEmpty" => new(TestAssertionKind.NotNullOnly, "not null", line),
                "Equal" or "Be" or "BeEquivalentTo" or "Same" or "StrictEqual" or "AreEqual" or "NotEqual" => new(TestAssertionKind.ValueEquality, "value equality", line),
                "True" or "False" or "BeTrue" or "BeFalse" or "IsTrue" or "IsFalse" => new(TestAssertionKind.BooleanCondition, "condition", line),
                "Single" or "Empty" or "Contains" or "DoesNotContain" or "All" or "Collection" or "HaveCount" or "Contain" or "ContainSingle" or "BeEmpty" => new(TestAssertionKind.CollectionShape, "collection", line),
                "Null" or "BeNull" => new(TestAssertionKind.ValueEquality, "is null", line),
                _ => new(TestAssertionKind.Other, name, line),
            });
        }
        return facts.Take(40).ToList();
    }

    private static string? StatusIn(string text)
    {
        var named = Regex.Match(text, @"HttpStatusCode\.(\w+)");
        if (named.Success) return StatusNames.TryGetValue(named.Groups[1].Value, out var code) ? code : named.Groups[1].Value;
        var numeric = Regex.Match(text, @"StatusCode\W[^;]*?\b([1-5]\d\d)\b");
        if (numeric.Success) return numeric.Groups[1].Value;
        var results = Regex.Match(text, @"\b(Ok|NotFound|BadRequest|Unauthorized|Forbid|Conflict|UnprocessableEntity|NoContent|Created)(Object)?Result\b");
        return results.Success ? results.Groups[1].Value switch { "Ok" => "200", "NotFound" => "404", "BadRequest" => "400", "Unauthorized" => "401", "Forbid" => "403", "Conflict" => "409", "UnprocessableEntity" => "422", "NoContent" => "204", "Created" => "201", _ => null } : null;
    }

    // ── targets, categories, names ──────────────────────────────────────────────────────────────────────────────────

    private static List<string> Targets(MethodDeclarationSyntax method, ClassDeclarationSyntax cls)
    {
        var types = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Select(c => Short(c.Type))
            .Concat(cls.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText.Contains("sut", StringComparison.OrdinalIgnoreCase)))
                .Select(f => Short(f.Declaration.Type)))
            .Where(t => t.Length > 1 && char.IsUpper(t[0]) && !FrameworkTypes.Contains(t) && !t.StartsWith("Mock", StringComparison.Ordinal) && !t.StartsWith("Fake", StringComparison.Ordinal)
                && !t.StartsWith("Stub", StringComparison.Ordinal) && !t.EndsWith("Builder", StringComparison.Ordinal) && !t.EndsWith("Options", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(10).ToList();
        return types;
    }

    private static List<string> Categories(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(a => a.Attributes)
        .Where(a => Short(a.Name) is "Trait" or "Category" or "TestCategory" && a.ArgumentList is { Arguments.Count: > 0 })
        .Select(a => a.ArgumentList!.Arguments.Last().ToString().Trim('"')).Where(v => v.Length is > 0 and < 60).ToList();

    private static string Framework(IEnumerable<AttributeSyntax> attributes)
    {
        var names = attributes.Select(a => Short(a.Name)).ToHashSet(StringComparer.Ordinal);
        return names.Overlaps(["Fact", "Theory", "SkippableFact", "SkippableTheory"]) ? "xUnit" : names.Overlaps(["TestMethod", "DataTestMethod"]) ? "MSTest" : "NUnit";
    }

    internal static List<string> Tokens(string name) => NameSplit().Split(name).Select(t => t.ToLowerInvariant().Trim()).Where(t => t.Length > 1 && !NoiseTokens.Contains(t))
        .Distinct(StringComparer.Ordinal).ToList();

    private static List<string> ProjectReferences(string csproj)
    {
        try
        {
            return XDocument.Parse(csproj).Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => e.Attribute("Include")?.Value).OfType<string>()
                .Select(v => Path.GetFileNameWithoutExtension(v.Replace('\\', '/'))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    private static List<UnsupportedTestFiles> UnsupportedFiles(IReadOnlyList<string> paths)
    {
        var groups = new Dictionary<(string Language, string Framework), int>();
        foreach (var raw in paths)
        {
            var p = raw.Replace('\\', '/');
            if (p.Contains("/node_modules/", StringComparison.Ordinal)) continue;
            var file = p[(p.LastIndexOf('/') + 1)..];
            (string, string)? key =
                Regex.IsMatch(file, @"\.(test|spec)\.(ts|tsx)$") ? ("TypeScript", p.Contains("e2e", StringComparison.OrdinalIgnoreCase) || p.Contains("playwright", StringComparison.OrdinalIgnoreCase) ? "Playwright / E2E" : "Jest / Vitest")
                : Regex.IsMatch(file, @"\.(test|spec)\.(js|jsx|mjs)$") ? ("JavaScript", p.Contains("e2e", StringComparison.OrdinalIgnoreCase) ? "Playwright / E2E" : "Jest / Vitest / Mocha")
                : Regex.IsMatch(file, @"^test_.*\.py$|_test\.py$") ? ("Python", "pytest")
                : Regex.IsMatch(file, @"(Test|Tests|IT)\.java$") && p.Contains("/src/test/", StringComparison.Ordinal) ? ("Java", "JUnit")
                : Regex.IsMatch(file, @"_test\.go$") ? ("Go", "go test") : null;
            if (key is { } k) groups[k] = groups.GetValueOrDefault(k) + 1;
        }
        return groups.Select(g => new UnsupportedTestFiles(g.Key.Language, g.Key.Framework, g.Value, "Source Analysis does not retain this language's source; these tests are counted, not analyzed."))
            .OrderBy(u => u.Language, StringComparer.Ordinal).ToList();
    }

    private static string StableId(string project, string fqn) =>
        "src:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project + "|" + fqn)))[..16].ToLowerInvariant();
    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static string Dir(string path) { var p = path.Replace('\\', '/'); return p.Contains('/') ? p[..p.LastIndexOf('/')] : ""; }
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    internal static string Short(SyntaxNode name) => name switch
    {
        QualifiedNameSyntax q => Short(q.Right),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        AliasQualifiedNameSyntax a => Short(a.Name),
        NullableTypeSyntax n => Short(n.ElementType),
        _ => name.ToString().Split('.').Last().Split('<')[0],
    };
}
