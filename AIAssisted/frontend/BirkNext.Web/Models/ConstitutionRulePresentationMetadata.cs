namespace BirkNext.Web.Models;

/// <summary>
/// Presentation metadata for Constitution rules in QA Auditor findings.
/// Uses a reference type (record) instead of ValueTuple to ensure proper marshaling across WASM boundaries.
/// </summary>
public sealed record ConstitutionRulePresentationMetadata(string RuleTitle, string RuleType);
