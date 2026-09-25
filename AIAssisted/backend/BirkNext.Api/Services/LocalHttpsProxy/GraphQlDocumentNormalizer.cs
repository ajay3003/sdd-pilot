using System.Security.Cryptography;
using System.Text;
using BirkNext.LocalHttpsProxy;
using HotChocolate.Language;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Turns an observed GraphQL operation document into the one form Endpoint Discovery keeps: every literal VALUE redacted (strings →
/// <c>""</c>, integers → <c>0</c>, floats → <c>0.0</c>), comments and formatting dropped, tokens joined canonically. The structure a
/// contract check needs — operations, selections, aliases, arguments, variable definitions, fragments, directives, enum literals — is
/// preserved; a user id or search text typed into an inline argument never is. Variables are never part of the document and are
/// never read.
///
/// Tokenised with the GraphQL reader, not a regex, and re-parsed to confirm the result is still a valid document. The hash identifies
/// a document variant: two operations with the same name but different selections get different hashes.
/// </summary>
public static class GraphQlDocumentNormalizer
{
    /// <summary>Normalized documents above this size are not kept; the operation stays observed but cannot be contract-checked.</summary>
    public const int MaxDocumentLength = 16 * 1024;

    /// <summary>A kept document, or — with <see cref="Omission"/> set — an empty document whose hash still identifies the variant.</summary>
    public sealed record Normalized(string Document, string Hash, GraphQlDocumentOmission Omission = GraphQlDocumentOmission.None);

    /// <summary>
    /// The normalization outcome for evidence: a kept document, or an explicit oversize marker (the redacted token stream's hash, no
    /// content) so the operation is reported as "exceeded the retained-query size limit" rather than silently losing its document.
    /// Unparseable text yields neither.
    /// </summary>
    public static (string? Document, string? Hash, GraphQlDocumentOmission Omission) NormalizeWithOutcome(string? query)
    {
        var result = NormalizeCore(query);
        return result is null ? (null, null, GraphQlDocumentOmission.None)
            : result.Omission == GraphQlDocumentOmission.None ? (result.Document, result.Hash, GraphQlDocumentOmission.None)
            : (null, result.Hash, result.Omission);
    }

    /// <summary>Kept document or oversize marker, as carried through the capture pipeline. Null when the text is not parseable GraphQL.</summary>
    public static Normalized? NormalizeForEvidence(string? query) => NormalizeCore(query);

    /// <summary>The normalized, literal-redacted document and its hash; null when the text is not a parseable GraphQL document or too large.</summary>
    public static Normalized? Normalize(string? query) => NormalizeCore(query) is { Omission: GraphQlDocumentOmission.None } kept ? kept : null;

    private static Normalized? NormalizeCore(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            var builder = new StringBuilder(query.Length);
            var reader = new Utf8GraphQLReader(Encoding.UTF8.GetBytes(query));
            var previousWasWord = false;
            while (reader.Read())
            {
                string? token = reader.Kind switch
                {
                    TokenKind.Name => reader.GetName(),
                    TokenKind.String or TokenKind.BlockString => "\"\"",
                    TokenKind.Integer => "0",
                    TokenKind.Float => "0.0",
                    TokenKind.Comment => null,
                    TokenKind.EndOfFile or TokenKind.StartOfFile => null,
                    _ => Punctuator(reader.Kind),
                };
                if (token is null) continue;
                // One space only between two name/number tokens ("query Get", "on User"); punctuators and quoted strings need none.
                var isWord = reader.Kind is TokenKind.Name or TokenKind.Integer or TokenKind.Float;
                if (isWord && previousWasWord) builder.Append(' ');
                builder.Append(token);
                previousWasWord = isWord;
            }
            var document = builder.ToString();
            Utf8GraphQLParser.Parse(document);   // still a valid document after redaction
            // Over the limit: the redacted text lives only in this method; evidence keeps the hash and the marker, never the content.
            return document.Length > MaxDocumentLength
                ? new Normalized("", Hash(document), GraphQlDocumentOmission.ExceededRetentionLimit)
                : new Normalized(document, Hash(document));
        }
        catch (SyntaxException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static string Hash(string document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document)))[..16].ToLowerInvariant();

    private static string? Punctuator(TokenKind kind) => kind switch
    {
        TokenKind.Bang => "!",
        TokenKind.Dollar => "$",
        TokenKind.Ampersand => "&",
        TokenKind.LeftParenthesis => "(",
        TokenKind.RightParenthesis => ")",
        TokenKind.Spread => "...",
        TokenKind.Colon => ":",
        TokenKind.Equal => "=",
        TokenKind.At => "@",
        TokenKind.LeftBracket => "[",
        TokenKind.RightBracket => "]",
        TokenKind.LeftBrace => "{",
        TokenKind.RightBrace => "}",
        TokenKind.Pipe => "|",
        _ => null,
    };
}
