using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Reads the facts BirkNext needs from an Altinn 3 app's deployed OpenAPI document (<c>/{org}/{app}/v1/customOpenapi.json</c>). app-lib
/// generates it from the deployed app: the operations and schemas (read with the shared <see cref="IOpenApiExtractor"/>), and in
/// <c>info.description</c> a data-type table and a process-task table (app-lib CustomOpenApiController: every BPMN task and service task from
/// IProcessReader.GetProcessTasks, with the task's Altinn type and declared actions; gateways and events are not listed). Only normalized facts
/// are returned; the document itself is never kept.
/// </summary>
public static class AltinnDeployedOpenApiReader
{
    public const string DocumentPath = "v1/customOpenapi.json";
    private const string TaskHeader = "| TaskId | Name | Type | Actions |";
    private const string DataTypeHeader = "| DataTypeId | Type | Allowed number | MimeTypes | TaskId |";

    public static AltinnDeployedProcessContract Read(string json, string appId, IOpenApiExtractor extractor)
    {
        var extraction = extractor.Extract(json);
        if (!extraction.Success || extraction.Contract is not { } contract)
            throw new AltinnContractException($"The app's deployed OpenAPI document could not be read ({extraction.ErrorMessage}).");
        // The document must describe this app: app-lib prefixes every app operation with /{org}/{app}/.
        if (!contract.Operations.Any(operation => operation.Method == "POST" && operation.Path == $"/{appId}/instances"))
            throw new AltinnContractException($"The deployed OpenAPI document does not describe {appId}.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        var info = document.RootElement.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object ? infoElement : default;
        var description = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("description", out var text) ? text.GetString() ?? "" : "";
        var version = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var notes = new List<string>();

        var taskRows = Table(description, TaskHeader, 4);
        var tasks = taskRows?.Select(row => new AltinnDeclaredProcessTask(row[0], row[1], row[2], Actions(row[3]))).ToArray() ?? [];
        if (taskRows is null) notes.Add("The document has no generated process-task table; it says nothing about the process tasks.");
        var dataTypes = Table(description, DataTypeHeader, 5)?.Select(row => new AltinnDataTypeTask(row[0], row[4].Length > 0 ? row[4] : null)).ToArray() ?? [];

        var nextPath = $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}/process/next";
        var next = contract.Operations.FirstOrDefault(operation => operation.Path == nextPath && operation.Method == "PUT");
        var responseSchema = next?.ResponseSchemaRefs.GetValueOrDefault("200");
        var stateFields = responseSchema is null ? [] : contract.Schemas.FirstOrDefault(schema => schema.Name == responseSchema)?.Properties.Select(property => property.Name).ToArray() ?? [];
        var bodyRequired = next is not null && document.RootElement.TryGetProperty("paths", out var paths) && paths.TryGetProperty(nextPath, out var item)
            && item.TryGetProperty("put", out var put) && put.TryGetProperty("requestBody", out var body) && body.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True;
        if (next is null) notes.Add("The document does not declare PUT …/process/next.");

        return new AltinnDeployedProcessContract
        {
            Document = DocumentPath, Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(), AppLibVersion = version,
            TaskInventoryFound = taskRows is not null, Tasks = tasks, DataTypeTasks = dataTypes,
            ProcessNextDeclared = next is not null, ProcessNextMethod = next?.Method, ProcessNextBodyRequired = bodyRequired,
            ProcessNextBodyFields = next?.RequestBody?.Fields.Select(field => field.Name).ToArray() ?? [],
            ProcessNextResponseSchema = responseSchema, ProcessStateFields = stateFields, Notes = notes,
        };
    }

    /// <summary>app-lib writes an empty cell when the task's action list is empty and "[no actions]" when it has none at all.</summary>
    private static IReadOnlyList<string> Actions(string cell) =>
        cell.Length == 0 || cell == "[no actions]" ? [] : cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The rows of the markdown table with exactly this header (separator row skipped), or null when the header is absent.</summary>
    private static List<string[]>? Table(string markdown, string header, int columns)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim()).ToArray();
        var start = Array.FindIndex(lines, line => line == header);
        if (start < 0) return null;
        var rows = new List<string[]>();
        for (var index = start + 2; index < lines.Length && lines[index].StartsWith('|') && rows.Count < 100; index++)
        {
            // Exactly one edge pipe each side: a row may end with an empty cell ("…|application/pdf||").
            var line = lines[index][1..];
            if (line.EndsWith('|')) line = line[..^1];
            var cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length == columns && cells[0].Length > 0) rows.Add(cells);
        }
        return rows;
    }
}

/// <summary>How one process/next ended, judged against the plan. Only <see cref="ProcessEnded"/> and <see cref="MovedToExpectedTask"/> are expected.</summary>
public enum AltinnTransitionOutcome { ProcessEnded, MovedToExpectedTask, UnexpectedNextTask, UnexpectedProcessState }

