namespace BirkNext.Web.Models;

public enum ClassificationSignal
{
    BddPattern,
    Rfc2119Uppercase,
    Rfc2119Lowercase,
    FrPrefix,
    QuestionTerminator,
    DeferralMarker,
    // Fired when a PrefixMatchCondition rule wins (US4 configured prefix rules).
    // Classification and priority are determined by the matching PrefixRuleEntry.
    ConfiguredPrefix,
    Default,
}
