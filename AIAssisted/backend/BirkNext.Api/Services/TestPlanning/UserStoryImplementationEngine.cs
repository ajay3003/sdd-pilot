using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.ImplementationGaps;
using BirkNext.Interpretation;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The user story implementation status engine (2026-11-26): one deterministic rule set over the task lines the import extracted
/// (<see cref="GapDocumentSet"/>, the Implementation Gaps inputs — no second tasks.md parser) and the specification's stories. Pure: the same inputs
/// give the same result. Rules (documented in the 2026-11-26 report):
/// <list type="number">
/// <item>Ownership: the task's own [USn] tag (the Spec Kit convention, strongest) → else a phase titled for one story («Phase 4: User Story 2»)
/// → else shared/foundational work of the specification, never every story's. A tag that names another story than its phase is counted for its
/// tag and reported as a conflict; a tag that is not one story ("US1-2") is never counted. Titles and wording never link a task.</item>
/// <item>State: [x]/[X] complete, [ ] open. Any other marker only by what the task itself says (**Superseded**, **Partially done**) or what the
/// tasks document defines («A task marked `[~]` was superseded»); otherwise Unknown — never complete.</item>
/// <item>Superseded/cancelled tasks are listed and left out of the count (the source says nothing is built for them). Deferred but unchecked tasks
/// stay open work. Partial and unknown never count as complete; an unknown marker makes the percentage unavailable.</item>
/// <item>Dependencies: only explicit wording on the task line («depends on T004», «requires T017», «after T119») is a blocking dependency; a
/// task merely mentioned is related work; a task id that does not exist is unresolved. Every task id is scoped to its own tasks.md.</item>
/// <item>A fully checked list is «US-oppgaver fullført», never «Implementert»; a person's confirmation is a separate dimension and applies only
/// to the task evidence it was given on.</item>
/// </list>
/// </summary>
public static partial class UserStoryImplementationEngine
{
    public const string RuleVersion = "us-status/1";

    /// <summary>A person's latest confirmation of one story (from the manual-review decisions).</summary>
    public sealed record Confirmation(bool Confirmed, string EvidenceFingerprint, string By, DateTimeOffset At);

    [GeneratedRegex(@"\*\*\s*(?:Superseded|Cancelled|Canceled|Removed|Utgått|Avlyst|Erstattet)\b", RegexOptions.IgnoreCase)] private static partial Regex SupersededRe();
    [GeneratedRegex(@"\*\*\s*(?:Partially\s+done|Partly\s+done|Delvis(?:\s+(?:ferdig|gjort|utført))?)\b", RegexOptions.IgnoreCase)] private static partial Regex PartialRe();
    [GeneratedRegex(@"\b(?:deferred|utsatt|postponed)\b", RegexOptions.IgnoreCase)] private static partial Regex DeferredRe();
    [GeneratedRegex(@"\b(?:superseded|cancel+ed|removed|dropped|obsolete|utgått|avlyst|erstattet|not built|nothing is built)\b", RegexOptions.IgnoreCase)] private static partial Regex LegendCancelledRe();
    [GeneratedRegex(@"\b(?:partial\w*|partly|in progress|delvis|pågår)\b", RegexOptions.IgnoreCase)] private static partial Regex LegendPartialRe();
    [GeneratedRegex(@"\b(?:depends\s+on|depend\s+on|requires|after|blocked\s+by|avhenger\s+av|krever|etter)\s+(?<ids>T\d{2,4}[a-zA-Z]?(?:\s*(?:,|and|og|or|eller|–|—|-|to|til)\s*T\d{2,4}[a-zA-Z]?)*)", RegexOptions.IgnoreCase)] private static partial Regex DependsRe();
    [GeneratedRegex(@"\bT(\d{2,4})([a-zA-Z]?)\b")] private static partial Regex TaskIdRe();
    [GeneratedRegex(@"\bT(\d{2,4})\s*(?:–|—|-|to|til)\s*T(\d{2,4})\b", RegexOptions.IgnoreCase)] private static partial Regex RangeRe();

