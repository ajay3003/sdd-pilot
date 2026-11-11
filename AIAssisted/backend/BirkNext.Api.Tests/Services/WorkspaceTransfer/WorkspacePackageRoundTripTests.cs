using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.WorkspaceTransfer;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BirkNext.Api.Tests.Services.WorkspaceTransfer.TransferInstallation;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>
/// Independent round trips between two installations (two temporary databases, two file-store roots). The expectations come from
/// <see cref="TransferInstallation.Ledger"/> and direct database reads, never from BirkNext's own summaries.
/// </summary>
public sealed class WorkspacePackageRoundTripTests : IAsyncLifetime
{
    private TransferInstallation _source = null!;
    private TransferInstallation _destination = null!;

    public async Task InitializeAsync()
    {
        _source = await CreateAsync("birknext_xfersrc");
        _destination = await CreateAsync("birknext_xferdst");
        await _source.SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _destination.DisposeAsync();
    }

    private async Task<string> ExportAsync(ExportRequest request, TransferInstallation from = null!)
    {
        var (service, db) = (from ?? _source).Service();
        await using (db)
        {
            var result = await service.ExportAsync(request, "test", default);
            return service.ResolveExport(result.FileName);
        }
    }

    private async Task<ImportPreview> StageAndPreviewAsync(string packagePath, params EnvironmentCandidate[] candidates) =>
        await StageAndPreviewAsync(await File.ReadAllBytesAsync(packagePath), candidates);

    private async Task<ImportPreview> StageAndPreviewAsync(byte[] package, params EnvironmentCandidate[] candidates)
    {
        var (service, db) = _destination.Service();
        await using (db)
        {
            var (id, _, _) = await service.StageAsync(new MemoryStream(package), "test", default);
            return await service.PreviewImportAsync(id, candidates, default);
        }
    }

    private async Task<ImportReport> ApplyAsync(ImportPreview preview, EnvironmentMappingAction env = EnvironmentMappingAction.LeaveUnresolved, string? target = null,
        Dictionary<string, ImportAction>? decisions = null, Action<WorkspacePackageService>? arrange = null)
    {
        var (service, db) = _destination.Service();
        await using (db)
        {
            arrange?.Invoke(service);
            return await service.ApplyImportAsync(new ImportApplyRequest
            {
                OperationId = preview.OperationId, PlanFingerprint = preview.PlanFingerprint, EnvironmentAction = env, TargetEnvironmentId = target, Decisions = decisions ?? [],
            }, "test", default);
        }
    }

    private async Task<T> Read<T>(Func<BirkNext.Api.Data.AppDbContext, Task<T>> query)
    {
        await using var db = _destination.Db();
        return await query(db);
    }

    [Fact]
    public async Task Project_round_trip_preserves_identities_versions_and_history_and_reimport_creates_no_duplicates()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth", IncludeEvidence = true });

        var preview = await StageAndPreviewAsync(package);
        preview.CanApply.Should().BeTrue(string.Join(" | ", preview.Checks.Select(c => c.Detail)));
        preview.Manifest!.IncludedProjects.Should().Equal("m2lb-synth");
        preview.Manifest.Authenticity.Should().StartWith("UNSIGNED");
        // Other projects' stage sources are referenced, never pulled into a project package.
        preview.Items.Where(i => i.Type == PackageTypes.Snapshot).Select(i => i.LogicalId).Should().BeEquivalentTo(Ledger.M2lbSnapshots);
        preview.Manifest.DependencyManifest.Where(d => d.Status == DependencyStatus.ExternalReference).Select(d => d.To.LogicalId)
            .Should().BeEquivalentTo([MuSnapshot.ToString(), AltinnSnapshot.ToString()]);
        preview.Items.Single(i => i.Type == PackageTypes.Composite).Status.Should().Be(ImportStatus.MissingDependency);

        var report = await ApplyAsync(preview);
        report.State.Should().Be(WorkspacePackageService.Completed, report.Error);

