using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// The state of one evidence role, judged on THAT role's capability only (never a package-global status). Ready = bound evidence satisfies the
/// role. Partial = bound, but this capability's own extraction is incomplete. Suggested = unbound, exactly one compatible package (shown, never
/// bound silently). NeedsDecision = unbound, several compatible packages (never the newest). Missing = required and nothing compatible.
/// NotProvided = optional and nothing compatible (neutral). Unavailable = bound, but the import is not in this workspace. Conflict = a
/// single-source role holds several bound sources (never resolved silently).
/// </summary>
public enum EvidenceRoleState { Ready, Partial, Suggested, NeedsDecision, Missing, NotProvided, Unavailable, Conflict }

public sealed record EvidenceCandidate(ProjectEvidencePackage Package, string Why, bool Bound);

public sealed record EvidenceRoleRow(
    EnvironmentProjectEvidenceBinding Role,
    EvidenceRoleState State,
    IReadOnlyList<ProjectEvidencePackage> Bound,
    IReadOnlyList<ProjectEvidenceSourceReference> UnavailableSources,
    IReadOnlyList<EvidenceCandidate> Candidates)
{
    public bool IsRequired => Role.Requirement == ProjectEvidenceRequirement.Required;
    /// <summary>Bound: a source has been selected and is usable (Ready or Partial). Bound is not Ready — see <see cref="IsReady"/>.</summary>
    public bool IsResolved => State is EvidenceRoleState.Ready or EvidenceRoleState.Partial;
    public bool IsBound => IsResolved;
    /// <summary>Ready: the selected evidence satisfies the role.</summary>
    public bool IsReady => State == EvidenceRoleState.Ready;
    /// <summary>Required evidence that is not bound: project evidence readiness is incomplete. Optional roles never block.</summary>
    public bool Blocks => IsRequired && !IsResolved;

    /// <summary>For a Partial role: what is present, what is incomplete and what resolves it (role-specific — never "the package is partial").</summary>
    public string? PartialDetail => State != EvidenceRoleState.Partial ? null : string.Join(" ", Bound.Select(b =>
    {
        var capability = Role.Capability == ProjectEvidenceCapability.SupportingSource
            ? b.Capability(ProjectEvidenceCapability.SupportingSource) ?? b.Capability(ProjectEvidenceCapability.ApplicationSource) : b.Capability(Role.Capability);
        return capability is null ? $"{b.ArchiveFileName}: this capability is not available."
            : capability.Status == PackageCapabilityStatus.Ready ? null
            : $"{b.ArchiveFileName}: present — {capability.Fact}; incomplete — Source Analysis reports this capability as {capability.Status.ToString().ToLowerInvariant()}. Review it in Source Analysis or re-analyze the source.";
    }).OfType<string>());

    /// <summary>How a source was bound ("Bound from Project setup", "Bound manually", …): provenance is never hidden.</summary>
    public static string OriginLabel(ProjectEvidenceSourceReference source) => source.BoundBy switch
    {
        "ProjectSelection" => "Bound from Project setup",
        "User" => "Bound manually",
        "TestPack" => "Suggested by Test Pack",
        "Iqr" => "Confirmed by IQR",
        "Detected" => "Detected from imported evidence",
        var other => $"Bound by {other}",
    };

    public string StatusLabel => State switch
    {
        EvidenceRoleState.Ready => "Ready",
        EvidenceRoleState.Partial => "Partial",
        EvidenceRoleState.Suggested => "Suggested",
        EvidenceRoleState.NeedsDecision => "Needs decision",
        EvidenceRoleState.Missing => "Missing",
        EvidenceRoleState.Unavailable => "Unavailable in this workspace",
        EvidenceRoleState.Conflict => "Conflict · review required",
        _ => IsRequired ? "Missing" : "Optional · not provided",
    };

    public string Tone => State switch
    {
        EvidenceRoleState.Ready => "available",
        EvidenceRoleState.Partial or EvidenceRoleState.Suggested or EvidenceRoleState.NeedsDecision or EvidenceRoleState.Unavailable => "partial",
        EvidenceRoleState.Missing or EvidenceRoleState.Conflict => "rejected",
        _ => "neutral",
    };

    public string RequirementLabel => IsRequired ? "Required" : "Optional";
}

