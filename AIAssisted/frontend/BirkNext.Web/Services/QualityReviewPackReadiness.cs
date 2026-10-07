using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pre-run readiness of a review pack. It describes whether the pack's inputs are present, never whether it will pass.
/// Ready: every input the pack reads is present. Partial: the pack runs, but part of what it checks has no input.
/// Blocked: the pack's core input is absent, so it cannot run. NotApplicable: the pack does not apply (neutral).
/// </summary>
public enum PackReadinessState { Ready, Partial, Blocked, NotApplicable }

/// <summary>Which artifact roles currently have usable (loaded) content.</summary>
public sealed record ReviewArtifactPresence(bool Constitution, bool Specification, bool Plan, bool Tasks, bool DataModel)
{
    public bool AnyDocument => Constitution || Specification || Plan || Tasks;
    public int DocumentCount => new[] { Constitution, Specification, Plan, Tasks }.Count(x => x);
}

public sealed record PackReadiness(
    string PackId,
    string PackName,
    PackReadinessState State,
    string Summary,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> InputsUsed)
{
    public bool IsRunnable => State is PackReadinessState.Ready or PackReadinessState.Partial;
}

public sealed record PackReadinessSummary(int Selected, int Ready, int Partial, int Blocked, int NotApplicable, IReadOnlyList<string> ExpectedLimitations)
{
    public int Runnable => Ready + Partial;
}

/// <summary>
/// Derives pack readiness from the inputs each pack adapter in <see cref="QualityReviewService"/> actually reads. Every pack is
/// document-based today: none reads source snapshots, runtime/browser evidence or shared SDD lifecycle evidence, so those never
/// block or limit a pack.
/// </summary>
public static class QualityReviewPackReadiness
{
    public static string StateLabel(PackReadinessState state) => state switch
    {
        PackReadinessState.Ready => "Ready",
        PackReadinessState.Partial => "Partial",
        PackReadinessState.Blocked => "Blocked",
        _ => "Not applicable",
    };

    public static PackReadiness Evaluate(QualityReviewPackDescriptor pack, ReviewArtifactPresence p)
    {
        if (pack.PackGroup == "Standards")
            return Standards(pack, p);

        return pack.PackId switch
        {
            "qa-auditor" => Documents(pack, p, core: p.Specification || p.Plan || p.Tasks,
                blocked: "Needs a Specification, Plan or Task artifact.",
                effect: "structural checks that need them have no input",
                missing => $"No {missing}: structural checks that need it have no input."),
            "constitution-compliance" => ConstitutionCompliance(pack, p),
            "qa-readiness" => Documents(pack, p, core: p.Specification || p.Tasks,
                blocked: "Needs a Specification or Task artifact.",
                effect: "their readiness categories are reported Not assessed",
                missing => $"No {missing}: its readiness category is reported Not assessed."),
            "delivery-readiness" => Documents(pack, p, core: p.Plan || p.Tasks,
                blocked: "Needs a Plan or Task artifact.",
                effect: "gates that need them are reported as missing-input blockers or skipped",
                missing => missing == "Constitution"
                    ? "No Constitution: the compliance gate is skipped."
                    : $"No {missing}: the gate that needs it is reported as a missing-input blocker, not as a review of that artifact."),
            "data-model-quality" => p.DataModel
                ? new(pack.PackId, pack.PackName, PackReadinessState.Ready, "Data Model is available.", [], ["Data Model"])
                : new(pack.PackId, pack.PackName, PackReadinessState.Blocked, "Needs a Data Model artifact.", [], ["Data Model"]),
            _ => new(pack.PackId, pack.PackName, PackReadinessState.Ready, "No input requirements are declared for this pack.", [], []),
        };
    }

    public static PackReadinessSummary Summarize(IReadOnlyList<PackReadiness> selected) => new(
        selected.Count,
        selected.Count(r => r.State == PackReadinessState.Ready),
        selected.Count(r => r.State == PackReadinessState.Partial),
        selected.Count(r => r.State == PackReadinessState.Blocked),
        selected.Count(r => r.State == PackReadinessState.NotApplicable),
        selected.Where(r => r.State == PackReadinessState.Partial).Select(r => $"{r.PackName}: {r.Summary}").ToList());

    private static readonly string[] DocumentInputs = ["Constitution", "Specification", "Plan", "Tasks"];

    private static IEnumerable<string> MissingDocuments(ReviewArtifactPresence p)
    {
        if (!p.Constitution) yield return "Constitution";
        if (!p.Specification) yield return "Specification";
        if (!p.Plan) yield return "Plan";
        if (!p.Tasks) yield return "Tasks";
    }

    private static PackReadiness Documents(QualityReviewPackDescriptor pack, ReviewArtifactPresence p, bool core, string blocked, string effect, Func<string, string> limitation)
    {
        if (!core) return new(pack.PackId, pack.PackName, PackReadinessState.Blocked, blocked, [], DocumentInputs);
        var missing = MissingDocuments(p).ToList();
        return missing.Count == 0
            ? new(pack.PackId, pack.PackName, PackReadinessState.Ready, "All inputs this pack reads are available.", [], DocumentInputs)
            : new(pack.PackId, pack.PackName, PackReadinessState.Partial, $"Runs without {Join(missing)}; {effect}.", missing.Select(limitation).ToList(), DocumentInputs);
    }

    private static PackReadiness ConstitutionCompliance(QualityReviewPackDescriptor pack, ReviewArtifactPresence p)
    {
        if (!p.Constitution) return new(pack.PackId, pack.PackName, PackReadinessState.Blocked, "Needs a Constitution artifact.", [], DocumentInputs);
        var missing = MissingDocuments(p).ToList();
        return missing.Count == 0
            ? new(pack.PackId, pack.PackName, PackReadinessState.Ready, "All inputs this pack reads are available.", [], DocumentInputs)
            : new(pack.PackId, pack.PackName, PackReadinessState.Partial, $"Constitution rules are checked without {Join(missing)}.",
                missing.Select(m => $"No {m}: Constitution rules are not checked against it.").ToList(), DocumentInputs);
    }

    private static PackReadiness Standards(QualityReviewPackDescriptor pack, ReviewArtifactPresence p)
    {
        if (!p.AnyDocument)
            return new(pack.PackId, pack.PackName, PackReadinessState.Blocked, "Needs Constitution, Specification, Plan or Task text.", [], DocumentInputs);
        var missing = MissingDocuments(p).ToList();
        const string scope = "Keyword checks over document text only; no runtime, browser or source evidence is used, and a result is not a compliance verdict.";
        return missing.Count == 0
            ? new(pack.PackId, pack.PackName, PackReadinessState.Ready, scope, [], DocumentInputs)
            : new(pack.PackId, pack.PackName, PackReadinessState.Partial, $"Keyword checks see only {p.DocumentCount} of 4 documents (no {Join(missing)}); a check can report Warning or Failed when its evidence would be in a missing document.",
                [scope],
                DocumentInputs);
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1],
    };
}