    /// <summary>The tasks.md beside a specification ("…/specs/002-x/spec.md" → "…/specs/002-x/tasks.md").</summary>
    public static string TasksPathOf(string specificationPath)
    {
        var p = specificationPath.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        return (slash < 0 ? "" : p[..(slash + 1)]) + "tasks.md";
    }

    private static bool SamePath(string a, string b) => string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The status of every story of one specification. <paramref name="docs"/> null: the import has no task extraction at all.</summary>
    public static List<UserStoryImplementationStatus> Compute(string specificationPath, string module, IReadOnlyList<CoverageUserStory> stories, GapDocumentSet? docs,
        IReadOnlyDictionary<string, Confirmation>? confirmations = null)
    {
        var tasksPath = TasksPathOf(specificationPath);
        var hasTasksDoc = docs is not null && docs.Documents.Any(d => d.Role == "Tasks" && SamePath(d.Path, tasksPath));
        var lines = hasTasksDoc ? docs!.Tasks.Where(t => SamePath(t.DocumentPath, tasksPath)).ToList() : [];
        var legend = hasTasksDoc ? docs!.MarkerConventions.FirstOrDefault(kv => SamePath(kv.Key, tasksPath)).Value ?? new() : new Dictionary<string, string>();
        var duplicates = hasTasksDoc ? (docs!.DuplicateTaskIds.FirstOrDefault(kv => SamePath(kv.Key, tasksPath)).Value ?? []).ToHashSet(StringComparer.Ordinal) : [];
        var defined = stories.Where(s => s.StoryId is { Length: > 0 }).Select(s => WorkItemInterpreter.StoryKey(s.StoryId)).OfType<string>().ToHashSet(StringComparer.Ordinal);

        // Every task's owner, link and state — once, for the whole file (a dependency may point to another story's task).
        var all = new Dictionary<string, (GapTaskInput Task, string? Owner, UserStoryTaskRef Ref)>(StringComparer.Ordinal);
        var unresolvedTags = new List<(string Phase, string TaskId, string Tag)>();
        foreach (var t in lines)
        {
            var explicitKey = WorkItemInterpreter.StoryKey(t.UserStory);
            var phaseKey = WorkItemInterpreter.PhaseStoryKey(t.Phase);
            if (t.UserStory is { Length: > 0 } rawTag && explicitKey is null && phaseKey is not null) unresolvedTags.Add((phaseKey, t.TaskId, rawTag));
            var owner = t.UserStory is { Length: > 0 } ? explicitKey : phaseKey;
            var link = explicitKey is not null ? (phaseKey is not null && phaseKey != explicitKey ? UserStoryTaskLink.ConflictingPhase : UserStoryTaskLink.ExplicitTag) : UserStoryTaskLink.StoryPhase;
            var (state, note) = StateOf(t, legend);
            var linkNote = link == UserStoryTaskLink.ConflictingPhase ? $"Merket [{explicitKey}], men står i fasen «{t.Phase}»." : null;
            all[t.TaskId] = (t, owner, new UserStoryTaskRef(t.TaskId, t.Text, state, t.Marker ?? MarkerOf(t.State), link, t.Phase, t.DocumentPath, t.Line, note, linkNote));
        }
        var shared = all.Values.Where(x => x.Owner is null && x.Ref.State is not (UserStoryTaskState.Complete or UserStoryTaskState.CancelledOrDeferred)).Select(x => x.Ref).ToList();
        var blockingEdges = all.Values.SelectMany(x => Dependencies(x.Task).Select(to => (From: x.Task.TaskId, To: to))).ToList();

        var result = new List<UserStoryImplementationStatus>();
        foreach (var story in stories)
        {
            var id = WorkItemInterpreter.StoryKey(story.StoryId) ?? story.StoryId ?? "";
            var baseStatus = new UserStoryImplementationStatus
            {
                StoryKey = story.Key, StoryId = story.StoryId ?? "", Title = story.Title, SpecificationPath = specificationPath.Replace('\\', '/'), Module = module,
                TasksPath = hasTasksDoc ? tasksPath : null,
            };
            if (docs is null)
            {
                result.Add(baseStatus with
                {
                    Status = UserStoryTaskStatus.Unknown, Evidence = UserStoryEvidenceQuality.Missing,
                    EvidenceText = "Oppgavene er ikke lest for denne importen. Importer prosjektet på nytt for å beregne status.",
                    Explanation = "Kan ikke beregne sikker status fordi oppgavegrunnlaget mangler.",
                });
                continue;
            }
            if (!hasTasksDoc)
            {
                result.Add(baseStatus with
                {
                    Status = UserStoryTaskStatus.Unknown, Evidence = UserStoryEvidenceQuality.Missing, EvidenceText = "tasks.md mangler for spesifikasjonen.",
                    Explanation = "Kan ikke beregne sikker status fordi oppgavegrunnlaget mangler.",
                });
                continue;
            }
            var direct = all.Values.Where(x => x.Owner == id).Select(x => x.Ref).OrderBy(r => r.Line).ToList();
            var eligible = direct.Where(r => r.State != UserStoryTaskState.CancelledOrDeferred).ToList();
            int Count(UserStoryTaskState s) => eligible.Count(r => r.State == s);
            var completed = Count(UserStoryTaskState.Complete);
            var partial = Count(UserStoryTaskState.Partial);
            var unknown = Count(UserStoryTaskState.Unknown);
            var open = Count(UserStoryTaskState.Open);
            var cancelled = direct.Count - eligible.Count;
            var diagnostics = new List<string>();
            var conflicts = direct.Where(r => r.Link == UserStoryTaskLink.ConflictingPhase).ToList();
            if (conflicts.Count > 0) diagnostics.Add($"Oppgavekoblingen er motstridende for {string.Join(", ", conflicts.Select(c => c.TaskId))}: merket {id}, men plassert under en annen brukerhistorie. Merkingen er brukt.");
            foreach (var (_, taskId, tag) in unresolvedTags.Where(u => u.Phase == id))
                diagnostics.Add($"{taskId} står i fasen for {id}, men er merket [{tag}], som ikke er én brukerhistorie. Oppgaven er ikke tatt med.");
            foreach (var dup in direct.Where(r => duplicates.Contains(r.TaskId))) diagnostics.Add($"Oppgave-ID {dup.TaskId} står flere ganger i tasks.md; bare første forekomst er brukt.");
            if (unknown > 0) diagnostics.Add($"{unknown} oppgave{(unknown == 1 ? "" : "r")} har en markering som tasks.md ikke forklarer.");

            // Dependencies of this story's tasks on tasks outside it (inside the story they are simply its remaining work).
            var mine = direct.Select(r => r.TaskId).ToHashSet(StringComparer.Ordinal);
            var dependencies = new List<UserStoryDependency>();
            foreach (var r in direct)
            {
                var task = all[r.TaskId].Task;
                var blocking = Dependencies(task).ToHashSet(StringComparer.Ordinal);
                foreach (var target in blocking.Where(t => !mine.Contains(t)))
                    dependencies.Add(all.TryGetValue(target, out var dep)
                        ? new(r.TaskId, target, UserStoryDependencyKind.Blocking, dep.Ref.State is not (UserStoryTaskState.Complete or UserStoryTaskState.CancelledOrDeferred),
                            $"{r.TaskId}: «{Excerpt(task.Text)}»", dep.Owner)
                        : new(r.TaskId, target, UserStoryDependencyKind.Unresolved, false, $"{r.TaskId} nevner {target}, som ikke finnes i tasks.md."));
                foreach (var target in task.ReferencedTaskIds.Where(t => !mine.Contains(t) && !blocking.Contains(t)).Take(10))
                    dependencies.Add(all.TryGetValue(target, out var rel)
                        ? new(r.TaskId, target, UserStoryDependencyKind.Related, rel.Ref.State is not (UserStoryTaskState.Complete or UserStoryTaskState.CancelledOrDeferred), $"{r.TaskId} nevner {target}.", rel.Owner)
                        : new(r.TaskId, target, UserStoryDependencyKind.Unresolved, false, $"{r.TaskId} nevner {target}, som ikke finnes i tasks.md."));
            }
            dependencies = [.. dependencies.DistinctBy(d => (d.FromTaskId, d.ToTaskId, d.Kind))];
            if (InCycle(blockingEdges, mine) is { } cycle) diagnostics.Add($"Avhengighetene danner en sirkel ({cycle}).");
            var openBlocking = dependencies.Any(d => d.Kind == UserStoryDependencyKind.Blocking && d.TargetOpen);

            var status = eligible.Count == 0 ? UserStoryTaskStatus.Unknown
                : unknown > 0 ? UserStoryTaskStatus.NeedsClarification
                : completed == eligible.Count ? (openBlocking ? UserStoryTaskStatus.DependencyPending : UserStoryTaskStatus.TasksCompleted)
                : completed == 0 && partial == 0 ? UserStoryTaskStatus.NotStarted
                : UserStoryTaskStatus.InProgress;
            var (quality, qualityText) = eligible.Count == 0 ? (UserStoryEvidenceQuality.Missing, cancelled > 0 ? "Alle oppgavene til brukerhistorien er utgått." : "Ingen oppgaver i tasks.md er koblet til brukerhistorien.")
                : conflicts.Count > 0 || unknown > 0 ? (UserStoryEvidenceQuality.Ambiguous, conflicts.Count > 0 ? "Motstridende oppgavekoblinger." : "Noen oppgavemarkeringer er ikke forklart i kilden.")
                : direct.Any(r => r.Link == UserStoryTaskLink.StoryPhase) || diagnostics.Count > 0 ? (UserStoryEvidenceQuality.Partial, "Noen oppgaver mangler tydelig US-merking og er koblet via fasen de står i.")
                : (UserStoryEvidenceQuality.Complete, "Oppgaver funnet og koblet til US.");
            var fingerprint = Fingerprint(direct);
            var (confirmation, by, at) = confirmations is not null && confirmations.TryGetValue(story.Key, out var c) && c.Confirmed
                ? (c.EvidenceFingerprint == fingerprint ? UserStoryConfirmation.ConfirmedImplemented : UserStoryConfirmation.ReconfirmationRequired, c.By, (DateTimeOffset?)c.At)
                : (UserStoryConfirmation.NotConfirmed, null, null);
            result.Add(baseStatus with
            {
                Status = status, Evidence = quality, EvidenceText = qualityText, Confirmation = confirmation, ConfirmedBy = by, ConfirmedAt = at,
                Completed = completed, Open = open, Partial = partial, UnknownState = unknown, Cancelled = cancelled, Eligible = eligible.Count,
                ProgressPercent = eligible.Count == 0 || unknown > 0 ? null : (int)Math.Round(100.0 * completed / eligible.Count, MidpointRounding.AwayFromZero),
                Tasks = direct, SharedOpen = [.. shared.Take(20)], Dependencies = dependencies, Diagnostics = diagnostics, EvidenceFingerprint = fingerprint,
                Explanation = status switch
                {
                    UserStoryTaskStatus.TasksCompleted => "Alle direkte US-oppgaver er avkrysset. Dette bekrefter ikke alene at funksjonaliteten er ferdig implementert.",
                    UserStoryTaskStatus.DependencyPending => "Alle direkte US-oppgaver er avkrysset, men en oppgave de uttrykkelig avhenger av er ikke fullført.",
                    UserStoryTaskStatus.NeedsClarification => "Framdrift kan ikke beregnes sikkert: noen oppgaver har en markering som kilden ikke forklarer.",
                    UserStoryTaskStatus.Unknown => "Kan ikke beregne sikker status fordi oppgavegrunnlaget mangler.",
                    _ => "Statusen er beregnet fra oppgavene som er markert i tasks.md.",
                },
            });
        }
        return result;
    }

