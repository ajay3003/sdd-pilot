using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Specifications;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Controllers;

/// <summary>
/// System Settings → Spesifikasjonsformat (2026-11-27): the per-application specification format (default Spec Kit), its impact preview, and the
/// read-only OpenSpec views (source status, source changes, Data Quality reconciliation). Reading is open; changing a format needs the
/// system-configuration permission. Changing a format never reprocesses snapshots and never touches catalogs, plans, cases or executions.
/// </summary>
[ApiController]
[Route("api/specification-format")]
public sealed class SpecificationFormatController(SpecificationFormatSettingsStore settings, ProjectImportService imports, IAuthorizationService authorization,
    LocalTrustedExecution local, AppDbContext db, SourceBaselineService baselines, TimeProvider? clock = null) : ControllerBase
{
    private string Actor => SystemConfigurationPermissions.Actor(User,
        local.EvaluateFor(SystemConfigurationPermissions.LocalSetting, SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed) ?? "not attributed";
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    [HttpGet]
    public async Task<ActionResult<SpecificationFormatSettingsView>> Get(CancellationToken ct)
    {
        SpecificationFormatSettingsStore.Document doc;
        try { doc = settings.Read(); }
        catch (InvalidOperationException e) { return StatusCode(500, new { code = "specification-format-unreadable", message = e.Message }); }
        var known = await imports.ApplicationsAsync(ct);
        var snapshots = await baselines.SnapshotsAsync(ct);
        List<string> ProjectsOf(string key) => [.. snapshots.Where(s => SourceBaselineService.KeyOf(s) == key && s.ProjectImport?.ImportId is not null).OrderByDescending(s => s.AnalyzedAt)
            .Select(s => SpecificationCatalogService.ProjectKeyOf(s.ProjectImport!.ImportId)).Distinct(StringComparer.Ordinal)];
        return new SpecificationFormatSettingsView
        {
            Applications = [.. known.Select(a => doc.Applications.TryGetValue(a.Key, out var e)
                ? new SpecificationFormatApplication { ApplicationKey = a.Key, ApplicationName = a.Name, Format = e.Format, IsDefault = false, UpdatedAt = e.UpdatedAt, UpdatedBy = e.UpdatedBy, Imports = a.Imports, LatestImportedAt = a.LatestImportedAt, Projects = ProjectsOf(a.Key) }
                : new SpecificationFormatApplication { ApplicationKey = a.Key, ApplicationName = a.Name, Format = SpecificationFormat.SpecKit, IsDefault = true, Imports = a.Imports, LatestImportedAt = a.LatestImportedAt, Projects = ProjectsOf(a.Key) })],
            CanWrite = (await authorization.AuthorizeAsync(User, null, SystemConfigurationPermissions.Policy)).Succeeded,
        };
    }

    [HttpGet("impact")]
    public async Task<ActionResult<SpecificationFormatImpact>> Impact([FromQuery] string application, [FromQuery] SpecificationFormat format, CancellationToken ct)
    {
        var known = (await imports.ApplicationsAsync(ct)).FirstOrDefault(a => a.Key == application);
        if (known is null) return NotFound(new { code = "application-unknown", message = "Applikasjonen finnes ikke blant de importerte applikasjonene." });
        return await ImpactOfAsync(known, format, ct);
    }

    [HttpPut]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<SpecificationFormatSettingsView>> Save([FromBody] SaveSpecificationFormatRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Format)) return BadRequest(new { code = "specification-format-unknown", message = "Ukjent spesifikasjonsformat." });
        var known = (await imports.ApplicationsAsync(ct)).FirstOrDefault(a => a.Key == request.ApplicationKey);
        if (known is null) return BadRequest(new { code = "application-unknown", message = "Velg en applikasjon som er importert." });
        var impact = await ImpactOfAsync(known, request.Format, ct);
        if (impact.RequiresConfirmation && !request.ConfirmImpact)
            return Conflict(new { code = "impact-confirmation-required", message = "Applikasjonen har importerte data. Se over konsekvensene og bekreft endringen.", impact });
        try { settings.Save(known.Key, request.Format, Actor, Now); }
        catch (ArgumentException e) { return BadRequest(new { code = "specification-format-invalid", message = e.Message }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return StatusCode(500, new { code = "specification-format-not-saved", message = "Spesifikasjonsformatet kunne ikke lagres. Ingenting er endret." });
        }
        return await Get(ct);
    }

    /// <summary>Which BirkNext modules are verified for each format (shown in System Settings; nothing is hidden behind an empty result).</summary>
    [HttpGet("consumers")]
    public ActionResult<List<FormatConsumerStatus>> Consumers() => SpecificationFormatConsumers.All;

    [HttpGet("projects/{projectKey}/openspec")]
    public async Task<ActionResult<OpenSpecSourceStatus>> OpenSpecStatus(string projectKey, [FromServices] OpenSpecStatusService status, CancellationToken ct) =>
        await status.StatusAsync(TestScenarioDiscoveryService.Key(projectKey), ct);

    [HttpGet("projects/{projectKey}/openspec/source-changes")]
    public async Task<ActionResult<OpenSpecSourceChangeReport>> OpenSpecSourceChanges(string projectKey, [FromServices] OpenSpecStatusService status, CancellationToken ct) =>
        await status.SourceChangesAsync(TestScenarioDiscoveryService.Key(projectKey), ct);

    [HttpGet("projects/{projectKey}/openspec/reconciliation")]
    public async Task<ActionResult<OpenSpecReconciliationReport>> OpenSpecReconciliation(string projectKey, [FromServices] OpenSpecStatusService status, CancellationToken ct) =>
        await status.ReconcileAsync(TestScenarioDiscoveryService.Key(projectKey), ct);

    /// <summary>The reconciliation as a downloadable Markdown report (read-only).</summary>
    [HttpGet("projects/{projectKey}/openspec/reconciliation/export")]
    public async Task<IActionResult> OpenSpecReconciliationExport(string projectKey, [FromServices] OpenSpecStatusService status, CancellationToken ct)
    {
        var report = await status.ReconcileAsync(TestScenarioDiscoveryService.Key(projectKey), ct);
        var bytes = System.Text.Encoding.UTF8.GetBytes(OpenSpecStatusService.ToMarkdown(report));
        return File(bytes, "text/markdown; charset=utf-8", $"openspec-avstemming-{report.CatalogRevision?.ToString() ?? "x"}.md");
    }

    private async Task<SpecificationFormatImpact> ImpactOfAsync(BirkNext.ProjectImport.KnownApplication app, SpecificationFormat requested, CancellationToken ct)
    {
        var current = settings.FormatOf(app.Key).Format;
        var importIds = (await baselines.SnapshotsAsync(ct)).Where(s => SourceBaselineService.KeyOf(s) == app.Key).Select(s => s.ProjectImport?.ImportId).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        var projectKeys = importIds.Select(SpecificationCatalogService.ProjectKeyOf).ToList();
        var catalogs = await db.SpecificationCatalogs.AsNoTracking().Where(c => projectKeys.Contains(c.ProjectKey)).Select(c => c.Fingerprint).ToListAsync(ct);
        var owners = projectKeys.Select(k => "project:" + k).ToList();
        var plans = await db.TestPlanDrafts.AsNoTracking().Where(d => owners.Contains(d.GuideKey)).Select(d => d.ContentJson).ToListAsync(ct);
        var cases = 0;
        foreach (var json in plans)
            try { cases += JsonSerializer.Deserialize<TestPlanDraft>(json, TestPlanSuggestionEngine.Json)?.Cases.Count ?? 0; } catch (JsonException) { }
        var openSpecCatalogs = catalogs.Count(SpecificationFormatMarker.IsOpenSpec);
        var hasData = app.Imports > 0 || catalogs.Count > 0 || plans.Count > 0;
        var changes = current != requested;
        var consequences = new List<string>();
        if (!changes) consequences.Add($"{app.Name} bruker allerede {SpecificationFormatMarker.Label(requested)}. Ingenting endres.");
        else
        {
            consequences.Add($"Valget gjelder fremtidige importer og kildeoppdateringer av {app.Name}. Det behandler ingenting på nytt nå.");
            consequences.Add($"Eksisterende data endres ikke: {app.Imports} kildeversjon(er), {catalogs.Count} spesifikasjonskatalog(er) ({catalogs.Count - openSpecCatalogs} Spec Kit, {openSpecCatalogs} OpenSpec), {plans.Count} testplan(er), {cases} testtilfelle(r), godkjenninger og kjøringshistorikk.");
            consequences.Add(requested == SpecificationFormat.OpenSpec
                ? "Neste import leses av OpenSpec-ruten: bare openspec/specs/ blir gjeldende krav og scenarioer; aktive forslag (openspec/changes/) og arkiv vises separat og teller ikke i dekning. Uten openspec/-mappe lagres ingen spesifikasjon — Spec Kit brukes ikke som reserve."
                : "Neste import leses av Spec Kit-ruten som før. OpenSpec-kataloger som allerede er lagret, beholdes som historikk.");
            consequences.Add("Lagrede testtilfeller beholder sine kildeidentiteter. Spec Kit- og OpenSpec-identiteter kobles aldri automatisk; tilfeller fra den gamle baselinen kan vises som «kilde ikke tilgjengelig» mot en ny baseline og må vurderes manuelt.");
            if (hasData) consequences.Add("Trenger du en ren overgang uten blandet historikk, importer kilden som en ny applikasjon (isolert kildegrunnlag) i stedet for å bytte format.");
        }
        return new SpecificationFormatImpact
        {
            ApplicationKey = app.Key, ApplicationName = app.Name, Current = current, Requested = requested, Changes = changes, HasImportedData = hasData, Imports = app.Imports,
            SpecificationCatalogs = catalogs.Count, OpenSpecCatalogs = openSpecCatalogs, SpecKitCatalogs = catalogs.Count - openSpecCatalogs, TestPlans = plans.Count, TestCases = cases,
            Consequences = consequences, RequiresConfirmation = changes && hasData,
        };
    }
}

