using System.Text;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>How a text entry was decoded, and whether its (valid) text already contains encoding damage from an earlier step.</summary>
public sealed record SourceTextEncoding(string Path, string Encoding, bool SuspectedMojibake);

/// <summary>
/// Safe source text decoding, one rule for every archive entry:
/// <list type="number">
/// <item>Valid UTF-8 (with or without BOM) → UTF-8.</item>
/// <item>Invalid UTF-8 → Windows-1252 (every byte maps, so nothing becomes U+FFFD), reported as such.</item>
/// <item>Valid UTF-8 that already contains mojibake ("Ã¸", "â€”") → kept exactly as written and reported as suspected source corruption.
/// The text is never "repaired": the damage is in the source itself.</item>
/// </list>
/// </summary>
public static partial class SourceTextDecoder
{
    public const string Utf8 = "UTF-8", Windows1252 = "Windows-1252";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Cp1252 = CodePagesEncoding();

    // UTF-8 bytes of Norwegian letters / dashes / quotes / emoji read once as Windows-1252: "Ã¸", "Ã¥", "Ã¦", "Ã˜", "â€”", "â€™", "ðŸ".
    [GeneratedRegex(@"Ã[\u0080-¿ŒœŠšŸŽžƒˆ˜–-›€™]|â€[\u0090-¿Œ-™€]|ðŸ")]
    private static partial Regex MojibakeRe();

    public static (string Text, string Encoding, bool SuspectedMojibake) Decode(ReadOnlySpan<byte> bytes)
    {
        var body = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
        try
        {
            var text = StrictUtf8.GetString(body);
            return (text, Utf8, MojibakeRe().IsMatch(text));
        }
        catch (DecoderFallbackException)
        {
            return (Cp1252.GetString(body), Windows1252, false);
        }
    }

    /// <summary>True when valid text shows the typical damage of UTF-8 read as Windows-1252.</summary>
    public static bool LooksLikeMojibake(string text) => MojibakeRe().IsMatch(text);

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
