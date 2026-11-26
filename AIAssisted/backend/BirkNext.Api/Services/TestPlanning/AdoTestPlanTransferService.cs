using System.Collections.Concurrent;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Manual Testing → Overfør til Azure DevOps → «Send direkte»: the SAME saved cases, checks and step text as the file export
/// (<see cref="AdoTestPlanExporter"/>), written as Azure DevOps test cases into the chosen test plan + suite through the stored connection.
/// Duplicate protection: one link per case and destination project (<see cref="AdoTestCaseLinkRecord"/>); a linked case is updated or skipped,
/// never created again unless the user explicitly chooses «Opprett nytt»; an update is refused when the test case changed in Azure DevOps since the
/// last transfer; a create is recorded as «uncertain» BEFORE it is sent, so a lost answer is never followed by a blind second create. Nothing is
/// sent without <see cref="AdoTransferRequest.Confirmed"/>, and BirkNext plans and cases are never changed.
/// </summary>
public sealed class AdoTestPlanTransferService(AppDbContext db, AdoTestPlanExportService export, AzureDevOpsTestPlansClient ado, AzureDevOpsConnectionStore connections,
    AdoTestPlanSettingsService settings, TimeProvider clock, ILogger<AdoTestPlanTransferService> logger)
{
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset Now => clock.GetUtcNow();

    private static AdoExportRequest ExportRequest(AdoTransferRequest r) => new()
    {
        ProjectKey = r.ProjectKey, DraftId = r.DraftId, PlanRevision = r.PlanRevision, CaseIds = r.CaseIds, IncludeNotApproved = r.IncludeNotApproved, Language = r.Language,
        Mode = AdoExportMode.CreateNew,
        // A direct transfer sets fields on the work item: the Area Path is optional (Azure DevOps uses the project's root area when it is empty) and
        // the mapped priority always travels (it is a field of the work item, not an optional file column).
        Profile = r.Profile with { Target = AdoTarget.Server, IncludePriority = true, Format = AdoExportFormat.Xlsx },
    };

    private static AdoCaseLink View(AdoTestCaseLinkRecord? r, Func<int, string> url) => r is null ? new() : new()
    {
        State = r.State == nameof(AdoLinkState.Uncertain) ? AdoLinkState.Uncertain : AdoLinkState.Linked,
        TestCaseId = r.TestCaseId, WebUrl = r.TestCaseId is { } id ? url(id) : null, CaseRevision = r.CaseRevision, RemoteRevision = r.RemoteRevision,
        PlanId = r.AdoPlanId, SuiteId = r.AdoSuiteId, At = r.UpdatedAt, By = r.UpdatedBy,
    };

    private Task<List<AdoTestCaseLinkRecord>> LinksAsync(string projectKey, AdoTestPlanDestination d, IEnumerable<string> caseKeys, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var keys = caseKeys.ToList();
        return db.AdoTestCaseLinks.Where(l => l.ProjectKey == key && l.Organization == d.Organization && l.AdoProject == d.Project && keys.Contains(l.CaseKey)).ToListAsync(ct);
    }

    public async Task<AdoTransferPreview> PreviewAsync(AdoTransferRequest request, CancellationToken ct) => (await BuildAsync(request, ct)).Preview;

    private sealed record Built(AdoTransferPreview Preview, TestPlanDraft Plan, AdoExportPreview Export, List<AdoTestCaseLinkRecord> Links, HashSet<int> InSuite);

    /// <summary>
    /// The remote suite tree a transfer with suite placement needs (2026-11-26): every BirkNext suite holding a ready case (or a descendant that
    /// does), parent before child, matched to an existing Static Test Suite with the same name under the matched parent (the destination suite at
    /// the top). Parent-aware: two suites with one name under different parents are different suites. Pure.
    /// </summary>
    internal static List<AdoSuitePlanItem> SuitePlan(TestPlanDraft plan, IReadOnlySet<string> readyCaseIds, IReadOnlyList<AdoTestSuiteInfo> remote, int destinationSuiteId)
    {
        var suites = plan.Suites;
        var counts = plan.Cases.Where(c => readyCaseIds.Contains(c.Source.ScenarioId)).GroupBy(c => c.SuiteId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        bool Needed(string id) => counts.ContainsKey(id) || suites.Any(c => c.ParentId == id && Needed(c.Id));
        var matched = new Dictionary<string, int?>(StringComparer.Ordinal);
        var items = new List<AdoSuitePlanItem>();
        foreach (var (suite, depth) in PlanStructure.Flatten(suites).Where(x => Needed(x.Suite.Id)))
        {
            int? parentRemote = suite.ParentId is null || suites.All(x => x.Id != suite.ParentId) ? destinationSuiteId : matched.GetValueOrDefault(suite.ParentId);
            var existing = parentRemote is null ? null : remote.FirstOrDefault(r => r.ParentId == parentRemote && string.Equals(r.Name.Trim(), suite.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            matched[suite.Id] = existing?.Id;
            items.Add(new AdoSuitePlanItem(suite.Id, PlanStructure.PathOf(suites, suite.Id), depth, counts.GetValueOrDefault(suite.Id), existing is not null, existing?.Id));
        }
        return items;
    }

    private async Task<Built> BuildAsync(AdoTransferRequest request, CancellationToken ct)
    {
        var plan = await export.PlanAsync(ExportRequest(request), ct);
        var checks = AdoTestPlanExporter.Preview(plan, ExportRequest(request));
        var revisions = AdoTestPlanExportService.Revisions(plan);
        var (saved, savedSource) = await settings.EffectiveAsync(request.ProjectKey, ct);
        var destination = request.Destination ?? saved;
        var source = request.Destination is null ? savedSource : saved is not null && request.Destination == saved ? savedSource : AdoDestinationSource.Explicit;

        string? problem = destination is null ? "Velg hvor testene skal i Azure DevOps (organisasjon, prosjekt, testplan og testgruppe)."
            : !destination.Complete ? "Målet er ikke fullstendig: velg testplan og testgruppe."
            : connections.List().FirstOrDefault(c => c.Id == destination.ConnectionId) is not { } connection ? "Tilkoblingen til målet finnes ikke lenger. Velg et nytt mål."
            : !string.Equals(connection.Organization, destination.Organization, StringComparison.OrdinalIgnoreCase) ? "Organisasjonen stemmer ikke med tilkoblingen. Velg målet på nytt."
            : null;

        // Read-only look at the target suite: which test cases are already there (titles flag possible duplicates of cases that are not linked).
        var inSuite = new HashSet<int>();
        var suiteTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (problem is null)
        {
            try
            {
                foreach (var (id, title) in await ado.SuiteTestCasesAsync(destination!.ConnectionId, destination.Project, destination.PlanId, destination.SuiteId, ct))
                { inSuite.Add(id); suiteTitles.Add(title.Trim()); }
            }
            catch (AzureDevOpsTestPlansException e) { problem = e.Code == "not-found" ? "Fant ikke testplanen eller testgruppen i Azure DevOps. Velg målet på nytt." : e.Message; }
        }

        var links = destination is { Complete: true } ? await LinksAsync(request.ProjectKey, destination, checks.Cases.Select(c => c.CaseKey), ct) : [];
        var cases = new List<AdoTransferCase>();
        foreach (var c in checks.Cases)
        {
            var link = links.FirstOrDefault(l => l.CaseKey == c.CaseKey);
            var current = revisions.GetValueOrDefault(c.CaseId) ?? "";
            var blocking = c.Issues.FirstOrDefault(i => i.Level == AdoIssueLevel.Blocking)?.Message;
            var ready = c.Included && blocking is null;
            var view = destination is { } dd ? View(link, id => ado.WorkItemUrl(dd.Organization, dd.Project, id)) : new AdoCaseLink();
            var changed = link is { State: "Linked" } && link.CaseRevision != current;
            List<AdoTransferAction> allowed;
            AdoTransferAction suggested;
            string? note = null;
            if (!ready) { allowed = []; suggested = AdoTransferAction.Skip; }
            else if (link is { State: "Uncertain" })
            {
                allowed = []; suggested = AdoTransferAction.Skip;
                note = $"Vi vet ikke om forrige overføring ({link.AttemptedAt?.ToLocalTime():dd.MM.yyyy HH:mm}) ble fullført. Kontroller den før du overfører på nytt.";
            }
            else if (link is not null)
            {
                allowed = [AdoTransferAction.Update, AdoTransferAction.Create, AdoTransferAction.Skip];
                suggested = changed ? AdoTransferAction.Update : AdoTransferAction.Skip;
                note = changed ? $"Endret i BirkNext siden overføringen {link.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}." : $"Uendret siden overføringen {link.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}.";
                if (link.TestCaseId is { } tid && link.AdoSuiteId == destination?.SuiteId && link.AdoPlanId == destination?.PlanId && !inSuite.Contains(tid) && problem is null)
                    note += " Den ligger ikke lenger i testgruppen og legges til igjen ved oppdatering.";
            }
            else
            {
                allowed = [AdoTransferAction.Create, AdoTransferAction.Skip];
                var possibleDuplicate = suiteTitles.Contains(c.Title.Trim());
                suggested = possibleDuplicate ? AdoTransferAction.Skip : AdoTransferAction.Create;
                if (possibleDuplicate) note = "Mulig duplikat: testgruppen har allerede et testtilfelle med samme tittel som ikke er koblet til BirkNext. Velg «Opprett nytt» bare hvis det er et annet testtilfelle.";
            }
            cases.Add(new AdoTransferCase
            {
                CaseId = c.CaseId, CaseKey = c.CaseKey, Title = c.Title, ScenarioTitle = c.ScenarioTitle, Steps = c.Steps, Priority = c.Priority, Language = c.Language,
                Ready = ready, NotReadyReason = ready ? null : c.ExcludedReason ?? blocking, Link = view, ChangedSinceTransfer = changed, CurrentRevision = current,
                Allowed = allowed, Suggested = suggested, Note = note,
            });
        }
        AdoTransferAction Effective(AdoTransferCase c) => request.Actions.TryGetValue(c.CaseId, out var chosen) && c.Allowed.Contains(chosen) ? chosen : c.Suggested;
        var toCreate = cases.Count(c => c.Allowed.Count > 0 && Effective(c) == AdoTransferAction.Create);
        var toUpdate = cases.Count(c => c.Allowed.Count > 0 && Effective(c) == AdoTransferAction.Update);
        // Suite placement (2026-11-26): the remote tree under the destination suite, read only (nothing is created by a preview).
        var suitePlan = new List<AdoSuitePlanItem>();
        if (request.PreserveSuites && problem is null && destination is { Complete: true })
        {
            try { suitePlan = SuitePlan(plan, cases.Where(c => c.Ready).Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal), await ado.SuitesAsync(destination.ConnectionId, destination.Project, destination.PlanId, ct), destination.SuiteId); }
            catch (AzureDevOpsTestPlansException e) { problem = $"Testgruppene i målet kunne ikke leses: {e.Message}"; }
        }
        var preview = new AdoTransferPreview
        {
            Destination = destination, DestinationSource = destination is null ? AdoDestinationSource.None : source, DestinationProblem = problem,
            PlanName = plan.PlanName, PlanRevision = plan.Revision, Cases = cases, ToCreate = toCreate, ToUpdate = toUpdate,
            ToSkip = cases.Count(c => c.Ready && c.Link.State != AdoLinkState.Uncertain && Effective(c) == AdoTransferAction.Skip),
            NeedReview = cases.Count(c => c.Ready && c.Link.State == AdoLinkState.Uncertain),
            // The file-only notes (mapping report, CSV cells, import columns) do not apply to a direct transfer.
            Issues = [.. checks.Issues.Where(i => i.CaseId is not null || i.Level == AdoIssueLevel.Warning).Where(i => !i.Message.Contains("CSV", StringComparison.Ordinal))],
            CanTransfer = problem is null && toCreate + toUpdate > 0, SuitePlan = suitePlan,
        };
        return new(preview, plan, checks, links, inSuite);
    }

    /// <summary>«Overfør til Azure DevOps» after the user confirmed the reviewed summary. Returns what happened to every case.</summary>
    public async Task<AdoTransferResult> TransferAsync(AdoTransferRequest request, string actor, CancellationToken ct)
    {
        if (!request.Confirmed) throw new TestPlanException(400, "confirmation-required", "Se over og bekreft overføringen først.");
        if (!request.LeadApprovalDeclared)
            throw new TestPlanException(400, "lead-approval-required", "Godkjenning fra testleder kreves: bekreft at testleder har godkjent denne overføringen.");
        var lockKey = $"{TestScenarioDiscoveryService.Key(request.ProjectKey)}|{request.Destination?.Organization}|{request.Destination?.Project}";
        if (!Running.TryAdd(lockKey, 0)) throw new TestPlanException(409, "transfer-running", "En overføring til dette prosjektet pågår allerede. Vent til den er ferdig.");
        var started = Now;
        try { return await RunAsync(request, actor, started, ct); }
        catch (TestPlanException e) when (e.Status is 409)
        {
            // Refused before anything was sent (destination problem, nothing selected, plan saved again): kept in the history as well.
            await RecordRefusalAsync(request, actor, started, e.Message, ct);
            throw;
        }
        finally { Running.TryRemove(lockKey, out _); }
    }

    private async Task RecordRefusalAsync(AdoTransferRequest request, string actor, DateTimeOffset started, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var name = await db.TestPlanDrafts.AsNoTracking().Where(r => r.Id == request.DraftId).Select(r => r.PlanName).SingleOrDefaultAsync(ct) ?? "";
        var d = request.Destination;
        db.AdoExports.Add(new AdoExportRecord
        {
            Id = Guid.NewGuid(), ProjectKey = key, DraftId = request.DraftId, PlanName = Cut(name, 300), PlanRevision = request.PlanRevision, Format = "Direct", Mode = "Direct",
            Destination = d is null ? null : Cut($"{d.Organization} / {d.Project} / {d.PlanName} / {d.SuiteName}", 300),
            ContentJson = JsonSerializer.Serialize(new
            {
                cases = Array.Empty<string>(), status = "Failed", startedAt = started, completedAt = Now, reason,
                declaration = new { declared = request.LeadApprovalDeclared, text = AdoTransferDeclaration.Text, by = actor, verifiedApproval = false },
            }, E.Json),
            ExportedBy = actor.Length <= 200 ? actor : actor[..200], ExportedAt = Now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Transferred" only when every requested create/update succeeded; partial, failed and must-check are never reported as success.</summary>
    public static string Status(IReadOnlyCollection<AdoTransferCaseResult> cases)
    {
        var requested = cases.Where(c => c.Outcome != AdoTransferOutcome.Skipped).ToList();
        var ok = requested.Count(c => c.Outcome is AdoTransferOutcome.Created or AdoTransferOutcome.Updated);
        if (requested.Count > 0 && ok == requested.Count) return "Transferred";
        if (ok > 0) return "Partial";
        return requested.Any(c => c.Outcome is AdoTransferOutcome.NeedsReview or AdoTransferOutcome.Uncertain or AdoTransferOutcome.NotInSuite) ? "NeedsReview" : "Failed";
    }

    private async Task<AdoTransferResult> RunAsync(AdoTransferRequest request, string actor, DateTimeOffset started, CancellationToken ct)
    {
        var built = await BuildAsync(request, ct);
        var preview = built.Preview;
        if (preview.DestinationProblem is { } problem) throw new TestPlanException(409, "destination-problem", problem);
        if (!preview.CanTransfer) throw new TestPlanException(409, "nothing-to-transfer", "Ingen testtilfeller er valgt for oppretting eller oppdatering.");
        var d = preview.Destination!;
        var who = actor.Length <= 200 ? actor : actor[..200];
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var results = new List<AdoTransferCaseResult>();
        var toSuite = new List<(int Id, AdoTransferCase Case, AdoTransferOutcome Outcome)>();

        foreach (var c in preview.Cases)
        {
            if (!c.Ready) continue;
            if (c.Link.State == AdoLinkState.Uncertain)
            {
                results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview, c.Note ?? "Kontroller forrige overføring først."));
                continue;
            }
            var action = request.Actions.TryGetValue(c.CaseId, out var chosen) && c.Allowed.Contains(chosen) ? chosen : c.Suggested;
            if (action == AdoTransferAction.Skip) { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Skipped, "Hoppet over.") { TestCaseId = c.Link.TestCaseId, WebUrl = c.Link.WebUrl }); continue; }
            var rows = built.Export.Rows.Where(r => r.CaseId == c.CaseId).OrderBy(r => r.TestStep).ToList();
            var first = rows.FirstOrDefault();
            var fields = new AdoTestCaseFields(c.Title, [.. rows.Select(r => (r.StepAction, r.StepExpected))], first?.AreaPath is { Length: > 0 } area ? area : null,
                first?.AssignedTo is { Length: > 0 } who2 ? who2 : null, first?.Priority is { Length: > 0 } pr ? pr : null, first?.Description);
            var link = built.Links.FirstOrDefault(l => l.CaseKey == c.CaseKey);

            if (action == AdoTransferAction.Update && link?.TestCaseId is { } existingId)
            {
                AdoWorkItem? remote;
                try { remote = await ado.WorkItemAsync(d.ConnectionId, existingId, ct); }
                catch (AzureDevOpsTestPlansException e) { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Failed, e.Message) { TestCaseId = existingId, WebUrl = c.Link.WebUrl }); continue; }
                if (remote is null)
                { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview, $"Testtilfelle {existingId} finnes ikke lenger i Azure DevOps. Velg «Opprett nytt» hvis det skal lages på nytt.") { TestCaseId = existingId }); continue; }
                if (!string.Equals(remote.WorkItemType, "Test Case", StringComparison.OrdinalIgnoreCase) || !string.Equals(remote.Project, d.Project, StringComparison.OrdinalIgnoreCase))
                { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview, $"Arbeidselement {existingId} er ikke et testtilfelle i {d.Project}. Det ble ikke endret.") { TestCaseId = existingId, WebUrl = remote.WebUrl }); continue; }
                if (link.RemoteRevision is { } knownRev && remote.Rev != knownRev)
                {
                    results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview,
                        $"Testtilfelle {existingId} er endret i Azure DevOps etter siste overføring (revisjon {remote.Rev}, BirkNext overførte revisjon {knownRev}). Det ble ikke overskrevet — kontroller endringen i Azure DevOps.")
                        { TestCaseId = existingId, WebUrl = remote.WebUrl });
                    continue;
                }
                try
                {
                    var updated = await ado.UpdateTestCaseAsync(d.ConnectionId, existingId, remote.Rev, fields, ct);
                    Apply(link, c, d, preview.PlanRevision, updated, who);
                    await db.SaveChangesAsync(ct);
                    toSuite.Add((existingId, c, AdoTransferOutcome.Updated));
                }
                catch (AzureDevOpsTestPlansException e) when (e.Code == "changed-remotely")
                { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview, e.Message) { TestCaseId = existingId, WebUrl = remote.WebUrl }); }
                catch (AzureDevOpsTestPlansException e) when (e.Uncertain)
                { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Uncertain, $"{e.Message} Åpne testtilfellet i Azure DevOps og kontroller stegene.") { TestCaseId = existingId, WebUrl = remote.WebUrl }); }
                catch (AzureDevOpsTestPlansException e)
                { results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Failed, e.Message) { TestCaseId = existingId, WebUrl = remote.WebUrl }); }
                continue;
            }

            // Create: recorded as uncertain BEFORE it is sent, so a lost answer can never be followed by a blind second create.
            (string State, DateTimeOffset? AttemptedAt, string Title)? previous = link is null ? null : (link.State, link.AttemptedAt, link.Title);
            if (link is null)
            {
                link = new AdoTestCaseLinkRecord { Id = Guid.NewGuid(), ProjectKey = key, CaseKey = c.CaseKey, Organization = d.Organization, AdoProject = d.Project };
                db.AdoTestCaseLinks.Add(link);
            }
            link.State = nameof(AdoLinkState.Uncertain);
            link.AttemptedAt = Now;
            link.Title = Cut(c.Title);
            link.UpdatedBy = who;
            link.UpdatedAt = Now;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NeedsReview, "En annen overføring av dette testtilfellet ble registrert samtidig. Hent forhåndsvisningen på nytt."));
                continue;
            }
            try
            {
                var created = await ado.CreateTestCaseAsync(d.ConnectionId, d.Project, fields, ct);
                Apply(link, c, d, preview.PlanRevision, created, who);
                await db.SaveChangesAsync(ct);
                toSuite.Add((created.Id, c, AdoTransferOutcome.Created));
            }
            catch (AzureDevOpsTestPlansException e) when (e.Uncertain)
            {
                results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Uncertain, $"{e.Message} Velg «Kontroller» før du overfører det på nytt."));
            }
            catch (AzureDevOpsTestPlansException e)
            {
                // Certainly not created: back to what it was (a new row is removed; an existing link keeps its test case).
                if (previous is { } p) { link.State = p.State; link.AttemptedAt = p.AttemptedAt; link.Title = p.Title; }
                else db.AdoTestCaseLinks.Remove(link);
                await db.SaveChangesAsync(ct);
                results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.Failed, e.Message));
            }
        }

        // Suite placement (2026-11-26): the suite tree is created or reused parent before child, through the same safe creation as «Opprett
        // testgruppe» (an existing suite with the same name under the same parent is reused; an uncertain earlier create blocks a blind second one).
        var suiteResults = new List<AdoSuiteResult>();
        var remoteSuite = new Dictionary<string, int>(StringComparer.Ordinal);
        if (request.PreserveSuites && toSuite.Count > 0)
        {
            foreach (var item in preview.SuitePlan)
            {
                var local = built.Plan.Suites.First(x => x.Id == item.LocalSuiteId);
                int? parent = local.ParentId is null || built.Plan.Suites.All(x => x.Id != local.ParentId) ? d.SuiteId : remoteSuite.TryGetValue(local.ParentId, out var p) ? p : null;
                if (parent is null) { suiteResults.Add(new(item.LocalSuiteId, item.Path, AdoCreateOutcome.Failed, null, "Den overordnede testgruppen ble ikke opprettet.")); continue; }
                var created = await settings.CreateSuiteAsync(new AdoCreateSuiteRequest(d.ConnectionId, d.Project, d.PlanId, parent.Value, local.Name, Confirmed: true), actor, ct);
                if (created.Suite is { } suite) remoteSuite[item.LocalSuiteId] = suite.Id;
                suiteResults.Add(new(item.LocalSuiteId, item.Path, created.Outcome, created.Suite?.Id, created.Message));
            }
        }
        // Add each test case once to its suite, only what is not there already (a repeated transfer never adds a test case twice).
        var caseSuite = built.Plan.Cases.ToDictionary(c => c.Source.ScenarioId, c => c.SuiteId, StringComparer.Ordinal);
        int? TargetOf(AdoTransferCase c) => !request.PreserveSuites ? d.SuiteId
            : caseSuite.TryGetValue(c.CaseId, out var local) && remoteSuite.TryGetValue(local, out var remote) ? remote
            : built.Plan.Suites.Any(x => x.Id == caseSuite.GetValueOrDefault(c.CaseId)) ? null : d.SuiteId;
        foreach (var group in toSuite.GroupBy(t => TargetOf(t.Case)))
        {
            string? suiteError = null;
            var suiteUncertain = false;
            var missing = new List<int>();
            if (group.Key is not { } target)
                suiteError = "Testgruppen den skal ligge i kunne ikke opprettes eller gjenbrukes i Azure DevOps (se testgruppene over).";
            else
            {
                var present = target == d.SuiteId ? built.InSuite : [];
                if (target != d.SuiteId)
                    try { present = [.. (await ado.SuiteTestCasesAsync(d.ConnectionId, d.Project, d.PlanId, target, ct)).Select(x => x.Id)]; }
                    catch (AzureDevOpsTestPlansException e) { suiteError = e.Message; suiteUncertain = e.Uncertain; }
                missing = [.. group.Where(t => !present.Contains(t.Id)).Select(t => t.Id).Distinct()];
                if (suiteError is null && missing.Count > 0)
                    try { await ado.AddToSuiteAsync(d.ConnectionId, d.Project, d.PlanId, target, missing, ct); }
                    catch (AzureDevOpsTestPlansException e) { suiteError = e.Message; suiteUncertain = e.Uncertain; }
            }
            foreach (var (id, c, outcome) in group)
            {
                var url = ado.WorkItemUrl(d.Organization, d.Project, id);
                if (suiteError is not null && (group.Key is null || missing.Contains(id)))
                    results.Add(new(c.CaseId, c.Title, AdoTransferOutcome.NotInSuite,
                        $"Testtilfelle {id} ble {(outcome == AdoTransferOutcome.Created ? "opprettet" : "oppdatert")}, men {(suiteUncertain ? "vi vet ikke om det ble lagt i testgruppen" : "kunne ikke legges i testgruppen")}: {suiteError} Overfør på nytt for å legge det til (det lages ikke på nytt).")
                        { TestCaseId = id, WebUrl = url });
                else
                    results.Add(new(c.CaseId, c.Title, outcome, outcome == AdoTransferOutcome.Created ? $"Opprettet som testtilfelle {id}." : $"Testtilfelle {id} ble oppdatert.") { TestCaseId = id, WebUrl = url });
            }
        }

        var ordered = preview.Cases.Select(c => results.FirstOrDefault(r => r.CaseId == c.CaseId)).OfType<AdoTransferCaseResult>().ToList();
        var result = new AdoTransferResult { Destination = d, SuiteUrl = ado.PlanUrl(d.Organization, d.Project, d.PlanId, d.SuiteId), Cases = ordered, Suites = suiteResults };

        // Export history (date, time, who, where, what happened): the same history as the file exports.
        db.AdoExports.Add(new AdoExportRecord
        {
            Id = Guid.NewGuid(), ProjectKey = key, DraftId = built.Plan.Id, PlanName = Cut(built.Plan.PlanName, 300), PlanRevision = built.Plan.Revision, Format = "Direct", Mode = "Direct",
            Cases = result.Succeeded, Rows = built.Export.Rows.Count(r => ordered.Any(o => o.CaseId == r.CaseId && o.Outcome is AdoTransferOutcome.Created or AdoTransferOutcome.Updated or AdoTransferOutcome.NotInSuite)),
            Destination = Cut($"{d.Organization} / {d.Project} / {d.PlanName} / {d.SuiteName}", 300),
            ContentJson = JsonSerializer.Serialize(new
            {
                cases = ordered.Where(o => o.Outcome is AdoTransferOutcome.Created or AdoTransferOutcome.Updated).Select(o => $"{o.CaseId}@{preview.Cases.First(c => c.CaseId == o.CaseId).CurrentRevision}"),
                outcomes = ordered.Select(o => new { o.CaseId, outcome = o.Outcome.ToString(), o.TestCaseId }),
                selected = preview.Cases.Where(c => c.Ready).Select(c => c.CaseId),
                destination = new { d.Organization, d.Project, d.PlanId, d.SuiteId },
                suites = suiteResults.Select(x => new { x.LocalSuiteId, x.Path, outcome = x.Outcome.ToString(), x.RemoteSuiteId }),
                status = Status(ordered), startedAt = started, completedAt = Now,
                created = ordered.Count(o => o.Outcome == AdoTransferOutcome.Created), updated = ordered.Count(o => o.Outcome == AdoTransferOutcome.Updated),
                skipped = ordered.Count(o => o.Outcome == AdoTransferOutcome.Skipped), problems = result.Problems,
                // A declaration by the person transferring — not a verified test lead approval, and no approver identity is invented.
                declaration = new { declared = true, text = AdoTransferDeclaration.Text, by = who, verifiedApproval = false },
            }, E.Json),
            ExportedBy = who, ExportedAt = Now,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Direct Azure DevOps transfer to {Organization}/{Project} plan {PlanId} suite {SuiteId}: {Succeeded} succeeded, {Problems} need attention", d.Organization, d.Project, d.PlanId, d.SuiteId, result.Succeeded, result.Problems);
        return result;
    }

    private void Apply(AdoTestCaseLinkRecord link, AdoTransferCase c, AdoTestPlanDestination d, int planRevision, AdoWorkItem item, string who)
    {
        link.State = nameof(AdoLinkState.Linked);
        link.TestCaseId = item.Id;
        link.RemoteRevision = item.Rev;
        link.CaseRevision = c.CurrentRevision;
        link.PlanRevision = planRevision;
        link.AdoPlanId = d.PlanId;
        link.AdoSuiteId = d.SuiteId;
        link.Title = Cut(c.Title);
        link.UpdatedBy = who;
        link.UpdatedAt = Now;
    }

    /// <summary>«Kontroller»: an uncertain create is looked up in Azure DevOps (BirkNext tag + exact title + created since the attempt), or the user names the id.</summary>
    public async Task<AdoReconcileResult> ReconcileAsync(AdoReconcileRequest request, string actor, CancellationToken ct)
    {
        var plan = await export.PlanAsync(new AdoExportRequest { ProjectKey = request.ProjectKey, DraftId = request.DraftId, PlanRevision = (await PlanRevisionAsync(request, ct)) }, ct);
        var caseKey = $"{plan.Id:N}/{request.CaseId}";
        var d = request.Destination;
        var link = (await LinksAsync(request.ProjectKey, d, [caseKey], ct)).FirstOrDefault();
        AdoCaseLink View2() => View(link, id => ado.WorkItemUrl(d.Organization, d.Project, id));
        if (link is not { State: "Uncertain" }) return new(AdoVerifyOutcome.NotFound, "Det er ingenting å kontrollere for dette testtilfellet.", View2(), []);
        var who = actor.Length <= 200 ? actor : actor[..200];
        try
        {
            List<int> candidates;
            if (request.AdoptTestCaseId is { } chosen) candidates = [chosen];
            else candidates = await ado.FindCreatedAsync(d.ConnectionId, d.Project, link.Title, link.AttemptedAt ?? link.UpdatedAt, ct);
            if (candidates.Count == 1)
            {
                var item = await ado.WorkItemAsync(d.ConnectionId, candidates[0], ct);
                if (item is null || !string.Equals(item.WorkItemType, "Test Case", StringComparison.OrdinalIgnoreCase) || !string.Equals(item.Project, d.Project, StringComparison.OrdinalIgnoreCase))
                    return new(AdoVerifyOutcome.StillUnknown, $"Arbeidselement {candidates[0]} er ikke et testtilfelle i {d.Project}.", View2(), candidates);
                link.State = nameof(AdoLinkState.Linked);
                link.TestCaseId = item.Id;
                link.RemoteRevision = item.Rev;
                // The text Azure DevOps holds is not known for certain: the next preview offers an update.
                link.CaseRevision = null;
                link.UpdatedBy = who;
                link.UpdatedAt = Now;
                await db.SaveChangesAsync(ct);
                return new(AdoVerifyOutcome.Found, $"Fant testtilfelle {item.Id}. Det er nå koblet, og en oppdatering foreslås ved neste overføring.", View2(), candidates);
            }
            if (candidates.Count == 0)
            {
                if (link.TestCaseId is null) db.AdoTestCaseLinks.Remove(link);
                else { link.State = nameof(AdoLinkState.Linked); link.UpdatedAt = Now; link.UpdatedBy = who; }
                await db.SaveChangesAsync(ct);
                return new(AdoVerifyOutcome.NotFound, "Testtilfellet ble ikke opprettet. Du kan overføre det på nytt.", link.TestCaseId is null ? new AdoCaseLink() : View2(), []);
            }
            return new(AdoVerifyOutcome.StillUnknown, $"Fant {candidates.Count} mulige testtilfeller ({string.Join(", ", candidates)}). Kontroller dem i Azure DevOps og velg hvilket som hører til.", View2(), candidates);
        }
        catch (AzureDevOpsTestPlansException e) { return new(AdoVerifyOutcome.StillUnknown, $"Kunne ikke kontrollere ennå: {e.Message}", View2(), []); }
    }

    private async Task<int> PlanRevisionAsync(AdoReconcileRequest request, CancellationToken ct) =>
        (await db.TestPlanDrafts.AsNoTracking().Where(r => r.Id == request.DraftId).Select(r => (int?)r.Revision).SingleOrDefaultAsync(ct)) ?? 0;

    private static string Cut(string value, int max = 300) => value.Length <= max ? value : value[..max];
}
