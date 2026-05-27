using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Microsoft.Extensions.Logging;

namespace BirkNext.Web.Services;

public sealed class ExtractionRuleSetCompiler
{
    private readonly ILogger<ExtractionRuleSetCompiler> _logger;

    // Base keyword sets — must stay synchronized with ExtractionRuleSet.Default().
    // If a keyword is changed in Default(), update the matching array here in the same commit.
    private static readonly string[] BddBaseKeywords = ["Given", "When", "Then"];
    private static readonly string[] Rfc2119UppercaseBaseKeywords = ["MUST NOT", "SHALL NOT", "MUST", "SHALL", "SHOULD", "MAY"];
    private static readonly string[] Rfc2119LowercaseBaseKeywords = ["must not", "shall not", "is required to", "must", "shall", "required"];
    private static readonly string[] DeferralMarkerBaseKeywords = ["TBD", "TODO", "TBC", "open question", "to be defined", "to be decided"];

    private const string BddRuleName = "Classify:BddPattern";
    private const string Rfc2119UpperRuleName = "Classify:Rfc2119Uppercase";
    private const string Rfc2119LowerRuleName = "Classify:Rfc2119Lowercase";
    private const string DeferralRuleName = "Classify:DeferralMarker";
    private const string DefaultRuleName = "Classify:Default";

    private static readonly char[] RegexMetachars =
        ['\\', '^', '$', '.', '|', '?', '*', '+', '(', ')', '[', ']', '{', '}'];

    // Compiler-internal transient. Never surfaces outside this class; no field value content in logs.
    private sealed record ConfigurationViolation(string FieldName, string ViolationType, int? EntryIndex);

