using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Configuration;
using BirkNext.Api.Data;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Dependencies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Source-free dependency health review over stored inventories: SBOM/lock-file import and validation, registry version/age/deprecation with
/// typed failures, OSV-style advisory matching, license evidence without invented policy, comparisons (source ↔ SBOM, expected ↔ deployed,
/// baseline), the security-fix ↔ Renovate cross-check, automation runtime separate from configuration, immutable runs with refresh, bounded
/// lookups and secret redaction.
/// </summary>
public sealed class DependencyHealthTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeRegistry : IPackageRegistryProvider
    {
        public Dictionary<string, RegistryPackageResult> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public RegistryPackageResult? Default { get; set; }
        public List<string> Calls { get; } = [];
        public int MaxConcurrent { get; private set; }
        private int _current;
        public TimeSpan Delay { get; set; }
        public string Manager => "nuget";
        public string Registry => "nuget.org";

        public async Task<RegistryPackageResult> GetPackageAsync(string packageName, CancellationToken ct)
        {
            lock (Calls) { Calls.Add(packageName); MaxConcurrent = Math.Max(MaxConcurrent, ++_current); }
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                return Packages.TryGetValue(packageName, out var r) ? r with { RetrievedAt = DateTimeOffset.UtcNow }
                    : Default ?? new RegistryPackageResult(RegistryState.PackageNotFound, "Not found on nuget.org.", [], DateTimeOffset.UtcNow);
            }
            finally { lock (Calls) _current--; }
        }
    }

    private sealed class FakeAdvisories : IAdvisoryProvider
    {
        public Dictionary<string, List<AdvisoryRecord>> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Lookups { get; private set; }
        public List<string> Queried { get; } = [];
        public string Name => "OSV (osv.dev)";
        public bool Supports(InventoryDependency dep) => RegistryClassifier.Supported(dep);

        public Task<AdvisoryLookup> LookupAsync(IReadOnlyList<string> packageNames, CancellationToken ct)
        {
            Lookups++;
            Queried.AddRange(packageNames);
            return Task.FromResult(new AdvisoryLookup(
                packageNames.Where(n => !Down.Contains(n)).ToDictionary(n => n, n => Records.GetValueOrDefault(n) ?? [], StringComparer.OrdinalIgnoreCase),
                packageNames.Where(Down.Contains).ToDictionary(n => n, _ => "OSV is unavailable (HTTP 503).", StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeAutomation(AutomationEvidence? evidence = null) : IDependencyAutomationSource
    {
        public Task<AutomationEvidence> GetAsync(string? repository, int? stale, CancellationToken ct) =>
            Task.FromResult(evidence ?? new AutomationEvidence { State = AutomationState.NotConfigured, Provider = "Azure DevOps", Detail = "No repository provider is configured." });
    }

    private sealed class FakeDeployed(DependencyInventorySnapshot? inventory, string? error = null) : IDeployedDependencySource
    {
        public Task<(DependencyInventorySnapshot?, string?)> CaptureAsync(string targetUrl, string? environment, CancellationToken ct) => Task.FromResult((inventory, error));
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(route(request)); }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RegistryPackageResult Versions(params (string Version, bool Listed, string? Published)[] versions) =>
        new(RegistryState.Observed, $"{versions.Length} version(s) on nuget.org.", versions.Select(v => new RegistryVersion(v.Version, v.Listed, v.Published is null ? null : DateTimeOffset.Parse(v.Published), null, "MIT", null)).ToList(), DateTimeOffset.UtcNow);

    private static AdvisoryRecord Advisory(string id, string package, string introduced, string? fixedIn, string? severity = "HIGH") => new()
    {
        Id = id, Aliases = [$"CVE-2026-{id.Length}000"], Summary = $"{package} issue", SourceSeverity = severity,
        Affected = [new AdvisoryAffected("NuGet", package, [new AdvisoryRange("ECOSYSTEM", [new("introduced", introduced), .. fixedIn is null ? Array.Empty<AdvisoryRangeEvent>() : [new AdvisoryRangeEvent("fixed", fixedIn)]])], [])],
    };

    private sealed record Harness(AppDbContext Db, FakeRegistry Registry, FakeAdvisories Advisories, DependencyHealthService Service, CapturingLogger<DependencyHealthService> Log);

    private static Harness Create(AutomationEvidence? automation = null, DependencyHealthOptions? options = null, DependencyInventorySnapshot? deployed = null, AppDbContext? db = null, IDependencyAutomationSource? automationSource = null)
    {
        db ??= Db();
        var registry = new FakeRegistry();
        var advisories = new FakeAdvisories();
        var log = new CapturingLogger<DependencyHealthService>();
        var service = new DependencyHealthService(db, [registry], advisories, automationSource ?? new FakeAutomation(automation), new FakeDeployed(deployed, deployed is null ? "not captured" : null),
            new DependencyEvidenceCache(), MsOptions.Create(options ?? new DependencyHealthOptions()), log);
        return new Harness(db, registry, advisories, service, log);
    }

    private static string CycloneDxJson(string timestamp = "2026-09-28T15:10:00Z", params (string Name, string? Version, string? License, string? Relationship)[] components)
    {
        var list = components.Length == 0 ? [("Foo", "1.0.0", "MIT", "direct"), ("Bar", "2.1.0", null, "transitive")] : components;
        var comps = string.Join(",", list.Select(c =>
            $"{{\"type\":\"library\",\"name\":\"{c.Name}\"{(c.Version is null ? "" : $",\"version\":\"{c.Version}\"")},\"bom-ref\":\"{c.Name}@{c.Version}\",\"purl\":\"pkg:nuget/{c.Name}{(c.Version is null ? "" : "@" + c.Version)}\""
            + (c.License is null ? "" : $",\"licenses\":[{{\"license\":{{\"id\":\"{c.License}\"}}}}]")
            + ",\"hashes\":[{\"alg\":\"SHA-256\",\"content\":\"" + new string('a', 64) + "\"}]}"));
        var direct = string.Join(",", list.Where(c => c.Relationship == "direct").Select(c => $"\"{c.Name}@{c.Version}\""));
        var transitive = string.Join(",", list.Where(c => c.Relationship == "transitive").Select(c => $"\"{c.Name}@{c.Version}\""));
        var firstDirect = list.FirstOrDefault(c => c.Relationship == "direct") is { Name: not null } d ? $"{d.Name}@{d.Version}" : null;
        return $"{{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.5\",\"version\":1,\"metadata\":{{\"timestamp\":\"{timestamp}\",\"component\":{{\"type\":\"application\",\"name\":\"M2LB.Api\",\"version\":\"2026.09.28\",\"bom-ref\":\"app\"}}}},"
            + $"\"components\":[{comps}],\"dependencies\":[{{\"ref\":\"app\",\"dependsOn\":[{direct}]}}{(firstDirect is null || transitive.Length == 0 ? "" : $",{{\"ref\":\"{firstDirect}\",\"dependsOn\":[{transitive}]}}")}]}}";
    }

    private static async Task<InventorySummary> ImportSbom(Harness h, string json, SbomRole role = SbomRole.BuildArtifact, string? environment = null, string? name = null)
    {
        var result = await h.Service.ImportAsync("m2lb.cdx.json", Encoding.UTF8.GetBytes(json), role, environment, name);
        result.Error.Should().BeNull(string.Join("; ", result.Validation?.Errors ?? []));
        return result.Inventory!;
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private static string Csproj(params (string Id, string Version)[] refs) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + string.Join("\n", refs.Select(r => $"    <PackageReference Include=\"{r.Id}\" Version=\"{r.Version}\" />")) + "\n  </ItemGroup>\n</Project>";

    /// <summary>A stored source review (as the existing Renovate review creates it) for repository "Repo".</summary>
    private static async Task<DependencyReviewResult> SourceReview(AppDbContext db, string? renovate, params (string Id, string Version)[] refs)
    {
        var files = new List<(string, string)> { ("Api/Api.csproj", Csproj(refs)) };
        if (renovate is not null) files.Add(("renovate.json", renovate));
        var (run, error) = await new DependencyReviewService(db, NullLogger<DependencyReviewService>.Instance).RunAsync("", [("Repo.zip", Zip([.. files]))], []);
        error.Should().BeNull();
        return run!;
    }

    // ── Source-free review ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoredInventoryIsReviewedWithoutAnyRepositoryArchive()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, "2025-01-10T00:00:00Z"), ("1.2.0", true, "2026-06-01T00:00:00Z"));
        h.Registry.Packages["Bar"] = Versions(("2.1.0", true, "2026-01-01T00:00:00Z"));
        var inventory = await ImportSbom(h, CycloneDxJson());

        var (run, error) = await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id });

        error.Should().BeNull();
        h.Db.DependencyReviewRuns.Should().BeEmpty("no source archive was uploaded or reviewed");
        run!.Items.Select(i => i.Dependency.PackageName).Should().BeEquivalentTo("Foo", "Bar");
        run.Summary.Dependencies.Should().Be(2);
        run.Categories.Single(c => c.Name == "Renovate policy").Should().Match<ReviewCategory>(c => c.State == ReviewCategoryState.NotAssessed && c.Detail.StartsWith("Not assessed — no source/config selected"));
        run.Sources.Single(s => s.Category == CheckCategory.Registry).State.Should().Be("Used");
    }

    [Fact]
    public async Task OnePackageDeclaredInManyProjectsIsOneItemWithAllLocations()
    {
        var h = Create();
        h.Registry.Packages["xunit"] = new RegistryPackageResult(RegistryState.Observed, "", [new RegistryVersion("2.9.3", true, null, new DeprecationEvidence(["Legacy"], null, "xunit.v3"), "Apache-2.0", null)], DateTimeOffset.UtcNow);
        var files = new List<(string, string)> { ("renovate.json", "{}") };
        for (var i = 0; i < 3; i++) files.Add(($"T{i}/T{i}.csproj", Csproj(("xunit", "2.9.3"))));
        var (review, _) = await new DependencyReviewService(h.Db, NullLogger<DependencyReviewService>.Instance).RunAsync("", [("Repo.zip", Zip([.. files]))], []);

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = InventorySources.SourceReviewInventoryId(review!.RunId, "Repo") })).Run!;

        run.Items.Should().ContainSingle().Which.Locations.Should().HaveCount(3);
        run.Summary.Should().Match<DependencyHealthSummary>(s => s.Dependencies == 1 && s.Declarations == 3);
        run.Observations.Should().ContainSingle(o => o.Id.StartsWith("deprecated:"));
    }

    [Fact]
    public async Task SourceReviewInventoryIsListedAndReviewableWithFreshness()
    {
        var h = Create();
        var review = await SourceReview(h.Db, "{}", ("Foo", "1.0.0"), ("Bar", "2.1.0"));

        var inventories = await h.Service.InventoriesAsync();

        var derived = inventories.Single(i => i.SourceType == InventorySourceType.SourceReview);
        derived.Id.Should().Be(InventorySources.SourceReviewInventoryId(review.RunId, "Repo"));
        derived.Stage.Should().Be(InventoryStage.Declared);
        derived.Freshness.Should().Be(InventoryFreshness.Current);
        derived.Dependencies.Should().Be(2);
        (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = derived.Id })).Run!.Inventory.Provenance.Should().Contain("Declared dependencies from repository archive Repo");
    }

    // ── Registry ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneMinorBehindIsObservedEvidenceNotAFinding()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, "2025-01-10T00:00:00Z"), ("1.2.0", true, "2026-06-01T00:00:00Z"), ("2.0.0-beta.1", true, "2026-09-01T00:00:00Z"));
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct")]));

        var item = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!.Items.Single();

        item.VersionStatus.Should().Be(VersionStatus.MinorBehind);
        item.Registry.LatestStable.Should().Be("1.2.0");
        item.Registry.LatestPrerelease.Should().Be("2.0.0-beta.1");
        item.Registry.ObservedPublished.Should().Be(DateTimeOffset.Parse("2025-01-10T00:00:00Z"));
        item.ObservedAgeDays.Should().BeGreaterThan(500, "age is evidence with no threshold applied");
        item.VersionDetail.Should().Contain(DependencyHealthLabels.LatestStable).And.NotContain("Recommended").And.Contain("not a defect");
        (await h.Service.GetAsync((await h.Service.HistoryAsync()).Single().RunId))!.Observations.Should().NotContain(o => o.Kind == ObservationKind.Finding);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", VersionStatus.PatchBehind)]
    [InlineData("1.0.0", "1.3.0", VersionStatus.MinorBehind)]
    [InlineData("1.9.9", "2.0.0", VersionStatus.MajorBehind)]
    [InlineData("1.2.0", "1.2.0", VersionStatus.Current)]
    [InlineData("1.2", "1.2.0", VersionStatus.Current)]
    [InlineData("1.2.0.1", "1.2.0.4", VersionStatus.PatchBehind)]
    [InlineData("2.0.0-rc.1", "1.9.0", VersionStatus.NewerThanLatestStable)]
    public void VersionClassificationUsesNuGetSemantics(string observed, string latest, VersionStatus expected) =>
        RegistryClassifier.Compare(NuGetVersion.Parse(observed), NuGetVersion.Parse(latest)).Status.Should().Be(expected);

    [Fact]
    public void NonSemanticVersionIsNotComparable()
    {
        var dep = new InventoryDependency { PackageName = "Foo", Version = "latest-build", PackageManager = "nuget", Datasource = "nuget" };
        RegistryClassifier.Classify(dep, Versions(("1.0.0", true, null)), "nuget.org", DateTimeOffset.UtcNow).Status.Should().Be(VersionStatus.NotComparable);
        RegistryClassifier.Classify(dep with { Version = null, VersionRange = "[1.0,2.0)" }, Versions(("1.0.0", true, null)), "nuget.org", DateTimeOffset.UtcNow).Status.Should().Be(VersionStatus.NotComparable);
        RegistryClassifier.Classify(new InventoryDependency { PackageName = "mcr.microsoft.com/dotnet/aspnet", Version = "8.0", PackageManager = "dockerfile", Datasource = "docker" }, null, "nuget.org", DateTimeOffset.UtcNow)
            .Status.Should().Be(VersionStatus.NotAssessed);
    }

    [Fact]
    public async Task RegistryUnavailableDrawsNoOutdatedConclusion()
    {
        var h = Create();
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "nuget.org is unavailable (HTTP 503).", [], DateTimeOffset.UtcNow);
        var inventory = await ImportSbom(h, CycloneDxJson());

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Items.Should().OnlyContain(i => i.Registry.State == RegistryState.RegistryUnavailable && i.VersionStatus == VersionStatus.RegistryUnavailable);
        run.Summary.Should().Match<DependencyHealthSummary>(s => s.RegistryUnavailable == 2 && s.PatchBehind + s.MinorBehind + s.MajorBehind + s.Current == 0 && s.RegistryNotFound == 0);
        run.Observations.Single(o => o.Id == "registry-unavailable").Should().Match<HealthObservation>(o => o.Kind == ObservationKind.Limitation && o.Detail.Contains("does not mean the packages are missing"));
        run.Categories.Single(c => c.Name == "Version health").State.Should().Be(ReviewCategoryState.Partial);
    }

    [Fact]
    public async Task ExactVersionMissingIsVersionNotFoundNotRegistryUnavailable()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.1.0", true, null), ("1.2.0", true, null));
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct")]));

        var item = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!.Items.Single();

        item.Registry.State.Should().Be(RegistryState.VersionNotFound);
        item.VersionStatus.Should().Be(VersionStatus.VersionUnavailable);
        DependencyHealthLabels.Registry(item.Registry.State).Should().Be("Version not found");
    }

    [Fact]
    public void UnlistedDeprecatedAndPrivateFeedStatesUseRegistryTerms()
    {
        var registry = new RegistryPackageResult(RegistryState.Observed, "", [
            new RegistryVersion("1.0.0", false, null, new DeprecationEvidence(["Legacy"], "Use Foo.Next", "Foo.Next"), null, null),
            new RegistryVersion("1.1.0", true, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, "MIT", null)], DateTimeOffset.UtcNow);
        var dep = new InventoryDependency { PackageName = "Foo", Version = "1.0.0", PackageManager = "nuget", Datasource = "nuget" };

        var c = RegistryClassifier.Classify(dep, registry, "nuget.org", DateTimeOffset.UtcNow);

        c.Registry.State.Should().Be(RegistryState.Unlisted);
        c.Registry.Deprecation!.Reasons.Should().Equal("Legacy");
        c.Status.Should().Be(VersionStatus.MinorBehind);
        var missing = RegistryClassifier.Classify(dep, new RegistryPackageResult(RegistryState.PackageNotFound, "Not found on nuget.org. A package served only by a private feed is not visible here: private registry metadata is not configured.", [], DateTimeOffset.UtcNow), "nuget.org", DateTimeOffset.UtcNow);
        missing.Registry.Detail.Should().Contain("private registry metadata is not configured");
        missing.Status.Should().NotBe(VersionStatus.RegistryUnavailable);
    }

    [Fact]
    public async Task NuGetProviderReadsInlineAndPagedRegistrationsWithGetOnly()
    {
        var index = """{"items":[{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/foo/page1.json","items":[{"catalogEntry":{"version":"1.0.0","listed":false,"published":"1900-01-01T00:00:00Z","deprecation":{"reasons":["CriticalBugs"],"alternatePackage":{"id":"Foo2"}}}}]},{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/foo/page2.json"}]}""";
        var page2 = """{"items":[{"catalogEntry":{"version":"1.2.0","published":"2026-05-01T00:00:00Z","licenseExpression":"Apache-2.0"}}]}""";
        var handler = new RouteHandler(r => r.RequestUri!.AbsolutePath.EndsWith("page2.json") ? JsonResponse(page2) : r.RequestUri.AbsolutePath.Contains("/missing/") ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : r.RequestUri.AbsolutePath.Contains("/down/") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : r.RequestUri.AbsolutePath.Contains("/limited/") ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : JsonResponse(index));
        var provider = new NuGetRegistryProvider(new HttpClient(handler));

        var result = await provider.GetPackageAsync("Foo", default);

        result.State.Should().Be(RegistryState.Observed);
        result.Versions.Should().HaveCount(2);
        result.Versions[0].Should().Match<RegistryVersion>(v => !v.Listed && v.Published == null && v.Deprecation!.AlternatePackage == "Foo2");
        result.Versions[1].LicenseExpression.Should().Be("Apache-2.0");
        (await provider.GetPackageAsync("missing", default)).State.Should().Be(RegistryState.PackageNotFound);
        (await provider.GetPackageAsync("down", default)).State.Should().Be(RegistryState.RegistryUnavailable);
        (await provider.GetPackageAsync("limited", default)).State.Should().Be(RegistryState.RateLimited);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Headers.Authorization == null);
    }

    // ── Advisories ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObservedVersionInsideAdvisoryRangeIsAffectedWithFixedVersionEvidence()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null), ("1.0.5", true, null));
        h.Advisories.Records["Foo"] = [Advisory("GHSA-aaaa-bbbb-cccc", "Foo", "0", "1.0.5")];
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        var item = run.Items.Single();
        item.Security.State.Should().Be(AdvisoryState.Affected);
        item.Security.FixedIn.Should().Be("1.0.5");
        item.Security.Detail.Should().Contain("not a tested or recommended upgrade").And.Contain("Exploitability is not assessed");
        item.Security.Advisories.Single().SourceSeverity.Should().Be("HIGH");
        run.Observations.Single(o => o.Kind == ObservationKind.Finding).Should().Match<HealthObservation>(o => o.Category == CheckCategory.Security && o.SourceSeverity == "HIGH");
        item.Remediation!.State.Should().Be(RemediationPolicyState.NotAssessed, "no source review with a Renovate config was selected");
        run.Categories.Single(c => c.Name == "Security advisories").State.Should().Be(ReviewCategoryState.NeedsReview);
    }

    [Fact]
    public async Task NoMatchedAdvisoryIsNeverCalledSafe()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.6", true, null));
        h.Advisories.Records["Foo"] = [Advisory("GHSA-aaaa-bbbb-cccc", "Foo", "0", "1.0.5")];
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.6", "MIT", "direct"), ("Bar", "2.1.0", null, "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Items.Single(i => i.Dependency.PackageName == "Foo").Security.State.Should().Be(AdvisoryState.NotAffectedByMatchedAdvisory);
        var bar = run.Items.Single(i => i.Dependency.PackageName == "Bar").Security;
        bar.State.Should().Be(AdvisoryState.NoMatchedAdvisoryObserved);
        DependencyHealthLabels.Advisory(bar.State).Should().Be("No matched advisories observed");
        bar.Detail.Should().Contain("not a statement that the dependency is safe");
        Enum.GetValues<AdvisoryState>().Select(DependencyHealthLabels.Advisory).Should().NotContain(l => l.Equals("Safe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdvisorySourceDownIsUnavailableNotZeroVulnerabilities()
    {
        var h = Create();
        h.Advisories.Down.Add("Foo");
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null));
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Items.Single().Security.State.Should().Be(AdvisoryState.AdvisorySourceUnavailable);
        DependencyHealthLabels.Advisory(AdvisoryState.AdvisorySourceUnavailable).Should().Be("Security evidence unavailable");
        run.Summary.AdvisorySourceUnavailable.Should().Be(1);
        run.Categories.Single(c => c.Name == "Security advisories").Should().Match<ReviewCategory>(c => c.State == ReviewCategoryState.Partial && c.Detail.Contains("1 advisory source unavailable"));
        run.Observations.Should().Contain(o => o.Id == "advisory-unavailable" && o.Detail.Contains("not \"0 vulnerabilities\""));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.5", false)]
    [InlineData("0.9.0", false)]
    public void OsvRangeSemantics(string version, bool expected)
    {
        var range = new AdvisoryRange("ECOSYSTEM", [new("introduced", "1.0.0"), new("fixed", "1.0.5")]);
        AdvisoryMatcher.InRange(range, NuGetVersion.Parse(version)).Should().Be(expected);
        AdvisoryMatcher.InRange(new AdvisoryRange("ECOSYSTEM", [new("introduced", "0"), new("last_affected", "2.0.0")]), NuGetVersion.Parse("2.0.0")).Should().BeTrue();
        AdvisoryMatcher.InRange(new AdvisoryRange("ECOSYSTEM", [new("introduced", "0"), new("last_affected", "2.0.0")]), NuGetVersion.Parse("2.0.1")).Should().BeFalse();
        AdvisoryMatcher.InRange(new AdvisoryRange("GIT", [new("introduced", "abc123")]), NuGetVersion.Parse(version)).Should().BeNull();
    }

    [Fact]
    public async Task OsvProviderQueriesBatchThenReadsDetailsAndReportsFailures()
    {
        var handler = new RouteHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/querybatch" => JsonResponse("""{"results":[{"vulns":[{"id":"GHSA-1111-2222-3333","modified":"2026-01-01T00:00:00Z"}]},{}]}"""),
            "/v1/vulns/GHSA-1111-2222-3333" => JsonResponse("""{"id":"GHSA-1111-2222-3333","aliases":["CVE-2026-1"],"summary":"x","database_specific":{"severity":"MODERATE"},"severity":[{"type":"CVSS_V3","score":"CVSS:3.1/AV:N"}],"affected":[{"package":{"ecosystem":"NuGet","name":"Foo"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"1.0.5"}]}]}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var lookup = await new OsvAdvisoryProvider(new HttpClient(handler)).LookupAsync(["Foo", "Bar"], default);

        lookup.ByPackage["Foo"].Single().Should().Match<AdvisoryRecord>(a => a.SourceSeverity == "MODERATE" && a.SeverityVectors.Single() == "CVSS_V3 CVSS:3.1/AV:N" && a.Aliases.Single() == "CVE-2026-1");
        lookup.ByPackage["Bar"].Should().BeEmpty();
        lookup.Failed.Should().BeEmpty();
        var down = await new OsvAdvisoryProvider(new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))).LookupAsync(["Foo"], default);
        down.Failed.Should().ContainKey("Foo");
        down.ByPackage.Should().BeEmpty();
    }

    // ── Security fix ↔ Renovate policy ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecurityFixRequiringBlockedMajorIsReportedWithoutChangingPolicy()
    {
        var h = Create();
        const string renovate = """{"packageRules":[{"description":"No majors","matchUpdateTypes":["major"],"enabled":false}]}""";
        var review = await SourceReview(h.Db, renovate, ("Foo", "1.0.0"), ("Bar", "1.2.3"));
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null), ("2.0.0", true, null));
        h.Registry.Packages["Bar"] = Versions(("1.2.3", true, null), ("1.2.5", true, null));
        h.Advisories.Records["Foo"] = [Advisory("GHSA-major-fix-0001", "Foo", "0", "2.0.0")];
        h.Advisories.Records["Bar"] = [Advisory("GHSA-patch-fix-0001", "Bar", "0", "1.2.5")];
        var configBefore = review.Repositories.Single().NormalizedConfig;

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = InventorySources.SourceReviewInventoryId(review.RunId, "Repo") })).Run!;

        var foo = run.Items.Single(i => i.Dependency.PackageName == "Foo").Remediation!;
        foo.State.Should().Be(RemediationPolicyState.BlockedByPolicy);
        DependencyHealthLabels.Remediation(foo.State).Should().Be("Security remediation requires a version currently blocked by Renovate policy");
        foo.MatchedRules.Should().Equal(1);
        foo.Explanation.Should().Contain("not changed");
        run.Items.Single(i => i.Dependency.PackageName == "Bar").Remediation!.State.Should().Be(RemediationPolicyState.PermittedByPolicy);
        DependencyHealthLabels.Remediation(RemediationPolicyState.PermittedByPolicy).Should().Be("Security fix is compatible with current Renovate policy");
        run.Observations.Should().Contain(o => o.Id.StartsWith("remediation:") && o.PackageName == "Foo");
        run.PolicySource.Should().Contain("Renovate policy snapshot of Repo");
        JsonSerializer.Deserialize<DependencyReviewResult>((await h.Db.DependencyReviewRuns.SingleAsync()).ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Repositories.Single().NormalizedConfig.Should().Be(configBefore);
    }

    // ── License ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LicenseIsEvidenceAndPolicyIsNotAssumed()
    {
        var h = Create();
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "down", [], DateTimeOffset.UtcNow);
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct"), ("Bar", "2.1.0", null, "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        var foo = run.Items.Single(i => i.Dependency.PackageName == "Foo").License;
        foo.Should().Match<LicenseEvidence>(l => l.State == LicenseState.Detected && l.Licenses.Single() == "MIT" && l.Policy == LicensePolicyState.NotConfigured && l.Sources.Single() == "SBOM");
        var bar = run.Items.Single(i => i.Dependency.PackageName == "Bar").License;
        bar.State.Should().Be(LicenseState.NotDeclared);
        DependencyHealthLabels.License(bar.State).Should().Be("Not declared");
        bar.Detail.Should().NotContain("compliant", "missing license metadata is not non-compliance");
        run.Observations.Should().NotContain(o => o.Category == CheckCategory.License && o.Kind == ObservationKind.Finding);
        run.Observations.Single(o => o.Id == "license-missing").Kind.Should().Be(ObservationKind.MissingEvidence);
    }

    [Fact]
    public async Task ConfiguredDenyListProducesPolicyFinding()
    {
        var h = Create(options: new DependencyHealthOptions { AllowedLicenses = ["MIT"], DeniedLicenses = ["GPL-3.0-only"] });
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "down", [], DateTimeOffset.UtcNow);
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct"), ("Bar", "2.1.0", "GPL-3.0-only", "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Items.Single(i => i.Dependency.PackageName == "Foo").License.Policy.Should().Be(LicensePolicyState.Allowed);
        run.Items.Single(i => i.Dependency.PackageName == "Bar").License.Policy.Should().Be(LicensePolicyState.Denied);
        run.Observations.Should().ContainSingle(o => o.Category == CheckCategory.License && o.Kind == ObservationKind.Finding);
    }

    [Fact]
    public async Task LicenseChangeAgainstBaselineIsNeutral()
    {
        var h = Create();
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "down", [], DateTimeOffset.UtcNow);
        var before = await ImportSbom(h, CycloneDxJson("2026-08-01T00:00:00Z", ("Foo", "1.0.0", "MIT", "direct"), ("Gone", "1.0.0", "MIT", "direct")), name: "Release 2026.08");
        var after = await ImportSbom(h, CycloneDxJson("2026-09-28T00:00:00Z", ("Foo", "1.0.0", "Apache-2.0", "direct"), ("New", "3.0.0", "MIT", "direct")), name: "Build 2026.09.28");

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = after.Id, BaselineInventoryId = before.Id })).Run!;

        var drift = run.Comparisons.Single(c => c.Kind == ComparisonKind.Baseline);
        drift.Entries.Single(e => e.State == ComparisonState.LicenseChanged).Should().Match<ComparisonEntry>(e => e.PackageName == "Foo" && e.LeftValue == "MIT" && e.RightValue == "Apache-2.0");
        drift.Entries.Should().Contain(e => e.State == ComparisonState.Added && e.PackageName == "New").And.Contain(e => e.State == ComparisonState.Removed && e.PackageName == "Gone");
        run.Observations.Should().NotContain(o => o.Kind == ObservationKind.Finding);
        run.Observations.Single(o => o.Id == "license-changed:Foo").Kind.Should().Be(ObservationKind.Observed);
    }

    // ── SBOM ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidCycloneDxJsonCreatesInventoryWithGraphIdentifiersAndHashes()
    {
        var h = Create();
        var result = await h.Service.ImportAsync("m2lb.cdx.json", Encoding.UTF8.GetBytes(CycloneDxJson()), SbomRole.BuildArtifact, null, null);

        result.Validation!.Should().Match<SbomValidation>(v => v.Valid && v.Format == SbomFormat.CycloneDxJson && v.SpecVersion == "1.5" && v.Components == 2 && v.Direct == 1 && v.Transitive == 1 && v.HasDependencyGraph && v.WithHashes == 2 && v.MissingLicenses == 1);
        result.Inventory!.Should().Match<InventorySummary>(i => i.SourceType == InventorySourceType.Sbom && i.Stage == InventoryStage.Packaged && i.CapturedAt == DateTimeOffset.Parse("2026-09-28T15:10:00Z") && i.Name == "M2LB.Api 2026.09.28 · SBOM");
        var stored = JsonSerializer.Deserialize<DependencyInventorySnapshot>((await h.Db.DependencyInventories.SingleAsync()).SnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        stored.Dependencies.Single(d => d.PackageName == "Foo").Should().Match<InventoryDependency>(d => d.Purl == "pkg:nuget/Foo@1.0.0" && d.PackageManager == "nuget" && d.Hashes.Single().Algorithm == "SHA-256");
    }

    [Fact]
    public void CycloneDxXmlAndSpdxJsonAreSupportedFormats()
    {
        var bom = CycloneDX.Json.Serializer.Deserialize(CycloneDxJson());
        var xml = CycloneDX.Xml.Serializer.Serialize(bom);
        var (xmlValidation, xmlInventory) = InventorySources.Import(new("m2lb.cdx.xml", Encoding.UTF8.GetBytes(xml), SbomRole.BuildArtifact, null, null, DateTimeOffset.UtcNow));
        xmlValidation.Should().Match<SbomValidation>(v => v.Valid && v.Format == SbomFormat.CycloneDxXml);
        xmlInventory!.Dependencies.Should().HaveCount(2);

        var spdx = """
        {"spdxVersion":"SPDX-2.3","dataLicense":"CC0-1.0","SPDXID":"SPDXRef-DOCUMENT","name":"m2lb","documentNamespace":"https://example.com/m2lb","creationInfo":{"created":"2026-09-28T10:00:00Z","creators":["Tool: x"]},
         "packages":[{"SPDXID":"SPDXRef-App","name":"M2LB.Api","downloadLocation":"NOASSERTION"},{"SPDXID":"SPDXRef-Foo","name":"Foo","versionInfo":"1.0.0","downloadLocation":"NOASSERTION","licenseDeclared":"MIT","licenseConcluded":"NOASSERTION",
           "externalRefs":[{"referenceCategory":"PACKAGE-MANAGER","referenceType":"purl","referenceLocator":"pkg:nuget/Foo@1.0.0"}],"checksums":[{"algorithm":"SHA256","checksumValue":"aa"}]}],
         "relationships":[{"spdxElementId":"SPDXRef-DOCUMENT","relationshipType":"DESCRIBES","relatedSpdxElement":"SPDXRef-App"},{"spdxElementId":"SPDXRef-App","relationshipType":"DEPENDS_ON","relatedSpdxElement":"SPDXRef-Foo"}]}
        """;
        var (spdxValidation, spdxInventory) = InventorySources.Import(new("m2lb.spdx.json", Encoding.UTF8.GetBytes(spdx), SbomRole.BuildArtifact, null, null, DateTimeOffset.UtcNow));
        spdxValidation.Should().Match<SbomValidation>(v => v.Valid && v.Format == SbomFormat.SpdxJson && v.Direct == 1);
        spdxInventory!.Dependencies.Single().Should().Match<InventoryDependency>(d => d.PackageName == "Foo" && d.Licenses.Single() == "MIT" && d.Relationship == DependencyRelationship.Direct);
        spdxInventory.CapturedAt.Should().Be(DateTimeOffset.Parse("2026-09-28T10:00:00Z"));
        InventorySources.Import(new("x.spdx", Encoding.UTF8.GetBytes("SPDXVersion: SPDX-2.3\nDataLicense: CC0-1.0"), SbomRole.BuildArtifact, null, null, DateTimeOffset.UtcNow))
            .Validation.Errors.Single().Should().Contain("tag/value documents are not supported");
    }

    [Theory]
    [InlineData("""{"bomFormat":"CycloneDX","specVersion":"1.5","components":[{"name":"x"}]}""", "type")]
    [InlineData("""{"bomFormat":"CycloneDX","specVersion":"1.5","components":[{"type":"library","name":"x","hashes":[{"alg":"SHA-256","content":"abcd"}]}]}""", "hashes")]
    [InlineData("not json", "Unsupported document")]
    [InlineData("{\"hello\":1}", "Unsupported document")]
    public async Task InvalidSbomIsRejectedWithoutCreatingAnInventory(string document, string mentions)
    {
        var h = Create();

        var result = await h.Service.ImportAsync("bad.json", Encoding.UTF8.GetBytes(document), SbomRole.BuildArtifact, null, null);

        result.Inventory.Should().BeNull();
        result.Error.Should().Contain("no inventory was created");
        result.Validation!.Valid.Should().BeFalse();
        string.Join(" ", result.Validation.Errors).Should().Contain(mentions);
        result.Validation.Errors.Should().OnlyContain(e => e.Length < 400 && !e.StartsWith("{"));
        h.Db.DependencyInventories.Should().BeEmpty();
    }

    [Fact]
    public async Task ComponentWithoutVersionIsKeptAsUnknownAndLimitsComparisons()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null));
        h.Advisories.Records["Foo"] = [Advisory("GHSA-aaaa-bbbb-cccc", "Foo", "0", "1.0.5")];
        var import = await h.Service.ImportAsync("m.json", Encoding.UTF8.GetBytes(CycloneDxJson(components: [("Foo", null, "MIT", "direct")])), SbomRole.BuildArtifact, null, null);

        import.Validation!.MissingVersions.Should().Be(1);
        import.Validation.Warnings.Should().Contain(w => w.Contains("version Unknown"));
        var item = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = import.Inventory!.Id })).Run!.Items.Single();
        item.Dependency.Version.Should().BeNull();
        item.VersionStatus.Should().Be(VersionStatus.NotComparable);
        item.Security.State.Should().Be(AdvisoryState.VersionNotComparable);
    }

    [Fact]
    public async Task LockFileIsAResolvedInventoryWithContentHashes()
    {
        var h = Create();
        var lockFile = """{"version":1,"dependencies":{"net8.0":{"Foo":{"type":"Direct","requested":"[1.0.0, )","resolved":"1.0.0","contentHash":"abc=="},"Bar":{"type":"Transitive","resolved":"2.1.0"},"My.Project":{"type":"Project"}},"net9.0":{"Foo":{"type":"Direct","resolved":"1.0.0"}}}}""";

        var result = await h.Service.ImportAsync("packages.lock.json", Encoding.UTF8.GetBytes(lockFile), SbomRole.BuildArtifact, null, null);

        result.Validation!.Format.Should().Be(SbomFormat.NuGetLockFile);
        result.Inventory!.Should().Match<InventorySummary>(i => i.SourceType == InventorySourceType.LockFile && i.Stage == InventoryStage.Resolved && i.Dependencies == 2 && i.Freshness == InventoryFreshness.Unknown);
    }

    [Fact]
    public async Task SourceAndSbomConflictIsShownWithBothSourcesAndTransitiveIsNotDrift()
    {
        var h = Create();
        var review = await SourceReview(h.Db, null, ("WolverineFx", "5.34.0"), ("Foo", "1.0.0"));
        var sbom = await ImportSbom(h, CycloneDxJson(components: [("WolverineFx", "5.33.0", "MIT", "direct"), ("Foo", "1.0.0", "MIT", "direct"), ("Bar", "2.1.0", "MIT", "transitive")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = InventorySources.SourceReviewInventoryId(review.RunId, "Repo"), ComparisonInventoryId = sbom.Id })).Run!;

        var cross = run.Comparisons.Single(c => c.Kind == ComparisonKind.SourceVsSbom);
        cross.Entries.Single(e => e.State == ComparisonState.EvidenceConflict).Should().Match<ComparisonEntry>(e => e.PackageName == "WolverineFx" && e.LeftValue == "5.34.0" && e.RightValue == "5.33.0");
        cross.Entries.Single(e => e.PackageName == "Bar").State.Should().Be(ComparisonState.PresentNotDeclaredDirectly);
        cross.Entries.Single(e => e.PackageName == "Bar").Detail.Should().Contain("not source drift");
        cross.Unchanged.Should().Be(1);
        run.Observations.Single(o => o.Kind == ObservationKind.EvidenceConflict).Evidence.Should().HaveCount(2);
        run.Items.Single(i => i.Dependency.PackageName == "WolverineFx").Conflicts.Single().Should().Contain("5.34.0").And.Contain("5.33.0");
        var stages = run.Items.Single(i => i.Dependency.PackageName == "WolverineFx").Stages;
        stages.Single(s => s.Stage == InventoryStage.Declared).Version.Should().Be("5.34.0");
        stages.Single(s => s.Stage == InventoryStage.Packaged).Version.Should().Be("5.33.0");
        stages.Single(s => s.Stage == InventoryStage.Deployed).State.Should().Be("Not assessed");
        stages.Single(s => s.Stage == InventoryStage.RuntimeObserved).State.Should().Be("Not assessed");
    }

    // ── Freshness, immutability, refresh ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreshnessIsStaleUnknownOrCurrentAndNamesItsThreshold()
    {
        var now = DateTimeOffset.Parse("2026-09-29T00:00:00Z");
        var old = new DependencyInventorySnapshot { Id = Guid.NewGuid(), SourceType = InventorySourceType.Sbom, SourceName = "a.json", CapturedAt = now.AddDays(-94), RecordedAt = now.AddDays(-94) };
        var recent = old with { Id = Guid.NewGuid(), SourceName = "b.json", CapturedAt = now.AddDays(-2) };
        var unknown = old with { Id = Guid.NewGuid(), CapturedAt = null };

        InventorySources.Freshness(old, [old], 30, now).Should().Match<(InventoryFreshness F, string D)>(f => f.F == InventoryFreshness.Stale && f.D.Contains("94 day(s)") && f.D.Contains("InventoryStaleAfterDays"));
        InventorySources.Freshness(recent, [recent], 30, now).Freshness.Should().Be(InventoryFreshness.Current);
        InventorySources.Freshness(unknown, [unknown], 30, now).Freshness.Should().Be(InventoryFreshness.Unknown);
        var sameSourceNewer = recent with { Id = Guid.NewGuid(), SourceName = "a.json" };
        InventorySources.Freshness(old with { CapturedAt = now.AddDays(-3) }, [old with { CapturedAt = now.AddDays(-3) }, sameSourceNewer], 30, now).Detail.Should().Contain("newer inventory of the same source");
    }

    [Fact]
    public async Task OpeningAnOldRunNeverRequeriesAndRefreshCreatesANewRun()
    {
        var h = Create();
        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null), ("1.2.0", true, null));
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.0.0", "MIT", "direct")]));
        var first = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;
        var calls = h.Registry.Calls.Count;

        h.Registry.Packages["Foo"] = Versions(("1.0.0", true, null), ("1.3.0", true, null)); // the registry changes "tomorrow"
        var reopened = await h.Service.GetAsync(first.RunId);

        h.Registry.Calls.Should().HaveCount(calls, "opening a stored run does not query the registry");
        reopened!.Items.Single().Registry.LatestStable.Should().Be("1.2.0");
        var (refreshed, _) = await h.Service.RefreshAsync(first.RunId);
        refreshed!.RunId.Should().NotBe(first.RunId);
        refreshed.RefreshOf.Should().Be(first.RunId);
        refreshed.Items.Single().Registry.LatestStable.Should().Be("1.3.0", "refresh bypasses the cache");
        refreshed.Items.Single().Registry.FromCache.Should().BeFalse();
        (await h.Service.GetAsync(first.RunId))!.Items.Single().Registry.LatestStable.Should().Be("1.2.0");
        (await h.Service.HistoryAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task LookupsAreDeduplicatedBoundedAndCachedWithOriginalRetrievalTime()
    {
        var h = Create(options: new DependencyHealthOptions { RegistryConcurrency = 2 });
        h.Registry.Delay = TimeSpan.FromMilliseconds(30);
        h.Registry.Default = Versions(("1.0.0", true, null));
        var components = Enumerable.Range(0, 12).Select(i => ($"Pkg{i}", (string?)"1.0.0", (string?)"MIT", (string?)"direct")).Append(("Pkg0", "1.0.1", "MIT", "direct")).ToArray();
        var inventory = await ImportSbom(h, CycloneDxJson(components: components));

        var first = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;
        var second = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        h.Registry.Calls.Should().HaveCount(12, "one lookup per distinct package name (Pkg0 appears in two versions), then served from cache");
        h.Registry.MaxConcurrent.Should().BeLessThanOrEqualTo(2);
        h.Advisories.Lookups.Should().Be(1);
        second.Items.Should().OnlyContain(i => i.Registry.FromCache);
        second.Items[0].Registry.RetrievedAt.Should().Be(first.Items[0].Registry.RetrievedAt, "a cached value keeps its original retrieval time");
    }

    [Fact]
    public async Task PrivatePackagesAreNeverSentToPublicSourcesAndAreNotCalledMissing()
    {
        var h = Create(options: new DependencyHealthOptions { PrivatePackagePatterns = ["M2LB.*"] });
        h.Registry.Default = Versions(("1.0.0", true, null));
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("M2LB.Common.Messaging", "1.0.0", null, "direct"), ("Serilog", "1.0.0", "Apache-2.0", "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        h.Registry.Calls.Should().Equal("Serilog");
        h.Advisories.Queried.Should().Equal("Serilog");
        var internalItem = run.Items.Single(i => i.Dependency.PackageName == "M2LB.Common.Messaging");
        internalItem.Registry.Should().Match<RegistryObservation>(r => r.State == RegistryState.NotAssessed && r.Detail.Contains("private registry metadata unavailable"));
        internalItem.Security.State.Should().Be(AdvisoryState.NotAssessed);
        run.Summary.RegistryNotFound.Should().Be(0);
        run.Limitations.Should().Contain(l => l.Contains("PrivatePackagePatterns (M2LB.*)"));
        (await Create().Service.ImportAsync("x.json", Encoding.UTF8.GetBytes(CycloneDxJson()), SbomRole.BuildArtifact, null, null)).Inventory.Should().NotBeNull();
    }

    [Fact]
    public async Task WithoutPrivatePatternsTheRunSaysNamesWereSentExternally()
    {
        var h = Create();
        h.Registry.Default = Versions(("1.0.0", true, null));
        var inventory = await ImportSbom(h, CycloneDxJson());

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Limitations.Should().Contain(l => l.Contains("were sent to nuget.org and OSV (osv.dev) as read-only lookups"));
    }

    // ── Automation runtime ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutomationRuntimeIsNotConfiguredWhileStaticConfigIsReportedSeparately()
    {
        var source = new AzureDevOpsRenovateAutomationSource(new HttpClient(new RouteHandler(_ => throw new InvalidOperationException("must not call"))), MsOptions.Create(new AzureDevOpsOptions()));
        var h = Create(automationSource: source);
        var review = await SourceReview(h.Db, "{}", ("Foo", "1.0.0"));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = InventorySources.SourceReviewInventoryId(review.RunId, "Repo") })).Run!;

        run.Automation.State.Should().Be(AutomationState.NotConfigured);
        DependencyHealthLabels.Automation(run.Automation.State).Should().Be("Not configured");
        run.Automation.StaticConfiguration.Should().Contain("Renovate configuration (static").And.Contain("Ready");
        run.Categories.Single(c => c.Name == "Automation").State.Should().Be(ReviewCategoryState.NotConfigured);
    }

    private static AzureDevOpsRenovateAutomationSource AdoSource(RouteHandler handler) =>
        new(new HttpClient(handler), MsOptions.Create(new AzureDevOpsOptions { Enabled = true, OrganizationUrl = "https://dev.azure.com/org", Project = "M2LB", Pat = "secret-pat-value" }));

    [Fact]
    public async Task RenovateLastRunAndOpenPullRequestsAreObservedWithoutInference()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-12).ToString("O");
        var handler = new RouteHandler(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/pullrequests") => JsonResponse($$"""{"value":[{"pullRequestId":17,"title":"Update dependency Serilog to v3.1.2","sourceRefName":"refs/heads/renovate/serilog-3.x","creationDate":"{{created}}","status":"active","mergeStatus":"succeeded"},{"pullRequestId":18,"title":"Update dependency Foo to 1.0.5 [SECURITY]","sourceRefName":"refs/heads/renovate/foo","creationDate":"{{created}}","status":"active"},{"pullRequestId":19,"title":"Feature","sourceRefName":"refs/heads/feature/x","status":"active"}]}"""),
            var p when p.EndsWith("/build/definitions") => JsonResponse("""{"value":[{"id":42,"name":"renovate-weekly"}]}"""),
            var p when p.EndsWith("/build/builds") => JsonResponse("""{"value":[{"id":3,"buildNumber":"20260928.1","result":"failed","startTime":"2026-09-28T02:00:00Z","finishTime":"2026-09-28T02:10:00Z"},{"id":2,"buildNumber":"20260921.1","result":"succeeded","startTime":"2026-09-21T02:00:00Z","finishTime":"2026-09-21T02:09:00Z"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var evidence = await AdoSource(handler).GetAsync("M2LB", null, default);

        evidence.State.Should().Be(AutomationState.Observed);
        evidence.LastRun!.Result.Should().Be("failed");
        evidence.LastSuccessfulRun!.Id.Should().Be("20260921.1");
        evidence.FailedRunsInWindow.Should().Be(1);
        evidence.Detail.Should().Contain("does not mean dependencies are current").And.Contain("no open PR does not mean no update exists");
        evidence.OpenPullRequests.Should().HaveCount(2);
        var serilog = evidence.OpenPullRequests.Single(p => p.Id == "17");
        serilog.Should().Match<DependencyPullRequest>(p => p.PackageName == "Serilog" && p.ToVersion == "3.1.2" && p.MergeStatus == "succeeded" && !p.SecurityMarked && p.Stale == null && p.AgeDays == 12);
        evidence.OpenPullRequests.Single(p => p.Id == "18").Should().Match<DependencyPullRequest>(p => p.SecurityMarked && p.MergeStatus == null);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        (await AdoSource(handler).GetAsync("M2LB", 7, default)).OpenPullRequests.Should().OnlyContain(p => p.Stale == true, "stale only with a configured threshold");
    }

    [Fact]
    public async Task OpenPullRequestsTakeCurrentVersionFromTheInventoryAndNeverImplySafety()
    {
        var h = Create(automation: new AutomationEvidence { State = AutomationState.Observed, Provider = "Azure DevOps", Detail = "1 open Renovate PR(s).",
            OpenPullRequests = [new DependencyPullRequest { Id = "17", Title = "Update dependency Foo to v2.0.0", PackageName = "Foo", ToVersion = "2.0.0", Status = "active", AgeDays = 3 }] });
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "down", [], DateTimeOffset.UtcNow);
        var inventory = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.4.0", "MIT", "direct")]));

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Automation.OpenPullRequests.Single().Should().Match<DependencyPullRequest>(p => p.FromVersion == "1.4.0" && p.UpdateType == DependencyUpdateType.Major && p.Stale == null && p.MergeStatus == null);
        run.Categories.Single(c => c.Name == "Automation").State.Should().Be(ReviewCategoryState.Ready);
        run.Observations.Should().NotContain(o => o.Category == CheckCategory.Automation);
    }

    [Fact]
    public async Task AdoUnauthorizedIsTypedAndNeverEchoesThePat()
    {
        var evidence = await AdoSource(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>sign in</html>") })).GetAsync("M2LB", null, default);

        evidence.State.Should().Be(AutomationState.Unauthorized);
        JsonSerializer.Serialize(evidence).Should().NotContain("secret-pat-value");
    }

    // ── Deployed evidence ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployedVersionMatchAndMismatchWithoutSeverity()
    {
        var h = Create();
        h.Registry.Default = new RegistryPackageResult(RegistryState.RegistryUnavailable, "down", [], DateTimeOffset.UtcNow);
        var expected = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.2.3", "MIT", "direct"), ("Bar", "2.0.0", "MIT", "direct"), ("Baz", "1.0.0", "MIT", "direct")]), name: "Build 42");
        var deployed = await ImportSbom(h, CycloneDxJson(components: [("Foo", "1.2.3", "MIT", "direct"), ("Bar", "1.9.0", "MIT", "direct"), ("Extra", "1.0.0", "MIT", "direct")]), SbomRole.DeployedArtifact, "qa", "QA deployed SBOM");

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = expected.Id, DeployedInventoryId = deployed.Id })).Run!;

        var comparison = run.Comparisons.Single(c => c.Kind == ComparisonKind.ExpectedVsDeployed);
        comparison.Entries.Single(e => e.PackageName == "Foo").State.Should().Be(ComparisonState.Matched);
        comparison.Entries.Single(e => e.PackageName == "Foo").Hash.Should().Be(HashComparison.Matched);
        comparison.Entries.Single(e => e.PackageName == "Bar").Should().Match<ComparisonEntry>(e => e.State == ComparisonState.DifferentVersion && e.Detail.Contains("Severity is not inferred"));
        comparison.Entries.Single(e => e.PackageName == "Baz").State.Should().Be(ComparisonState.MissingInDeployment);
        comparison.Entries.Single(e => e.PackageName == "Extra").State.Should().Be(ComparisonState.AdditionalInDeployment);
        run.Observations.Should().NotContain(o => o.Category == CheckCategory.Deployment);
        run.Items.Single(i => i.Dependency.PackageName == "Bar").Stages.Single(s => s.Stage == InventoryStage.Deployed).Version.Should().Be("1.9.0");
    }

    [Fact]
    public async Task BootManifestProvesPresenceNotVersionsOrAbsence()
    {
        var boot = """{"mainAssemblyName":"M2LB.Web","resources":{"assembly":{"MudBlazor.wasm":"sha256-abc","M2LB.Web.wasm":"sha256-def"}}}""";
        var handler = new RouteHandler(r => r.RequestUri!.AbsolutePath.EndsWith("blazor.boot.json") ? JsonResponse(boot) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><base href=\"/\" /></html>") });
        var (deployed, error) = await new BootManifestDeployedSource(new HttpClient(handler)).CaptureAsync("https://qa.example.test", "qa", default);

        error.Should().BeNull();
        deployed!.Dependencies.Should().OnlyContain(d => d.Version == null && d.Stage == InventoryStage.Deployed && d.RuntimeObserved == null);
        deployed.Dependencies.Single(d => d.PackageName == "MudBlazor").Hashes.Single().Value.Should().Be("abc");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);

        var expected = new DependencyInventorySnapshot { Id = Guid.NewGuid(), Name = "source", Dependencies = [
            new InventoryDependency { PackageName = "MudBlazor", Version = "7.0.0", PackageManager = "nuget" }, new InventoryDependency { PackageName = "Serilog", Version = "3.1.1", PackageManager = "nuget" }] };
        var comparison = InventoryComparer.ExpectedVsDeployed(expected, deployed);
        comparison.Entries.Should().OnlyContain(e => e.State == ComparisonState.NotComparable, "no versions and no proof of absence");
        comparison.Entries.Single(e => e.PackageName == "Serilog").Detail.Should().Contain("does not prove absence");
        (await new BootManifestDeployedSource(new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))).CaptureAsync("https://qa.example.test", null, default))
            .Error.Should().Contain("dotnet.js");
    }

    [Fact]
    public async Task SbomEntryIsNeverRuntimeLoaded()
    {
        var h = Create();
        var inventory = await ImportSbom(h, CycloneDxJson(), SbomRole.DeployedArtifact, "prod");

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        run.Inventory.Dependencies.Should().OnlyContain(d => d.RuntimeObserved == null);
        run.Items.SelectMany(i => i.Stages).Where(s => s.Stage == InventoryStage.RuntimeObserved).Should().OnlyContain(s => s.State == "Not assessed" && s.Version == null);
        run.Inventory.Limitations.Should().Contain(l => l.Contains("not proof that a component is loaded at runtime"));
        DependencyHealthLabels.Stage(InventoryStage.RuntimeObserved).Should().Be("Runtime loaded");
    }

    // ── Secrets ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderErrorsWithCredentialsAreRedactedEverywhere()
    {
        const string bearer = "eyJhbGciOiJIUzI1NiJ9.secretpayload";
        const string gitToken = "ghp_abcdefghijklmnopqrstuvwxyz0123";
        var h = Create(automation: new AutomationEvidence { State = AutomationState.ProviderUnavailable, Provider = "Azure DevOps", Detail = $"failed https://user:{gitToken}@dev.azure.com/org?token=abc123 Authorization: Bearer {bearer}" });
        h.Registry.Default = new RegistryPackageResult(RegistryState.Unauthorized, $"401 from feed; Authorization: Basic OnNlY3JldHBhdA== ; sig=supersecretsig", [], DateTimeOffset.UtcNow);
        var inventory = await ImportSbom(h, CycloneDxJson());

        var run = (await h.Service.RunAsync(new DependencyHealthRequest { InventoryId = inventory.Id })).Run!;

        var stored = (await h.Db.DependencyHealthRuns.SingleAsync()).ResultJson;
        foreach (var secret in new[] { bearer, gitToken, "OnNlY3JldHBhdA==", "supersecretsig", "abc123" })
        {
            stored.Should().NotContain(secret);
            JsonSerializer.Serialize(run).Should().NotContain(secret);
            string.Join("\n", h.Log.Lines).Should().NotContain(secret);
        }
        run.Items[0].Registry.Detail.Should().Contain("[redacted]");
        run.Automation.Detail.Should().Contain("[redacted]");
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijk", "abcdefghijk")]
    [InlineData("https://build:p4ssw0rd@pkgs.dev.azure.com/org/_packaging/feed", "p4ssw0rd")]
    [InlineData("pat=abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst", "abcdefghijklmnopqrstuvwxyz234567")]
    [InlineData("token glpat-abcdefghij123456 used", "glpat-abcdefghij123456")]
    public void RedactionRemovesCredentialShapes(string text, string secret) =>
        DependencyEvidenceRedaction.Redact(text).Should().NotContain(secret).And.Contain("[redacted]");
}
