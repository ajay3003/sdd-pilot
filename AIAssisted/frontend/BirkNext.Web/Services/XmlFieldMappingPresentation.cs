using BirkNext.Integrations;
using BirkNext.TestData;

namespace BirkNext.Web.Services;

/// <summary>
/// The state of one XSD field in the XML fixture builder. Distinct facts, never one "mapped": a required field without a value, an optional field
/// left out, a mapping to an asset that changed since the fixture was generated (stale), an asset that cannot fill the field (incompatible:
/// removed, not approved or of another data type) and a mapping to an element the selected XSD does not declare (unresolved).
/// </summary>
public enum XmlFieldState { RequiredMissing, NeedsReview, Unresolved, IncompatibleAsset, Stale, Suggested, Valid, Synthetic, OptionalUnmapped }

/// <summary>One row of the required-field table: path, requirement, expected type, Test Data alias, mapping state, validation issue and guidance.
/// <see cref="Status"/> keeps the builder's mapping wording ("Mapped", "Role mapped", "Suggested", "Synthetic value" …).</summary>
public sealed record XmlFieldRowView(string Path, string Display, string Technical, bool Required, string ExpectedType, string? Alias, string Source,
    XmlFieldState State, string Status, string? Issue, string? Guidance, bool HasMapping, bool IsSuggested)
{
    public bool NeedsAttention => State is XmlFieldState.RequiredMissing or XmlFieldState.NeedsReview or XmlFieldState.Unresolved or XmlFieldState.IncompatibleAsset or XmlFieldState.Stale
        || Issue is not null;
}

public static class XmlFieldMappingPresentation
{
    /// <summary>
    /// The rows for the selected XSD's fields plus every mapping or role binding whose element the XSD does not declare (unresolved). Pure: the
    /// panel passes what it holds; <paramref name="staleAliases"/> are the mapped assets whose value changed since the saved fixture was generated
    /// (<see cref="TestDataValueVersions.Changed"/>), <paramref name="issues"/> the current XSD validation diagnostics.
    /// </summary>
    public static IReadOnlyList<XmlFieldRowView> Rows(IReadOnlyList<XmlParameter> parameters, IReadOnlyList<XmlValueMapping> mappings,
        IReadOnlyList<TestDataFixtureBinding> bindings, Func<TestDataFixtureBinding, TestDataAssetSummary?> chosen, IReadOnlyList<TestDataAssetSummary> assets,
        IReadOnlyCollection<string> staleAliases, IReadOnlyList<string> issues)
    {
        var byAlias = assets.GroupBy(a => a.Alias, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<XmlFieldRowView>();
        foreach (var p in parameters)
        {
            var mapping = mappings.FirstOrDefault(m => m.Path == p.Path);
            var binding = bindings.FirstOrDefault(b => b.ElementPath == p.Path);
            rows.Add(Row(p.Path, p.Display, p.Technical, p.Required, p.Type, mapping, binding, binding is null ? null : chosen(binding), byAlias, staleAliases, Issue(p, issues), resolved: true));
        }
        if (parameters.Count > 0)
        {
            var known = parameters.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var m in mappings.Where(m => m.Path.Length > 0 && !known.Contains(m.Path)).DistinctBy(m => m.Path))
                rows.Add(Row(m.Path, m.Path.Replace(".", " → "), m.Path, false, null, m, null, null, byAlias, staleAliases, null, resolved: false));
            foreach (var b in bindings.Where(b => b.ElementPath is { Length: > 0 } path && !known.Contains(path) && rows.All(r => r.Path != path)))
                rows.Add(Row(b.ElementPath!, b.ElementPath!.Replace(".", " → "), b.ElementPath!, false, null, null, b, chosen(b), byAlias, staleAliases, null, resolved: false));
        }
        return [.. rows.OrderBy(r => r.NeedsAttention ? 0 : 1).ThenByDescending(r => r.Required).ThenBy(r => r.State is XmlFieldState.Valid ? 1 : 0)];
    }

