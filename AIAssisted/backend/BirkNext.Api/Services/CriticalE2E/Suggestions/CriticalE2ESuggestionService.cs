using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.CriticalE2E.Suggestions;

/// <summary>
/// Suggested E2E tests inside Critical E2E Regression. Generation is offline (Source Analysis facts + existing flows, nothing executed); locator
/// verification goes through the paired Edge Companion on the attended page only (VerifySelectors: identity, no values, no DOM); a person
/// accepts, chooses or rejects each mapping; approval saves a Diagnostic flow in the existing flow format, which then runs only through the
/// existing Critical E2E runner after explicit authorization. Nothing here executes a test or writes to the target application.
/// </summary>
public sealed partial class CriticalE2ESuggestionService(
    IqrSourceStore sources,
    ICriticalE2EStore flows,
    ICriticalE2EService critical,
    ICriticalE2ESuggestionStore reviews,
    IBrowserCompanionService companion,
    TimeProvider time,
    ILogger<CriticalE2ESuggestionService>? logger = null,
    TestDataCatalog? catalog = null)
{
    public const string Reviewer = "Local session";

    public async Task<IReadOnlyList<SuggestionSnapshotOption>> SnapshotsAsync(CancellationToken ct)
    {
        var list = await sources.ListSourceAnalysisAsync(60, ct);
        return [.. list.Select(s => new SuggestionSnapshotOption(s.Id, ContractRegistryService.ProjectKeyOf(s), s.Archive.FileName, s.AnalyzedAt, s.UiSurface is not null,
            s.UiSurface?.Pages.Count ?? 0, s.UiSurface?.Pages.Sum(p => p.Routes.Count) ?? 0, s.UiSurface?.Pages.Sum(p => p.Controls.Count) ?? 0))];
    }

    private sealed record Loaded(IqrSourceSnapshot Snapshot, string ProjectKey, List<TestSuggestion> Suggestions, List<string> Limitations);

    private async Task<Loaded?> LoadAsync(Guid snapshotId, string environmentId, CancellationToken ct)
    {
        var snapshot = await sources.FindSourceAnalysisAsync(snapshotId, ct);
        if (snapshot is null) return null;
        var projectKey = ContractRegistryService.ProjectKeyOf(snapshot);
        var limitations = new List<string>();
        if (snapshot.UiSurface is not { } surface)
            return new Loaded(snapshot, projectKey, [], ["This snapshot was analysed before UI facts were captured: import the project again to get suggestions."]);
        limitations.AddRange(surface.Limitations);
        var existing = flows.Flows(environmentId);
        var suggestions = SuggestionEngine.Generate(projectKey, snapshot.Id, surface, existing).Select(s => Merge(s, snapshot.Id)).ToList();
        return new Loaded(snapshot, projectKey, suggestions, limitations);
    }

    /// <summary>The persisted review state onto a regenerated suggestion. A verification recorded against another snapshot is STALE: a locator
    /// verified for one application version never silently stays verified for another.</summary>
    private TestSuggestion Merge(TestSuggestion s, Guid snapshotId)
    {
        var record = reviews.Get(s.SuggestionId);
        var verifications = (record?.Verifications ?? []).Select(v => v.SourceSnapshotId == snapshotId ? v
            : v with { State = LocatorVerification.Stale, Note = "Verified for another source snapshot; verify again." }).ToList();
        var decisions = record?.SnapshotId == snapshotId ? record.Decisions : [];
        var merged = s with { Verifications = verifications, Decisions = decisions, ApprovedFlowId = record?.ApprovedFlowId, ApprovedFlowVersion = record?.ApprovedFlowVersion, ManualReview = record?.ManualReview };
        var executed = merged.ApprovedFlowId is { } fid && flows.FlowHistory(fid).Count > 0;
        return merged with { States = SuggestionEngine.States(merged, executed), TestDataState = SuggestionEngine.TestDataStateOf(merged) };
    }

    public async Task<SuggestionGenerationResult?> GenerateAsync(SuggestionGenerateRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SnapshotId, request.EnvironmentId, ct);
        if (loaded is null) return null;
        var s = loaded.Suggestions;
        var summary = new BirkNext.CriticalE2E.SuggestionSummary(s.Count,
            ExistingReused: s.Count(x => x.Coverage is SuggestionCoverage.AlreadyCovered or SuggestionCoverage.PartiallyCovered),
            NewSuggested: s.Count(x => x.Coverage == SuggestionCoverage.NewSuggestion),
            MissingDomMappings: s.Count(x => x.States.Contains(SuggestionState.NeedsDomVerification)),
            MissingExpectedResults: s.Count(x => x.States.Contains(SuggestionState.NeedsExpectedResult)),
            MissingTestData: s.Count(x => x.States.Contains(SuggestionState.NeedsTestData)),
            ReadyForApproval: s.Count(x => x.States.Contains(SuggestionState.ReadyForReview)),
            Approved: s.Count(x => x.States.Contains(SuggestionState.Approved)),
            UiSmoke: s.Count(x => x.Category == SuggestionCategory.UiSmoke),
            UiFunctional: s.Count(x => x.Category == SuggestionCategory.UiFunctional),
            ReadyForManualTest: s.Count(x => x.States.Contains(SuggestionState.ReadyForManualTest)));
        return new SuggestionGenerationResult
        {
            ProjectKey = loaded.ProjectKey, SnapshotId = loaded.Snapshot.Id, ArchiveFileName = loaded.Snapshot.Archive.FileName, Summary = summary, Suggestions = s,
            PagesAnalyzed = loaded.Snapshot.UiSurface?.Pages.Count ?? 0, Limitations = loaded.Limitations, Impact = Impact(loaded, request.EnvironmentId),
        };
    }

    /// <summary>Approved suggested flows of this project checked against this snapshot's UI facts. Proposals only — no flow is ever rewritten.</summary>
    private List<SuggestionImpact> Impact(Loaded loaded, string environmentId)
    {
        var impacts = new List<SuggestionImpact>();
        if (loaded.Snapshot.UiSurface is not { } surface) return impacts;
        var controls = surface.Pages.SelectMany(p => p.Controls).ToList();
        var routes = surface.Pages.SelectMany(p => p.Routes).Select(r => CriticalE2ERoutes.Normalize(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in flows.Flows(environmentId).Where(f => f.Source == CriticalE2EFlowSource.SuggestedFromSource && f.Tags.Contains($"project:{loaded.ProjectKey}")
                     && !f.Tags.Contains($"snapshot:{loaded.Snapshot.Id}")))
        {
            foreach (var step in flow.Steps)
            {
                if (step.BrowserAction == CompanionActionKind.Navigate && step.Value is { } r && !routes.Contains(CriticalE2ERoutes.Normalize(r)))
                    impacts.Add(new(SuggestionImpactKind.TestMayBeObsolete, flow.Id, flow.Name, step.StepId, $"The route {r} no longer exists in the source."));
                if (step.BrowserAction == CompanionActionKind.AssertRoute && step.Expected is { } target && !routes.Contains(CriticalE2ERoutes.Normalize(target)))
                    impacts.Add(new(SuggestionImpactKind.AssertionReviewNeeded, flow.Id, flow.Name, step.StepId, $"The expected route {target} no longer exists in the source."));
                if (step.Selector is not { } sel) continue;
                var still = sel.Kind switch
                {
                    CompanionSelectorKind.TestId => controls.Any(c => c.TestId == sel.Value),
                    CompanionSelectorKind.Label => controls.Any(c => string.Equals(c.Label, sel.Value, StringComparison.OrdinalIgnoreCase)),
                    CompanionSelectorKind.Role => controls.Any(c => c.Role == sel.Role && string.Equals(c.Text ?? c.Label, sel.Name, StringComparison.OrdinalIgnoreCase)),
                    CompanionSelectorKind.Css when sel.Value.StartsWith('#') => controls.Any(c => c.ElementId == sel.Value[1..]),
                    _ => true,
                };
                if (!still) impacts.Add(new(SuggestionImpactKind.LocatorRevalidationNeeded, flow.Id, flow.Name, step.StepId, $"{sel.Describe()} is no longer declared in the source: verify the locator again before the next run."));
                // A valid value that the changed constraint now rejects (deliberately invalid steps say so in their description).
                if (step.BrowserAction == CompanionActionKind.Fill && step.Value is { Length: > 0 } value && !step.Description.Contains("more than the maximum", StringComparison.Ordinal)
                    && controls.FirstOrDefault(c => (c.TestId is not null && c.TestId == sel.Value) || (c.Label is not null && c.Label == sel.Value)) is { MaxLength: { } max } && value.Length > max)
                    impacts.Add(new(SuggestionImpactKind.TestDataReviewNeeded, flow.Id, flow.Name, step.StepId, $"The field now allows at most {max} characters; the flow's value has {value.Length}."));
            }
        }
        // New behaviour in a changed snapshot of a project that already has approved suggested flows.
        if (flows.Flows(environmentId).Any(f => f.Source == CriticalE2EFlowSource.SuggestedFromSource && f.Tags.Contains($"project:{loaded.ProjectKey}") && !f.Tags.Contains($"snapshot:{loaded.Snapshot.Id}")))
            foreach (var s in loaded.Suggestions.Where(s => s.Coverage == SuggestionCoverage.NewSuggestion).Take(5))
                impacts.Add(new(SuggestionImpactKind.NewTestSuggested, "", "", "", $"Not covered yet in this snapshot: {s.Title}"));
        return impacts;
    }

    /// <summary>Selectors of the steps that act on the starting page (validation output appears only after submitting and is verified when the test runs).</summary>
    internal static List<(SuggestedStep Step, List<CompanionSelector> Selectors)> Verifiable(TestSuggestion s)
    {
        var list = new List<(SuggestedStep, List<CompanionSelector>)>();
        foreach (var step in s.Steps.Where(st => st.ControlId is not null))
        {
            if (step.Locators.All(l => l.Basis.Contains("Validation", StringComparison.Ordinal))) continue;
            list.Add((step, [.. step.Locators.OrderBy(l => l.Priority).Take(3).Select(l => l.Selector)]));
        }
        return list;
    }

    public async Task<SuggestionVerifyResult> VerifyAsync(SuggestionVerifyRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SnapshotId, request.EnvironmentId, ct);
        var suggestion = loaded?.Suggestions.FirstOrDefault(s => s.SuggestionId == request.SuggestionId);
        if (suggestion is null) return new(null, "Suggestion not found for this snapshot.", false);
        var live = companion.Status(request.ProfileId).Live;
        if (!live.ExtensionConnected) return new(suggestion, "The Edge Companion is not connected: pair it and open the page in the attended browser.", false);
        if (!live.SupportsSelectorVerify) return new(suggestion, "The paired Edge Companion cannot verify selectors. Reload the extension to update it.", false);
        if (live.CurrentPage is null) return new(suggestion, "Keep exactly one approved page of the target open in the attended browser.", false);
        if (!CriticalE2ERoutes.Matches(suggestion.Route, live.CurrentRoute))
            return new(suggestion, $"The attended page is on {live.CurrentRoute}; open {suggestion.Route} to verify this suggestion's locators.", false);
        var targets = Verifiable(suggestion);
        var selectors = targets.SelectMany(t => t.Selectors).DistinctBy(x => (x.Kind, x.Value, x.Role, x.Name)).Take(CompanionCapabilities.MaxSelectorsPerVerify).ToList();
        if (selectors.Count == 0) return new(suggestion, "This suggestion has no control to verify on its starting page.", false);

        companion.OpenAutomationWindow(request.ProfileId);
        var commandId = $"verify:{Guid.NewGuid():N}";
        var dispatch = companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = commandId, StepId = "verify", ProfileId = request.ProfileId, EnvironmentId = request.EnvironmentId, TargetOrigin = live.CurrentOrigin!, PageId = live.CurrentPageId,
            Action = CompanionActionKind.VerifySelectors, Selectors = selectors, TimeoutMs = 8_000,
        });
        if (!dispatch.Accepted) return new(suggestion, dispatch.Message, false);
        CompanionAutomationResult result;
        try { result = await companion.AwaitResultAsync(commandId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { companion.CancelCommand(commandId, "Verification was cancelled."); return new(suggestion, "Verification was cancelled.", true); }
        if (result.Status != CriticalE2EStatus.Passed || result.SelectorChecks is not { } checks)
            return new(suggestion, result.SanitizedError ?? "The Edge Companion returned no selector checks.", true);

        var now = time.GetUtcNow();
        var verified = new List<StepVerification>();
        foreach (var step in suggestion.Steps.Where(st => st.ControlId is not null))
            verified.Add(targets.Any(t => t.Step.StepId == step.StepId)
                ? SuggestionEngine.Verify(step, checks, result.ObservedRoute ?? live.CurrentRoute, live.CurrentOrigin, now, request.SnapshotId)
                : new StepVerification { StepId = step.StepId, State = LocatorVerification.NotAssessed, SourceSnapshotId = request.SnapshotId, VerifiedAt = now,
                    Note = "Shown only after the form is submitted: verified when the approved test runs, never assumed." });
        reviews.Update(suggestion.SuggestionId, request.SnapshotId, r => r with { SnapshotId = request.SnapshotId, Verifications = verified });
        logger?.LogInformation("Suggested test {SuggestionId}: {Count} locator(s) verified on the attended page.", suggestion.SuggestionId, verified.Count(v => v.State == LocatorVerification.VerifiedUnique));
        var updated = (await LoadAsync(request.SnapshotId, request.EnvironmentId, ct))!.Suggestions.First(s => s.SuggestionId == request.SuggestionId);
        return new(updated, $"{verified.Count(v => v.State == LocatorVerification.VerifiedUnique)} of {verified.Count} step locator(s) verified unique on {live.CurrentOrigin}{live.CurrentRoute}. Observed browser state; the running build is not proven to be this source snapshot.", true);
    }

    public async Task<(TestSuggestion? Suggestion, string? Problem)> DecideAsync(SuggestionDecisionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SnapshotId, request.EnvironmentId, ct);
        var suggestion = loaded?.Suggestions.FirstOrDefault(s => s.SuggestionId == request.SuggestionId);
        if (suggestion is null) return (null, "Suggestion not found for this snapshot.");
        var step = suggestion.Steps.FirstOrDefault(s => s.StepId == request.StepId && s.ControlId is not null);
        if (step is null) return (null, "That step addresses no control.");
        var verification = suggestion.Verifications.FirstOrDefault(v => v.StepId == step.StepId);
        static bool Same(CompanionSelector a, CompanionSelector b) => a.Kind == b.Kind && a.Value == b.Value && a.Role == b.Role && a.Name == b.Name;
        CompanionSelector? chosen = null;
        switch (request.Decision)
        {
            case LocatorDecision.Accepted:
                // Only a locator the attended page showed to be unique and visible, on this snapshot's verification. Ambiguity is never accepted.
                if (verification is not { State: LocatorVerification.VerifiedUnique, Selector: { } unique }) return (null, "Accept needs a locator verified unique on the attended page first (Verify DOM in Edge).");
                chosen = unique;
                break;
            case LocatorDecision.AlternativeChosen:
                if (request.Selector is not { } alt) return (null, "Choose the alternative locator.");
                var check = verification?.Checks.FirstOrDefault(c => Same(c.Selector, alt));
                if (check is not { MatchCount: 1 } && string.IsNullOrWhiteSpace(request.Reason))
                    return (null, "An alternative that the attended page did not show as unique needs a written reason (manual locator).");
                if (check is { MatchCount: > 1 }) return (null, "That locator matches several elements; it cannot be chosen.");
                chosen = alt;
                break;
            case LocatorDecision.Rejected or LocatorDecision.Unresolved:
                break;
            default:
                return (null, "Unknown decision.");
        }
        var decision = new StepDecision(step.StepId, request.Decision, chosen, Reviewer, time.GetUtcNow(), string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()[..Math.Min(300, request.Reason.Trim().Length)]);
        reviews.Update(suggestion.SuggestionId, request.SnapshotId, r => r with { SnapshotId = request.SnapshotId, Decisions = [.. (r.SnapshotId == request.SnapshotId ? r.Decisions : []), decision] });
        var updated = (await LoadAsync(request.SnapshotId, request.EnvironmentId, ct))!.Suggestions.First(s => s.SuggestionId == request.SuggestionId);
        return (updated, null);
    }

    public const int MaxManualText = 600;

    /// <summary>
    /// A manual tester reviews a suggestion without the Edge Companion and without source: states the expected result in their own words,
    /// which Test Data to use and notes, and decides Draft / Ready for manual test / Rejected. Ready for manual test needs an expected result
    /// (the tester's, or one the source states) and, when the suggestion needs Test Data, a Test Data reference. Text that looks like a
    /// national identity number is refused: Test Data is named by an approved alias or description, never pasted as personal data. This is
    /// not an automation approval and never runs anything.
    /// </summary>
    public async Task<(TestSuggestion? Suggestion, string? Problem)> ManualReviewAsync(SuggestionManualReviewRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SnapshotId, request.EnvironmentId, ct);
        var suggestion = loaded?.Suggestions.FirstOrDefault(s => s.SuggestionId == request.SuggestionId);
        if (suggestion is null) return (null, "Suggestion not found for this snapshot.");
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(MaxManualText, text.Trim().Length)];
        var expected = Clean(request.ExpectedResult);
        var data = Clean(request.TestData);
        var notes = Clean(request.Notes);
        if (new[] { expected, data, notes }.Any(t => t is not null && NationalIdLike().IsMatch(t)))
            return (null, "That text looks like a national identity number. Name the Test Data by its approved alias or describe it (\"the synthetic child in set A\") instead.");
        if (request.Decision == ManualReviewDecision.ReadyForManualTest)
        {
            if (expected is null && suggestion.ExpectedResult is null) return (null, "State the expected result first: what should the page show after the last step?");
            if (suggestion.TestDataNeeds.Count > 0 && data is null) return (null, $"Name the Test Data first: {string.Join("; ", suggestion.TestDataNeeds)}.");
        }
        var review = new SuggestionManualReview { ExpectedResult = expected, TestData = data, Notes = notes, Decision = request.Decision, ReviewedBy = Reviewer, ReviewedAt = time.GetUtcNow() };
        reviews.Update(suggestion.SuggestionId, request.SnapshotId, r => r with { ManualReview = review });
        logger?.LogInformation("Suggested test {SuggestionId}: manual review {Decision}.", suggestion.SuggestionId, request.Decision);
        var updated = (await LoadAsync(request.SnapshotId, request.EnvironmentId, ct))!.Suggestions.First(s => s.SuggestionId == request.SuggestionId);
        return (updated, null);
    }

    [GeneratedRegex(@"(?<!\d)\d{6}\s?\d{5}(?!\d)")] private static partial Regex NationalIdLike();

    public async Task<SuggestionApproveResult> ApproveAsync(SuggestionApproveRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SnapshotId, request.EnvironmentId, ct);
        var s = loaded?.Suggestions.FirstOrDefault(x => x.SuggestionId == request.SuggestionId);
        if (s is null) return new(null, ["Suggestion not found for this snapshot."]);
        var problems = new List<string>();
        if (s.ApprovedFlowId is not null) problems.Add("Already approved: open the flow in Critical E2E Regression.");
        if (s.Coverage == SuggestionCoverage.AlreadyCovered) problems.Add($"Already covered by an existing flow ({s.CoverageNote}); extend that flow instead of adding a copy.");
        var mapping = new Dictionary<string, (CompanionSelector Selector, bool Manual)>();
        foreach (var step in s.Steps.Where(st => st.ControlId is not null))
        {
            var d = s.Decisions.LastOrDefault(x => x.StepId == step.StepId);
            var validationOnly = step.Locators.All(l => l.Basis.Contains("Validation", StringComparison.Ordinal));
            if (d is { Decision: LocatorDecision.Accepted or LocatorDecision.AlternativeChosen, Selector: { } sel }) mapping[step.StepId] = (sel, d.Reason is not null);
            else if (validationOnly && step.Locators.FirstOrDefault() is { } conv) mapping[step.StepId] = (conv.Selector, true);   // verified when the test runs
            else problems.Add($"Step {step.StepId} ({step.Description}): no accepted locator.");
        }
        if (s.AutomationBlocker is { } blocker) problems.Add(blocker);
        // Each ${data:…} placeholder is mapped to an approved Test Data alias chosen by the reviewer; the value stays in Test Data and is revealed
        // to the backend only while a run executes. A need that no placeholder carries cannot be mapped here.
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in s.DataVariables)
        {
            var alias = request.TestData?.FirstOrDefault(b => string.Equals(b.Variable, variable.Variable, StringComparison.Ordinal))?.Alias?.Trim();
            if (string.IsNullOrEmpty(alias)) problems.Add($"Choose approved Test Data for ${{data:{variable.Variable}}} ({variable.Description}).");
            else bindings[variable.Variable] = alias;
        }
        if (s.TestDataNeeds.Count > 0 && s.DataVariables.Count == 0) problems.Add($"Test Data needed first: {string.Join("; ", s.TestDataNeeds)}.");
        foreach (var unaddressed in s.Steps.Where(st => st.ControlId is null && st.Action is CompanionActionKind.Click or CompanionActionKind.Fill or CompanionActionKind.Select))
            problems.Add($"Step {unaddressed.StepId} ({unaddressed.Description}) has no element from source: pick it in the flow editor.");
        var steps = s.Steps.ToList();
        if (!steps.Any(st => st.IsFinalAssertion))
        {
            if (request.OutcomeKind is CriticalE2EOutcomeKind.UrlEquals or CriticalE2EOutcomeKind.UrlContains && request.Expected is { Length: > 0 } expected && expected.StartsWith('/'))
                steps.Add(new SuggestedStep { StepId = $"s{steps.Count + 1}", Description = $"The browser is on {expected} (expected result stated by the reviewer)", Action = CompanionActionKind.AssertRoute,
                    Expected = expected, Match = request.OutcomeKind == CriticalE2EOutcomeKind.UrlContains ? CompanionTextMatch.Contains : CompanionTextMatch.Equals, IsFinalAssertion = true });
            else problems.Add("No expected result: the source does not state one. State the expected route (or add the outcome in the flow editor after approval is possible).");
        }
        if (problems.Count > 0) return new(null, problems);

        var final = steps.Last(st => st.IsFinalAssertion);
        CriticalE2EStepDefinition Map(SuggestedStep st) => new()
        {
            StepId = st.StepId, Description = st.Description, BrowserAction = st.Action, Value = st.Value, Expected = st.Expected, Match = st.Match, IsFinalAssertion = st.IsFinalAssertion,
            Selector = mapping.TryGetValue(st.StepId, out var m) ? m.Selector : null, SelectorReviewed = mapping.TryGetValue(st.StepId, out var mm) && mm.Manual,
            ExecutionRoute = st.Action == CompanionActionKind.Navigate || st.IsFinalAssertion ? null : s.Route,
        };
        var flow = new CriticalE2EFlowDefinition
        {
            Module = s.Module.Length > 0 ? s.Module : s.ProjectKey, Name = s.Title, Description = $"Suggested from source ({s.RuleVersion}): {s.Kind}. Expected: {s.ExpectedResult ?? request.Expected} — basis: {s.ExpectedBasis ?? "stated by the reviewer"}.",
            Kind = CriticalE2EFlowKind.Diagnostic, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = request.ProfileId, EnvironmentId = request.EnvironmentId,
            Steps = [.. steps.Select(Map)], Source = CriticalE2EFlowSource.SuggestedFromSource, RequirementRef = s.RequirementRefs.FirstOrDefault(),
            Tags = [$"suggestion:{s.SuggestionId}", $"snapshot:{s.SourceSnapshotId}", $"project:{s.ProjectKey}", s.RuleVersion],
            DataMappings = bindings.Count == 0 ? null : [.. bindings.Select(b => new CriticalE2EDataMapping(b.Key, [b.Value], BirkNext.TestData.TestDataDataType.SimpleValues,
                steps.FirstOrDefault(st => CriticalE2EVariables.DataPlaceholders(st.Value).Contains(b.Key))?.Description ?? b.Key)
                { AssetIds = catalog is null ? null : [TestDataReferenceResolver.Resolve(catalog, b.Value, null).AssetId] })],
            AutomationBoundary = "Browser UI only: what the application does after the submission is not verified by this flow.",
            FinalOutcome = final.Action switch
            {
                CompanionActionKind.AssertRoute => new CriticalE2EFinalOutcome { Kind = final.Match == CompanionTextMatch.Contains ? CriticalE2EOutcomeKind.UrlContains : CriticalE2EOutcomeKind.UrlEquals, Expected = final.Expected, Description = final.Description },
                CompanionActionKind.AssertText => new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.TextContains, Selector = mapping[final.StepId].Selector, Expected = final.Expected, Description = final.Description },
                _ => new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.ElementVisible, Selector = mapping[final.StepId].Selector, Description = final.Description },
            },
        };
        if (flow.ConfigurationProblem() is { } problem) return new(null, [problem]);
        // The same Test Data rules a run applies, checked before anything is saved: unknown, unapproved, wrong type, other environment, no value.
        if (bindings.Count > 0)
        {
            var issues = catalog is null ? ["Test Data review required: the Test Data catalog is unavailable."]
                : CriticalE2ETestData.Issues(flow, catalog, time.GetUtcNow(), request.EnvironmentName, request.EnvironmentType);
            if (issues.Count > 0) return new(null, issues);
        }
        var saved = critical.SaveFlow(flow);
        reviews.Update(s.SuggestionId, request.SnapshotId, r => r with
        {
            SnapshotId = request.SnapshotId, ApprovedFlowId = saved.Id, ApprovedFlowVersion = saved.Version, ApprovedAt = time.GetUtcNow(), ApprovedBy = Reviewer, ReviewerExpected = request.Expected,
        });
        return new(saved, []);
    }
}
