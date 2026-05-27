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
    // Fired when a strong ambiguity phrase is present (e.g. "how should", "should we", "unresolved").
    // Higher priority than RequirementLanguage; prevents "how should we handle X" from becoming REQUIREMENT.
    ClarificationSignal,
    // Fired when requirement-language words ("should", "can") are present but no stronger signal matched.
    // Lower priority than QuestionTerminator and DeferralMarker so "Should we?" stays NeedsClarification.
    RequirementLanguage,
    // Fired when a heading-context rule matches the block's preceding section heading.
    // Used by profile-specific rules to boost classification based on document section.
    HeadingContext,
    Default,
}