    /// <summary>A task's state from its marker as written, then what the task or its tasks document says the marker means.</summary>
    internal static (UserStoryTaskState State, string? Note) StateOf(GapTaskInput t, IReadOnlyDictionary<string, string> legend)
    {
        var deferredNote = t.NotApplicableReason is { } reason ? $"Utsatt ({reason})." : DeferredRe().IsMatch(t.Text) ? "Utsatt ifølge oppgaveteksten." : null;
        switch (t.Marker)
        {
            case "x" or "X": return (UserStoryTaskState.Complete, null);
            case " ": return (UserStoryTaskState.Open, deferredNote);
            case null:
                // Inputs extracted before 2026-11-26 kept no marker: only the unambiguous states are used.
                return t.State switch
                {
                    GapTaskState.Completed => (UserStoryTaskState.Complete, null),
                    GapTaskState.NotStarted => (UserStoryTaskState.Open, deferredNote),
                    _ => (UserStoryTaskState.Unknown, "Markeringen er ikke lagret for denne importen; importer prosjektet på nytt for å tolke den."),
                };
        }
        if (SupersededRe().IsMatch(t.Text)) return (UserStoryTaskState.CancelledOrDeferred, $"[{t.Marker}] og «Superseded»/utgått i oppgaveteksten: ingenting bygges for den.");
        if (PartialRe().IsMatch(t.Text)) return (UserStoryTaskState.Partial, $"[{t.Marker}] og «delvis» i oppgaveteksten.");
        if (legend.TryGetValue(t.Marker, out var definition))
        {
            if (LegendCancelledRe().IsMatch(definition)) return (UserStoryTaskState.CancelledOrDeferred, $"tasks.md forklarer [{t.Marker}]: «{definition}».");
            if (LegendPartialRe().IsMatch(definition)) return (UserStoryTaskState.Partial, $"tasks.md forklarer [{t.Marker}]: «{definition}».");
        }
        return (UserStoryTaskState.Unknown, $"Markeringen [{t.Marker}] er ikke forklart i tasks.md eller i oppgaven.");
    }

