using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Places Test Data values into a generated XML fixture: each binding's alias is resolved in the Test Data catalog (approved assets with a value
/// only) and written into the element at its path under the root (local names). Nothing else changes; an element that is not in the XML is a
/// problem, never created. The XML is parsed without DTD processing; values are never logged or returned outside the XML.
/// </summary>
public static partial class TestDataXmlBinder
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*(\.[A-Za-z_][A-Za-z0-9_\-]*){0,15}$")]
    private static partial Regex PathPattern();

    public static TestDataXmlBindResult Bind(TestDataXmlBindRequest request, IReadOnlyList<TestDataAsset> assets)
    {
        if (string.IsNullOrWhiteSpace(request.Xml) || request.Xml.Length > 2 * 1024 * 1024) return new(null, ["The XML must contain at most 2 MB."]);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(request.Xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { return new(null, ["The XML could not be read."]); }
        if (document.Root is not { } root) return new(null, ["The XML has no root element."]);
        var problems = new List<string>();
        foreach (var binding in request.Bindings)
        {
            if (!PathPattern().IsMatch(binding.ElementPath ?? "")) { problems.Add($"The element path for {binding.Alias} is not valid."); continue; }
            var asset = assets.FirstOrDefault(a => string.Equals(a.Alias, binding.Alias, StringComparison.Ordinal));
            if (asset is null) { problems.Add($"Test Data has no asset {binding.Alias}."); continue; }
            if (asset.Approval != TestDataApproval.Approved) { problems.Add($"{binding.Alias} is not approved in Test Data."); continue; }
            if (asset.Value?.Reveal() is not { Length: > 0 } value) { problems.Add($"{binding.Alias} has no value in this backend."); continue; }
            var element = binding.ElementPath.Split('.').Aggregate<string, XElement?>(root, (current, name) => current?.Elements().FirstOrDefault(e => e.Name.LocalName == name));
            if (element is null) { problems.Add($"The generated XML has no element {binding.ElementPath} for {binding.Alias}."); continue; }
            element.Value = value;
        }
        return problems.Count > 0 ? new(null, problems) : new(document.Declaration is null ? document.ToString(SaveOptions.DisableFormatting) : document.Declaration + document.ToString(SaveOptions.DisableFormatting), []);
    }
}
