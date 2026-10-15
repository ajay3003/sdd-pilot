using System.Text.RegularExpressions;
using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>One member of the effective schema in reading order, with its path and depth below the selected root.</summary>
public sealed record ReadableField(string Path, int Depth, EffectiveMember Member);

/// <summary>
/// Deterministic, presentation-only wording for Schema Studio's readable views. Every statement is a direct mapping of a schema fact
/// (type, occurrence, facet, xs:choice, documentation) from the shared effective schema; nothing is inferred from names, and documentation
/// is shown verbatim in its source language. Business rules that the XSD does not state (date order, sums, exclusivity by naming) are never produced.
/// </summary>
public static class SchemaReadablePresentation
{
    public const int MaxFields = 2000;

    /// <summary>The members below <paramref name="root"/> in document order (depth-first), bounded to <see cref="MaxFields"/>.</summary>
    public static IReadOnlyList<ReadableField> Fields(EffectiveMember root, out bool truncated)
    {
        var result = new List<ReadableField>();
        var stopped = false;
        void Visit(EffectiveMember member, string path, int depth)
        {
            foreach (var child in member.Children)
            {
                if (result.Count >= MaxFields) { stopped = true; return; }
                var childPath = path + "/" + child.Name;
                result.Add(new(childPath, depth, child));
                Visit(child, childPath, depth + 1);
                if (stopped) return;
            }
        }
        Visit(root, root.Name, 1);
        truncated = stopped;
        return result;
    }

    public static bool IsGroup(EffectiveMember member) => member.Children.Count > 0 || member.Type is null && member.Attributes is { Count: > 0 };

    /// <summary>Readable type of a member: built-in XML Schema types map to fixed Norwegian labels; anything else shows its declared name.</summary>
    public static string TypeLabel(EffectiveMember member)
    {
        if (IsGroup(member)) return "Gruppe";
        return BuiltInLabel(member.BaseType ?? member.Type) ?? XmlSchemaPackageAnalyzer.LocalName(member.Type) ?? "Ikke angitt";
    }

    public static string TypeLabel(EffectiveAttribute attribute) =>
        BuiltInLabel(attribute.BaseType ?? attribute.Type) ?? XmlSchemaPackageAnalyzer.LocalName(attribute.Type) ?? "Tekst";

    /// <summary>The exact lexical type for technical detail ("xs:double", "tns:PeriodeType"), never rewritten.</summary>
    public static string RawType(EffectiveMember member) => member.Type is null
        ? IsGroup(member) ? "inline complexType" : member.BaseType is { } builtIn ? $"inline simpleType ({builtIn})" : "inline simpleType"
        : member.BaseType is { } baseType && baseType != member.Type ? $"{member.Type} ({baseType})" : member.Type;

    private static string? BuiltInLabel(string? lexical)
    {
        if (lexical is null || !lexical.StartsWith("xs:", StringComparison.Ordinal) && !lexical.StartsWith("xsd:", StringComparison.Ordinal)) return null;
        return XmlSchemaPackageAnalyzer.LocalName(lexical) switch
        {
            "string" or "normalizedString" or "token" => "Tekst",
            "boolean" => "Ja/nei",
            "integer" or "int" or "long" or "short" or "byte" or "nonNegativeInteger" or "positiveInteger" or "negativeInteger" or "nonPositiveInteger"
                or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" => "Heltall",
            "decimal" or "double" or "float" => "Tall",
            "date" => "Dato",
            "dateTime" => "Dato og tidspunkt",
            "time" => "Tidspunkt",
            _ => null,
        };
    }

    /// <summary>Exact occurrence as written in the schema: "1..1", "0..*".</summary>
    public static string ExactCardinality(EffectiveMember member) => $"{member.MinOccurs}..{(member.MaxOccurs == "unbounded" ? "*" : member.MaxOccurs)}";

    /// <summary>Readable occurrence. Members inside a real xs:choice are described as alternatives; naming conventions are never used.</summary>
    public static string CardinalityLabel(EffectiveMember member)
    {
        var text = (member.MinOccurs, member.MaxOccurs) switch
        {
            ("1", "1") => "Obligatorisk",
            ("0", "1") => "Valgfritt",
            ("0", "unbounded") => "Valgfritt, kan forekomme flere ganger",
            ("1", "unbounded") => "Minst én, kan forekomme flere ganger",
            (var min, "unbounded") => $"Minst {min}, kan forekomme flere ganger",
            ("0", var max) => $"Valgfritt, høyst {max} ganger",
            (var min, var max) when min == max => $"Nøyaktig {min} ganger",
            (var min, var max) => $"Mellom {min} og {max} ganger",
        };
        return member.InChoice ? text + " · ett av alternativene (xs:choice)" : text;
    }

