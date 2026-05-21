using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Services;

public sealed class PrefixRuleEntry
{
    public string?      Name           { get; set; }
    public string       Prefix         { get; set; } = string.Empty;
    public ScenarioKind Classification { get; set; }
    public int          Priority       { get; set; } = 10;
}

public sealed class ExtractionRuleConfiguration
{
    public string[]               BddKeywordAdditions       { get; set; } = [];
    public string[]               Rfc2119UppercaseAdditions { get; set; } = [];
    public string[]               Rfc2119LowercaseAdditions { get; set; } = [];
    public string[]               DeferralMarkerAdditions   { get; set; } = [];
    public PrefixRuleEntry[]      PrefixRules               { get; set; } = [];
    public string[]               IgnorePrefixes            { get; set; } = [];
    public string[]               DisabledRuleNames         { get; set; } = [];
    public Dictionary<string, int> PriorityOverrides        { get; set; } = [];
}
