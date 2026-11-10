using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E.Suggestions;

/// <summary>
/// Turns UI-surface facts into DRAFT test suggestions in the existing Critical E2E step vocabulary. Rules (versioned by <see cref="RuleVersion"/>):
/// <list type="bullet">
/// <item>Page opens — navigate to a parameterless route; the final assertion is the page's own heading from source (else the route itself).</item>
/// <item>Navigation — a link whose href is another page's route in the same snapshot; the final assertion is that route.</item>
/// <item>Required fields — a form whose required fields come from markup or DataAnnotations, with a ValidationSummary (Blazor renders
///   <c>ul.validation-errors</c>) or a single ValidationMessage (<c>div.validation-message</c>). Submit empty; the final assertion is that
///   validation output, its text only when the source declares ErrorMessage.</item>
/// <item>Invalid input — one constraint (max length, range) violated by exactly one generated value while every other required field gets a
///   valid synthetic value; asserted like required fields.</item>
/// <item>Valid submission — every field valid, submit; the expected result is NOT known from source, so there is no final assertion and the
///   suggestion needs one from a reviewer or a requirement.</item>
/// </list>
/// Values are synthetic and generated from the declared constraints; identifiers (ids, national numbers, references) and patterns are never
/// generated — they need Test Data. Controls inside @if/@foreach are marked; unrelated controls are never chained into a flow.
/// </summary>
public static partial class SuggestionEngine
{
    public const string RuleVersion = "suggest-v1";
    public const int MaxNavigationPerPage = 5;

