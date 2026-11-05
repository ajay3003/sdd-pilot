using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Services;

/// <summary>What happened for one Test Pack XML fixture after an import.</summary>
public sealed record XmlFixtureAutoDraftOutcome(string Context, string? Alias, bool Created, int? Version, string Message, string? Xsd = null);

/// <summary>
/// After a Project Import: a draft of each Test Pack's canonical XML fixture (e.g. skolenaervaer-valid-report), built from the snapshot that import
/// produced — exactly what Test Data → XML fixtures does, without a click. It only acts when nothing needs a person:
/// <list type="bullet">
/// <item>the active Target Environment is bound to a project, and the template resolves exactly one XSD in this import's snapshot (the one
/// canonical resolver; "Needs decision" or another snapshot is never guessed);</item>
/// <item>the role mappings are deterministic (the child at its element path from Test Data by alias; the submitting reporter is never in the XML)
/// and none needs review;</item>
/// <item>the generated XML is schema-valid.</item>
/// </list>
/// The result is a new DRAFT version of the canonical fixture, kept by Test Data in the backend; approval stays a separate person's step, and the
/// approved version stays in use. Anything else is skipped with its reason. Values never pass through this class (the backend binds by alias).
/// </summary>
public static class XmlFixtureAutoDraft
{
    public const string GeneratorVersion = "Test Data XML fixture v2 · automatic after import";