/// <summary>
/// A Test Pack logical source activated for this project. Its binding stays the canonical IQR evidence binding (not copied here). When the
/// project already binds a snapshot the pack recognizes as this source, the role is satisfied by project evidence: nobody is asked to choose
/// the same source again.
/// </summary>
public sealed record TestPackEvidenceRow(string PackId, string PackName, TestPackSourceStatus Status, bool ProjectBound = false)
{
    public bool IsRequired => Status.Role.Requirement == TestPackSourceRequirement.Required;
    public bool Blocks => IsRequired && !ProjectBound && Status.State is TestPackSourceState.Missing or TestPackSourceState.ReviewRequired or TestPackSourceState.AvailableNotBound;
    public bool IsReady => ProjectBound || Status.State is TestPackSourceState.Bound or TestPackSourceState.ReferenceAvailable;
}

/// <summary>One independent status dimension of the project's evidence (Part Z): never folded into one universal "Ready".</summary>
public sealed record EvidenceDimension(string Key, string Label, string Value, string Detail, string Tone);

/// <summary>Target Environment → Project, resolved: the project, every role with its state and candidates, activated Test Pack roles.</summary>
public sealed record ProjectEvidenceView(
    string? EnvironmentId,
    string? EnvironmentName,
    EnvironmentProject? Project,
    ProjectEvidencePackage? Main,
    IReadOnlyList<EvidenceRoleRow> Rows,
    IReadOnlyList<TestPackEvidenceRow> TestPackRows,
    IReadOnlyList<ProjectEvidencePackage> Packages,
    IReadOnlyList<(string PackId, string PackName, string Reason)> SuggestedTestPacks)
{
    public bool IsConfigured => Project is not null;
    public bool MainAvailable => Main is not null;
    public string ProjectName => Project?.ProjectName ?? "";

    public int RequiredTotal => Rows.Count(r => r.IsRequired) + TestPackRows.Count(r => r.IsRequired);
    /// <summary>Required roles with a selected source (bound) — not the same as ready.</summary>
    public int RequiredResolved => Rows.Count(r => r.IsRequired && r.IsResolved) + TestPackRows.Count(r => r.IsRequired && !r.Blocks);
    public int RequiredBound => RequiredResolved;
    /// <summary>Required roles whose selected evidence satisfies the role.</summary>
    public int RequiredReady => Rows.Count(r => r.IsRequired && r.IsReady) + TestPackRows.Count(r => r.IsRequired && r.IsReady);
    public int RequiredPartial => Rows.Count(r => r.IsRequired && r.State == EvidenceRoleState.Partial);
    public int RequiredMissing => Rows.Count(r => r.IsRequired && r.State is EvidenceRoleState.Missing or EvidenceRoleState.Unavailable or EvidenceRoleState.Suggested)
        + TestPackRows.Count(r => r.Blocks);
    public int NeedsDecision => Rows.Count(r => r.State == EvidenceRoleState.NeedsDecision);
    public int Conflicts => Rows.Count(r => r.State == EvidenceRoleState.Conflict);
    /// <summary>Ready only when every required role is ready (bound AND satisfied) and nothing conflicts — never merely "everything selected".</summary>
    public bool RequiredReadyAll => RequiredReady == RequiredTotal && Conflicts == 0;

    /// <summary>"2 required roles partial", "1 required role missing", "1 role needs decision", "1 conflict" — the compact problems summary.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
            var problems = new List<string>();
            if (Conflicts > 0) problems.Add(N(Conflicts, "role has a conflict", "roles have conflicts"));
            if (RequiredMissing > 0) problems.Add(N(RequiredMissing, "required role missing", "required roles missing"));
            if (NeedsDecision > 0) problems.Add(N(NeedsDecision, "role needs decision", "roles need decision"));
            if (RequiredPartial > 0) problems.Add(N(RequiredPartial, "required role partial", "required roles partial"));
            return problems;
        }
    }

    /// <summary>When no project is configured: the only application/system candidate, proposed (never persisted) — null when none or several.</summary>
    public ProjectEvidencePackage? SuggestedProject => !IsConfigured && Packages.Count(p => p.IsApplicationCandidate) == 1 ? Packages.First(p => p.IsApplicationCandidate) : null;
    public IReadOnlyList<ProjectEvidencePackage> ProjectCandidates => [.. Packages.Where(p => p.IsApplicationCandidate)];
    public int OptionalTotal => Rows.Count(r => !r.IsRequired) + TestPackRows.Count(r => !r.IsRequired);
    public int OptionalAvailable => Rows.Count(r => !r.IsRequired && r.IsResolved)
        + TestPackRows.Count(r => !r.IsRequired && r.Status.State is TestPackSourceState.Bound or TestPackSourceState.Available);

    /// <summary>Required evidence not resolved, in table order (project roles, then activated Test Pack roles).</summary>
    public IReadOnlyList<string> BlockingLabels => [.. Rows.Where(r => r.Blocks).Select(r => r.Role.Label), .. TestPackRows.Where(r => r.Blocks).Select(r => r.Status.Role.Label)];

    public EvidenceRoleRow? Row(string roleKey) => Rows.FirstOrDefault(r => string.Equals(r.Role.RoleKey, roleKey, StringComparison.Ordinal));
    public EvidenceRoleRow? Documentation => Row(ProjectEvidenceRoles.Documentation);

    /// <summary>"6 / 6 required bound · 4 ready · 2 partial · 3 optional available": bound and ready are reported separately.</summary>
    public string Summary
    {
        get
        {
            var missing = RequiredTotal - RequiredResolved;
            return $"{RequiredBound} / {RequiredTotal} required bound · {RequiredReady} ready{(RequiredPartial > 0 ? $" · {RequiredPartial} partial" : "")}"
                + $"{(missing > 0 ? $" · {missing} unresolved" : "")} · {OptionalAvailable} optional available";
        }
    }
}