    public ExtractionRuleSetCompiler(ILogger<ExtractionRuleSetCompiler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public ExtractionRuleSet Compile(ExtractionRuleSet baseSet, ExtractionRuleConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(baseSet);

        // Empty / null config: emit loaded (all zeros) + fallback, return baseSet unchanged.
        if (config is null || IsEffectivelyEmpty(config))
        {
            // OBS-US4-005: counts only
            _logger.LogInformation(
                "ExtractionRuleConfigurationLoaded: bddKeywordAdditionCount={BddKeywordAdditionCount}, rfc2119UppercaseAdditionCount={Rfc2119UppercaseAdditionCount}, rfc2119LowercaseAdditionCount={Rfc2119LowercaseAdditionCount}, deferralMarkerAdditionCount={DeferralMarkerAdditionCount}, prefixRuleCount={PrefixRuleCount}, ignorePrefixCount={IgnorePrefixCount}, disabledRuleCount={DisabledRuleCount}, priorityOverrideCount={PriorityOverrideCount}",
                0, 0, 0, 0, 0, 0, 0, 0);
            _logger.LogInformation(
                "ExtractionRuleConfigurationFallback: reason={Reason}",
                "no_configuration");
            return baseSet;
        }

        // Step 1: Validation — fail fast on the first violation; no partial application.
        var violation = Validate(baseSet, config);
        if (violation is not null)
        {
            // OBS-US4-005: field name + code, no value content
            _logger.LogWarning(
                "ExtractionRuleConfigurationFailed: fieldName={FieldName}, violationType={ViolationType}, entryIndex={EntryIndex}, fallbackApplied={FallbackApplied}",
                violation.FieldName, violation.ViolationType, violation.EntryIndex, true);
            _logger.LogInformation(
                "ExtractionRuleConfigurationFallback: reason={Reason}",
                "validation_failure");
            return baseSet;
        }

        // Steps 2–8: Compilation.
        // Non-mutability invariant: all work is on List<> copies; baseSet is never modified.
        var workingFilterRules = baseSet.FilterRules.ToList();
        var workingRules = baseSet.ClassificationRules.ToList();

        // Step 2: Disable rules (both filter and classification).
        var disabled = new HashSet<string>(config.DisabledRuleNames, StringComparer.Ordinal);
        workingFilterRules.RemoveAll(r => disabled.Contains(r.Name));
        workingRules.RemoveAll(r => disabled.Contains(r.Name));

        // Step 3: Priority overrides — reconstruct immutable rule instances at the new priority.
        for (int i = 0; i < workingFilterRules.Count; i++)
        {
            var r = workingFilterRules[i];
            if (config.PriorityOverrides.TryGetValue(r.Name, out int p))
                workingFilterRules[i] = new FilterRule(r.Name, p, r.Condition);
        }
        for (int i = 0; i < workingRules.Count; i++)
        {
            var r = workingRules[i];
            if (config.PriorityOverrides.TryGetValue(r.Name, out int p))
                workingRules[i] = new ClassificationRule(r.Name, p, r.Condition, r.Outcome, r.ApplicableBlockTypes);
        }

        // Step 4: Keyword extend — only replaces when additions are non-empty.
        // If the target rule was disabled in Step 2, TryExtendKeywordRule skips silently.
        TryExtendKeywordRule(workingRules, BddRuleName, config.BddKeywordAdditions, BddBaseKeywords);
        TryExtendKeywordRule(workingRules, Rfc2119UpperRuleName, config.Rfc2119UppercaseAdditions, Rfc2119UppercaseBaseKeywords);
        TryExtendKeywordRule(workingRules, Rfc2119LowerRuleName, config.Rfc2119LowercaseAdditions, Rfc2119LowercaseBaseKeywords);
        TryExtendKeywordRule(workingRules, DeferralRuleName, config.DeferralMarkerAdditions, DeferralMarkerBaseKeywords);

        // Step 5: Add prefix classification rules.
        for (int i = 0; i < config.PrefixRules.Length; i++)
        {
            var entry = config.PrefixRules[i];
            var name = !string.IsNullOrEmpty(entry.Name) ? entry.Name : $"Configure:Prefix:{i}";
            workingRules.Add(new ClassificationRule(
                name,
                entry.Priority,
                new PrefixMatchCondition(entry.Prefix),
                new ClassificationOutcome(entry.Classification, ClassificationSignal.ConfiguredPrefix)));
        }

        // Step 6: Set IgnorePrefixes — base set built-ins are always preserved;
        // user-configured additions are appended after them.
        var ignorePrefixes = config.IgnorePrefixes.Length > 0
            ? ImmutableArray.CreateRange(baseSet.IgnorePrefixes.Concat(config.IgnorePrefixes))
            : (IReadOnlyList<string>)baseSet.IgnorePrefixes;

        // Step 7: Sort (stable, priority descending).
        // ExtractionRuleSet constructor also sorts; this step makes the intent explicit.
        workingFilterRules = [.. workingFilterRules.OrderByDescending(r => r.Priority)];
        workingRules = [.. workingRules.OrderByDescending(r => r.Priority)];

        // Step 8: Construct and return compiled set.
        var compiledSet = new ExtractionRuleSet(workingFilterRules, workingRules, ignorePrefixes);

        // OBS-US4-005: counts only
        _logger.LogInformation(
            "ExtractionRuleConfigurationLoaded: bddKeywordAdditionCount={BddKeywordAdditionCount}, rfc2119UppercaseAdditionCount={Rfc2119UppercaseAdditionCount}, rfc2119LowercaseAdditionCount={Rfc2119LowercaseAdditionCount}, deferralMarkerAdditionCount={DeferralMarkerAdditionCount}, prefixRuleCount={PrefixRuleCount}, ignorePrefixCount={IgnorePrefixCount}, disabledRuleCount={DisabledRuleCount}, priorityOverrideCount={PriorityOverrideCount}",
            config.BddKeywordAdditions.Length,
            config.Rfc2119UppercaseAdditions.Length,
            config.Rfc2119LowercaseAdditions.Length,
            config.DeferralMarkerAdditions.Length,
            config.PrefixRules.Length,
            config.IgnorePrefixes.Length,
            config.DisabledRuleNames.Length,
            config.PriorityOverrides.Count);

        return compiledSet;
    }

    // =============================================================================
    // Helpers
    // =============================================================================

    private static bool IsEffectivelyEmpty(ExtractionRuleConfiguration config)
        => config.BddKeywordAdditions.Length == 0
        && config.Rfc2119UppercaseAdditions.Length == 0
        && config.Rfc2119LowercaseAdditions.Length == 0
        && config.DeferralMarkerAdditions.Length == 0
        && config.PrefixRules.Length == 0
        && config.IgnorePrefixes.Length == 0
        && config.DisabledRuleNames.Length == 0
        && config.PriorityOverrides.Count == 0;

    private static ConfigurationViolation? Validate(ExtractionRuleSet baseSet, ExtractionRuleConfiguration config)
    {
        // Check 1: Array length limits (all string[] arrays and PrefixRules ≤ 50).
        if (config.BddKeywordAdditions.Length > 50) return new("BddKeywordAdditions", "too_many_entries", null);
        if (config.Rfc2119UppercaseAdditions.Length > 50) return new("Rfc2119UppercaseAdditions", "too_many_entries", null);
        if (config.Rfc2119LowercaseAdditions.Length > 50) return new("Rfc2119LowercaseAdditions", "too_many_entries", null);
        if (config.DeferralMarkerAdditions.Length > 50) return new("DeferralMarkerAdditions", "too_many_entries", null);
        if (config.PrefixRules.Length > 50) return new("PrefixRules", "too_many_entries", null);
        if (config.IgnorePrefixes.Length > 50) return new("IgnorePrefixes", "too_many_entries", null);
        if (config.DisabledRuleNames.Length > 50) return new("DisabledRuleNames", "too_many_entries", null);

        // Check 2: String value constraints for keyword addition arrays.
        (string Field, string[] Values)[] keywordArrays =
        [
            ("BddKeywordAdditions",       config.BddKeywordAdditions),
            ("Rfc2119UppercaseAdditions", config.Rfc2119UppercaseAdditions),
            ("Rfc2119LowercaseAdditions", config.Rfc2119LowercaseAdditions),
            ("DeferralMarkerAdditions",   config.DeferralMarkerAdditions),
        ];
        foreach (var (field, values) in keywordArrays)
        {
            for (int i = 0; i < values.Length; i++)
            {
                var code = ValidateStringValue(values[i]);
                if (code is not null) return new(field, code, i);
            }
            // Belt-and-suspenders: verify the assembled pattern actually compiles.
            if (values.Length > 0 && !TryCompileKeywordPattern(values))
                return new(field, "pattern_compile_failure", null);
        }

        // Check 3: PrefixRuleEntry constraints.
        for (int i = 0; i < config.PrefixRules.Length; i++)
        {
            var entry = config.PrefixRules[i];
            var code = ValidateStringValue(entry.Prefix);
            if (code is not null) return new($"PrefixRules[{i}].Prefix", code, i);
            if (!Enum.IsDefined(entry.Classification))
                return new($"PrefixRules[{i}].Classification", "invalid_classification", i);
            if (entry.Priority < 1 || entry.Priority > 99)
                return new($"PrefixRules[{i}].Priority", "priority_out_of_range", i);
        }

        // Check 4: DisabledRuleNames — must exist in baseSet; Classify:Default is protected.
        var allRuleNames = baseSet.FilterRules.Select(r => r.Name)
            .Concat(baseSet.ClassificationRules.Select(r => r.Name))
            .ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < config.DisabledRuleNames.Length; i++)
        {
            var name = config.DisabledRuleNames[i];
            if (name == DefaultRuleName) return new("DisabledRuleNames", "default_rule_disabled", i);
            if (!allRuleNames.Contains(name)) return new("DisabledRuleNames", "unknown_rule_name", i);
        }

        // Check 5: PriorityOverrides — keys must exist in baseSet; Classify:Default is protected; values 1–99.
        foreach (var (key, value) in config.PriorityOverrides)
        {
            if (key == DefaultRuleName) return new("PriorityOverrides", "default_priority_override", null);
            if (!allRuleNames.Contains(key)) return new("PriorityOverrides", "unknown_rule_name", null);
            if (value < 1 || value > 99) return new("PriorityOverrides", "priority_out_of_range", null);
        }

        // Check 6: IgnorePrefixes string value constraints.
        for (int i = 0; i < config.IgnorePrefixes.Length; i++)
        {
            var code = ValidateStringValue(config.IgnorePrefixes[i]);
            if (code is not null) return new("IgnorePrefixes", code, i);
        }

        return null;
    }

