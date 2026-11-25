using System.Collections.Concurrent;
using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.DeliveryReporting;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// System Settings → Azure DevOps – Testplaner: choose a stored connection, an Azure DevOps project, a test plan and a test suite; create a new
/// test plan or suite only through an explicit confirmed request; save the destination as the system default or a project's default (local only —
/// saving never creates anything in Azure DevOps). Creates are protected against duplicates three ways: a same-name check first (an existing plan
/// or suite with that name is offered instead, never overwritten or created twice), one create per name at a time, and a create whose answer was
/// lost is recorded as pending and must be checked before the same name can be created again.
/// </summary>
public sealed class AdoTestPlanSettingsService(AzureDevOpsConnectionStore connections, AzureDevOpsTestPlansClient ado, AzureDevOpsTestPlanSettingsStore store,
    AdoTestPlanExportService export, TimeProvider clock, ILogger<AdoTestPlanSettingsService> logger)
{
    public const int MaxNameLength = 256;
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AdoTestPlanSettingsView> ViewAsync(string? projectKey, CancellationToken ct) => new()
    {
        Connections = [.. connections.List().Select(c => new AzureDevOpsConnectionInfo(c.Id, c.DisplayName, c.Organization, c.CredentialConfigured, c.LastValidationStatus, c.LastValidatedAt))],
        SystemDefault = store.SystemDefault(),
        ProjectKey = string.IsNullOrWhiteSpace(projectKey) ? null : projectKey,
        ProjectDefault = string.IsNullOrWhiteSpace(projectKey) ? null : (await export.ProfileAsync(projectKey, ct)).DefaultDestination,
        Pending = store.Pending(),
    };

    /// <summary>The destination that applies to the project: its own default, else the system default.</summary>
    public async Task<(AdoTestPlanDestination? Destination, AdoDestinationSource Source)> EffectiveAsync(string projectKey, CancellationToken ct)
    {
        if ((await export.ProfileAsync(projectKey, ct)).DefaultDestination is { } project) return (project.Destination, AdoDestinationSource.Project);
        return store.SystemDefault() is { } system ? (system.Destination, AdoDestinationSource.System) : (null, AdoDestinationSource.None);
    }

    private static async Task<AdoListResult<T>> List<T>(Func<Task<List<T>>> read)
    {
        try { return new(await read(), null); }
        catch (AzureDevOpsTestPlansException e) { return new([], e.Message, e.Code); }
    }

    public Task<AdoListResult<AdoProjectInfo>> ProjectsAsync(Guid connectionId, CancellationToken ct) => List(() => ado.ProjectsAsync(connectionId, ct));
    public Task<AdoListResult<AdoTestPlanInfo>> PlansAsync(Guid connectionId, string project, CancellationToken ct) => List(() => ado.PlansAsync(connectionId, project, ct));
    public Task<AdoListResult<AdoTestSuiteInfo>> SuitesAsync(Guid connectionId, string project, int planId, CancellationToken ct) => List(() => ado.SuitesAsync(connectionId, project, planId, ct));

    /// <summary>«Kontroller tilkobling»: reads the organization's projects (and the project's test plans when one is chosen). Records the result.</summary>
    public async Task<AzureDevOpsValidationResult> CheckAsync(Guid connectionId, string? project, CancellationToken ct)
    {
        AzureDevOpsValidationResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(project))
            {
                var projects = await ado.ProjectsAsync(connectionId, ct);
                result = new(true, "Connected", $"Tilkoblet. {projects.Count} prosjekt{(projects.Count == 1 ? "" : "er")} er tilgjengelig.");
            }
            else
            {
                var plans = await ado.CheckAsync(connectionId, project, ct);
                result = new(true, "Connected", $"Tilkoblet. {plans} testplan{(plans == 1 ? "" : "er")} i {project}.");
            }
        }
        catch (AzureDevOpsTestPlansException e)
        {
            result = new(false, e.Code switch { "authentication" => "AuthenticationFailed", "forbidden" => "AccessDenied", _ => "Failed" }, e.Message);
        }
        connections.RecordValidation(connectionId, result.Status, clock.GetUtcNow());
        return result;
    }

    private string? Organization(Guid connectionId) => connections.List().FirstOrDefault(c => c.Id == connectionId)?.Organization;

    private static string? NameProblem(string? name, string what) =>
        string.IsNullOrWhiteSpace(name) ? $"Skriv inn et navn på {what}."
        : name.Trim().Length > MaxNameLength ? $"Navnet kan være høyst {MaxNameLength} tegn."
        : name.Any(char.IsControl) ? "Navnet kan ikke inneholde linjeskift eller kontrolltegn."
        : null;

    /// <summary>Creates a test plan after confirmation. An existing plan with the same name is returned instead (nothing is created).</summary>
    public async Task<AdoCreateResult> CreatePlanAsync(AdoCreatePlanRequest request, string actor, CancellationToken ct)
    {
        if (!request.Confirmed) return new(AdoCreateOutcome.Failed, "Bekreft først: se over og velg «Opprett i Azure DevOps».");
        if (NameProblem(request.Name, "testplanen") is { } problem) return new(AdoCreateOutcome.Failed, problem);
        if (Organization(request.ConnectionId) is not { } organization) return new(AdoCreateOutcome.Failed, "Velg en tilkobling først.");
        if (string.IsNullOrWhiteSpace(request.Project)) return new(AdoCreateOutcome.Failed, "Velg et prosjekt først.");
        var name = request.Name.Trim();
        return await CreateAsync(AdoPendingKind.Plan, request.ConnectionId, organization, request.Project, null, null, name, actor, async () =>
        {
            var existing = (await ado.PlansAsync(request.ConnectionId, request.Project, ct)).FirstOrDefault(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return new AdoCreateResult(AdoCreateOutcome.AlreadyExists, $"Det finnes allerede en testplan med navnet «{existing.Name}» i {request.Project}. Ingenting ble opprettet — bruk den eksisterende, eller velg et annet navn.") { Plan = existing };
            var plan = await ado.CreatePlanAsync(request.ConnectionId, request.Project, name, request.Description?.Trim(), ct);
            logger.LogInformation("Azure DevOps test plan {PlanId} created in {Organization}/{Project} by {Actor}", plan.Id, organization, request.Project, actor);
            return new AdoCreateResult(AdoCreateOutcome.Created, $"Testplanen «{plan.Name}» ble opprettet.") { Plan = plan };
        });
    }

    /// <summary>Creates a static suite under the parent after confirmation. An existing suite with the same name under that parent is returned instead.</summary>
    public async Task<AdoCreateResult> CreateSuiteAsync(AdoCreateSuiteRequest request, string actor, CancellationToken ct)
    {
        if (!request.Confirmed) return new(AdoCreateOutcome.Failed, "Bekreft først: se over og velg «Opprett i Azure DevOps».");
        if (NameProblem(request.Name, "testgruppen") is { } problem) return new(AdoCreateOutcome.Failed, problem);
        if (Organization(request.ConnectionId) is not { } organization) return new(AdoCreateOutcome.Failed, "Velg en tilkobling først.");
        if (string.IsNullOrWhiteSpace(request.Project) || request.PlanId <= 0 || request.ParentSuiteId <= 0) return new(AdoCreateOutcome.Failed, "Velg prosjekt, testplan og overordnet testgruppe først.");
        var name = request.Name.Trim();
        return await CreateAsync(AdoPendingKind.Suite, request.ConnectionId, organization, request.Project, request.PlanId, request.ParentSuiteId, name, actor, async () =>
        {
            var suites = await ado.SuitesAsync(request.ConnectionId, request.Project, request.PlanId, ct);
            if (suites.All(s => s.Id != request.ParentSuiteId))
                return new AdoCreateResult(AdoCreateOutcome.Failed, "Den overordnede testgruppen finnes ikke lenger i testplanen. Hent testgruppene på nytt.");
            var existing = suites.FirstOrDefault(s => s.ParentId == request.ParentSuiteId && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return new AdoCreateResult(AdoCreateOutcome.AlreadyExists, $"Det finnes allerede en testgruppe med navnet «{existing.Name}» her. Ingenting ble opprettet — bruk den eksisterende.") { Suite = existing };
            var suite = await ado.CreateSuiteAsync(request.ConnectionId, request.Project, request.PlanId, request.ParentSuiteId, name, ct);
            logger.LogInformation("Azure DevOps test suite {SuiteId} created in plan {PlanId} ({Organization}/{Project}) by {Actor}", suite.Id, request.PlanId, organization, request.Project, actor);
            return new AdoCreateResult(AdoCreateOutcome.Created, $"Testgruppen «{suite.Name}» ble opprettet.") { Suite = suite };
        });
    }

    private async Task<AdoCreateResult> CreateAsync(AdoPendingKind kind, Guid connectionId, string organization, string project, int? planId, int? parentSuiteId, string name,
        string actor, Func<Task<AdoCreateResult>> create)
    {
        var what = kind == AdoPendingKind.Plan ? "testplanen" : "testgruppen";
        if (store.PendingFor(kind, organization, project, planId, parentSuiteId, name) is { } pending)
            return new(AdoCreateOutcome.Blocked, $"Vi vet ikke om {what} «{name}» ble opprettet {pending.AttemptedAt.ToLocalTime():dd.MM.yyyy HH:mm}. Velg «Kontroller i Azure DevOps» før du prøver igjen.") { Pending = pending };
        var key = $"{kind}|{organization}|{project}|{planId}|{parentSuiteId}|{name}";
        if (!InFlight.TryAdd(key, 0)) return new(AdoCreateOutcome.Blocked, $"{char.ToUpperInvariant(what[0])}{what[1..]} «{name}» opprettes allerede. Vent til det er ferdig.");
        try { return await create(); }
        catch (AzureDevOpsTestPlansException e) when (e.Uncertain)
        {
            var record = store.AddPending(new AdoPendingCreation
            {
                Id = Guid.NewGuid(), Kind = kind, ConnectionId = connectionId, Organization = organization, Project = project, PlanId = planId, ParentSuiteId = parentSuiteId,
                Name = name, AttemptedAt = clock.GetUtcNow(), AttemptedBy = actor.Length <= 200 ? actor : actor[..200],
            });
            logger.LogWarning("Azure DevOps {Kind} create in {Organization}/{Project} is uncertain (answer lost); recorded as pending {PendingId}", kind, organization, project, record.Id);
            return new(AdoCreateOutcome.Uncertain, $"Vi vet ikke om {what} ble opprettet. Kontroller Azure DevOps før du prøver igjen.") { Pending = record };
        }
        catch (AzureDevOpsTestPlansException e)
        {
            return new(AdoCreateOutcome.Failed, e.Code == "forbidden" ? $"Du har ikke tilgang til å opprette {what}. {e.Message}" : e.Message);
        }
        finally { InFlight.TryRemove(key, out _); }
    }

    /// <summary>«Kontroller i Azure DevOps» for a pending create: found by name → it exists (use it); not found → it was not created (may be created again).</summary>
    public async Task<AdoVerifyResult> VerifyAsync(Guid pendingId, CancellationToken ct)
    {
        if (store.GetPending(pendingId) is not { } pending) return new(AdoVerifyOutcome.NotFound, "Det er ingenting å kontrollere.");
        try
        {
            if (pending.Kind == AdoPendingKind.Plan)
            {
                var plan = (await ado.PlansAsync(pending.ConnectionId, pending.Project, ct)).FirstOrDefault(p => string.Equals(p.Name.Trim(), pending.Name, StringComparison.OrdinalIgnoreCase));
                store.RemovePending(pending.Id);
                return plan is null
                    ? new(AdoVerifyOutcome.NotFound, $"Testplanen «{pending.Name}» ble ikke opprettet. Du kan opprette den på nytt.")
                    : new(AdoVerifyOutcome.Found, $"Testplanen «{plan.Name}» finnes i Azure DevOps. Den er valgt.") { Plan = plan };
            }
            var suite = (await ado.SuitesAsync(pending.ConnectionId, pending.Project, pending.PlanId ?? 0, ct))
                .FirstOrDefault(s => s.ParentId == pending.ParentSuiteId && string.Equals(s.Name.Trim(), pending.Name, StringComparison.OrdinalIgnoreCase));
            store.RemovePending(pending.Id);
            return suite is null
                ? new(AdoVerifyOutcome.NotFound, $"Testgruppen «{pending.Name}» ble ikke opprettet. Du kan opprette den på nytt.")
                : new(AdoVerifyOutcome.Found, $"Testgruppen «{suite.Name}» finnes i Azure DevOps. Den er valgt.") { Suite = suite };
        }
        catch (AzureDevOpsTestPlansException e) { return new(AdoVerifyOutcome.StillUnknown, $"Kunne ikke kontrollere ennå: {e.Message}"); }
    }

    /// <summary>Saves (or clears, with null) a default destination. LOCAL only: nothing is created or changed in Azure DevOps.</summary>
    public async Task<AdoSavedDestination?> SaveDefaultAsync(AdoSaveDestinationRequest request, string actor, CancellationToken ct)
    {
        if (request.Destination is { } d)
        {
            if (!d.Complete) throw new TestPlanException(400, "destination-incomplete", "Velg tilkobling, prosjekt, testplan og testgruppe før du lagrer.");
            if (Organization(d.ConnectionId) is not { } organization) throw new TestPlanException(400, "connection-missing", "Tilkoblingen finnes ikke lenger. Velg en annen.");
            if (!string.Equals(organization, d.Organization, StringComparison.OrdinalIgnoreCase))
                throw new TestPlanException(400, "organization-mismatch", "Organisasjonen stemmer ikke med tilkoblingen. Velg tilkoblingen på nytt.");
        }
        var clean = request.Destination is { } x ? x with
        {
            Organization = x.Organization.Trim(), Project = x.Project.Trim(), PlanName = Cut(x.PlanName), SuiteName = Cut(x.SuiteName), SuitePath = x.SuitePath is null ? null : Cut(x.SuitePath, 1000),
        } : null;
        if (request.Scope == AdoDestinationScope.Project)
        {
            if (string.IsNullOrWhiteSpace(request.ProjectKey)) throw new TestPlanException(400, "project-missing", "Velg et BirkNext-prosjekt først.");
            return await export.SaveDefaultDestinationAsync(request.ProjectKey, clean, actor, ct);
        }
        return store.SaveSystemDefault(clean, actor.Length <= 200 ? actor : actor[..200], clock.GetUtcNow());
    }

    private static string Cut(string value, int max = MaxNameLength) => value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