/// <summary>The default evidence roles of a project and stable role keys. Domain roles come from Test Packs, never from names here.</summary>
public static class ProjectEvidenceRoles
{
    public const string ApplicationSource = "application-source", Documentation = "documentation", Contracts = "contracts",
        Infrastructure = "infrastructure", SupportingSource = "supporting-source", GeneratedDocumentation = "generated-docs";

    public static IReadOnlyList<EnvironmentProjectEvidenceBinding> Defaults() =>
    [
        Role(ApplicationSource, "Application source", ProjectEvidenceCapability.ApplicationSource, ProjectEvidenceRequirement.Required, ProjectEvidenceCardinality.Single,
            "Project definition (main application)"),
        Role(Documentation, "Project documentation", ProjectEvidenceCapability.Documentation, ProjectEvidenceRequirement.Required, ProjectEvidenceCardinality.Multiple,
            "Recommended Workflow document reviews"),
        Role(Contracts, "Contracts and schemas", ProjectEvidenceCapability.ContractSchema, ProjectEvidenceRequirement.Optional, ProjectEvidenceCardinality.Multiple,
            "Schema Studio and contract-source discovery"),
        Role(Infrastructure, "Infrastructure", ProjectEvidenceCapability.Infrastructure, ProjectEvidenceRequirement.Optional, ProjectEvidenceCardinality.Multiple,
            "Infrastructure and configuration reviews"),
        Role(SupportingSource, "Supporting/shared source", ProjectEvidenceCapability.SupportingSource, ProjectEvidenceRequirement.Optional, ProjectEvidenceCardinality.Multiple,
            "Integration discovery and source reviews"),
        Role(GeneratedDocumentation, "Generated documentation", ProjectEvidenceCapability.GeneratedDocumentation, ProjectEvidenceRequirement.Optional, ProjectEvidenceCardinality.Single,
            "Generated Documentation Health and Documentation Drift"),
    ];

    public static EnvironmentProjectEvidenceBinding Role(string key, string label, ProjectEvidenceCapability capability, ProjectEvidenceRequirement requirement,
        ProjectEvidenceCardinality cardinality, string requiredBy, ProjectEvidenceRoleOrigin origin = ProjectEvidenceRoleOrigin.ProjectDefinition) =>
        new() { RoleKey = key, Label = label, Capability = capability, Requirement = requirement, Cardinality = cardinality, RequiredBy = requiredBy, Origin = origin };
}

