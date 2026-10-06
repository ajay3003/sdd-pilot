using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Web.Services;

/// <summary>
/// The one content fingerprint for SDD artifact text: upper-case hex SHA-256 of the UTF-8 text. Used by the artifact
/// repository's revision capture and by Sample Project document discovery, so both identify the same content alike.
/// </summary>
public static class ArtifactFingerprint
{
    public static string Compute(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