    [GeneratedRegex(@"(^|[^a-z])(id|nr|nummer|fnr|fodselsnummer|fødselsnummer|personnummer|ssn|orgnr|organisasjonsnummer|reference|referanse|token|password|passord|secret)([^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifierLike();

    public static string Id(params string[] parts) =>
        "sug-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)))).ToLowerInvariant()[..16];

    public static bool Parameterless(string route) => !route.Contains('{');

    /// <summary>Locator candidates in priority order: test id → role + accessible name → label → stable id → nothing else (no positional CSS).</summary>
    public static List<SuggestedLocator> Locators(SourceUiControl c)
    {
        var list = new List<SuggestedLocator>();
        if (c.TestId is { Length: > 0 } tid) list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = tid }, 1, "data-testid in source"));
        var name = c.Kind is SourceUiControlKind.Button or SourceUiControlKind.SubmitButton or SourceUiControlKind.Link or SourceUiControlKind.Heading ? c.Text ?? c.Label : c.Label;
        if (c.Role is { } role && name is { Length: > 0 } && c.Kind is not (SourceUiControlKind.TextInput or SourceUiControlKind.NumberInput or SourceUiControlKind.DateInput))
            list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = role, Name = name }, 2, "role + accessible name from source"));
        if (c.Label is { Length: > 0 } label && c.Kind is SourceUiControlKind.TextInput or SourceUiControlKind.NumberInput or SourceUiControlKind.DateInput or SourceUiControlKind.Select
            or SourceUiControlKind.Checkbox or SourceUiControlKind.TextArea)
            list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.Label, Value = label }, 3, "associated label in source"));
        if (c.ElementId is { Length: > 0 } id && Regex.IsMatch(id, @"^[A-Za-z][\w-]*$"))
            list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "#" + id }, 4, "static id in source"));
        if (c.Kind == SourceUiControlKind.ValidationSummary)
            list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "ul.validation-errors" }, 6, "Blazor ValidationSummary rendering convention (scoped CSS fallback)"));
        if (c.Kind == SourceUiControlKind.ValidationMessage)
            list.Add(new(new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "div.validation-message" }, 6, "Blazor ValidationMessage rendering convention (scoped CSS fallback)"));
        return list;
    }

    /// <summary>A safe synthetic value satisfying the declared constraints, or null when one must come from Test Data (identifier, pattern, file).</summary>
    public static string? ValidValue(SourceUiControl c)
    {
        // Credentials are never generated, not even synthetic-looking ones: they come from approved Test Data or the attended session.
        if (c.InputType == "password" || c.Pattern is not null || c.Kind == SourceUiControlKind.FileInput) return null;
        if (IdentifierLike().IsMatch(c.Binding ?? "") || IdentifierLike().IsMatch(c.Label ?? "")) return null;
        return c.Kind switch
        {
            SourceUiControlKind.NumberInput => decimal.TryParse(c.RangeMin, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var min) ? min.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1",
            SourceUiControlKind.DateInput => "2026-01-15",
            SourceUiControlKind.Select => c.Options.FirstOrDefault(),
            SourceUiControlKind.Checkbox => null,
            _ when c.InputType == "email" => "synthetic@example.invalid",
            _ => Fit("Synthetic", c.MinLength, c.MaxLength),
        };
    }

    private static string Fit(string seed, int? min, int? max)
    {
        var v = seed;
        while (min is { } m && v.Length < m) v += "x";
        return max is { } mx && v.Length > mx ? v[..Math.Max(1, mx)] : v;
    }

    private static string Ref(SourceUiPage p, SourceUiControl c) => $"{p.File}:{c.Line}";

    private static SuggestedStep Navigate(string route) => new() { StepId = "s1", Description = $"Open {route}", Action = CompanionActionKind.Navigate, Value = route };

    public static List<TestSuggestion> Generate(string projectKey, Guid snapshotId, SourceUiSurface surface, IReadOnlyList<CriticalE2EFlowDefinition> flows)
    {
        var result = new List<TestSuggestion>();
        var routes = surface.Pages.SelectMany(p => p.Routes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var page in surface.Pages.Where(p => p.Routes.Count > 0))
        {
            var route = page.Routes.FirstOrDefault(Parameterless);
            if (route is null) continue;   // a parameterised route needs a real record from Test Data; not suggested blindly
            var feature = page.Title ?? System.IO.Path.GetFileNameWithoutExtension(page.File);
            var preconditions = new List<string>();
            if (page.RequiresAuthorization) preconditions.Add(page.Roles.Count > 0 ? $"Signed in with a role: {string.Join(", ", page.Roles)}" : "Signed in (the page requires authorization)");
            TestSuggestion Base(SuggestionKind kind, string title, IEnumerable<string> keyParts) => new()
            {
                SuggestionId = Id([RuleVersion, projectKey, route, kind.ToString(), .. keyParts]), RuleVersion = RuleVersion, ProjectKey = projectKey, SourceSnapshotId = snapshotId,
                Module = page.Module, Feature = feature, Kind = kind, Route = route, Title = title, RequirementRefs = page.RequirementRefs, Preconditions = preconditions,
                Origins = page.RequirementRefs.Count > 0 ? [SuggestionOrigin.SourceImplementationDerived, SuggestionOrigin.RequirementDerived] : [SuggestionOrigin.SourceImplementationDerived],
                Limitations = [.. page.Limitations.Take(3)],
            };

            // Page opens.
            var heading = page.Controls.FirstOrDefault(c => c.Kind == SourceUiControlKind.Heading && !c.Conditional && !c.Repeated && (c.Text is not null || c.TestId is not null));
            var opens = Base(SuggestionKind.PageOpens, $"{feature} opens", []);
            result.Add(opens with
            {
                SourceRefs = [page.File + (heading is null ? "" : $":{heading.Line}")],
                Steps = heading is not null
                    ? [Navigate(route), new SuggestedStep { StepId = "s2", Description = $"Heading \"{heading.Text}\" is visible", Action = CompanionActionKind.AssertVisible, ControlId = heading.Id, Locators = Locators(heading), IsFinalAssertion = true }]
                    : [Navigate(route), new SuggestedStep { StepId = "s2", Description = $"The browser is on {route}", Action = CompanionActionKind.AssertRoute, Expected = route, IsFinalAssertion = true }],
                ExpectedResult = heading is not null ? $"The page heading \"{heading.Text}\" is visible on {route}." : $"The browser shows {route}.",
                ExpectedBasis = heading is not null ? $"The page's own heading in source ({Ref(page, heading)})" : $"The page's @page route ({page.File})",
                Confidence = heading?.TestId is not null ? "High" : "Medium",
            });

            // Navigation to another page of the same snapshot.
            foreach (var link in page.Controls.Where(c => c.Kind == SourceUiControlKind.Link && c.Href is { } h && h.StartsWith('/') && routes.Contains(h.TrimEnd('/') is "" ? "/" : h.TrimEnd('/'))
                         && !string.Equals(h, route, StringComparison.OrdinalIgnoreCase) && !c.Repeated).Take(MaxNavigationPerPage))
            {
                var target = link.Href!;
                result.Add(Base(SuggestionKind.Navigation, $"{feature}: \"{link.Text ?? target}\" opens {target}", [link.Id, target]) with
                {
                    SourceRefs = [Ref(page, link)],
                    Steps =
                    [
                        Navigate(route),
                        new SuggestedStep { StepId = "s2", Description = $"Click \"{link.Text ?? target}\"", Action = CompanionActionKind.Click, ControlId = link.Id, Locators = Locators(link) },
                        new SuggestedStep { StepId = "s3", Description = $"The browser is on {target}", Action = CompanionActionKind.AssertRoute, Expected = target, IsFinalAssertion = true },
                    ],
                    ExpectedResult = $"The browser shows {target}.", ExpectedBasis = $"The link's href in source ({Ref(page, link)}) and the target page's @page route",
                    Confidence = link.TestId is not null ? "High" : "Medium",
                    Limitations = link.Conditional ? ["The link is conditional in source (@if): it may be absent for some users or states."] : [],
                });
            }

            foreach (var form in page.Forms)
            {
                // Conditional or repeated fields are never filled automatically: whether they render depends on state the source does not fix.
                var fields = page.Controls.Where(c => c.FormId == form.Id && !c.Conditional && !c.Repeated && c.Kind is SourceUiControlKind.TextInput or SourceUiControlKind.NumberInput or SourceUiControlKind.DateInput
                    or SourceUiControlKind.Select or SourceUiControlKind.TextArea or SourceUiControlKind.Checkbox or SourceUiControlKind.FileInput).ToList();
                var submit = page.Controls.Where(c => c.FormId == form.Id && c.Kind == SourceUiControlKind.SubmitButton).ToList();
                if (fields.Count == 0 || submit.Count != 1) continue;   // no submit, or several: which one is the submission is a human decision
                var summary = page.Controls.FirstOrDefault(c => c.FormId == form.Id && c.Kind == SourceUiControlKind.ValidationSummary);
                var messages = page.Controls.Where(c => c.FormId == form.Id && c.Kind == SourceUiControlKind.ValidationMessage).ToList();
                var required = fields.Where(f => f.Required).ToList();
                var formTitle = $"{feature}{(page.Forms.Count > 1 ? $" ({form.Id})" : "")}";
                var submitStep = (string id) => new SuggestedStep { StepId = id, Description = $"Click \"{submit[0].Text ?? "submit"}\"", Action = CompanionActionKind.Click, ControlId = submit[0].Id, Locators = Locators(submit[0]), Mutating = true };
                // The validation output this form renders, and the assertion it justifies.
                (SuggestedStep? Step, string? Expected, string? Basis) Validation(string stepId, SourceUiControl? violated, bool requiredViolation)
                {
                    var message = requiredViolation ? violated?.ErrorMessage : violated?.ConstraintMessage;
                    if (summary is not null)
                        return message is not null
                            ? (new SuggestedStep { StepId = stepId, Description = $"Validation summary contains \"{message}\"", Action = CompanionActionKind.AssertText, ControlId = summary.Id, Locators = Locators(summary), Expected = message, Match = CompanionTextMatch.Contains, IsFinalAssertion = true },
                                $"The validation summary shows \"{message}\".", $"DataAnnotations ErrorMessage in source and the ValidationSummary ({Ref(page, summary)})")
                            : (new SuggestedStep { StepId = stepId, Description = "Validation summary is visible", Action = CompanionActionKind.AssertVisible, ControlId = summary.Id, Locators = Locators(summary), IsFinalAssertion = true },
                                "The form is not submitted and the validation summary is shown.", $"Required/constraint metadata and the ValidationSummary in source ({Ref(page, summary)}); the message text is not declared, so only its presence is asserted");
                    var single = violated is not null ? messages.Where(m => m.Binding == violated.Binding).ToList() : messages;
                    if (messages.Count == 1 && single.Count == 1)
                        return (new SuggestedStep { StepId = stepId, Description = "Validation message is visible", Action = CompanionActionKind.AssertVisible, ControlId = single[0].Id, Locators = Locators(single[0]), IsFinalAssertion = true },
                            "The validation message is shown.", $"The only ValidationMessage of the form in source ({Ref(page, single[0])})");
                    return (null, null, null);
                }
                List<SuggestedStep> Fill(IEnumerable<SourceUiControl> controls, int start, List<string> needs)
                {
                    var steps = new List<SuggestedStep>();
                    foreach (var f in controls)
                    {
                        var v = ValidValue(f);
                        if (v is null)
                        {
                            if (f.Kind != SourceUiControlKind.Checkbox) needs.Add($"{f.Label ?? f.Binding ?? f.Id}: a value from approved Test Data ({(f.InputType == "password" ? "credential" : f.Pattern is not null ? "pattern-constrained" : f.Kind == SourceUiControlKind.FileInput ? "file" : "identifier-like")})");
                            continue;
                        }
                        steps.Add(new SuggestedStep
                        {
                            StepId = $"s{start + steps.Count}", Description = $"{(f.Kind == SourceUiControlKind.Select ? "Select" : "Fill")} {f.Label ?? f.Binding}",
                            Action = f.Kind == SourceUiControlKind.Select ? CompanionActionKind.Select : CompanionActionKind.Fill, ControlId = f.Id, Locators = Locators(f), Value = v,
                            ValueBasis = "synthetic value generated from the declared constraints",
                        });
                    }
                    return steps;
                }
                var refs = fields.Select(f => Ref(page, f)).Append(Ref(page, submit[0])).ToList();

                if (required.Count > 0)
                {
                    var (assert, expected, basis) = Validation("s3", required.Count == 1 ? required[0] : null, requiredViolation: true);
                    result.Add(Base(SuggestionKind.RequiredFields, $"{formTitle}: required fields are enforced", [form.Id, .. required.Select(r => r.Id)]) with
                    {
                        SourceRefs = refs, Steps = assert is null ? [Navigate(route), submitStep("s2")] : [Navigate(route), submitStep("s2"), assert],
                        ExpectedResult = expected, ExpectedBasis = basis, Confidence = assert is null ? "Low" : "Medium",
                        Limitations = assert is null ? ["The form has no ValidationSummary and not exactly one ValidationMessage: how the error is shown is not known from source."] : [],
                    });
                }

                foreach (var f in fields.Where(f => f.MaxLength is not null || f.RangeMax is not null).Take(3))
                {
                    var needs = new List<string>();
                    var others = Fill(required.Where(r => r.Id != f.Id), 2, needs);
                    var invalid = f.MaxLength is { } max ? new string('x', max + 1)
                        : decimal.TryParse(f.RangeMax, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var top) ? (top + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                    if (invalid is null) continue;
                    var violation = f.MaxLength is not null ? $"one character more than the maximum length {f.MaxLength}" : $"one above the range maximum {f.RangeMax}";
                    var fillInvalid = new SuggestedStep { StepId = $"s{2 + others.Count}", Description = $"Fill {f.Label ?? f.Binding} with {violation}", Action = CompanionActionKind.Fill,
                        ControlId = f.Id, Locators = Locators(f), Value = invalid, ValueBasis = $"violates exactly one constraint: {violation}" };
                    var (assert, expected, basis) = Validation($"s{4 + others.Count}", f, requiredViolation: false);
                    var steps = new List<SuggestedStep> { Navigate(route) };
                    steps.AddRange(others); steps.Add(fillInvalid); steps.Add(submitStep($"s{3 + others.Count}"));
                    if (assert is not null) steps.Add(assert);
                    result.Add(Base(SuggestionKind.InvalidInput, $"{formTitle}: {f.Label ?? f.Binding} rejects {violation}", [form.Id, f.Id, "invalid"]) with
                    {
                        SourceRefs = refs, Steps = steps, ExpectedResult = expected, ExpectedBasis = basis, TestDataNeeds = needs, Confidence = assert is null ? "Low" : "Medium",
                    });
                }

                {
                    var needs = new List<string>();
                    var steps = new List<SuggestedStep> { Navigate(route) };
                    steps.AddRange(Fill(fields, 2, needs));
                    steps.Add(submitStep($"s{steps.Count + 1}"));
                    result.Add(Base(SuggestionKind.ValidSubmission, $"{formTitle}: a valid submission", [form.Id, "valid"]) with
                    {
                        SourceRefs = refs, Steps = steps, TestDataNeeds = needs, Confidence = "Low",
                        // What a successful submission shows is not stated in source: never invented.
                        ExpectedResult = null, ExpectedBasis = null,
                        Limitations = [$"The expected result of a successful submission is not known from source ({form.SubmitHandler ?? "no handler"}); a reviewer or a requirement must state it."],
                    });
                }
            }
        }
        return [.. result.Select(s => Covered(s, flows))];
    }

    /// <summary>Existing flows are reused, never duplicated: same route + every control selector already in a flow = covered; same route and some
    /// selectors = partially covered (extend that flow); same requirement on another route = overlap a person reviews.</summary>
    internal static TestSuggestion Covered(TestSuggestion s, IReadOnlyList<CriticalE2EFlowDefinition> flows)
    {
        static string Key(CompanionSelector sel) => sel.Kind == CompanionSelectorKind.Role ? $"role:{sel.Role}:{sel.Name}".ToLowerInvariant() : $"{sel.Kind}:{sel.Value}".ToLowerInvariant();
        var mine = s.Steps.Where(st => st.Locators.Count > 0).Select(st => st.Locators.Select(l => Key(l.Selector)).ToHashSet()).ToList();
        foreach (var flow in flows)
        {
            if (flow.Tags.Contains($"suggestion:{s.SuggestionId}")) return s with { Coverage = SuggestionCoverage.AlreadyCovered, CoveredByFlowId = flow.Id, CoverageNote = $"Approved from this suggestion as \"{flow.Name}\"." };
            var flowRoutes = flow.Steps.Select(st => st.BrowserAction == CompanionActionKind.Navigate ? st.Value : st.ExecutionRoute).OfType<string>()
                .Select(r => CriticalE2ERoutes.Normalize(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!flowRoutes.Contains(CriticalE2ERoutes.Normalize(s.Route))) continue;
            var theirs = flow.Steps.Where(st => st.Selector is not null).Select(st => Key(st.Selector!)).ToHashSet();
            var hit = mine.Count(set => set.Overlaps(theirs));
            if (mine.Count > 0 && hit == mine.Count && (s.Kind != SuggestionKind.PageOpens || flow.Steps.Count > 0))
                return s with { Coverage = SuggestionCoverage.AlreadyCovered, CoveredByFlowId = flow.Id, CoverageNote = $"\"{flow.Name}\" already opens {s.Route} and uses every control of this suggestion." };
            if (hit > 0)
                return s with { Coverage = SuggestionCoverage.PartiallyCovered, CoveredByFlowId = flow.Id, CoverageNote = $"\"{flow.Name}\" covers {hit} of {mine.Count} control(s): extend it rather than adding a copy." };
        }
        if (s.RequirementRefs.Count > 0 && flows.FirstOrDefault(f => f.RequirementRef is { } r && s.RequirementRefs.Contains(r, StringComparer.OrdinalIgnoreCase)) is { } same)
            return s with { Coverage = SuggestionCoverage.OverlapRequiresReview, CoveredByFlowId = same.Id, CoverageNote = $"\"{same.Name}\" covers requirement {same.RequirementRef} on another route." };
        return s with { Coverage = SuggestionCoverage.NewSuggestion };
    }

    /// <summary>Readiness states from the suggestion and its recorded verification/decisions. Readiness never implies an execution result.</summary>
    public static List<SuggestionState> States(TestSuggestion s, bool executed)
    {
        var states = new List<SuggestionState>();
        // Validation output exists only after a submission: it is verified when the approved test runs, not during mapping.
        var controlSteps = s.Steps.Where(st => st.ControlId is not null && !st.Locators.All(l => l.Basis.Contains("Validation", StringComparison.Ordinal))).ToList();
        bool Mapped(SuggestedStep st) => s.Decisions.LastOrDefault(d => d.StepId == st.StepId) is { Decision: LocatorDecision.Accepted or LocatorDecision.AlternativeChosen, Selector: not null };
        var unmapped = controlSteps.Count(st => !Mapped(st));
        if (unmapped > 0) states.Add(SuggestionState.NeedsDomVerification);
        if (s.TestDataNeeds.Count > 0) states.Add(SuggestionState.NeedsTestData);
        if (!s.Steps.Any(st => st.IsFinalAssertion)) states.Add(SuggestionState.NeedsExpectedResult);
        if (s.ApprovedFlowId is not null) { states.Add(SuggestionState.Approved); states.Add(executed ? SuggestionState.Executed : SuggestionState.Executable); }
        else if (states.Count == 0) states.Add(SuggestionState.ReadyForReview);
        if (states.Count == 0) states.Add(SuggestionState.Suggested);
        return states;
    }

    /// <summary>The verification of one step from the companion's checks: the highest-priority locator that matches exactly one visible element.</summary>
    public static StepVerification Verify(SuggestedStep step, IReadOnlyList<CompanionSelectorCheck> checks, string? route, string? origin, DateTimeOffset at, Guid snapshotId)
    {
        static string K(CompanionSelector x) => $"{x.Kind}|{x.Value}|{x.Role}|{x.Name}";
        var mine = checks.Where(c => step.Locators.Any(l => K(l.Selector) == K(c.Selector))).ToList();
        var unique = step.Locators.Select(l => mine.FirstOrDefault(c => K(c.Selector) == K(l.Selector))).OfType<CompanionSelectorCheck>().FirstOrDefault(c => c.MatchCount == 1 && c.Visible);
        var state = unique is not null ? LocatorVerification.VerifiedUnique
            : mine.Any(c => c.MatchCount > 1) ? LocatorVerification.Ambiguous
            : mine.Any(c => c.MatchCount == 1) ? LocatorVerification.NotVisible
            : mine.Any(c => c.Limitation?.Contains("frame", StringComparison.OrdinalIgnoreCase) == true) ? LocatorVerification.UnsupportedFrame
            : mine.Count > 0 ? LocatorVerification.NotFound : LocatorVerification.NotAssessed;
        return new StepVerification
        {
            StepId = step.StepId, State = state, Selector = unique?.Selector, Checks = mine, ObservedRoute = route, ObservedOrigin = origin, VerifiedAt = at, SourceSnapshotId = snapshotId,
            Note = state switch
            {
                LocatorVerification.Ambiguous => "Several elements match: choose one deliberately or add a test id; never accepted automatically.",
                LocatorVerification.NotVisible => "The element exists but is not visible on this page state.",
                LocatorVerification.UnsupportedFrame => "Not found in the top frame; frames are not searched.",
                LocatorVerification.NotFound => "No element matched any candidate on the observed page.",
                _ => null,
            },
        };
    }
}