/// <summary>
/// Judges whether BirkNext may perform its intended process operation — one bounded process/next from the form's data task — from the
/// evidence it has: a static process.bpmn (optional), the app's deployed OpenAPI contract, and the application metadata. Pure. It never claims
/// the BPMN topology is known unless a static definition was parsed, and it never widens the operation beyond the execution bound.
/// </summary>
public static class AltinnProcessContractEvaluator
{
    public const string NormalNext = "Normal next transition · no action body";
    public const string StaticNotProvided = "Not provided · Optional";
    public const string StaticLabel = "Static process.bpmn (Source Analysis)";
    public const string DeployedLabel = "TT02 deployed OpenAPI contract";
    private static readonly string[] RequiredStateFields = ["currentTask", "ended", "endEvent"];
    private static readonly string[] SupportedTaskTypes = ["data"];

    public static AltinnProcessAssessment Assess(IReadOnlyList<AltinnProcessTask>? staticTasks, AltinnDeployedProcessContract? deployed, string? formDataTypeId,
        string? metadataTaskId, int maxTransitions)
    {
        var reasons = new List<string>();
        var usableDeployed = deployed is { TaskInventoryFound: true } ? deployed : null;
        var sources = new List<string>();
        if (staticTasks is not null) sources.Add(StaticLabel);
        if (usableDeployed is not null) sources.Add(DeployedLabel);
        var staticLabel = staticTasks is null ? StaticNotProvided : $"Provided · {staticTasks.Count} sequential task(s)";

        AltinnProcessAssessment Result(AltinnProcessContractStatus status, string summary, IReadOnlyList<AltinnDeclaredProcessTask>? planned = null) => new()
        {
            Status = status, Summary = summary, Sources = sources, DeclaredTaskCount = staticTasks?.Count ?? usableDeployed?.Tasks.Count,
            PlannedTasks = planned ?? [], Operation = NormalNext, Transitions = planned?.Count ?? 0, MaxTransitions = maxTransitions,
            ExpectedPostCondition = "The returned AppProcessState must show the process ended (ended and endEvent set, no current task); anything else stops the run.",
            StaticDefinition = staticLabel, TopologyKnown = staticTasks is not null, Reasons = reasons.Distinct().ToArray(),
        };

        if (staticTasks is null && usableDeployed is null)
        {
            reasons.Add(deployed is null
                ? "No process evidence: neither the app's deployed OpenAPI contract (read the public TT02 metadata) nor a static process.bpmn (Source Analysis)."
                : "The app's deployed OpenAPI document has no process-task table, and no static process.bpmn was supplied.");
            return Result(AltinnProcessContractStatus.NotConfirmed, "Not confirmed: no trustworthy process evidence.");
        }

        // Conflicts: the sources must agree on the tasks and on the task the form belongs to.
        var deployedFormTask = formDataTypeId is null ? null : usableDeployed?.DataTypeTasks.FirstOrDefault(item => item.DataTypeId == formDataTypeId)?.TaskId;
        if (staticTasks is not null && usableDeployed is not null)
        {
            static string Key(string id, string type) => $"{id}:{type}";
            var fromStatic = staticTasks.Select(task => Key(task.TaskId, task.TaskType)).Order(StringComparer.Ordinal).ToArray();
            var fromDeployed = usableDeployed.Tasks.Select(task => Key(task.TaskId, task.TaskType)).Order(StringComparer.Ordinal).ToArray();
            if (!fromStatic.SequenceEqual(fromDeployed, StringComparer.Ordinal))
                reasons.Add($"process.bpmn declares {string.Join(", ", fromStatic)} but the deployed TT02 contract declares {string.Join(", ", fromDeployed)}: the uploaded source is not what is deployed.");
        }
        if (deployedFormTask is not null && metadataTaskId is not null && deployedFormTask != metadataTaskId)
            reasons.Add($"The deployed OpenAPI contract binds {formDataTypeId} to {deployedFormTask}, the application metadata to {metadataTaskId}.");
        var formTask = metadataTaskId ?? deployedFormTask;
        var declaredIds = (staticTasks?.Select(task => task.TaskId) ?? usableDeployed!.Tasks.Select(task => task.TaskId)).ToHashSet(StringComparer.Ordinal);
        if (formTask is not null && !declaredIds.Contains(formTask))
            reasons.Add($"The form data type belongs to {formTask}, which the process evidence does not declare.");
        if (reasons.Count > 0)
        {
            staticLabel = staticTasks is null ? staticLabel : $"{staticLabel} · conflicts with the deployed contract";
            return Result(AltinnProcessContractStatus.Conflict, "Conflict: the process evidence disagrees; nothing is executed.");
        }
        if (staticTasks is not null && usableDeployed is not null) staticLabel = $"{staticLabel} · agrees with the deployed contract";

        // The plan: the tasks process/next transitions from, in order. Order is known from a static definition, or trivially for one task.
        IReadOnlyList<AltinnDeclaredProcessTask> planned;
        if (staticTasks is not null)
            planned = staticTasks.Select(task => usableDeployed?.Tasks.FirstOrDefault(item => item.TaskId == task.TaskId)
                ?? new AltinnDeclaredProcessTask(task.TaskId, "", task.TaskType, [])).ToArray();
        else if (usableDeployed!.Tasks.Count == 1)
            planned = usableDeployed.Tasks;
        else
        {
            reasons.Add(usableDeployed.Tasks.Count == 0
                ? "The deployed OpenAPI contract declares no process task."
                : $"The deployed OpenAPI contract declares {usableDeployed.Tasks.Count} tasks but not their order or the flows between them; planning more than one transition needs the app's process.bpmn.");
            return Result(AltinnProcessContractStatus.Partial, "Partial: the transition plan cannot be established.");
        }

        if (formTask is null) reasons.Add("Which task the form data type belongs to is not established (no taskId in the application metadata or the deployed contract).");
        else if (planned[0].TaskId != formTask) reasons.Add($"The form data type belongs to {formTask}, but the process starts at {planned[0].TaskId}; only the form's own task is supported.");
        foreach (var task in planned.Where(task => !SupportedTaskTypes.Contains(task.TaskType, StringComparer.Ordinal)))
            reasons.Add($"Task {task.TaskId} is a {task.TaskType} task; completing it needs an action contract BirkNext does not have (only data tasks are supported).");
        foreach (var task in planned.Where(task => task.Actions.Count > 0))
            reasons.Add($"Task {task.TaskId} declares action(s) {string.Join(", ", task.Actions)}; what they require at process/next is not established.");
        if (usableDeployed is not null)
        {
            if (!usableDeployed.ProcessNextDeclared) reasons.Add("The deployed OpenAPI contract does not declare PUT …/process/next.");
            else
            {
                if (usableDeployed.ProcessNextBodyRequired) reasons.Add("The deployed contract requires a process/next body; the normal transition without an action is not established.");
                var missing = RequiredStateFields.Where(field => !usableDeployed.ProcessStateFields.Contains(field, StringComparer.Ordinal)).ToArray();
                if (usableDeployed.ProcessNextResponseSchema is null || missing.Length > 0)
                    reasons.Add($"The process/next response does not declare {(usableDeployed.ProcessNextResponseSchema is null ? "a process-state schema" : string.Join(", ", missing))}, so its result cannot be verified.");
            }
        }
        if (planned.Count > maxTransitions)
            reasons.Add($"The plan needs {planned.Count} process transitions; the execution policy allows {maxTransitions}.");
        if (reasons.Count > 0) return Result(AltinnProcessContractStatus.Partial, "Partial: a fact the operation needs is not established.", planned);

        var tasksText = string.Join(" → ", planned.Select(task => $"{task.TaskId} ({task.TaskType})"));
        return Result(AltinnProcessContractStatus.ConfirmedForCurrentOperation,
            $"Confirmed for the current operation: process/next × {planned.Count} from {tasksText}, then the process must have ended."
            + (staticTasks is null ? " The full BPMN topology (gateways, events) is not known and not needed for this operation." : ""), planned);
    }