/// <summary>
/// Pure resolution of Target Environment → Project. Discover candidates once, bind explicitly, and let every consumer reuse the bindings:
/// a bound reference is authoritative; an unbound role is only ever Suggested (one compatible package) or Needs decision (several) — never the
/// newest package or snapshot.
/// </summary>
public static class ProjectEvidenceResolver
{
    public static ProjectEvidenceView Resolve(string? environmentId, string? environmentName, EnvironmentProject? project,
        IReadOnlyList<ProjectEvidencePackage> packages, SourceSetupView? setup)
    {
        if (project is null)
            return new(environmentId, environmentName, null, null, [], [], packages, []);
        var main = Find(packages, project.ProjectImportId, project.ArchiveSha256);
        var rows = project.Evidence.Select(role => Row(role, packages)).ToList();
        // A Test Pack is suggested when one of its logical sources recognizes (by content) a package already bound to this project.
        var boundSnapshots = rows.SelectMany(r => r.Bound).Select(p => p.SnapshotId).OfType<Guid>().ToHashSet();
        if (main?.SnapshotId is { } mainSnapshot) boundSnapshots.Add(mainSnapshot);
        // An activated pack's source that the project already binds (a recognized snapshot) is satisfied by project evidence — one binding, shared.
        var activated = (setup?.Packs ?? []).Where(p => project.TestPacks.Contains(p.PackId, StringComparer.Ordinal))
            .SelectMany(p => p.Sources.Select(s => new TestPackEvidenceRow(p.PackId, p.DisplayName, s,
                s.State != TestPackSourceState.Bound && s.Candidates.Count(c => boundSnapshots.Contains(c.SnapshotId)) == 1))).ToList();
        var suggested = (setup?.Packs ?? []).Where(p => !project.TestPacks.Contains(p.PackId, StringComparer.Ordinal))
            .Select(p => (Pack: p, Match: p.Sources.FirstOrDefault(s => s.Candidates.Any(c => boundSnapshots.Contains(c.SnapshotId)))))
            .Where(x => x.Match is not null)
            .Select(x => (x.Pack.PackId, x.Pack.DisplayName, $"{x.Match!.Role.Label} is recognized in this project's bound source."))
            .ToList();
        return new(environmentId, environmentName, project, main, rows, activated, packages, suggested);
    }

    public static ProjectEvidencePackage? Find(IReadOnlyList<ProjectEvidencePackage> packages, string? importId, string? sha256) =>
        packages.FirstOrDefault(p => importId is not null && p.ImportId == importId)
        ?? packages.FirstOrDefault(p => !string.IsNullOrEmpty(sha256) && string.Equals(p.ArchiveSha256, sha256, StringComparison.Ordinal));

    public static EvidenceRoleRow Row(EnvironmentProjectEvidenceBinding role, IReadOnlyList<ProjectEvidencePackage> packages)
    {
        var bound = new List<ProjectEvidencePackage>();
        var unavailable = new List<ProjectEvidenceSourceReference>();
        foreach (var source in role.Sources)
        {
            if (Find(packages, source.ImportId, source.ArchiveSha256) is { } package) bound.Add(package);
            else unavailable.Add(source);
        }
        var candidates = packages.Where(p => ProjectEvidencePackages.Satisfies(p, role.Capability))
            .Select(p => new EvidenceCandidate(p, ProjectEvidencePackages.WhyMatches(p, role.Capability), bound.Any(b => b.ImportId == p.ImportId)))
            // Compatible, bound first; then by name — never by import date (newest is not a reason).
            .OrderByDescending(c => c.Bound).ThenBy(c => c.Package.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Package.ImportId, StringComparer.Ordinal)
            .ToList();
        EvidenceRoleState state;
        if (role.Cardinality == ProjectEvidenceCardinality.Single && bound.Count > 1) state = EvidenceRoleState.Conflict;
        else if (bound.Count > 0)
        {
            var capability = role.Capability == ProjectEvidenceCapability.SupportingSource
                ? bound.Select(b => b.Capability(ProjectEvidenceCapability.SupportingSource) ?? b.Capability(ProjectEvidenceCapability.ApplicationSource))
                : bound.Select(b => b.Capability(role.Capability));
            state = capability.Any(c => c is null || c.Status != PackageCapabilityStatus.Ready) ? EvidenceRoleState.Partial : EvidenceRoleState.Ready;
        }
        else if (unavailable.Count > 0) state = EvidenceRoleState.Unavailable;
        else
        {
            var open = candidates.Count(c => !c.Bound);
            state = open == 1 ? EvidenceRoleState.Suggested
                : open > 1 ? EvidenceRoleState.NeedsDecision
                : role.Requirement == ProjectEvidenceRequirement.Required ? EvidenceRoleState.Missing : EvidenceRoleState.NotProvided;
        }
        return new EvidenceRoleRow(role, state, bound, unavailable, candidates);
    }