        // Oracle: read the destination directly.
        (await Read(db => db.IqrSourceSnapshots.Select(s => s.Id.ToString()).ToListAsync())).Should().BeEquivalentTo(Ledger.M2lbSnapshots);
        var contract = await Read(db => db.ApiContracts.SingleAsync(c => c.Key == ContractKey));
        contract.ProjectKey.Should().Be("m2lb-synth");
        (await Read(db => db.ApiContractVersions.Where(v => v.ContractId == contract.Id).OrderBy(v => v.Version).Select(v => new { v.Version, v.Sha256, v.SourceSnapshotId }).ToListAsync()))
            .Select(v => (v.Version, v.Sha256)).Should().Equal(Ledger.ContractVersions);
        (await Read(db => db.ContractTestRevisions.Where(r => r.TestId == TestId).OrderBy(r => r.Revision).Select(r => r.Revision).ToListAsync())).Should().Equal(Ledger.TestRevisions);
        (await Read(db => db.ContractEndpointBindings.CountAsync())).Should().Be(0, "a project package never carries environment bindings");
        (await Read(db => db.IntegrationSuites.Select(s => s.SuiteKey + "@" + s.Revision).ToListAsync())).Should().Equal(Ledger.Suite);
        (await Read(db => db.CompositeSourceContexts.Select(c => c.ContextKey + "@" + c.Revision).ToListAsync())).Should().Equal(Ledger.Composite);
        report.Reconciliation.Should().Contain(r => r.Item.Contains("skolenaervaer:m2lb-synth") && r.Status == ReconciliationStatus.Unresolved, "the MU and Altinn stage sources are not here");
        (await Read(db => db.SourceRepositoryAssociations.Select(a => a.DeclaredBy).ToListAsync())).Should().Equal("Tester A");
        (await Read(db => db.ParquetRuleSets.Select(r => r.ApprovedBy).ToListAsync())).Should().Equal("Tester A");
        _destination.TestData.Assets().Select(a => a.Alias).Should().Equal("m2lb-report-xml");
        // History stays history: the decision and the run are imported records, not destination decisions or runs.
        (await Read(db => db.ImportedHistoricalRecords.Select(h => h.Kind).ToListAsync())).Should().BeEquivalentTo(["contract-review-decision", "integration-suite-run", "execution-run"]);
        (await Read(db => db.ExecutionRuns.CountAsync())).Should().Be(0);
        (await Read(db => db.IntegrationSuiteRuns.CountAsync())).Should().Be(0);
        (await Read(db => db.ContractReviewDecisions.CountAsync())).Should().Be(0);

