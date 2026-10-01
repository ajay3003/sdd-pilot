using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SourceAnalysis.Observability;

/// <summary>Configurable heuristics. Personal-data terms are generic field names, never project vocabulary; callers may replace them.</summary>
public sealed record ObservabilityAnalyzerOptions
{
    public static readonly ObservabilityAnalyzerOptions Default = new();
    public IReadOnlyList<string> PersonalDataTerms { get; init; } =
        ["ssn", "socialsecuritynumber", "nationalid", "nationalidentitynumber", "personalnumber", "personnummer", "fodselsnummer", "birthnumber", "email", "emailaddress", "phonenumber", "dateofbirth"];
    public int MaxEvidencePerFinding { get; init; } = 8;
}

/// <summary>One parsed C# file of a project: comment-stripped text for token scans, a syntax tree for call/catch analysis.</summary>
internal sealed class ObsFile
{
    public required string Path { get; init; }
    public required ArchProject Project { get; init; }
    public required string ComponentId { get; init; }
    public required string ComponentName { get; init; }
    public required string Text { get; init; }
    public required CompilationUnitSyntax Root { get; init; }
    public bool IsTest => Project.IsTest;
    public bool Has(Regex pattern) => pattern.IsMatch(Text);
}

/// <summary>A component (deployable from Architecture, or a library) with the files of its own project and of its library closure.</summary>
internal sealed class ObsComponent
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ArchitectureComponentType Type { get; init; }
    public required ArchProject Project { get; init; }
    public List<ObsFile> Own { get; } = [];
    public List<ObsFile> Libraries { get; } = [];
    public IEnumerable<ObsFile> All => Own.Concat(Libraries);
    public List<string> Packages { get; } = [];
    public bool HasPackage(string prefix) => Packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>First match in the component's own files (Confirmed) or its libraries (StronglySupported: the call path is not proven).</summary>
    public (ObservabilityEvidence Evidence, ArchitectureEvidenceState State)? Find(Regex pattern, string label)
    {
        foreach (var (files, state) in new[] { (Own, ArchitectureEvidenceState.Confirmed), (Libraries, ArchitectureEvidenceState.StronglySupported) })
            foreach (var f in files)
                if (pattern.Match(f.Text) is { Success: true } m)
                    return (ObsText.Evidence(f, m.Index, label, Id), state);
        return null;
    }
}

internal static class ObsText
{
    public static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    public static CompilationUnitSyntax Tree(string path, string content) =>
        CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(content), Parse, path).GetCompilationUnitRoot();

    public static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static ObservabilityEvidence Evidence(ObsFile file, SyntaxNode node, string pattern) =>
        new(ArchitectureText.Safe(file.Path), Line(node), Symbol(node), pattern, file.ComponentId);

    public static ObservabilityEvidence Evidence(ObsFile file, int index, string pattern, string? component = null)
    {
        var token = file.Root.FindToken(Math.Min(index, Math.Max(0, file.Root.FullSpan.End - 1)));
        return new(ArchitectureText.Safe(file.Path), file.Root.SyntaxTree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(index, 0)).StartLinePosition.Line + 1,
            token.Parent is { } parent ? Symbol(parent) : "", pattern, component ?? file.ComponentId);
    }

    /// <summary>Enclosing type.member, as a safe label. Never source text.</summary>
    public static string Symbol(SyntaxNode node)
    {
        var type = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        var member = node.AncestorsAndSelf().Select(a => a switch
        {
            LocalFunctionStatementSyntax l => l.Identifier.ValueText,
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax c => c.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            _ => null,
        }).FirstOrDefault(n => n is not null);
        var label = (type, member) switch
        {
            (null, null) => "(top-level statements)",
            (null, _) => member!,
            (_, null) => type!,
            _ => $"{type}.{member}",
        };
        return ArchitectureText.Safe(label);
    }

    /// <summary>The member name of an invocation (<c>x.Y(...)</c> → Y, <c>Y&lt;T&gt;(...)</c> → Y).</summary>
    public static string Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        _ => "",
    };

    public static string Receiver(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Expression switch
        {
            IdentifierNameSyntax i => i.Identifier.ValueText,
            MemberAccessExpressionSyntax mm => mm.Name.Identifier.ValueText,
            ThisExpressionSyntax => "this",
            _ => m.Expression.ToString().Length > 80 ? "" : m.Expression.ToString(),
        },
        _ => "",
    };

    /// <summary>A literal that may leave the analyzer: a short identifier-like label (header, service, sink or source name). Anything else is dropped.</summary>
    public static string? Label(string? value) =>
        value is { Length: > 0 and <= 80 } && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_.\-*]*$") ? ArchitectureText.Safe(value) : null;

    public static string Slug(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>Collects findings, merging repeats of the same (category, component, title) into one finding with occurrences and capped evidence.</summary>
internal sealed class FindingSink(ObservabilityAnalyzerOptions options)
{
    private readonly Dictionary<string, ObservabilityFinding> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<(string, int)>> _seen = new(StringComparer.Ordinal);

    public void Add(ObservabilityCategory category, ObservabilityFindingKind kind, ObservabilitySeverity severity, ArchitectureEvidenceState state, string? component,
        string technology, string title, string detail, ObservabilityEvidence? evidence = null, string? limitation = null)
    {
        var id = $"{category}:{component ?? "snapshot"}:{ObsText.Slug(title)}";
        if (evidence is not null)
        {
            var seen = _seen.TryGetValue(id, out var s) ? s : _seen[id] = [];
            if (!seen.Add((evidence.File, evidence.Line))) return;
        }
        if (_findings.TryGetValue(id, out var existing))
        {
            var list = existing.Evidence;
            if (evidence is not null && list.Count < options.MaxEvidencePerFinding) list.Add(evidence);
            _findings[id] = existing with { Occurrences = existing.Occurrences + 1, EvidenceState = Stronger(existing.EvidenceState, state) };
            return;
        }
        _findings[id] = new ObservabilityFinding
        {
            Id = id, Kind = kind, Category = category, Component = component, Technology = technology, Title = title, Detail = detail, EvidenceState = state, Severity = severity,
            Evidence = evidence is null ? [] : [evidence], Limitation = limitation,
        };
    }

    private static ArchitectureEvidenceState Stronger(ArchitectureEvidenceState a, ArchitectureEvidenceState b) => (ArchitectureEvidenceState)Math.Min((int)a, (int)b);

    public List<ObservabilityFinding> ToList() => _findings.Values
        .OrderByDescending(f => f.Severity).ThenBy(f => f.Kind).ThenBy(f => f.Category).ThenBy(f => f.Component, StringComparer.Ordinal).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();

    public IEnumerable<ObservabilityFinding> Of(ObservabilityCategory category) => _findings.Values.Where(f => f.Category == category);
}