/// <summary>Phase 14 audit result: which modules consume the routed, format-neutral evidence and which still read documents with the Spec Kit parser.</summary>
public static class SpecificationFormatConsumers
{
    public static readonly List<FormatConsumerStatus> All =
    [
        new("Manual Testing – Testoversikt", FormatConsumerSupport.Supported, FormatConsumerSupport.Supported, "Leser lagret evidens fra den rutede katalogen; OpenSpec-capabilities grupperes som «Uten modultilknytning»; forslag vises separat."),
        new("Manual Testing – Finn og forstå / Planlegg og klargjør", FormatConsumerSupport.Supported, FormatConsumerSupport.Supported, "Discovery leser den importerte, rutede evidensen. Nettleserens Spec Kit-tolkning (arbeidsutkast) avvises for OpenSpec."),
        new("Manual Testing – Testplaner / Testgjennomføring / Versjoner", FormatConsumerSupport.Supported, FormatConsumerSupport.Supported, "Formatnøytralt: lagrede testtilfeller, revisjoner og kjøringer beholder sin kildeidentitet og kildeversjon."),
        new("Manual Testing – Kildeendringer", FormatConsumerSupport.Supported, FormatConsumerSupport.Supported, "Spec Kit: eksisterende sammenligning. OpenSpec: egen baseline-sammenligning og separat forslagspåvirkning."),
        new("Requirements Traceability (server-katalog)", FormatConsumerSupport.Supported, FormatConsumerSupport.Supported, "Kravreferanser kontrolleres mot katalogen; OpenSpec-krav har os:-nøkler (BirkNext-genererte, ikke kildens ID)."),
        new("Test Coverage (kravdekning)", FormatConsumerSupport.Supported, FormatConsumerSupport.NotVerified, "Serverdekning leser katalogen; nettleserens dekningsvisning tolker dokumenter med Spec Kit-parseren og er ikke verifisert for OpenSpec."),
        new("Specifications Explorer", FormatConsumerSupport.Supported, FormatConsumerSupport.NotSupported, "Tolker dokumenter i nettleseren med Spec Kit-parseren. For OpenSpec: bruk Testoversikt og OpenSpec-statusen."),
        new("System Understanding", FormatConsumerSupport.Supported, FormatConsumerSupport.NotVerified, "Guidegenerering leser katalogens evidens (formatnøytral), men er ikke verifisert med OpenSpec-kilder."),
        new("Relationship Review / Test Data / Performance Review", FormatConsumerSupport.Supported, FormatConsumerSupport.NotVerified, "Ingen direkte parseravhengighet funnet; ikke verifisert med OpenSpec-identiteter."),
        new("Release Readiness / Delivery Overview / Delivery Reports", FormatConsumerSupport.Supported, FormatConsumerSupport.NotVerified, "Leveranserapporter tolker spesifikasjoner med Spec Kit-parseren (DeliverySnapshotParser); OpenSpec er ikke verifisert."),
        new("Azure DevOps-eksport", FormatConsumerSupport.Supported, FormatConsumerSupport.PreviewOnly, "For OpenSpec-prosjekter er overføring og avstemming mot Azure DevOps sperret under første verifisering; forhåndsvisning og fil er tilgjengelig."),
    ];
}