    // Returns the violation code string, or null if the value is valid.
    private static string? ValidateStringValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "empty_value";
        if (value.Length > 200)
            return "value_too_long";
        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E)
                return "non_ascii_characters";
        }
        if (value.IndexOfAny(RegexMetachars) >= 0)
            return "regex_metacharacter";
        return null;
    }

    private static bool TryCompileKeywordPattern(string[] values)
    {
        try
        {
            var escaped = values.Select(Regex.Escape);
            var pattern = $@"\b(?:{string.Join("|", escaped)})\b";
            _ = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Replaces the PatternMatchCondition of the named rule with an extended keyword pattern.
    // No-op when additions is empty, or when the rule was removed in Step 2.
    private static void TryExtendKeywordRule(
        List<ClassificationRule> rules,
        string ruleName,
        string[] additions,
        string[] baseKeywords)
    {
        if (additions.Length == 0)
            return;

        int idx = rules.FindIndex(r => r.Name == ruleName);
        if (idx < 0)
            return; // rule was disabled; skip silently

        var rule = rules[idx];
        var escaped = baseKeywords.Concat(additions).Select(Regex.Escape);
        var pattern = $@"\b(?:{string.Join("|", escaped)})\b";
        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        rules[idx] = new ClassificationRule(
            rule.Name, rule.Priority, new PatternMatchCondition(regex), rule.Outcome, rule.ApplicableBlockTypes);
    }
}
