namespace BirkNext.Web.Services;

/// <summary>
/// Test-pack page registration: pack id → the page that hosts it. Registration metadata only (one entry per pack with a page of its own);
/// shared components never branch on a pack id. A pack without an entry is still listed and reviewable in Integration Quality Review.
/// </summary>
public static class TestPackPages
{
    public static readonly IReadOnlyDictionary<string, string> Registered = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["skolenaervaer"] = "extensions/skolenaervaer",
    };
}
