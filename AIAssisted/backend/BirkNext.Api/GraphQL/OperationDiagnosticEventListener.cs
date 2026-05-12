using HotChocolate.Execution;
using HotChocolate.Execution.Instrumentation;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;

namespace BirkNext.Api.GraphQL;

public sealed class OperationDiagnosticEventListener : ExecutionDiagnosticEventListener
{
    private readonly ILogger<OperationDiagnosticEventListener> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public OperationDiagnosticEventListener(
        ILogger<OperationDiagnosticEventListener> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public override IDisposable ExecuteRequest(IRequestContext context)
    {
        var start = Stopwatch.GetTimestamp();
        return new RequestScope(_logger, _httpContextAccessor, context, start);
    }

    private sealed class RequestScope(
        ILogger logger,
        IHttpContextAccessor httpContextAccessor,
        IRequestContext context,
        long start) : IDisposable
    {
        public void Dispose()
        {
            var durationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            var operationName = context.Operation?.Name
                ?? context.Request.OperationName
                ?? "(anonymous)";

            var correlationId = httpContextAccessor.HttpContext?
                .Response.Headers["X-Correlation-Id"]
                .FirstOrDefault() ?? "none";

            var projectId = TryGetProjectId(context.Variables);

            var hasErrors = context.Result is IOperationResult r && r.Errors?.Count > 0;

            logger.LogInformation(
                "GraphQLOperation {OperationName} {DurationMs}ms {ResultStatus} {CorrelationId} {ProjectId}",
                operationName, durationMs, hasErrors ? "error" : "ok", correlationId, projectId);
        }

        private static string TryGetProjectId(IReadOnlyList<IVariableValueCollection>? variables)
        {
            // `scenarios` exposes projectId as a top-level variable; `createScenario`
            // nests it inside `input` which is not addressable here without schema coercion.
            try
            {
                if (variables is { Count: > 0 }
                    && variables[0].TryGetVariable("projectId", out string? pid)
                    && pid is not null)
                    return pid;
            }
            catch
            {
                // Variable absent or wrong type — fall through to "none"
            }
            return "none";
        }
    }
}