    /// <summary>The registered Test Data client, else the shared on-demand one (a host without a backend URL has none).</summary>
    private static ITestDataApiService? ResolveTestData(IServiceProvider services)
    {
        if (services.GetService(typeof(ITestDataApiService)) is ITestDataApiService registered) return registered;
        if (services.GetService(typeof(System.Net.Http.IHttpClientFactory)) is null) return null;
        try { return TestDataApiService.Resolve(services); }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException) { return null; }
    }

    /// <param name="intoCurrentApplication">The import added evidence to the active application (not a new application): only then is its snapshot
    /// the Target Environment's evidence.</param>
    public static async Task<IReadOnlyList<XmlFixtureAutoDraftOutcome>> RunAsync(IServiceProvider services, Guid snapshotId, bool intoCurrentApplication, CancellationToken ct = default)
    {
        if (services.GetService(typeof(IIntegrationJourneysApiService)) is not IIntegrationJourneysApiService journeys
            || ResolveTestData(services) is not { } testData)
            return [];
        string? environmentId = null;
        if (services.GetService(typeof(IFrontendAnalysisContextFactory)) is IFrontendAnalysisContextFactory contexts)
            try { environmentId = (await contexts.GetActiveContextAsync()).ActiveProfileOrNull?.Id; } catch (InvalidOperationException) { }
        var project = (services.GetService(typeof(IFrontendAnalysisSettingsService)) as IFrontendAnalysisSettingsService)?.ActiveProfile?.Project;

        IReadOnlyList<IntegrationTemplateView> templates;
        try { templates = XmlFixturePresentation.Contexts((await journeys.IntegrationTemplatesAsync(environmentId, null)).Value ?? []); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return []; }
        templates = [.. templates.Where(t => t.CanonicalFixtureAlias is not null)];
        if (templates.Count == 0) return [];
        if (!intoCurrentApplication)
            return [.. templates.Select(t => Skip(t, "The package was imported as a new application, so it is not this Target Environment's evidence. Create the fixture in Test Data → XML fixtures if it should be."))];
        if (environmentId is null || project is null)
            return [.. templates.Select(t => Skip(t, "No Target Environment is bound to a project, so the import's snapshot is not this environment's evidence. Create the fixture in Test Data → XML fixtures."))];

        var (catalog, _) = await testData.CatalogAsync(ct);
        var assets = catalog?.Assets ?? [];
        var outcomes = new List<XmlFixtureAutoDraftOutcome>();
        foreach (var context in templates)
        {
            try { outcomes.Add(await DraftAsync(services, testData, context, assets, snapshotId, environmentId, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException or System.Xml.XmlException or JsonException)
            { outcomes.Add(Skip(context, $"The draft could not be created ({ex.GetType().Name}). Create it in Test Data → XML fixtures.")); }
        }
        return outcomes;
    }

    /// <summary>The decision for one context, before anything is generated (pure: tested without a backend).</summary>
    public static string? Precondition(IntegrationTemplateView context, XmlContractSelection selection, Guid snapshotId, IReadOnlyList<TestDataAssetSummary> assets,
        IReadOnlyList<XmlValueMapping> mappings)
    {
        if (selection.Snapshot is null || !context.SchemaCandidates.Any(c => c.SnapshotId == snapshotId))
            return $"This import's snapshot declares no XSD named as the contract {context.SchemaName}.";
        if (!selection.Resolved || selection.Contract is null)
            return $"The XSD needs a decision ({selection.ContractReason ?? selection.SnapshotReason ?? "several candidates"}). Choose it in Test Data → XML fixtures.";
        if (selection.Snapshot?.SnapshotId != snapshotId || selection.Contract.SnapshotId != snapshotId)
            return $"The resolved XSD is in another snapshot ({selection.Contract.ArchiveFileName} · {selection.Contract.SnapshotId.ToString()[..8]}), not in this import.";
        if (assets.FirstOrDefault(a => a.Alias == context.CanonicalFixtureAlias) is { } existing
            && (existing.SchemaCheck?.SourceSnapshotId ?? existing.Schema?.SourceSnapshotId) == snapshotId)
            return $"{context.CanonicalFixtureAlias} already has a version generated from this snapshot.";
        if (mappings.FirstOrDefault(m => m.NeedsReview is not null) is { } review) return $"A value mapping needs review ({review.NeedsReview}).";
        if (mappings.FirstOrDefault(m => m.Kind == XmlValueSourceKind.TestDataAsset && m.AssetAlias is null) is { } unset)
            return $"No approved Test Data asset for {unset.Path}. Add or approve it in Test Data.";
        return null;
    }

    private static async Task<XmlFixtureAutoDraftOutcome> DraftAsync(IServiceProvider services, ITestDataApiService testData, IntegrationTemplateView context,
        IReadOnlyList<TestDataAssetSummary> assets, Guid snapshotId, string environmentId, CancellationToken ct)
    {
        // The import is the person's explicit choice of snapshot; inside it, the one resolver picks the XSD named exactly as the contract (several = decision).
        var selection = XmlContractResolution.Resolve(context, new XmlExplicitSelection(snapshotId, null));
        // The roles' deterministic mappings: each role with an element path gets its chosen approved asset; the submitting actor is not in the XML.
        var aliases = context.FixtureBindings.ToDictionary(b => b.Role, b => XmlFixturePresentation.Chosen(XmlFixturePresentation.Candidates(assets, b.Capability), null)?.Alias);
        var mappings = XmlValueMappings.Suggested(context, b => aliases.GetValueOrDefault(b.Role)).ToList();
        if (Precondition(context, selection, snapshotId, assets, mappings) is { } reason) return Skip(context, reason);
        var xsd = selection.Contract!;

        var (package, problem) = await PackageAsync(services, xsd, ct);
        if (package is null) return Skip(context, problem!, xsd.File);
        var generated = SchemaStudioService.GenerateMinimumValid(package, xsd.File);
        if (generated.State == SchemaGenerationState.Blocked || string.IsNullOrEmpty(generated.Xml))
            return Skip(context, $"The XML could not be generated: {string.Join(" ", generated.Reasons)}", xsd.File);
        var mapped = mappings.Where(m => m.Path.Length > 0 && m.Kind != XmlValueSourceKind.SchemaDefault).ToList();
        var (applied, problems) = XmlValueMappings.Apply(SchemaStudioService.WithElements(package, generated.Xml, mapped.Select(m => m.Path)), mapped, DateTimeOffset.UtcNow);
        if (problems.Count > 0) return Skip(context, $"Mapped fields are missing from the generated XML: {string.Join(" ", problems)}", xsd.File);
        var bindings = mapped.Where(m => m.Kind == XmlValueSourceKind.TestDataAsset).Select(m => new TestDataXmlBinding(m.AssetAlias!, m.Path)).ToList();
        var xml = applied;
        if (bindings.Count > 0)
        {
            var (bound, bindProblems) = await testData.BindXmlFixtureAsync(new TestDataXmlBindRequest(applied, bindings), ct);
            if (bound is null) return Skip(context, $"Test Data values could not be filled in: {string.Join(" ", bindProblems)}", xsd.File);
            xml = bound;
        }
        var validation = SchemaStudioService.Validate(package, xsd.File, xml);
        if (validation.Status != BirkNext.Schemas.XmlSchemaValidationStatus.Valid)
            return Skip(context, $"The generated XML is not valid against {Path.GetFileName(xsd.File)}: {validation.Message}", xsd.File);

        var inXml = context.FixtureBindings.Where(b => b.ElementPath is not null).Select(b => aliases.GetValueOrDefault(b.Role)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var used = assets.Where(a => inXml.Contains(a.Alias)).ToList();
        var facts = new SchemaXmlRegistrationFacts("Valid", "Valid", GeneratorVersion: GeneratorVersion, RootElement: XDocument.Parse(xml).Root?.Name.LocalName,
            EnvironmentId: environmentId, Contract: context.SchemaName, TestDataBindings: XmlFixturePresentation.BindingsText(context, aliases),
            Capabilities: context.FixtureCapabilities, ValueMappings: XmlValueMappings.ProvenanceText(mappings), ManualChanges: false,
            TestDataValueVersions: used.Count == 0 ? null : TestDataValueVersions.Format(used),
            SourceSelection: $"Automatic after Project Import · the imported snapshot · {selection.ContractReason}");
        var builder = new SchemaGeneratedXmlStore();
        var item = builder.Save(package, xsd.File, "Test Data XML fixture", null, xml, null, null, facts);
        var entry = builder.Register(item.Id, null, context.CanonicalFixtureAlias!, "positive", null, facts);
        var (version, refusal) = await testData.SaveCanonicalFixtureAsync(JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
        if (version is null) return Skip(context, $"The draft could not be saved: {refusal}", xsd.File);
        return new(context.DisplayName, context.CanonicalFixtureAlias, true, version,
            $"Draft version {version} of {context.CanonicalFixtureAlias} created from {Path.GetFileName(xsd.File)}{(xsd.Version is { } v ? $" (version {v})" : "")} · {xsd.ArchiveFileName} · snapshot {xsd.SnapshotId.ToString()[..8]}. "
            + (version == 1 ? "Approve it in Test Data to use it in runs." : "The approved version stays in use until you approve this draft in Test Data."), xsd.File);
    }

    /// <summary>The XSD package of the exact snapshot, with every file's fingerprint checked (the same loading Test Data → XML fixtures uses).</summary>
    private static async Task<(SchemaPackage? Package, string? Problem)> PackageAsync(IServiceProvider services, IntegrationTemplateSchemaCandidate xsd, CancellationToken ct)
    {
        var source = await SourceSchemaApiService.Resolve(services).SourceSchemasAsync(xsd.SnapshotId);
        if (source is not { IsAvailable: true }) return (null, $"The schema snapshot {xsd.ArchiveFileName} is not available.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in source.Files)
        {
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(file.Text))).ToLowerInvariant();
            if (!string.Equals(actual, file.Fingerprint, StringComparison.OrdinalIgnoreCase)) return (null, "A schema file does not match its recorded fingerprint.");
            files[file.Path] = file.Text;
        }
        var package = new SchemaStudioService().Import(SchemaStudioService.ContractPackageFiles(files, xsd.File), Path.GetFileName(xsd.File),
            new SchemaProvenance("Source Analysis", source.SourceArchiveName, source.SnapshotId, source.SnapshotFingerprint, source.AnalyzedAt, xsd.File, "",
                source.IsComplete, source.OmittedFiles, source.ProjectImportId, source.SourceProjectName));
        return SchemaStudioPresentation.IsLibrary(package) ? (null, "The XSD is a reusable schema library, not a complete message.") : (package, null);
    }

    private static XmlFixtureAutoDraftOutcome Skip(IntegrationTemplateView context, string reason, string? xsd = null) =>
        new(context.DisplayName, context.CanonicalFixtureAlias, false, null, reason, xsd);
}