    /// <summary>
    /// A new project for <paramref name="main"/>: the default roles, with the main package's own evidence bound to the roles it provides
    /// (application source, documentation, contracts, generated documentation) — the user chose it, so that is not a silent guess. Every other
    /// role stays unbound (Suggested or Needs decision) until the user binds it.
    /// </summary>
    public static EnvironmentProject CreateProject(ProjectEvidencePackage main, DateTimeOffset now)
    {
        var project = new EnvironmentProject
        {
            ProjectImportId = main.ImportId, ProjectName = main.DisplayName, ArchiveSha256 = main.ArchiveSha256, SelectedAt = now,
            Evidence = [.. ProjectEvidenceRoles.Defaults()],
        };
        BindOwnEvidence(project, main, now);
        return project;
    }

    private static readonly string[] OwnEvidenceRoles =
        [ProjectEvidenceRoles.ApplicationSource, ProjectEvidenceRoles.Documentation, ProjectEvidenceRoles.Contracts, ProjectEvidenceRoles.GeneratedDocumentation];

    private static void BindOwnEvidence(EnvironmentProject project, ProjectEvidencePackage main, DateTimeOffset now)
    {
        foreach (var role in project.Evidence.Where(r => OwnEvidenceRoles.Contains(r.RoleKey, StringComparer.Ordinal)))
        {
            if (!ProjectEvidencePackages.Satisfies(main, role.Capability) || role.Sources.Any(s => s.ImportId == main.ImportId)) continue;
            if (role.Cardinality == ProjectEvidenceCardinality.Single && role.Sources.Count > 0) continue;
            role.Sources.Add(Reference(main, role.Capability, now, "ProjectSelection"));
        }
    }

    /// <summary>A reference to <paramref name="package"/> for a role. Source-backed capabilities name the exact snapshot; documentation does not.</summary>
    public static ProjectEvidenceSourceReference Reference(ProjectEvidencePackage package, ProjectEvidenceCapability capability, DateTimeOffset now, string boundBy = "User") => new()
    {
        ImportId = package.ImportId, ArchiveFileName = package.ArchiveFileName, ArchiveSha256 = package.ArchiveSha256,
        SnapshotId = capability is ProjectEvidenceCapability.Documentation or ProjectEvidenceCapability.GeneratedDocumentation ? null : package.SnapshotId,
        BoundAt = now, BoundBy = boundBy,
    };

    /// <summary>Binds a package to a role: a Single role is replaced, a Multiple role aggregates. Only a compatible package can be bound.</summary>
    public static bool Bind(EnvironmentProject project, string roleKey, ProjectEvidencePackage package, DateTimeOffset now)
    {
        var role = project.Evidence.FirstOrDefault(r => r.RoleKey == roleKey);
        if (role is null || !ProjectEvidencePackages.Satisfies(package, role.Capability)) return false;
        if (role.Cardinality == ProjectEvidenceCardinality.Single) role.Sources.Clear();
        else role.Sources.RemoveAll(s => s.ImportId == package.ImportId);
        role.Sources.Add(Reference(package, role.Capability, now));
        return true;
    }

    public static bool Unbind(EnvironmentProject project, string roleKey, string importId)
    {
        var role = project.Evidence.FirstOrDefault(r => r.RoleKey == roleKey);
        return role is not null && role.Sources.RemoveAll(s => s.ImportId == importId) > 0;
    }

