using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Compares two normalized Altinn app contracts (a confirmed one and a fresh read, or a live one and an imported document) fact by fact.
/// Each difference is rated by what it means for the one bounded operation BirkNext performs:
/// <list type="bullet">
/// <item><b>Blocking</b> — the app's identity, the number or type of process tasks, a new task action, a process/next that disappeared,
/// became body-required or no longer returns the process state, or a submission operation that disappeared;</item>
/// <item><b>NeedsReview</b> — renamed tasks, a different form data type or task ownership, changed content types or form fields, changed
/// operation paths;</item>
/// <item><b>Compatible</b> — a new app-lib version, or a different document with the same normalized facts.</item>
/// </list>
/// Pure: nothing is fetched, stored or accepted here.
/// </summary>
public static class AltinnContractComparer
{
    private static readonly string[] RequiredStateFields = ["currentTask", "ended", "endEvent"];

    /// <param name="processOnly">Compare only the deployed OpenAPI facts (an imported document carries no application metadata or schema).</param>
    public static AltinnContractChangeSet Compare(AltinnContractSnapshot before, AltinnContractSnapshot after, DateTimeOffset now, bool processOnly = false)
    {
        var changes = new List<AltinnContractChange>();
        void Add(string area, string? from, string? to, AltinnContractChangeImpact impact, string detail) =>
            changes.Add(new AltinnContractChange(area, Show(from), Show(to), impact, detail));

        if (!string.Equals(before.AppId, after.AppId, StringComparison.Ordinal))
            Add("Application", before.AppId, after.AppId, AltinnContractChangeImpact.Blocking, "A different app; its contract is not this app's.");

        if (!processOnly)
        {
            if (before.FormDataTypeId != after.FormDataTypeId)
                Add("Form data type", before.FormDataTypeId, after.FormDataTypeId, AltinnContractChangeImpact.NeedsReview, "The data type the report is stored as changed.");
            if (before.FormDataTaskId != after.FormDataTaskId)
                Add("Form data task", before.FormDataTaskId, after.FormDataTaskId, AltinnContractChangeImpact.NeedsReview, "The task the form belongs to changed.");
            if (!SetEqual(before.AllowedContentTypes, after.AllowedContentTypes))
                Add("Content types", string.Join(", ", before.AllowedContentTypes), string.Join(", ", after.AllowedContentTypes), AltinnContractChangeImpact.NeedsReview, "How the form may be submitted changed.");
            if (before.FormDataMaxCount != after.FormDataMaxCount)
                Add("Form elements allowed", before.FormDataMaxCount?.ToString(), after.FormDataMaxCount?.ToString(), AltinnContractChangeImpact.NeedsReview, "How many form elements an instance may hold changed.");
            var fieldsBefore = (before.Fields ?? []).Select(field => $"{field.Path}:{field.Type}").ToHashSet(StringComparer.Ordinal);
            var fieldsAfter = (after.Fields ?? []).Select(field => $"{field.Path}:{field.Type}").ToHashSet(StringComparer.Ordinal);
            if (!fieldsBefore.SetEquals(fieldsAfter))
                Add("Form fields", $"{fieldsBefore.Count} fields", $"{fieldsAfter.Count} fields (+{fieldsAfter.Except(fieldsBefore).Count()} −{fieldsBefore.Except(fieldsAfter).Count()})",
                    AltinnContractChangeImpact.NeedsReview, "The form schema changed; the field bindings are checked again.");
            // Everything else the generic form contract captures (requiredness, formats, XML namespace and root) must not be accepted silently.
            else if (AltinnContractEvolution.ToContractSnapshot(before) is { } formBefore && AltinnContractEvolution.ToContractSnapshot(after) is { } formAfter
                && formBefore.Fingerprint != formAfter.Fingerprint)
            {
                var structural = BirkNext.Api.Services.ContractEvolution.ContractDiff.Compare(formBefore, formAfter);
                Add("Form contract", Short(formBefore.Fingerprint), Short(formAfter.Fingerprint), AltinnContractChangeImpact.NeedsReview,
                    $"The form contract changed beyond field names and types: {string.Join("; ", structural.Take(5).Select(change => change.Detail))}");
            }
        }

        var (d0, d1) = (before.DeployedProcess, after.DeployedProcess);
        if (d0 is null != (d1 is null))
            Add("Deployed OpenAPI contract", d0 is null ? "not read" : "read", d1 is null ? "not read" : "read", AltinnContractChangeImpact.NeedsReview,
                d1 is null ? "The deployed OpenAPI contract could not be read this time." : "The deployed OpenAPI contract is now available.");
        if (d0 is not null && d1 is not null) CompareDeployed(d0, d1, Add);

        if (changes.Count == 0 && before.Fingerprint != after.Fingerprint)
            Add("Documents", Short(before.Fingerprint), Short(after.Fingerprint), AltinnContractChangeImpact.Compatible, "The documents changed, but every fact BirkNext uses is the same.");
        return new AltinnContractChangeSet
        {
            Impact = changes.Count == 0 ? AltinnContractChangeImpact.NoChange : changes.Max(change => change.Impact),
            Changes = changes, BaselineFingerprint = before.Fingerprint, CurrentFingerprint = after.Fingerprint, DetectedAt = now,
        };
    }

