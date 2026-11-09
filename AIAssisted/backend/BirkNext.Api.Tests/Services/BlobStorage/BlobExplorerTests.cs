using System.Reflection;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.BlobStorage;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.BlobStorage;
using BirkNext.ExecutionEvidence;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirkNext.Api.Tests.Services.BlobStorage;

/// <summary>
/// Blob explorer against a SYNTHETIC LOCAL store (a temporary folder; never Azure): listing with virtual folders and continuation, path safety,
/// redacted metadata, bounded previews per format (JSON, XML without DTD, CSV with formula safety, NDJSON with malformed lines, Parquet through the
/// shared engine, binary metadata only), expectation checks by level, correlation by exact execution id only, observations without content, and
/// an API surface without any write operation.
/// </summary>
public sealed class BlobExplorerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-blob-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db;
    private readonly BlobExplorerService _service;
    private static readonly Guid Run = Guid.Parse("11111111-aaaa-bbbb-cccc-555555555555");

    public BlobExplorerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "skolenaervaer-out", "2026", "11"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        Write("skolenaervaer-out/2026/11/report-1.json", """{"schoolId":"s-1","fodselsnummer":"01010112345","name":"Synthetic","absences":[1,2]}""",
            new() { ["X-BirkNext-Execution-Id"] = $"{Run:N}.1.1", ["owner"] = "test", ["token"] = "secret-value" });
        Write("skolenaervaer-out/2026/11/report.xml", "<Report><School>s-1</School><Fodselsnummer>01010112345</Fodselsnummer></Report>");
        Write("skolenaervaer-out/2026/11/rows.csv", "id;name;formula\n1;A;=1+1\n2;B;x\n");
        Write("skolenaervaer-out/2026/11/events.ndjson", "{\"id\":1,\"kind\":\"a\"}\nnot json\n{\"id\":2,\"other\":true}\n");
        Write("skolenaervaer-out/2026/11/blob.bin", "\u0000\u0001\u0002");
        Write("skolenaervaer-out/readme.txt", "contact person@example.test");
        Write("other/x.json", "{}");
        File.Copy(Path.Combine(FixtureDir(), "children_v1.parquet"), Path.Combine(_root, "skolenaervaer-out", "2026", "11", "children.parquet"));
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"blob-{Guid.NewGuid():N}").Options);
        var options = Options.Create(new BlobStorageOptions
        {
            Stores = [new() { Id = "synthetic", EnvironmentId = "env-a", Name = "Synthetic store", Kind = BlobStoreKind.SyntheticLocal, Root = _root, Containers = ["skolenaervaer-out"] },
                      new() { Id = "azure", EnvironmentId = "env-a", Kind = BlobStoreKind.AzureStorageAccount, AccountUrl = "https://synthetic.blob.core.windows.net" }],
        });
        _service = new BlobExplorerService(options, new Host("Development"), _db, new ExecutionEvidenceStore(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string name, string content, Dictionary<string, string>? metadata = null)
    {
        var path = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(path, content);
        if (metadata is not null) File.WriteAllText(path + ".metadata.json", System.Text.Json.JsonSerializer.Serialize(metadata));
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "backend", "BirkNext.Api.Tests", "Services", "Parquet", "Fixtures");
    }

    private sealed class Host(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Stores_report_readiness_and_label_the_synthetic_store_as_not_Azure()
    {
        var stores = _service.Stores("env-a");
        stores.Single(s => s.Id == "synthetic").Should().Match<BlobStoreView>(s => s.Readiness == BlobStoreReadiness.Ready && s.Detail.Contains("not Azure") && s.Endpoint == "synthetic://synthetic");
        stores.Single(s => s.Id == "azure").Readiness.Should().Be(BlobStoreReadiness.Disabled, "no Azure identity is configured");
        _service.Stores("env-other").Should().BeEmpty();
        var production = new BlobExplorerService(Options.Create(new BlobStorageOptions { Stores = [new() { Id = "s", EnvironmentId = "e", Kind = BlobStoreKind.SyntheticLocal, Root = _root }] }), new Host("Production"), _db, new ExecutionEvidenceStore(_db));
        production.Stores("e").Single().Readiness.Should().Be(BlobStoreReadiness.Disabled);
    }

    [Fact]
    public async Task Listing_shows_virtual_folders_pages_with_continuation_and_respects_the_allowed_containers()
    {
        (await _service.ContainersAsync("env-a", "synthetic", default)).Containers!.Select(c => c.Name).Should().Equal("skolenaervaer-out");
        var top = (await _service.ListAsync("env-a", "synthetic", "skolenaervaer-out", null, null, 100, default)).Listing!;
        top.Items.Select(i => (i.Name, i.IsPrefix)).Should().Equal(("2026/", true), ("readme.txt", false));
        var first = (await _service.ListAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/", null, 3, default)).Listing!;
        first.Items.Should().HaveCount(3);
        first.ContinuationToken.Should().NotBeNull();
        first.Items.Should().NotContain(i => i.Name.EndsWith(".metadata.json"));
        var rest = (await _service.ListAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/", first.ContinuationToken, 100, default)).Listing!;
        first.Items.Concat(rest.Items).Select(i => i.Name).Should().OnlyHaveUniqueItems().And.HaveCount(6);
        rest.ContinuationToken.Should().BeNull();
        (await _service.ListAsync("env-a", "synthetic", "other", null, null, 10, default)).Error.Should().Contain("allowed list");
    }

    [Theory]
    [InlineData("../other/x.json")]
    [InlineData("2026/../../other/x.json")]
    [InlineData("..\\other\\x.json")]
    [InlineData("2026/11/report-1.json.metadata.json")]
    public async Task Names_cannot_escape_the_container(string name) =>
        (await _service.PropertiesAsync("env-a", "synthetic", "skolenaervaer-out", name, default)).Error.Should().Contain("Invalid blob name");

    [Fact]
    public async Task Metadata_is_redacted_and_previews_are_bounded_and_sanitized_per_format()
    {
        var props = (await _service.PropertiesAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/report-1.json", default)).Properties!;
        props.Metadata["token"].Should().Be("[redacted]");
        props.Metadata["owner"].Should().Be("test");
        var json = (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/report-1.json", default)).Preview!;
        json.Should().Match<BlobPreview>(p => p.Kind == BlobPreviewKind.Json && p.SyntaxValid == true && p.Complete && p.Columns.Contains("absences"));
        json.Text.Should().Contain("\"schoolId\": \"s-1\"").And.NotContain("01010112345");
        var xml = (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/report.xml", default)).Preview!;
        xml.Should().Match<BlobPreview>(p => p.SyntaxValid == true && p.Columns.SequenceEqual(new[] { "School", "Fodselsnummer" }));
        xml.Text.Should().NotContain("01010112345");
        xml.Diagnostics.Should().Contain(d => d.Contains("no XSD applied"));
        var csv = (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/rows.csv", default)).Preview!;
        csv.Should().Match<BlobPreview>(p => p.Columns.SequenceEqual(new[] { "id", "name", "formula" }) && p.RecordsRead == 2 && p.SyntaxValid == true);
        csv.Text.Should().Contain("'=1+1");
        var nd = (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/events.ndjson", default)).Preview!;
        nd.Should().Match<BlobPreview>(p => p.SyntaxValid == false && p.RecordsRead == 3 && p.Columns.SequenceEqual(new[] { "id", "kind", "other" }));
        nd.Diagnostics.Should().Contain(d => d.Contains("malformed line(s): 2"));
        var parquet = (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/children.parquet", default)).Preview!;
        parquet.Should().Match<BlobPreview>(p => p.Kind == BlobPreviewKind.Parquet && p.ParquetRows == 1000 && p.Rows!.Count == BlobExplorerService.PreviewRows && p.Columns.Contains("registered_at"));
        (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "2026/11/blob.bin", default)).Preview!.Should().Match<BlobPreview>(p => p.Kind == BlobPreviewKind.Binary && p.Text == null);
        (await _service.PreviewAsync("env-a", "synthetic", "skolenaervaer-out", "readme.txt", default)).Preview!.Text.Should().NotContain("person@example.test");
    }

    [Fact]
    public void Xml_with_a_DTD_is_refused_and_a_partial_read_is_never_judged()
    {
        var xxe = BlobExplorerService.Build(BlobPreviewKind.Xml, "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>"u8.ToArray(), complete: true);
        xxe.SyntaxValid.Should().BeFalse();
        xxe.Text.Should().NotContain("root:");
        var partial = BlobExplorerService.Build(BlobPreviewKind.Json, "{\"a\": [1, 2"u8.ToArray(), complete: false);
        partial.SyntaxValid.Should().BeNull("a truncated read is never called valid or invalid");
        partial.Truncated.Should().BeTrue();
        var partialParquet = BlobExplorerService.Build(BlobPreviewKind.Parquet, File.ReadAllBytes(Path.Combine(FixtureDir(), "truncated.parquet")), complete: true);
        partialParquet.SyntaxValid.Should().BeFalse();
    }

    private async Task SeedRunAsync() => await new ExecutionEvidenceStore(_db).CreateAsync(
        new ExecutionRunSummary { RunId = Run, Name = "report", EnvironmentId = "env-a", StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed },
        [new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 1, Definition = new ExecutionCallDefinition { Name = "POST /reports" }, Attempts = [new ExecutionAttempt { AttemptNumber = 1, StartedAt = DateTimeOffset.UtcNow }] }], default);

    [Fact]
    public async Task An_expected_artifact_is_checked_level_by_level_and_correlated_only_by_an_execution_id()
    {
        await SeedRunAsync();
        var expectation = new BlobExpectation { Container = "skolenaervaer-out", Path = "2026/11/report-1.json", Format = BlobPreviewKind.Json, ContentType = "application/json", RequiredFields = ["schoolId", "absences"], RunId = Run };
        var v = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", expectation, false), "tester", default)).Validation!;
        v.Checks.Select(c => (c.Level, c.Outcome)).Should().Equal((BlobCheckLevel.Exists, BlobCheckOutcome.Pass), (BlobCheckLevel.Metadata, BlobCheckOutcome.Pass), (BlobCheckLevel.Readable, BlobCheckOutcome.Pass),
            (BlobCheckLevel.Syntax, BlobCheckOutcome.Pass), (BlobCheckLevel.Content, BlobCheckOutcome.Pass), (BlobCheckLevel.Correlation, BlobCheckOutcome.Pass));
        v.Correlation.Should().Be(BlobCorrelationState.Confirmed);
        v.CorrelatedRunId.Should().Be(Run);

        var uncorrelated = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", expectation with { Path = "2026/11/report.xml", Format = BlobPreviewKind.Xml, ContentType = null, RequiredFields = ["School"] }, false), "tester", default)).Validation!;
        uncorrelated.Correlation.Should().Be(BlobCorrelationState.NotCorrelated);
        uncorrelated.Checks.Single(c => c.Level == BlobCheckLevel.Correlation).Detail.Should().Contain("not proof");

        var missing = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", expectation with { Path = "2026/11/none.json" }, false), "tester", default)).Validation!;
        missing.Checks.Should().ContainSingle().Which.Outcome.Should().Be(BlobCheckOutcome.Fail);
        var window = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", expectation with { Path = "2026/11/", NotBefore = DateTimeOffset.UtcNow.AddHours(1) }, false), "tester", default)).Validation!;
        window.Checks.Single().Detail.Should().Contain("newer than");
        var fields = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", expectation with { RequiredFields = ["schoolId", "studentCount"] }, false), "tester", default)).Validation!;
        fields.Checks.Single(c => c.Level == BlobCheckLevel.Content).Should().Match<BlobCheck>(c => c.Outcome == BlobCheckOutcome.Fail && c.Detail.Contains("studentCount"));
        var parquet = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", new BlobExpectation { Container = "skolenaervaer-out", Path = "2026/11/children.parquet", RequiredFields = ["id", "status"], ExpectedRecords = 1000 }, false), "tester", default)).Validation!;
        parquet.Checks.Single(c => c.Level == BlobCheckLevel.Content).Outcome.Should().Be(BlobCheckOutcome.Pass);
    }

    [Fact]
    public async Task A_prefix_expectation_searches_every_level_below_the_prefix_while_browsing_stays_one_level()
    {
        // Regression (Blob acceptance BS-35): an application writes into dated sub-folders, so "2026/" must find 2026/11/... blobs.
        var newest = Path.Combine(_root, "skolenaervaer-out", "2026", "11", "rows.csv");
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(5));
        var v = (await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", new BlobExpectation { Container = "skolenaervaer-out", Path = "2026/" }, false), "tester", default)).Validation!;
        v.Checks[0].Outcome.Should().Be(BlobCheckOutcome.Pass, v.Checks[0].Detail);
        v.BlobName.Should().Be("2026/11/rows.csv");
        v.Checks[0].Detail.Should().StartWith("6 blob(s) under the prefix");

        var top = (await _service.ListAsync("env-a", "synthetic", "skolenaervaer-out", "2026/", null, 50, default)).Listing!;
        top.Items.Should().ContainSingle().Which.Should().Match<BlobItemView>(i => i.IsPrefix && i.Name == "2026/11/");
    }

    [Fact]
    public async Task Observations_keep_identity_checks_and_a_hash_but_no_content_and_deleting_one_never_touches_the_blob()
    {
        await SeedRunAsync();
        var (_, saved, error) = await _service.ValidateAsync(new BlobValidateRequest("env-a", "synthetic", new BlobExpectation { Container = "skolenaervaer-out", Path = "2026/11/report-1.json", RunId = Run }, true), "tester", default);
        error.Should().BeNull();
        saved!.Should().Match<BlobObservation>(o => o.StoreKind == BlobStoreKind.SyntheticLocal && o.WholeBlobRead && o.Sha256OfBytesRead!.Length == 64 && o.Metadata["token"] == "[redacted]");
        var stored = (await _db.BlobObservations.SingleAsync()).DocumentJson;
        stored.Should().NotContain("01010112345").And.NotContain("secret-value").And.NotContain("Synthetic\"");
        (await _service.DeleteObservationAsync("env-a", saved.Id, default)).Should().BeTrue();
        File.Exists(Path.Combine(_root, "skolenaervaer-out", "2026", "11", "report-1.json")).Should().BeTrue("deleting an observation never deletes the blob");
    }

    [Fact]
    public void Neither_the_API_nor_the_stores_can_write()
    {
        typeof(IBlobReadStore).GetMethods().Select(m => m.Name).Should().BeEquivalentTo("ContainersAsync", "ListAsync", "PropertiesAsync", "ReadAsync");
        var actions = typeof(BlobStorageController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToList();
        actions.Where(a => a.GetCustomAttribute<HttpDeleteAttribute>() is not null).Should().ContainSingle().Which.Name.Should().Be("DeleteObservation");
        actions.Single(a => a.Name == "Save").GetCustomAttributes<AuthorizeAttribute>().Should().Contain(a => a.Policy == BirkNextPermissions.IntegrationConfigurationWritePolicy);
        var source = File.ReadAllText(Path.Combine(FixtureDir(), "..", "..", "..", "..", "BirkNext.Api", "Services", "BlobStorage", "BlobReadStores.cs"));
        foreach (var write in new[] { "UploadAsync", "DeleteAsync", "DeleteIfExists", "SetMetadata", "SetAccessTier", "CreateSnapshot", "AcquireLease", "GenerateSas", "StartCopyFromUri", "SetHttpHeaders", "CreateIfNotExists", "File.Write", "File.Delete" })
            source.Should().NotContain(write, write);
    }
}