        var counts = await Counts();
        var again = await StageAndPreviewAsync(package);
        again.Items.Where(i => i.Type != PackageTypes.History).Should().OnlyContain(i => i.Status == ImportStatus.Identical || i.Status == ImportStatus.MissingDependency && i.Type == PackageTypes.Composite);
        again.Items.Single(i => i.Type == PackageTypes.Composite).Status.Should().Be(ImportStatus.Identical, "the context now exists here; its missing stage sources were reported at the first import");
        (await ApplyAsync(again)).State.Should().Be(WorkspacePackageService.Completed);
        (await Counts()).Should().BeEquivalentTo(counts, "re-importing the same package creates nothing new");
    }

    private async Task<Dictionary<string, int>> Counts() => new()
    {
        ["snapshots"] = await Read(db => db.IqrSourceSnapshots.CountAsync()),
        ["contracts"] = await Read(db => db.ApiContracts.CountAsync()),
        ["versions"] = await Read(db => db.ApiContractVersions.CountAsync()),
        ["revisions"] = await Read(db => db.ContractTestRevisions.CountAsync()),
        ["suites"] = await Read(db => db.IntegrationSuites.CountAsync()),
        ["composites"] = await Read(db => db.CompositeSourceContexts.CountAsync()),
        ["associations"] = await Read(db => db.SourceRepositoryAssociations.CountAsync()),
        ["history"] = await Read(db => db.ImportedHistoricalRecords.CountAsync()),
        ["assets"] = _destination.TestData.Assets().Count,
    };

    [Fact]
    public async Task Integration_round_trip_carries_every_stage_and_imports_it_disabled_into_a_new_environment()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.IntegrationWorkspace, EnvironmentId = Env, IncludeEvidence = true, TargetProfileJson = ProfileJson });
        var preview = await StageAndPreviewAsync(package, new EnvironmentCandidate("other-env", "QA synth", "Test"));
        preview.CanApply.Should().BeTrue(string.Join(" | ", preview.Checks.Select(c => c.Detail)));
        preview.Manifest!.IncludedProjects.Should().BeEquivalentTo(["m2lb-synth", "mu-synth", "altinn-synth"], "an integration scope carries every stage source, each in its own project");
        preview.Environment!.ImportedEnvironmentId.Should().Be(Env);
        preview.Items.Where(i => PackageTypes.EnvironmentScoped.Contains(i.Type)).Should().OnlyContain(i => i.Status == ImportStatus.EnvironmentRebindRequired,
            "nothing environment-bound is matched by name; a person chooses");
        System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(package)).Should().NotContain("should-never-travel");

        var report = await ApplyAsync(preview, EnvironmentMappingAction.CreateDisabled);
        report.State.Should().Be(WorkspacePackageService.Completed, report.Error);
        var env = report.DestinationEnvironmentId!;
        env.Should().StartWith("imported-");

        (await Read(db => db.IqrSourceSnapshots.Select(s => s.Id.ToString()).ToListAsync())).Should().BeEquivalentTo(Ledger.AllSnapshots);
        (await Read(db => db.IntegrationDefinitions.Where(d => d.EnvironmentId == env).OrderBy(d => d.Id).Select(d => new { d.Id, d.Enabled }).ToListAsync()))
            .Should().BeEquivalentTo(Ledger.IntegrationIds.Select(id => new { Id = id, Enabled = false }), "every system is imported disabled");
        (await Read(db => db.IntegrationEvidenceBindings.Where(b => b.EnvironmentId == env).Select(b => b.IntegrationId + "→" + b.SnapshotId).ToListAsync()))
            .Should().BeEquivalentTo([$"mm→{M2lbV2}", $"mu→{MuSnapshot}"]);
        (await Read(db => db.ContractEndpointBindings.Where(b => b.EnvironmentId == env).Select(b => b.BasePath).ToListAsync())).Should().Equal("/api/skole");
        var systems = await Read(db => db.ExternalIntegrations.Where(i => i.EnvironmentId == env).Select(i => i.DocumentJson).ToListAsync());
        systems.Should().HaveCount(Ledger.Systems.Length);
        systems.Select(json => JsonNode.Parse(json, null, default)!["enabled"]!.GetValue<bool>()).Should().OnlyContain(enabled => !enabled, "imported external connections never start enabled");
        var flow = _destination.Flows.Flows(env).Should().ContainSingle().Subject;
        (flow.Enabled, flow.RequiredForRelease).Should().Be((false, false));
        flow.Tags.Should().Contain(t => t.StartsWith("imported:"));
        _destination.Flows.RegisteredModules(env).Select(m => m.Name).Should().Equal("Skole");
        _destination.Authored.Find("kode6-case")!.Versions.Select(v => v.Fingerprint).Should().Equal(_source.Authored.Find("kode6-case")!.Versions.Select(v => v.Fingerprint));
        _destination.TestData.Assets().Single(a => a.Alias == "skole-child").EnvironmentId.Should().Be(env);
        var draft = JsonNode.Parse(report.ProfileDraftJson!)!.AsObject();
        draft["id"]!.ToString().Should().Be(env);
        draft.ToJsonString().Should().NotContain("apiKey").And.NotContain("bearerToken").And.NotContain("clientSecret").And.Contain("\"label\":\"ok\"");
        report.Readiness.Single(r => r.Dimension == "DOM_REVALIDATION_REQUIRED").Status.Should().Be("Required");
        report.Readiness.Single(r => r.Dimension == "CREDENTIALS_REQUIRED").Status.Should().Be("Required");
        report.Readiness.Should().NotContain(r => r.Status == "Ready", "an import never declares an integration ready");
        (await Read(db => db.ImportedHistoricalRecords.Where(h => h.EnvironmentId == env).Select(h => h.Kind).Distinct().ToListAsync())).Should().Contain("execution-run");
        (await Read(db => db.ExecutionRuns.CountAsync())).Should().Be(0, "imported runs are history, never destination runs");
    }

    [Fact]
    public async Task Leaving_the_environment_unresolved_imports_no_environment_bound_object()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.IntegrationWorkspace, EnvironmentId = Env });
        var report = await ApplyAsync(await StageAndPreviewAsync(package));
        report.State.Should().Be(WorkspacePackageService.Completed);
        (await Read(db => db.IntegrationDefinitions.CountAsync())).Should().Be(0);
        (await Read(db => db.ExternalIntegrations.CountAsync())).Should().Be(0);
        _destination.Flows.Flows(Env).Should().BeEmpty();
        report.Reconciliation.Where(r => r.Status == ReconciliationStatus.Unresolved).Should().NotBeEmpty();
        (await Read(db => db.IqrSourceSnapshots.CountAsync())).Should().Be(4, "sources are project data, not environment-bound");
    }

    [Fact]
    public async Task Reusing_an_environment_requires_one_offered_at_preview()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.IntegrationWorkspace, EnvironmentId = Env });
        var preview = await StageAndPreviewAsync(package, new EnvironmentCandidate("dest-qa", "Destination QA"));
        (await FluentActions.Awaiting(() => ApplyAsync(preview, EnvironmentMappingAction.ReuseVerified, "not-offered")).Should().ThrowAsync<TransferRequestException>())
            .Which.Code.Should().Be("ENVIRONMENT");
        var report = await ApplyAsync(preview, EnvironmentMappingAction.ReuseVerified, "dest-qa");
        report.DestinationEnvironmentId.Should().Be("dest-qa");
        (await Read(db => db.IntegrationDefinitions.Where(d => d.EnvironmentId == "dest-qa").CountAsync())).Should().Be(5);
    }

    [Fact]
    public async Task Conflicts_are_classified_never_merged_and_newer_revisions_are_added_only_on_decision()
    {
        // Destination: the same contract key owned by another project, and an older history of a second contract.
        await using (var db = _destination.Db())
        {
            db.ApiContracts.Add(new BirkNext.Api.Models.ApiContractRow { Id = Guid.NewGuid(), Key = ContractKey, Name = "Other", Protocol = "OpenAPI", ProjectKey = "other-project" });
            await db.SaveChangesAsync();
        }
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth" });
        var preview = await StageAndPreviewAsync(package);
        var item = preview.Items.Single(i => i.Type == PackageTypes.Contract);
        item.Status.Should().Be(ImportStatus.ConflictingContent);
        item.Detail.Should().Contain("never merged across projects");
        item.AllowedActions.Should().NotContain(ImportAction.CreateNew).And.NotContain(ImportAction.ImportAsNewRevision);
        (await FluentActions.Awaiting(() => ApplyAsync(preview, decisions: new() { [item.Key] = ImportAction.ImportAsNewRevision })).Should().ThrowAsync<TransferRequestException>())
            .Which.Code.Should().Be("DECISION");
        await ApplyAsync(preview);
        (await Read(db => db.ApiContracts.SingleAsync(c => c.Key == ContractKey))).ProjectKey.Should().Be("other-project", "the existing contract is kept");

        // A destination with only version 1 of the same project's contract: the package is a NEWER revision, added only on decision.
        await using var other = await CreateAsync("birknext_xferdst");
        await using (var db = other.Db())
        {
            db.ApiContracts.Add(new BirkNext.Api.Models.ApiContractRow { Id = ContractId, Key = ContractKey, Name = "Rapportinntak", Protocol = "OpenAPI", ProjectKey = "m2lb-synth" });
            db.ApiContractVersions.Add(new BirkNext.Api.Models.ApiContractVersionRow { Id = Guid.NewGuid(), ContractId = ContractId, Version = 1, Sha256 = "sha-v1", Source = "SourceAnalysis", SourceLabel = "v1", Format = "OpenAPI", DocumentText = "v1", ContractJson = "{}" });
            await db.SaveChangesAsync();
        }
        var (service, sdb) = other.Service();
        await using (sdb)
        {
            var (id, _, _) = await service.StageAsync(File.OpenRead(package), "test", default);
            var p = await service.PreviewImportAsync(id, [], default);
            var c = p.Items.Single(i => i.Type == PackageTypes.Contract);
            c.Status.Should().Be(ImportStatus.NewerRevision);
            (await service.ApplyImportAsync(new ImportApplyRequest { OperationId = id, PlanFingerprint = p.PlanFingerprint, Decisions = new() { [c.Key] = ImportAction.ImportAsNewRevision } }, "test", default))
                .State.Should().Be(WorkspacePackageService.Completed);
        }
        await using (var db = other.Db())
            (await db.ApiContractVersions.Where(v => v.ContractId == ContractId).OrderBy(v => v.Version).Select(v => v.Sha256).ToListAsync()).Should().Equal("sha-v1", "sha-v2");
    }

    [Fact]
    public async Task A_database_failure_rolls_back_every_object_and_a_file_phase_failure_is_retried_without_duplicates()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.IntegrationWorkspace, EnvironmentId = Env });
        var preview = await StageAndPreviewAsync(package);
        var rolledBack = await ApplyAsync(preview, EnvironmentMappingAction.CreateDisabled, arrange: s => s.FailDatabasePhaseAfter = n => n == 3);
        rolledBack.State.Should().Be(WorkspacePackageService.FailedRolledBack);
        (await Counts()).Values.Should().OnlyContain(v => v == 0, "nothing of a rolled-back import remains");

        var second = await StageAndPreviewAsync(package);
        var failed = await ApplyAsync(second, EnvironmentMappingAction.CreateDisabled, arrange: s => s.FailFilePhaseAt = type => type == PackageTypes.Flow);
        failed.State.Should().Be(WorkspacePackageService.FailedRecoverable);
        _destination.Flows.Flows(failed.DestinationEnvironmentId!).Should().BeEmpty();
        var afterFailure = await Counts();

        var retried = await ApplyAsync(second, EnvironmentMappingAction.CreateDisabled);
        retried.State.Should().Be(WorkspacePackageService.Completed, retried.Error);
        retried.DestinationEnvironmentId.Should().Be(failed.DestinationEnvironmentId, "a retry continues into the same new environment");
        (await Counts()).Should().BeEquivalentTo(afterFailure, "the database part is not applied twice");
        _destination.Flows.Flows(retried.DestinationEnvironmentId!).Should().ContainSingle();
        (await Read(db => db.ExternalIntegrations.CountAsync())).Should().Be(Ledger.Systems.Length);
    }

    [Fact]
    public async Task A_destination_change_after_preview_or_a_different_plan_needs_a_new_preview()
    {
        var package = await ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth" });
        var preview = await StageAndPreviewAsync(package);
        (await FluentActions.Awaiting(() => ApplyAsync(preview with { PlanFingerprint = new string('0', 64) })).Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("PLAN_CHANGED");
        await using (var db = _destination.Db())
        {
            db.IqrSourceSnapshots.Add(new BirkNext.Api.Models.IqrSourceSnapshotRecord { Id = M2lbV1, EnvironmentId = "", IntegrationId = "source-analysis", AnalyzedAt = DateTimeOffset.UtcNow, EvidenceJson = "{\"changed\":true}" });
            await db.SaveChangesAsync();
        }
        (await FluentActions.Awaiting(() => ApplyAsync(preview)).Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("DESTINATION_CHANGED");
        (await Read(db => db.ApiContracts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Tampered_corrupt_future_duplicate_and_cross_project_packages_are_refused_or_isolated()
    {
        var package = await File.ReadAllBytesAsync(await ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth" }));

        var tampered = Rewrite(package, entries => { var k = entries.Keys.First(p => p.StartsWith("contracts/")); entries[k] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries[k]).Replace("sha-v2", "sha-vX")); }, rehash: false);
        (await StageAndPreviewAsync(tampered)).Verdict.Should().Be("REFUSED");

        var corrupt = Rewrite(package, entries => entries["manifest.json"] = "{not json"u8.ToArray());
        (await StageAndPreviewAsync(corrupt)).Checks.Should().Contain(c => c.Name == "Manifest" && c.Status == TransferCheckStatus.Fail);

        var future = Rewrite(package, entries => entries["manifest.json"] = Edit(entries["manifest.json"], m => m["packageFormatVersion"] = 2));
        (await StageAndPreviewAsync(future)).Checks.Should().Contain(c => c.Name == "Format version" && c.Status == TransferCheckStatus.Fail);

        var duplicate = Rewrite(package, entries => entries["inventory/objects.json"] = Edit(entries["inventory/objects.json"], a => a.AsArray().Add(a.AsArray()[0]!.DeepClone())));
        (await StageAndPreviewAsync(duplicate)).Checks.Should().Contain(c => c.Name == "Identities" && c.Status == TransferCheckStatus.Fail);

        // A hand-edited project package claiming an object of an undeclared project: that object is refused, the rest stays importable.
        var cross = Rewrite(package, entries => entries["inventory/objects.json"] = Edit(entries["inventory/objects.json"], a =>
        {
            foreach (var o in a.AsArray().OfType<JsonObject>().Where(o => o["type"]!.ToString() == PackageTypes.ParquetRules)) o["owningProject"] = "someone-elses-project";
        }));
        var preview = await StageAndPreviewAsync(cross);
        preview.Items.Single(i => i.Type == PackageTypes.ParquetRules).Status.Should().Be(ImportStatus.Unauthorized);
        await ApplyAsync(preview);
        (await Read(db => db.ParquetRuleSets.CountAsync())).Should().Be(0);
        (await Read(db => db.ApiContracts.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Secrets_block_export_identifiers_need_acknowledgement_and_diagnostics_are_redacted()
    {
        await using var leaky = await CreateAsync("birknext_xferleak");
        await leaky.SeedAsync(withSecretInSource: true);
        var (service, db) = leaky.Service();
        await using (db)
        {
            var preview = await service.PreviewExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth" }, default);
            preview.Security.Should().Contain(f => f.Severity == PackageSecretScanner.Blocking);
            preview.Blockers.Should().Contain(b => b.Contains("look like secrets"));
            (await FluentActions.Awaiting(() => service.ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth", AcknowledgeSensitive = true, AllowPartial = true }, "t", default))
                .Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("EXPORT_BLOCKED");
            Directory.Exists(Path.Combine(leaky.Root, "transfer", "exports")).Should().BeFalse("no package file is written when export is refused");
        }

        await using (var sdb = _source.Db())
        {
            var suite = await sdb.IntegrationSuites.SingleAsync();
            sdb.IntegrationSuites.Remove(suite);
            sdb.IntegrationSuites.Add(new BirkNext.Api.Models.IntegrationSuiteRecord { Id = suite.Id, SuiteKey = suite.SuiteKey, Revision = suite.Revision, SnapshotId = suite.SnapshotId, ScopeId = suite.ScopeId, EnvironmentId = suite.EnvironmentId,
                GeneratedAt = suite.GeneratedAt, Fingerprint = suite.Fingerprint, DefinitionJson = """[{"testId":"kode6","person":"01010012345"}]""" });
            await sdb.SaveChangesAsync();
        }
        var (source, srcDb) = _source.Service();
        await using (srcDb)
        {
            var preview = await source.PreviewExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth" }, default);
            preview.Blockers.Should().Contain(b => b.Contains("identifier-shaped"));
            (await source.ExportAsync(new ExportRequest { Kind = TransferKind.ProjectWorkspace, ProjectKey = "m2lb-synth", AcknowledgeSensitive = true }, "t", default)).Security.Should().NotBeEmpty();
            var diagnostic = await source.ExportAsync(new ExportRequest { Kind = TransferKind.Diagnostic, SuiteKey = "skolenaervaer:m2lb-synth:abc123def456" }, "t", default);
            diagnostic.Security.Should().BeEmpty("the diagnostic package scans clean after deterministic redaction");
            diagnostic.Manifest.RedactionProfile.Should().StartWith("diagnostic-v1");
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(source.ResolveExport(diagnostic.FileName))).Should().NotContain("01010012345");
        }
    }

    [Fact]
    public async Task Unknown_capabilities_stay_unsupported_and_are_reported_as_unavailable()
    {
        var package = await File.ReadAllBytesAsync(await ExportAsync(new ExportRequest { Kind = TransferKind.IntegrationWorkspace, EnvironmentId = Env }));
        var withPack = Rewrite(package, entries => entries["manifest.json"] = Edit(entries["manifest.json"], m => m["requiredCapabilities"]!.AsArray().Add("journey-pack:not-in-this-build")));
        var preview = await StageAndPreviewAsync(withPack);
        preview.UnsupportedCapabilities.Should().Equal("journey-pack:not-in-this-build");
        var report = await ApplyAsync(preview, EnvironmentMappingAction.CreateDisabled);
        report.Readiness.Single(r => r.Dimension == "OBSERVER_UNAVAILABLE").Status.Should().Be("Yes");
    }

    // ───────────── package manipulation (test-side, independent of the product writer) ─────────────

    private static byte[] Edit(byte[] json, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(json)!;
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    /// <summary>Rewrites a package; with <paramref name="rehash"/> the integrity manifest is recomputed so only the intended defect remains.</summary>
    private static byte[] Rewrite(byte[] package, Action<Dictionary<string, byte[]>> change, bool rehash = true)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                entries[e.FullName] = m.ToArray();
            }
        change(entries);
        if (rehash)
        {
            var hashes = entries.Where(e => e.Key != "integrity/hashes.json").ToDictionary(e => e.Key, e => Convert.ToHexString(SHA256.HashData(e.Value)).ToLowerInvariant());
            entries["integrity/hashes.json"] = JsonSerializer.SerializeToUtf8Bytes(hashes);
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return output.ToArray();
    }
}
