using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Web.Models;

/// <summary>Construction-time source range for a meaningful Markdown block. Lines are 1-based and inclusive.</summary>
public sealed record SourceRangeProvenance(
    string SourceBlockId,
    int StartLine,
    int EndLine,
    string BlockType,
    string SourceFingerprint);

/// <summary>Orthogonal diagnostic metadata linking one Explorer projection to its authored source blocks.</summary>
public sealed record ProjectionProvenance(
    string ProjectionId,
    string ProjectionKind,
    IReadOnlyList<SourceRangeProvenance> Sources)
{
    public static SourceRangeProvenance Source(string documentFingerprint, int startLine, int endLine, string blockType, string sourceText)
    {
        var fingerprint = Hash(sourceText);
        var idMaterial = $"{documentFingerprint}|{blockType}|{startLine}|{endLine}|{fingerprint}";
        return new SourceRangeProvenance(Hash(idMaterial)[..32], startLine, endLine, blockType, fingerprint);
    }

    public static ProjectionProvenance Create(string role, string kind, string logicalKey, params SourceRangeProvenance[] sources)
    {
        var sourceIds = string.Join("|", sources.Select(s => s.SourceBlockId));
        var material = $"{role}|{kind}|{logicalKey}|{sourceIds}";
        return new ProjectionProvenance(Hash(material)[..32], kind, sources);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
