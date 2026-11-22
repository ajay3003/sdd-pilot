using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectEvidence;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>
/// "What does this ZIP contain?" (2026-11-21): roles a tester ticks before importing. After the import they become the package's confirmed roles
/// (the field Source Management edits and every consumer reads) — but only the roles its content supports: a role is a claim about the content,
/// and the content is known only once the archive is analysed. A role the content does not show is reported, never saved. Nothing ticked = the
/// role detected from content, exactly as before. Pure; the Target Environment association is done by <see cref="ProjectImportSession"/>.
/// </summary>
public static class ImportRoleChoice
{
    /// <summary>"What does this ZIP contain?" is answered: one or more roles, or "Not sure — let BirkNext detect it". The ZIP is read only then.</summary>
    public static bool Answered(IReadOnlyCollection<PackageEvidenceRole> roles, bool detect) => roles.Count > 0 || detect;

    public sealed record Outcome(IReadOnlyList<PackageEvidenceRole> Accepted, IReadOnlyList<PackageEvidenceRole> Refused, bool Verified)
    {
        public string Message(string packageName)
        {
            if (Accepted.Count == 0 && Refused.Count == 0) return "";
            var text = Accepted.Count > 0 ? $"Roles of {packageName}: {string.Join(", ", Accepted.Select(ApplicationContexts.RoleLabel))}." : $"No role of {packageName} was saved.";
            if (Refused.Count > 0) text += $" Not saved — its content does not show it: {string.Join(", ", Refused.Select(ApplicationContexts.RoleLabel))}.";
            if (!Verified) text += " The content could not be checked (Source Analysis not reachable); review the roles in Source Management.";
            return text;
        }
    }

    /// <summary>The roles a tester can tick, in the order Source Management lists them.</summary>
    public static IReadOnlyList<PackageEvidenceRole> Choices => SourceRoleVocabulary.Choices;

    /// <summary>
    /// Saves the ticked roles the content supports on <paramref name="record"/>. Without the backend's source setup (unreachable) the content cannot
    /// be checked: the ticked roles are saved as the tester's choice, as Source Management would, and the outcome says so.
    /// </summary>
    public static Outcome Apply(SddProjectImportRecord record, SddLifecycleState lifecycle, SourceSetupView? setup, IReadOnlyCollection<PackageEvidenceRole> chosen)
    {
        var wanted = chosen.Where(r => r != PackageEvidenceRole.NotClassified).Distinct().Order().ToList();
        if (wanted.Count == 0) return new([], [], true);
        if (setup is null)
        {
            record.ConfirmedRoles = [.. wanted.Select(r => r.ToString())];
            return new(wanted, [], false);
        }
        var package = ProjectEvidencePackages.Of(record, lifecycle, setup);
        var accepted = wanted.Where(r => ProjectEvidencePackages.Satisfies(package, SourceRoleVocabulary.CategoryOf(r))).ToList();
        var refused = wanted.Except(accepted).ToList();
        if (accepted.Count > 0) record.ConfirmedRoles = [.. accepted.Select(r => r.ToString())];
        return new(accepted, refused, true);
    }
}