    public static string AttributeUseLabel(EffectiveAttribute attribute) => attribute.Use switch
    {
        "required" => "Obligatorisk",
        "prohibited" => "Ikke tillatt",
        _ => "Valgfritt",
    };

    /// <summary>Allowed values in schema order. Labels come only from each value's own xs:documentation; none are invented.</summary>
    public static IReadOnlyList<(string Value, string? Documentation)> AllowedValues(IEnumerable<SchemaFacet> facets) =>
        [.. facets.Where(f => f.Kind == "enumeration").Select(f => (f.Value, f.Documentation)).Distinct()];

    /// <summary>Readable restrictions except enumerations. Only unambiguous patterns are translated; others say "Formatregel definert".</summary>
    public static IReadOnlyList<(string Text, string? Raw)> Rules(IEnumerable<SchemaFacet> facets)
    {
        var list = facets.ToList();
        var result = new List<(string, string?)>();
        var min = list.FirstOrDefault(f => f.Kind == "minLength")?.Value;
        var max = list.FirstOrDefault(f => f.Kind == "maxLength")?.Value;
        if (list.FirstOrDefault(f => f.Kind == "length") is { } exact) result.Add(($"Nøyaktig {exact.Value} tegn", "length=" + exact.Value));
        else if (min is not null && min == max) result.Add(($"Nøyaktig {min} tegn", $"minLength={min}, maxLength={max}"));
        else
        {
            if (min is not null) result.Add(($"Minst {min} tegn", "minLength=" + min));
            if (max is not null) result.Add(($"Maks {max} tegn", "maxLength=" + max));
        }
        foreach (var facet in list)
        {
            var text = facet.Kind switch
            {
                "pattern" => PatternLabel(facet.Value),
                "minInclusive" => $"Minst {facet.Value}",
                "maxInclusive" => $"Høyst {facet.Value}",
                "minExclusive" => $"Større enn {facet.Value}",
                "maxExclusive" => $"Mindre enn {facet.Value}",
                "totalDigits" => $"Høyst {facet.Value} sifre totalt",
                "fractionDigits" => $"Høyst {facet.Value} desimaler",
                _ => null,
            };
            if (text is not null) result.Add((text, facet.Kind + "=" + facet.Value));
        }
        return result;
    }

    /// <summary>Translates only digit-count patterns (\d{n}, \d{n,m}, [0-9]{n}); every other pattern is reported neutrally.</summary>
    public static string PatternLabel(string pattern)
    {
        var exact = Regex.Match(pattern, @"^(?:\\d|\[0-9\])\{(\d+)\}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (exact.Success) return $"Nøyaktig {exact.Groups[1].Value} sifre";
        var range = Regex.Match(pattern, @"^(?:\\d|\[0-9\])\{(\d+),(\d+)\}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (range.Success) return $"Mellom {range.Groups[1].Value} og {range.Groups[2].Value} sifre";
        return "Formatregel definert";
    }

    public static string CompositorLabel(string? compositor) => compositor switch
    {
        "sequence" => "i fast rekkefølge (xs:sequence)",
        "choice" => "ett av alternativene (xs:choice)",
        "all" => "i valgfri rekkefølge (xs:all)",
        _ => "",
    };

    public static string DependencyStatusLabel(SchemaDependencyStatus status) => status switch
    {
        SchemaDependencyStatus.Resolved => "Løst",
        SchemaDependencyStatus.Unresolved => "Avhengighet ikke løst",
        SchemaDependencyStatus.Circular => "Sirkulær avhengighet oppdaget",
        SchemaDependencyStatus.Unsupported => "Ikke støttet i lesbar modell",
        SchemaDependencyStatus.External => "Ekstern plassering – hentes ikke",
        SchemaDependencyStatus.NamespaceMismatch => "Navnerom stemmer ikke",
        _ => status.ToString(),
    };

    public static string KindLabel(string kind) => kind switch { "Imported" => "xs:import", "Included" => "xs:include", "Redefined" => "xs:redefine", _ => kind };

    /// <summary>Documentation to show for a member: its own, else its type's (labelled so the source stays clear). Verbatim, whitespace-normalized.</summary>
    public static (string Text, bool FromType)? Documentation(EffectiveMember member) =>
        member.Documentation is { Length: > 0 } own ? (Normalize(own), false)
        : member.TypeDocumentation is { Length: > 0 } type ? (Normalize(type), true) : null;

    /// <summary>Collapses runs of whitespace inside documentation lines (tabs from source formatting) without changing any words.</summary>
    public static string Normalize(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
        .Select(line => Regex.Replace(line, @"[ \t]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Trim())
        .Where(line => line.Length > 0));
}
