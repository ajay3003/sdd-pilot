using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Dependencies;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DependencyReview;

public interface IDependencyReviewService
{
    /// <summary>Not exposed over HTTP (source ingestion belongs to Source Analysis): reviews archives through the same evidence bridge Source
    /// Analysis uses, so archive-based tests exercise exactly the snapshot pipeline.</summary>
    Task<(DependencyReviewResult? Result, string? Error)> RunAsync(string label, IReadOnlyList<(string FileName, byte[] Bytes)> archives,
        IReadOnlyList<(string Repository, string FileName, string Content)> configOverrides, CancellationToken ct = default);
    /// <summary>Reviews prepared sources (one per repository/snapshot) and stores the run with its source scope.</summary>
    Task<DependencyReviewResult> ReviewAsync(string label, IReadOnlyList<(RepositoryInput Input, SourceDependencyEvidence Evidence)> sources, DependencyReviewSourceScope? scope, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dependency / supply-chain review (Renovate as one evidence source) over uploaded repository archives, read in memory and never written
/// anywhere: no dependency file, config, branch, commit, push, PR or Renovate run. Each run is stored as an immutable snapshot (facts, hashes,
/// redacted normalized config, simulations); single-package simulations evaluate against that snapshot, not against newer config.
/// </summary>
public sealed class DependencyReviewService(AppDbContext db, ILogger<DependencyReviewService> logger) : IDependencyReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const long MaxArchiveBytes = 50 * 1024 * 1024;
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", ".vs"];

    public const string Mechanism = "BirkNext internal evaluator for an explicit subset of Renovate configuration semantics (offline, no Renovate run); presets are not expanded.";

    /// <summary>The repository name from the archive file name ("M2LB (1).zip" → "M2LB").</summary>
    public static string RepositoryName(string fileName) =>
        Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileName(fileName.Replace('\\', '/'))), @"\s*\(\d+\)$", "").Trim();

    /// <summary>Only files the review reads: dependency manifests, Renovate configs and pipeline YAML (for automation evidence).</summary>
    public static (List<RepositoryFile> Files, string? Error) ReadArchive(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) return ([], $"{fileName} is empty.");
        if (bytes.Length > MaxArchiveBytes) return ([], $"{fileName} is larger than the {MaxArchiveBytes / (1024 * 1024)} MB limit.");
        var files = new List<RepositoryFile>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0 || entry.Length > MaxFileBytes || segments[..^1].Any(s => Skipped.Contains(s.ToLowerInvariant()))) continue;
                if (!DependencyInventory.IsInventoryFile(path) && !RenovateConfig.IsConfigCandidate(path) && !path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;
                using var reader = new StreamReader(entry.Open());
                files.Add(new RepositoryFile(path, reader.ReadToEnd()));
            }
        }
        catch (InvalidDataException) { return ([], $"{fileName} is not a valid zip archive."); }
        // A download wrapped in one top folder (e.g. "M2LB/…") is read at that folder, so root config files are found where Renovate looks.
        var tops = files.Select(f => f.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        if (tops.Count == 1 && files.All(f => f.Path.Contains('/')))
            files = files.Select(f => f with { Path = f.Path[(tops[0].Length + 1)..] }).ToList();
        return (files, null);
    }

    public async Task<(DependencyReviewResult? Result, string? Error)> RunAsync(string label, IReadOnlyList<(string FileName, byte[] Bytes)> archives,
        IReadOnlyList<(string Repository, string FileName, string Content)> configOverrides, CancellationToken ct = default)
    {
        if (archives.Count == 0) return (null, "Provide at least one repository archive (.zip).");
        var sources = new List<(RepositoryInput, SourceDependencyEvidence)>();
        foreach (var (name, bytes) in archives)
        {
            var repository = RepositoryName(name);
            var (evidence, error) = SourceDependencyEvidenceExtractor.Extract(repository, bytes, name);
            if (evidence is null) return (null, error);
            var over = configOverrides.FirstOrDefault(o => string.Equals(o.Repository, repository, StringComparison.OrdinalIgnoreCase));
            sources.Add((SourceDependencyEvidenceExtractor.Input(repository, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), evidence, over.Content is null ? null : (over.FileName, over.Content)), evidence));
        }
        return (await ReviewAsync(label, sources, null, ct), null);
    }

    public async Task<DependencyReviewResult> ReviewAsync(string label, IReadOnlyList<(RepositoryInput Input, SourceDependencyEvidence Evidence)> sources, DependencyReviewSourceScope? scope, CancellationToken ct = default)
    {
        var previous = await LatestAsync(ct);
        var repositories = new List<RepositoryDependencyReview>();
        foreach (var (input, _) in sources)
        {
            var repository = input.Name;
            var review = DependencyReviewBuilder.Build(input, previous?.Repositories.FirstOrDefault(r => r.Repository == repository));
            repositories.Add(review);
            logger.LogInformation("Dependency review {Repository}: Renovate {Coverage}, {Files} config file(s), {Dependencies} dependencies, {Rules} rule(s), {Simulations} simulation(s), {Findings} finding(s).",
                repository, review.Coverage, review.ConfigFiles.Count, review.Dependencies.Count, review.Rules.Count, review.Simulations.Count, review.Findings.Count);
        }
        var result = new DependencyReviewResult
        {
            RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow, Label = string.IsNullOrWhiteSpace(label) ? string.Join(" + ", repositories.Select(r => r.Repository)) : label.Trim(),
            EvaluationMechanism = Mechanism, UnsupportedSemantics = [.. RenovatePolicyEvaluator.Unsupported], Repositories = repositories, Categories = Categories(repositories),
            SourceScope = scope, CrossSource = CrossSource(repositories), SourceRelationships = Relationships(sources),
        };
        db.DependencyReviewRuns.Add(new DependencyReviewRunRecord { Id = result.RunId, CompletedAt = result.CompletedAt, Label = result.Label, ResultJson = JsonSerializer.Serialize(result, Json) });
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// The same dependency (manager + package name) declared with different values in different selected sources. Values are compared as
    /// declared; equal values produce nothing. An observation to review — never a failure, vulnerability or incompatibility.
    /// </summary>
    public static List<CrossSourceObservation> CrossSource(IReadOnlyList<RepositoryDependencyReview> repositories)
    {
        if (repositories.Count < 2) return [];
        var result = new List<CrossSourceObservation>();
        foreach (var group in repositories.SelectMany(r => r.Dependencies.Select(d => (Repo: r.Repository, Dep: d)))
                     .GroupBy(x => (x.Dep.Manager, Name: x.Dep.PackageName.ToLowerInvariant())).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            var perRepo = group.GroupBy(x => x.Repo).Select(g => new CrossSourceValue(g.Key, string.Join(", ", g.Select(x => x.Dep.CurrentValue ?? "(no version)").Distinct().Order(StringComparer.Ordinal)),
                g.SelectMany(x => new[] { x.Dep.OwnerFile }.Concat(x.Dep.ReferencedBy)).Where(f => f.Length > 0).Distinct().Take(6).ToList())).ToList();
            if (perRepo.Count < 2 || perRepo.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var ranged = group.Any(x => x.Dep.IsRange || (x.Dep.CurrentValue?.Contains('*') ?? false));
            result.Add(new(ranged ? "Version range difference observed" : "Version difference observed", group.Key.Manager, group.First().Dep.PackageName, perRepo));
        }
        return result;
    }

    /// <summary>A selected source references a package another selected source declares it produces (exact package ID).</summary>
    public static List<SourceRelationship> Relationships(IReadOnlyList<(RepositoryInput Input, SourceDependencyEvidence Evidence)> sources)
    {
        var result = new List<SourceRelationship>();
        foreach (var (from, fromEvidence) in sources)
        foreach (var (to, toEvidence) in sources.Where(s => s.Input.Name != from.Name))
        foreach (var published in toEvidence.PublishedPackages)
        {
            var uses = fromEvidence.Dependencies.Where(d => d.Manager == "nuget" && string.Equals(d.PackageName, published.PackageId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (uses.Count == 0) continue;
            result.Add(new(from.Name, published.PackageId, uses.Select(d => d.CurrentValue ?? "(no version)").Distinct().Order(StringComparer.Ordinal).ToList(),
                uses.SelectMany(d => d.ReferencedBy.Count > 0 ? d.ReferencedBy : [d.OwnerFile]).Distinct().Count(), to.Name, published.Version));
        }
        return result;
    }

    public static List<ReviewCategory> Categories(List<RepositoryDependencyReview> repos)
    {
        var configured = repos.Where(r => r.Coverage is RenovateCoverage.Configured or RenovateCoverage.Inherited).ToList();
        var bearing = repos.Where(r => r.Dependencies.Count > 0).ToList();
        var missing = bearing.Where(r => r.Coverage == RenovateCoverage.Missing).ToList();
        var invalid = repos.Where(r => r.ConfigFiles.Any(f => f.Used && !f.SyntaxValid)).ToList();
        var needsReview = repos.SelectMany(r => r.Findings).Count(f => f.Severity == DependencyFindingSeverity.NeedsReview);
        var sims = repos.SelectMany(r => r.Simulations).ToList();
        return
        [
            new("Configuration", invalid.Count > 0 ? ReviewCategoryState.Issue : configured.Count == 0 ? ReviewCategoryState.Missing : ReviewCategoryState.Ready,
                invalid.Count > 0 ? $"Not parseable: {string.Join(", ", invalid.Select(r => r.Repository))}." : configured.Count == 0 ? "No Renovate configuration found."
                    : "Parsed; keys checked against BirkNext's supported subset. Validation against Renovate's full schema is not performed by BirkNext."),
            new("Coverage", missing.Count == 0 && bearing.Count > 0 ? ReviewCategoryState.Ready : missing.Count < bearing.Count ? ReviewCategoryState.Partial : ReviewCategoryState.Missing,
                missing.Count == 0 ? $"{configured.Count} of {repos.Count} repositor{(repos.Count == 1 ? "y" : "ies")} configured." : $"Missing for {string.Join(", ", missing.Select(r => r.Repository))}."),
            new("Policy", needsReview > 0 ? ReviewCategoryState.NeedsReview : configured.Count == 0 ? ReviewCategoryState.NotAssessed
                    : configured.Any(r => r.UnresolvedPresets.Count > 0) ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                (needsReview > 0 ? $"{needsReview} rule/policy observation(s) need review." : configured.Count == 0 ? "No configuration to evaluate." : "No rule observations.")
                    + (configured.Any(r => r.UnresolvedPresets.Count > 0) ? " Presets are not expanded, so the policy result is partial." : "")),
            new("Simulation", sims.Count == 0 ? ReviewCategoryState.NotAssessed : sims.Any(s => s.Limitations.Count > 0 || s.Result == PolicyResult.NotAssessable) ? ReviewCategoryState.Partial : ReviewCategoryState.Ready,
                sims.Count == 0 ? "No policy simulation." : $"{sims.Count} synthetic scenario(s); {sims.Count(s => s.Result == PolicyResult.NotAssessable)} not assessable."),
            new("Security-update policy", repos.Any(r => r.SecurityUpdatePolicy.State == ReviewCategoryState.Partial) ? ReviewCategoryState.Partial : ReviewCategoryState.NotConfigured,
                string.Join(" ", repos.Select(r => $"{r.Repository}: {DependencyLabels.Category(r.SecurityUpdatePolicy.State)}."))),
            new("Runtime automation", ReviewCategoryState.NotAssessed, "Renovate runs, open PRs and failures are not read by the source review (see Automation status in the dependency health review). Pipeline definitions in source are listed per repository."),
        ];
    }

    private async Task<DependencyReviewResult?> LatestAsync(CancellationToken ct)
    {
        var json = await db.DependencyReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<DependencyReviewResult>(json, Json);
    }

    public async Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default)
    {
        var records = await db.DependencyReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(20).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<DependencyReviewResult>(r.ResultJson, Json)).OfType<DependencyReviewResult>()
            .Select(r => new DependencyReviewRunSummary(r.RunId, r.CompletedAt, r.Label, r.Repositories.Count, r.Repositories.Sum(x => x.Findings.Count))).ToList();
    }

    public async Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var record = await db.DependencyReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return record is null ? null : JsonSerializer.Deserialize<DependencyReviewResult>(record.ResultJson, Json);
    }

    /// <summary>One synthetic scenario against the run's stored (redacted) config — nothing is read from or written to a repository.</summary>
    public async Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default)
    {
        if (await GetAsync(request.RunId, ct) is not { } run) return (null, "Review run not found.");
        if (run.Repositories.FirstOrDefault(r => r.Repository == request.Repository) is not { } repo) return (null, "Repository not in this review run.");
        if (repo.NormalizedConfig is null) return (null, "This repository has no parsed Renovate configuration to simulate against.");
        if (string.IsNullOrWhiteSpace(request.PackageName) || string.IsNullOrWhiteSpace(request.CurrentVersion) || string.IsNullOrWhiteSpace(request.CandidateVersion))
            return (null, "Package, current version and synthetic candidate are required.");
        var config = JsonNode.Parse(repo.NormalizedConfig) as JsonObject ?? [];
        var known = repo.Dependencies.FirstOrDefault(d => string.Equals(d.PackageName, request.PackageName, StringComparison.OrdinalIgnoreCase) && d.Manager == request.Manager
            && (request.File is null || d.OwnerFile == request.File));
        var dep = (known ?? new DeclaredDependency { Repository = repo.Repository, Manager = request.Manager, Datasource = request.Manager == "nuget" ? "nuget" : "docker", PackageName = request.PackageName.Trim(), OwnerFile = request.File ?? "" })
            with { CurrentValue = request.CurrentVersion.Trim(), IsRange = request.CurrentVersion.IndexOfAny(['[', '(', ',', '*']) >= 0 };
        var evaluator = new RenovatePolicyEvaluator(config, repo.UnresolvedPresets);
        var simulation = evaluator.Simulate(dep with { IgnoredBy = null }, "Custom", request.CandidateVersion.Trim(), null);
        logger.LogInformation("Dependency policy simulation {Repository}: {Package} ({Manager}) {Current} → synthetic {Candidate}: {Type}, {Result}, rules {Rules}.",
            repo.Repository, dep.PackageName, dep.Manager, dep.CurrentValue, request.CandidateVersion, simulation.UpdateType, simulation.Result, string.Join(",", simulation.MatchedRules.Select(m => m.RuleIndex)));
        return (simulation, null);
    }
}
