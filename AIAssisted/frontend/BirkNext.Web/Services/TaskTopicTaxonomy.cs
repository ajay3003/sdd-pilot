using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The one topic taxonomy for tasks: which deterministic keywords put a task under a topic, the labels shown
/// for it, and how much test attention a topic warrants. Topics describe what a task touches; they are not a risk
/// verdict. Testing and Security also follow the Task artifact's own flags (the same flags Task Explorer shows as
/// Task Topics), so both pages agree on those two topics.
/// </summary>
public static class TaskTopicTaxonomy
{
    private static readonly (AffectedArea Topic, string[] Terms)[] TopicTerms =
    [
        (AffectedArea.Security, ["security", "kode 6", "kode 7", "sikkerhetsnivaa", "managed identity", "secret"]),
        (AffectedArea.Authorization, ["authorize", "authorization", "permission", "access control", "bearer", "token"]),
        (AffectedArea.Ingestion, ["cdc", "ingest", "full load", "batch", "event hubs", "eventhub"]),
        (AffectedArea.DomainEvents, ["event", "publish", "consumer", "processor", "service bus"]),
        (AffectedArea.Audit, ["audit", "revisjon"]),
        (AffectedArea.HealthMonitoring, ["health", "metric", "telemetry", "opentelemetry", "monitor"]),
        (AffectedArea.Infrastructure, ["csproj", "sln", "appsettings", "configuration", "program.cs", "di registration", "migration", "dbcontext", "key vault", "nuget"]),
        (AffectedArea.Validation, ["validation", "validate", "validator"]),
        (AffectedArea.Testing, ["test", "xunit", "nsubstitute", "testcontainers", "fixture", "mock", "fake"]),
        (AffectedArea.ExceptionHandling, ["exception", "error", "failure", "fault"]),
    ];

    private static readonly string[] InfrastructureFileSuffixes = [".csproj", ".sln", ".json", ".yaml", ".yml", ".props", ".targets"];

    /// <summary>Topics in taxonomy order. Infrastructure comes from setup keywords or setup files (.csproj, .json, …), never from any source file.</summary>
    public static List<AffectedArea> Detect(TaskItem task)
    {
        var text = TaskText(task);
        var topics = TopicTerms
            .Where(definition => MatchedTerm(text, definition.Terms) is not null)
            .Select(definition => definition.Topic)
            .ToList();

        if (task.IsTestingTask && !topics.Contains(AffectedArea.Testing))
            topics.Add(AffectedArea.Testing);
        if (task.IsSecurityTask && !topics.Contains(AffectedArea.Security))
            topics.Add(AffectedArea.Security);
        if (HasInfrastructureFile(task) && !topics.Contains(AffectedArea.Infrastructure))
            topics.Add(AffectedArea.Infrastructure);

        return topics;
    }

    public static bool HasInfrastructureFile(TaskItem task) =>
        task.RelatedFileIds.Any(file => InfrastructureFileSuffixes.Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Test attention a topic warrants: High for security, authorization, events and audit; Medium for ingestion, health, validation and error handling.</summary>
    public static ImpactLevel TestPriority(AffectedArea topic) => topic switch
    {
        AffectedArea.Security or AffectedArea.Authorization or AffectedArea.DomainEvents or AffectedArea.Audit => ImpactLevel.High,
        AffectedArea.Ingestion or AffectedArea.HealthMonitoring or AffectedArea.Validation or AffectedArea.ExceptionHandling => ImpactLevel.Medium,
        _ => ImpactLevel.Low,
    };

    public static string Label(AffectedArea topic) => topic switch
    {
        AffectedArea.AccessManagement => "Access management",
        AffectedArea.ReferenceData => "Reference data",
        AffectedArea.DomainEvents => "Domain events",
        AffectedArea.OperationRegistration => "Operation registration",
        AffectedArea.HealthMonitoring => "Health & monitoring",
        AffectedArea.BusinessRules => "Business rules",
        AffectedArea.ExceptionHandling => "Error handling",
        _ => topic.ToString(),
    };

    public static string ShortLabel(AffectedArea topic) => topic switch
    {
        AffectedArea.Authorization => "Authorization",
        AffectedArea.AccessManagement => "Access",
        AffectedArea.ReferenceData => "Reference data",
        AffectedArea.DomainEvents => "Events",
        AffectedArea.OperationRegistration => "Operations",
        AffectedArea.HealthMonitoring => "Health",
        AffectedArea.Infrastructure => "Infrastructure",
        AffectedArea.BusinessRules => "Business rules",
        AffectedArea.ExceptionHandling => "Errors",
        _ => topic.ToString(),
    };

    /// <summary>The task text keywords are matched against: title, full line and related files, in original case.</summary>
    public static string TaskText(TaskItem task) =>
        $"{task.Title} {task.Description} {string.Join(' ', task.RelatedFileIds)}";

    /// <summary>
    /// The first term found in <paramref name="text"/> (built by <see cref="TaskText"/>), ignoring case. A term must
    /// start a word or a camelCase part — "CdcEvent" and "AdapterDbContext" match "event" and "dbcontext", while
    /// "prevent" and "resolution" do not match "event" and "solution". Terms of three or fewer characters must also
    /// end the word (di, api, sln).
    /// </summary>
    public static string? MatchedTerm(string text, IEnumerable<string> terms) =>
        terms.FirstOrDefault(term => Regex.IsMatch(
            text,
            $@"(?:(?<![A-Za-z0-9])|(?-i:(?<=[a-z0-9])(?=[A-Z]))){Regex.Escape(term)}{(term.Length <= 3 ? "(?![A-Za-z0-9])" : "")}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

}