    /// <summary>
    /// Changing the project: explicit, and nothing imported is deleted. The new main package's own evidence is bound; references the previous
    /// project selection made to the old main package are dropped; every binding the user made is kept when its package still provides the role
    /// (otherwise it stays as an unresolved reference). Returns the impact, one line per affected role.
    /// </summary>
    public static IReadOnlyList<string> ChangeProject(EnvironmentProject project, ProjectEvidencePackage newMain, IReadOnlyList<ProjectEvidencePackage> packages, DateTimeOffset now)
    {
        var impact = new List<string>();
        var oldMain = project.ProjectImportId;
        foreach (var role in project.Evidence)
        {
            var dropped = role.Sources.RemoveAll(s => s.BoundBy == "ProjectSelection" && s.ImportId == oldMain);
            if (dropped > 0) impact.Add($"{role.Label}: the previous project's own evidence is no longer bound.");
            foreach (var source in role.Sources)
                if (Find(packages, source.ImportId, source.ArchiveSha256) is { } p && !ProjectEvidencePackages.Satisfies(p, role.Capability))
                    impact.Add($"{role.Label}: {source.ArchiveFileName} no longer satisfies this role and needs review.");
        }
        project.ProjectImportId = newMain.ImportId;
        project.ProjectName = newMain.DisplayName;
        project.ArchiveSha256 = newMain.ArchiveSha256;
        project.SelectedAt = now;
        foreach (var missing in ProjectEvidenceRoles.Defaults().Where(d => project.Evidence.All(r => r.RoleKey != d.RoleKey))) project.Evidence.Add(missing);
        BindOwnEvidence(project, newMain, now);
        impact.Add("Imported packages, Source Analysis snapshots and document history are unchanged.");
        return impact;
    }

