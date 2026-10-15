using System.Globalization;
using System.Text.Json;
using System.Xml;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Type-aware validation of an asset's value: scalars by their declared type, JSON by parsing, XML by a hardened parse (DTDs prohibited, no
/// resolver). Validation says whether the value is technically what it claims to be — never that it is approved, and never business validity.
/// Schema validation is not done here. A provider's own validation (for example a confirmed external profile) takes precedence.
/// </summary>
public static class TestDataValidator
{
    public static (TestDataValidation Validation, string Detail) Validate(TestDataAsset asset)
    {
        if (asset.Validation is { } declared) return (declared, asset.ValidationDetail ?? "Validated by its provider.");
        if (asset.ExpectsAbsence) return (TestDataValidation.Validated, "Expects that nothing exists; there is no value to validate.");
        if (asset.Value is null) return (TestDataValidation.Unavailable, "No value is available to the backend.");
        var value = asset.Value.Reveal();
        if (asset.Kind == TestDataKind.Scalar) return Scalar(asset.ValueType, value);
        if (asset.Kind == TestDataKind.EntityReference) return (TestDataValidation.Unknown, "A reference to data elsewhere; it is checked only by its provider.");
        var media = MediaType(asset.MediaType);
        if (media.EndsWith("json", StringComparison.Ordinal))
        {
            try { using var _ = JsonDocument.Parse(value); return (TestDataValidation.Validated, "Well-formed JSON (not validated against a schema)."); }
            catch (JsonException) { return (TestDataValidation.Invalid, "Not well-formed JSON."); }
        }
        if (media.EndsWith("xml", StringComparison.Ordinal))
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                while (reader.Read()) { }
                return (TestDataValidation.Validated, "Well-formed XML (not validated against a schema).");
            }
            catch (XmlException) { return (TestDataValidation.Invalid, "Not well-formed XML, or it declares a DTD (refused)."); }
        }
        return (TestDataValidation.Unknown, $"{(media.Length == 0 ? "No media type" : media)}: the format is not checked.");
    }

    private static (TestDataValidation, string) Scalar(TestDataValueType type, string value)
    {
        var text = value.Trim();
        var ok = type switch
        {
            TestDataValueType.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            TestDataValueType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            TestDataValueType.Boolean => bool.TryParse(text, out _),
            TestDataValueType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            TestDataValueType.DateTime => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            TestDataValueType.Guid => Guid.TryParse(text, out _),
            _ => true,
        };
        return ok ? (TestDataValidation.Validated, $"A valid {type} value.") : (TestDataValidation.Invalid, $"Not a valid {type} value.");
    }

    /// <summary>The media type without parameters, lower case ("application/xml; charset=utf-8" → "application/xml").</summary>
    public static string MediaType(string? mediaType) => (mediaType ?? "").Split(';')[0].Trim().ToLowerInvariant();
}