    private static void CompareDeployed(AltinnDeployedProcessContract d0, AltinnDeployedProcessContract d1, Action<string, string?, string?, AltinnContractChangeImpact, string> add)
    {
        if (d0.AppLibVersion != d1.AppLibVersion)
            add("App-lib version", d0.AppLibVersion, d1.AppLibVersion, AltinnContractChangeImpact.Compatible, "Informational unless the contract facts changed too.");
        if (d0.TaskInventoryFound != d1.TaskInventoryFound)
            add("Process-task table", d0.TaskInventoryFound ? "present" : "absent", d1.TaskInventoryFound ? "present" : "absent",
                d1.TaskInventoryFound ? AltinnContractChangeImpact.NeedsReview : AltinnContractChangeImpact.Blocking, "Whether the deployed contract lists its process tasks.");

        if (d0.Tasks.Count != d1.Tasks.Count)
            add("Process tasks", Tasks(d0.Tasks), Tasks(d1.Tasks), AltinnContractChangeImpact.Blocking,
                d1.Tasks.Count > d0.Tasks.Count ? "A task was added; the one-transition plan no longer holds." : "A task was removed.");
        else
            for (var index = 0; index < d0.Tasks.Count; index++)
            {
                var (t0, t1) = (d0.Tasks[index], d1.Tasks[index]);
                if (t0.TaskType != t1.TaskType)
                    add("Task type", $"{t0.TaskId} · {t0.TaskType}", $"{t1.TaskId} · {t1.TaskType}", AltinnContractChangeImpact.Blocking, "Only data tasks are supported.");
                if (t0.TaskId != t1.TaskId)
                    add("Task", t0.TaskId, t1.TaskId, AltinnContractChangeImpact.NeedsReview, "The task id changed; the live instance is checked against the new id.");
                if (t0.Name != t1.Name)
                    add("Task name", t0.Name, t1.Name, AltinnContractChangeImpact.NeedsReview, "The task was renamed.");
                var added = t1.Actions.Except(t0.Actions, StringComparer.Ordinal).ToArray();
                var removed = t0.Actions.Except(t1.Actions, StringComparer.Ordinal).ToArray();
                if (added.Length > 0)
                    add("Task actions", Actions(t0.Actions), Actions(t1.Actions), AltinnContractChangeImpact.Blocking, $"{t1.TaskId} now declares {string.Join(", ", added)}; what it requires at process/next is not established.");
                else if (removed.Length > 0)
                    add("Task actions", Actions(t0.Actions), Actions(t1.Actions), AltinnContractChangeImpact.NeedsReview, $"{t1.TaskId} no longer declares {string.Join(", ", removed)}.");
            }

        if (d0.ProcessNextDeclared != d1.ProcessNextDeclared)
            add("process/next", d0.ProcessNextDeclared ? "declared" : "not declared", d1.ProcessNextDeclared ? "declared" : "not declared",
                d1.ProcessNextDeclared ? AltinnContractChangeImpact.NeedsReview : AltinnContractChangeImpact.Blocking, "Whether the deployed contract declares process/next.");
        if (d0.ProcessNextBodyRequired != d1.ProcessNextBodyRequired)
            add("process/next body", d0.ProcessNextBodyRequired ? "required" : "optional", d1.ProcessNextBodyRequired ? "required" : "optional",
                d1.ProcessNextBodyRequired ? AltinnContractChangeImpact.Blocking : AltinnContractChangeImpact.NeedsReview, "A required body means the normal transition without an action is not established.");
        var lostState = RequiredStateFields.Where(field => d0.ProcessStateFields.Contains(field, StringComparer.Ordinal) && !d1.ProcessStateFields.Contains(field, StringComparer.Ordinal)).ToArray();
        if (lostState.Length > 0)
            add("process/next response", string.Join(", ", RequiredStateFields), string.Join(", ", RequiredStateFields.Except(lostState)), AltinnContractChangeImpact.Blocking,
                $"The response no longer declares {string.Join(", ", lostState)}, so the result cannot be verified.");
        else if (d0.ProcessNextResponseSchema != d1.ProcessNextResponseSchema)
            add("process/next response", d0.ProcessNextResponseSchema, d1.ProcessNextResponseSchema, AltinnContractChangeImpact.NeedsReview, "The response schema changed.");

        var formBefore = d0.DataTypeTasks.Where(IsForm).ToDictionary(item => item.DataTypeId, item => item.TaskId, StringComparer.Ordinal);
        var formAfter = d1.DataTypeTasks.Where(IsForm).ToDictionary(item => item.DataTypeId, item => item.TaskId, StringComparer.Ordinal);
        if (!formBefore.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(formAfter.Keys))
            add("Form data type", string.Join(", ", formBefore.Keys), string.Join(", ", formAfter.Keys), AltinnContractChangeImpact.NeedsReview, "The deployed contract's form data types changed.");
        else
            foreach (var (id, task) in formBefore.Where(item => formAfter[item.Key] != item.Value))
                add("Form data task", $"{id} → {task}", $"{id} → {formAfter[id]}", AltinnContractChangeImpact.NeedsReview, "The task the form data type belongs to changed.");

        foreach (var key in AltinnAppOperationKeys.Required)
        {
            var (o0, o1) = (d0.Operations.FirstOrDefault(item => item.Key == key), d1.Operations.FirstOrDefault(item => item.Key == key));
            if (o0 is not null && o1 is null) add($"Operation {key}", o0.PathTemplate, "not declared", AltinnContractChangeImpact.Blocking, "The submission needs this operation.");
            else if (o0 is null && o1 is not null) add($"Operation {key}", "not declared", o1.PathTemplate, AltinnContractChangeImpact.NeedsReview, "The operation is now declared.");
            else if (o0 is not null && o1 is not null && (o0.PathTemplate != o1.PathTemplate || o0.Method != o1.Method))
                add($"Operation {key}", $"{o0.Method} {o0.PathTemplate}", $"{o1.Method} {o1.PathTemplate}", AltinnContractChangeImpact.NeedsReview, "The endpoint path changed; requests use the new path.");
        }
    }

    private static bool IsForm(AltinnDataTypeTask item) => item.Kind?.StartsWith("FormData", StringComparison.Ordinal) == true || (item.Kind is null && item.TaskId is not null);
    private static bool SetEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
    private static string Tasks(IReadOnlyList<AltinnDeclaredProcessTask> tasks) => tasks.Count == 0 ? "none" : string.Join(", ", tasks.Select(AltinnProcessContractEvaluator.Describe));
    private static string Actions(IReadOnlyList<string> actions) => actions.Count == 0 ? "none" : string.Join(", ", actions);
    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Short(string fingerprint) => fingerprint.Length > 12 ? $"{fingerprint[..12]}…" : fingerprint;
}