    /// <summary>The impact of changing the project to <paramref name="newMain"/>, computed on a copy — shown before anything is applied.</summary>
    public static IReadOnlyList<string> PreviewChange(EnvironmentProject project, ProjectEvidencePackage newMain, IReadOnlyList<ProjectEvidencePackage> packages, DateTimeOffset now)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<EnvironmentProject>(System.Text.Json.JsonSerializer.Serialize(project))!;
        var impact = ChangeProject(copy, newMain, packages, now).ToList();
        var before = project.Evidence.ToDictionary(r => r.RoleKey, r => Row(r, packages));
        foreach (var role in copy.Evidence)
        {
            var after = Row(role, packages);
            if (before.TryGetValue(role.RoleKey, out var was) && was.IsResolved && !after.IsResolved)
                impact.Insert(0, $"{role.Label}: becomes {after.StatusLabel.ToLowerInvariant()}.");
            else if (was?.IsResolved == true && after.IsResolved && was.Bound.Select(b => b.ImportId).SequenceEqual(after.Bound.Select(b => b.ImportId)))
                impact.Insert(0, $"{role.Label}: binding retained.");
        }
        return impact;
    }

    /// <summary>
    /// The import scopes whose authored documents make up the project's documentation, in binding order: the documentation role's bound
    /// imports (several aggregate). Empty when the role has no available binding — then documentation is unresolved, not empty.
    /// </summary>
    public static IReadOnlyList<string> DocumentationImports(EnvironmentProject? project, SddLifecycleState lifecycle)
    {
        if (project?.Evidence.FirstOrDefault(r => r.RoleKey == ProjectEvidenceRoles.Documentation) is not { } role) return [];
        var imports = lifecycle.ProjectImports;
        return role.Sources
            .Select(s => imports.FirstOrDefault(i => s.ImportId is not null && i.ImportId == s.ImportId)
                         ?? imports.FirstOrDefault(i => !string.IsNullOrEmpty(s.ArchiveSha256) && i.ArchiveSha256 == s.ArchiveSha256))
            .OfType<SddProjectImportRecord>().Select(i => i.ImportId).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The main import of the project when it is recorded in this workspace (by id, else by archive fingerprint).</summary>
    public static SddProjectImportRecord? MainImport(EnvironmentProject? project, SddLifecycleState lifecycle) => project is null ? null
        : lifecycle.ProjectImports.FirstOrDefault(i => project.ProjectImportId is not null && i.ImportId == project.ProjectImportId)
          ?? lifecycle.ProjectImports.FirstOrDefault(i => !string.IsNullOrEmpty(project.ArchiveSha256) && i.ArchiveSha256 == project.ArchiveSha256);

    /// <summary>The exact snapshot bound to a source-backed role (Single roles only; several bound snapshots are not collapsed into one).</summary>
    public static Guid? BoundSnapshot(EnvironmentProject? project, string roleKey) =>
        project?.Evidence.FirstOrDefault(r => r.RoleKey == roleKey)?.Sources is { Count: 1 } sources ? sources[0].SnapshotId : null;

    /// <summary>Every exact snapshot bound to any role of the project: the high-confidence discovery scope for source consumers.</summary>
    public static IReadOnlyList<Guid> BoundSnapshots(EnvironmentProject? project) =>
        project is null ? [] : project.Evidence.SelectMany(r => r.Sources).Select(s => s.SnapshotId).OfType<Guid>().Distinct().ToList();

    /// <summary>Target Environment → Project, focused on a role (deep link).</summary>
    public static string ProjectTabHref(string? environmentId, string? roleKey = null) =>
        "/admin/system-settings?section=target-environments&tab=project"
        + (string.IsNullOrWhiteSpace(environmentId) ? "" : "&profile=" + Uri.EscapeDataString(environmentId))
        + (string.IsNullOrWhiteSpace(roleKey) ? "" : "&focus=" + Uri.EscapeDataString(roleKey));

    /// <summary>The independent status dimensions (Part Z).</summary>
    public static IReadOnlyList<EvidenceDimension> Dimensions(ProjectEvidenceView view)
    {
        if (!view.IsConfigured)
            return [new("project", "Project", "Not configured", "Choose which application/system this Target Environment represents.", "neutral")];
        string Sources(ProjectEvidenceCapability capability) =>
            view.Rows.Where(r => r.Role.Capability == capability).SelectMany(r => r.Bound).Select(b => b.ImportId).Distinct().Count() is var n and > 0
                ? $"Available from {n} source{(n == 1 ? "" : "s")}" : "";
        EvidenceDimension Of(string key, string label, string roleKey, ProjectEvidenceCapability capability)
        {
            var row = view.Row(roleKey);
            var value = row is null ? "Not configured" : row.IsResolved ? Sources(capability) : row.StatusLabel;
            var detail = row is null ? "" : $"{row.RequirementLabel} · {row.Role.RequiredBy}";
            return new(key, label, row is { IsRequired: false } && row.IsResolved ? $"Optional · {value}" : value, detail, row?.Tone ?? "neutral");
        }
        var missing = view.RequiredTotal - view.RequiredResolved;
        return
        [
            new("project", "Project", view.MainAvailable ? "Ready" : "Unavailable",
                view.MainAvailable ? $"{view.ProjectName} · {view.Main!.Classification}" : $"{view.ProjectName}: the main import is not in this workspace.",
                view.MainAvailable ? "available" : "partial"),
            // Bound is not ready: "Ready" only when every required role is satisfied; otherwise the bound count and the ready count are both shown.
            new("required", "Required evidence", view.RequiredReadyAll ? "Ready" : missing == 0 ? "Partial" : "Needs attention",
                $"{view.RequiredBound} / {view.RequiredTotal} bound · {view.RequiredReady} ready{(view.RequiredPartial > 0 ? $" · {view.RequiredPartial} partial" : "")}",
                view.RequiredReadyAll ? "available" : "partial"),
            Of("documentation", "Authored documentation", ProjectEvidenceRoles.Documentation, ProjectEvidenceCapability.Documentation),
            Of("source", "Source Analysis", ProjectEvidenceRoles.ApplicationSource, ProjectEvidenceCapability.ApplicationSource),
            Of("contracts", "Contracts/schema", ProjectEvidenceRoles.Contracts, ProjectEvidenceCapability.ContractSchema),
            Of("generated", "Generated documentation", ProjectEvidenceRoles.GeneratedDocumentation, ProjectEvidenceCapability.GeneratedDocumentation),
            new("optional", "Optional evidence", $"{view.OptionalAvailable} / {view.OptionalTotal} available", "Optional evidence never blocks.", "neutral"),
        ];
    }
}