    private static string MarkerOf(GapTaskState s) => s switch { GapTaskState.Completed => "x", GapTaskState.NotStarted => " ", _ => "?" };

    /// <summary>The tasks a task explicitly depends on (dependency wording on its own line or continuation), ranges expanded.</summary>
    internal static IEnumerable<string> Dependencies(GapTaskInput t)
    {
        var text = t.Text + "\n" + string.Join("\n", t.Continuation);
        foreach (Match m in DependsRe().Matches(text))
        {
            var ids = m.Groups["ids"].Value;
            foreach (Match r in RangeRe().Matches(ids))
            {
                var (from, to) = (int.Parse(r.Groups[1].Value), int.Parse(r.Groups[2].Value));
                if (to >= from && to - from <= 50) for (var n = from; n <= to; n++) yield return $"T{n:000}";
            }
            foreach (Match id in TaskIdRe().Matches(ids)) yield return $"T{int.Parse(id.Groups[1].Value):000}{id.Groups[2].Value}";
        }
    }

    /// <summary>A cycle through any of <paramref name="mine"/> in the blocking edges ("T010 → T012 → T010"), or null.</summary>
    private static string? InCycle(List<(string From, string To)> edges, HashSet<string> mine)
    {
        var next = edges.GroupBy(e => e.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var start in mine)
        {
            var path = new List<string> { start };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (Walk(start)) return string.Join(" → ", path);
            bool Walk(string node)
            {
                foreach (var n in next.GetValueOrDefault(node) ?? [])
                {
                    if (n == start) { path.Add(n); return true; }
                    if (!seen.Add(n)) continue;
                    path.Add(n);
                    if (Walk(n)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;
            }
        }
        return null;
    }

    private static string Excerpt(string text) => text.Length > 140 ? text[..140] + "…" : text;

    private static string Fingerprint(IEnumerable<UserStoryTaskRef> tasks) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", tasks.OrderBy(t => t.TaskId, StringComparer.Ordinal).Select(t => $"{t.TaskId}|{t.RawMarker}|{t.Text}")))))[..16].ToLowerInvariant();
}