    /// <summary>"Task_1 · Utfylling · data" — the name only when the evidence gives one (process.bpmn alone does not).</summary>
    public static string Describe(AltinnDeclaredProcessTask task) => string.Join(" · ", new[] { task.TaskId, task.Name, task.TaskType }.Where(part => part.Length > 0));

    /// <summary>Why the live instance must not be advanced now, or null. Checked immediately before each process/next.</summary>
    public static string? PreTransitionProblem(AltinnInstanceState live, AltinnDeclaredProcessTask expected)
    {
        if (live.Ended is not null) return "The instance's process has already ended.";
        if (live.CurrentTaskId != expected.TaskId) return $"The live instance is at {live.CurrentTaskId ?? "no task"}, not {expected.TaskId}.";
        if (live.CurrentTaskType != expected.TaskType) return $"The live task {live.CurrentTaskId} is reported as {live.CurrentTaskType ?? "an unknown type"}, not {expected.TaskType}.";
        if (live.CanCompleteTask == false) return $"Altinn reports that {live.CurrentTaskId} cannot be completed (validated.canCompleteTask = false).";
        return null;
    }

    /// <summary>
    /// Classifies the AppProcessState a process/next returned. app-lib's end-event handling sets currentTask = null, ended and endEvent together;
    /// only all three count as the process having ended.
    /// </summary>
    public static AltinnTransitionOutcome Classify(AltinnProcessStateResult state, string? expectedNextTaskId)
    {
        if (expectedNextTaskId is null && state.CurrentTaskId is null && state.Ended is not null && !string.IsNullOrEmpty(state.EndEvent)) return AltinnTransitionOutcome.ProcessEnded;
        if (expectedNextTaskId is not null && state.CurrentTaskId == expectedNextTaskId && state.Ended is null) return AltinnTransitionOutcome.MovedToExpectedTask;
        return state.CurrentTaskId is not null ? AltinnTransitionOutcome.UnexpectedNextTask : AltinnTransitionOutcome.UnexpectedProcessState;
    }
}