    private static XmlFieldRowView Row(string path, string display, string technical, bool required, string? type, XmlValueMapping? mapping, TestDataFixtureBinding? binding,
        TestDataAssetSummary? chosenForBinding, IReadOnlyDictionary<string, TestDataAssetSummary> byAlias, IReadOnlyCollection<string> staleAliases, string? issue, bool resolved)
    {
        var expected = type is { Length: > 0 } ? type : "—";
        var alias = mapping is { Kind: XmlValueSourceKind.TestDataAsset } ? mapping.AssetAlias : mapping is null && binding is not null ? chosenForBinding?.Alias : null;
        var source = mapping is not null
            ? $"{SourceLabel(mapping.Kind)}{(mapping.AssetAlias is { } a ? $" · {a}" : mapping.FixedValue is { Length: > 0 } fixedValue ? $" · {fixedValue}" : "")}"
            : binding is not null ? $"Test Data role · {binding.Role}{(chosenForBinding is { } c ? $" · {c.Alias}" : "")}"
            : required ? "XSD generator (synthetic placeholder)" : "Left out (optional)";
        var status = mapping is not null ? MappingStatus(mapping)
            : binding is not null ? chosenForBinding is { Approval: TestDataApproval.Approved } ? "Role mapped" : "Needs value"
            : required ? "Synthetic value" : "Not used";
        var hasMapping = mapping is not null || binding is not null;
        var asset = alias is null ? null : byAlias.GetValueOrDefault(alias);
        var usesAsset = mapping is { Kind: XmlValueSourceKind.TestDataAsset } || mapping is null && binding is not null;

        XmlFieldState state; string? guidance;
        if (!resolved)
            (state, guidance) = (XmlFieldState.Unresolved, "The selected XSD does not declare this element. Choose a field of this XSD or remove the mapping.");
        else if (mapping?.NeedsReview is { } why)
            (state, guidance) = (XmlFieldState.NeedsReview, $"{why} Confirm or change the mapping before generating.");
        else if (usesAsset && alias is not null && asset is null)
            (state, guidance) = (XmlFieldState.IncompatibleAsset, $"{alias} no longer exists in Test Data. Choose another asset.");
        else if (usesAsset && asset is { Approval: not TestDataApproval.Approved })
            (state, guidance) = (XmlFieldState.IncompatibleAsset, $"{asset.Alias} is not approved. Approve it in Test Data or choose an approved asset.");
        else if (usesAsset && asset is not null && !TypeCompatible(type, asset.ValueType))
            (state, guidance) = (XmlFieldState.IncompatibleAsset, $"{asset.Alias} holds a {asset.ValueType} value; the XSD expects {expected}. Choose an asset of a compatible type.");
        else if (usesAsset && alias is not null && staleAliases.Contains(alias))
            (state, guidance) = (XmlFieldState.Stale, $"{alias} changed after the saved fixture was generated. Generate, validate and save the XML again.");
        else if (status == "Needs value")
            (state, guidance) = required
                ? (XmlFieldState.RequiredMissing, binding is not null && mapping is null ? $"Choose an approved {binding.Role.ToLowerInvariant()} identity." : "Choose a value source for this required field.")
                : (XmlFieldState.OptionalUnmapped, "The mapping has no value yet: choose one or remove the mapping.");
        else if (status == "Suggested")
            (state, guidance) = (XmlFieldState.Suggested, "Suggested from the test pack's role bindings; confirmed when the XML is generated.");
        else if (status == "Synthetic value")
            (state, guidance) = (XmlFieldState.Synthetic, "No mapping: the generator writes a schema-valid placeholder. Map it if the test depends on the value.");
        else if (status == "Not used")
            (state, guidance) = (XmlFieldState.OptionalUnmapped, null);
        else
            (state, guidance) = (XmlFieldState.Valid, null);
        if (issue is not null && guidance is null) guidance = "Fix the value or its mapping, then validate again.";
        return new(path, display, technical, required, expected, alias, source, state, status, issue, guidance, hasMapping, mapping?.IsSuggested == true);
    }

    /// <summary>The first validation diagnostic naming the field's element (<c>'Fodselsnummer'</c>), if any.</summary>
    private static string? Issue(XmlParameter parameter, IReadOnlyList<string> issues)
    {
        var leaf = parameter.Path.Split('.')[^1];
        return issues.FirstOrDefault(i => i.Contains($"'{leaf}'", StringComparison.Ordinal) || i.Contains($":{leaf}'", StringComparison.Ordinal));
    }

    public static string MappingStatus(XmlValueMapping row) =>
        row.NeedsReview is not null ? "Needs review"
        : row.Path.Length == 0 || row.Kind == XmlValueSourceKind.TestDataAsset && row.AssetAlias is null || row.Kind == XmlValueSourceKind.Fixed && string.IsNullOrEmpty(row.FixedValue) ? "Needs value"
        : row.IsSuggested ? "Suggested" : "Mapped";

    public static string SourceLabel(XmlValueSourceKind kind) => kind switch
    {
        XmlValueSourceKind.TestDataAsset => "Test Data asset",
        XmlValueSourceKind.Fixed => "Fixed value",
        XmlValueSourceKind.Generated => "Generated value",
        _ => "Leave generated/default",
    };

    /// <summary>
    /// Whether an asset's declared value type can fill an XSD simple type. Only a specific declared type that contradicts a specific XSD type is
    /// incompatible (a Boolean asset for an xs:date); an untyped or string asset is not judged here — XSD validation decides.
    /// </summary>
    public static bool TypeCompatible(string? xsdType, TestDataValueType valueType)
    {
        if (string.IsNullOrWhiteSpace(xsdType) || valueType is TestDataValueType.None or TestDataValueType.String or TestDataValueType.Other) return true;
        var local = xsdType.Split(':')[^1].Trim().ToLowerInvariant();
        string[] integers = ["int", "integer", "long", "short", "byte", "nonnegativeinteger", "positiveinteger", "unsignedint", "unsignedlong", "unsignedshort", "unsignedbyte", "negativeinteger", "nonpositiveinteger"];
        string[] textual = ["string", "normalizedstring", "token", "anyuri", "name", "ncname", "id", "language"];
        if (textual.Contains(local)) return true;
        return valueType switch
        {
            TestDataValueType.Integer => integers.Contains(local) || local is "decimal" or "double" or "float",
            TestDataValueType.Decimal => local is "decimal" or "double" or "float",
            TestDataValueType.Boolean => local == "boolean",
            TestDataValueType.Date => local == "date",
            TestDataValueType.DateTime => local == "datetime",
            TestDataValueType.Guid => true, // a GUID is lexically a string pattern; only XSD validation can judge it
            _ => true,
        } || !(integers.Contains(local) || local is "decimal" or "double" or "float" or "boolean" or "date" or "datetime" or "time" or "gyear");
    }

    public static string Label(XmlFieldState state) => state switch
    {
        XmlFieldState.RequiredMissing => "Required · missing mapping",
        XmlFieldState.NeedsReview => "Needs review",
        XmlFieldState.Unresolved => "Unresolved schema element",
        XmlFieldState.IncompatibleAsset => "Incompatible asset",
        XmlFieldState.Stale => "Stale mapping",
        XmlFieldState.Suggested => "Suggested",
        XmlFieldState.Valid => "Valid mapping",
        XmlFieldState.Synthetic => "Placeholder (no mapping)",
        _ => "Optional · not mapped",
    };
}
