using System.Text.RegularExpressions;

namespace BirkNext.Api.Tests.Infrastructure;

/// <summary>Helpers for "nothing identifying is persisted" checks over serialized records.</summary>
public static partial class PersistedText
{
    /// <summary>
    /// Replaces every GUID — dashed ("D") or 32-character ("N") — with <c>{guid}</c>. Generated ids (run ids, step correlations) are random
    /// hex: a dashed GUID's last 12-character block or an N-form GUID holds an exact 11-digit run by chance (≈0.4 % per GUID), which an
    /// "11-digit national identifier" search would otherwise report. GUIDs are never identifiers of a person.
    /// </summary>
    public static string WithoutGuids(string text) => Guid().Replace(text, "{guid}");

    /// <summary>True when an 11-digit number (the shape of a Norwegian national identity number) appears outside any GUID.</summary>
    public static bool HasElevenDigitNumber(string text) => ElevenDigits().IsMatch(WithoutGuids(text));

    [GeneratedRegex("(?<![0-9a-fA-F])(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})(?![0-9a-fA-F])")]
    private static partial Regex Guid();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")]
    private static partial Regex ElevenDigits();
}
