using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.Integrations.Scim;

/// <summary>
/// SCIM provisioning evidence from uploaded source: syntax-only analysis (Roslyn parse, no compilation, nothing executed), every fact with the
/// file and line it came from. It states what the source DOES — the routes it maps, the order of publish and commit, the no-op guards, what the
/// PATCH parser accepts, which health checks are registered — never that anything ran. SCIM is detected only from a mapped SCIM route surface;
/// Service Bus usage alone never detects SCIM. Specification text is classified against the source, never taken as implemented behaviour.
/// </summary>
public static class ScimSourceAnalyzer
{
    public const int Version = 1;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly string[] MapMethods = ["MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"];

    internal sealed record Code(string Path, CompilationUnitSyntax Root, string Text, string? Project, string ProjectDir, bool IsTest);

    private sealed record Publish(string? Topic, string? EventType, int Position, SourceLocation Location, bool RetryWrapped, bool InStrategy);

    private sealed record Handler(string Name, Code Code, MethodDeclarationSyntax Method, List<Publish> Publishes, int? SaveAt, int? CommitAt, bool Transaction,
        bool Strategy, bool NoOp, bool UpsertWhenMissing, bool GeneratedIdWhenMissing, bool SoftDelete, bool HardDelete, bool EventIdPerCall, int Logs, List<string> Metrics);

    public static ScimSourceEvidence Analyze(string environmentId, IReadOnlyList<SourceArchive> archives, ScimSourceSet files, DateTimeOffset now)
    {
        var projects = files.Code.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Dir: Dir(f.Path), Name: System.IO.Path.GetFileNameWithoutExtension(f.Path),
                IsTest: System.IO.Path.GetFileNameWithoutExtension(f.Path).Contains("Test", StringComparison.OrdinalIgnoreCase) || f.Content.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal)))
            .ToList();
        var code = files.Code.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(f =>
        {
            var owner = projects.Where(p => f.Path.StartsWith(p.Dir, StringComparison.Ordinal)).OrderByDescending(p => p.Dir.Length).Select(p => ((string, string, bool)?)(p.Name, p.Dir, p.IsTest)).FirstOrDefault();
            var tree = CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(f.Content), Parse, f.Path);
            var isTest = owner?.Item3 ?? f.Path.Contains("/tests/", StringComparison.OrdinalIgnoreCase);
            return new Code(f.Path, tree.GetCompilationUnitRoot(), f.Content, owner?.Item1, owner?.Item2 ?? Dir(f.Path), isTest);
        }).ToList();
        var production = code.Where(c => !c.IsTest).ToList();
        var consts = Constants(production);

        // ── Route surface ────────────────────────────────────────────────────────────────────────────────────────────
        var (operations, basePath, policy, routeCode) = Routes(production, consts);
        var detected = operations.Count > 0;
        var facts = new List<ScimSourceFact>();
        var limitations = new List<string>();
        if (!detected)
        {
            return new ScimSourceEvidence
            {
                EnvironmentId = environmentId, AnalyzedAt = now, AnalyzerVersion = Version, Archives = archives.ToList(), Detected = false,
                Facts = [Fact("scim-detected", ScimArea.Configuration, "SCIM adapter", ScimEvidenceState.NotFound,
                    code.Count == 0 ? "No C# source in the uploaded archives." : "No mapped SCIM route (e.g. MapGroup(\"/scim/v2\") with Users operations) in the analyzed source. Service Bus or identity code alone does not establish SCIM.")],
                Limitations = ["SCIM was not detected, so no SCIM behaviour was analyzed."],
            };
        }
        var scimProject = routeCode!.Project;
        var scimCode = production.Where(c => c.Project == scimProject).ToList();
        var program = scimCode.FirstOrDefault(c => System.IO.Path.GetFileName(c.Path) == "Program.cs");
        facts.Add(Fact("scim-detected", ScimArea.Configuration, "SCIM adapter", ScimEvidenceState.SourceVerified,
            $"{scimProject ?? "The analyzed project"} maps {operations.Count} SCIM operation(s) under {basePath ?? "(no group)"}.", operations.Select(o => o.Location).OfType<SourceLocation>().Take(1)));
        if (basePath is not null)
            facts.Add(Fact("scim-base-path", ScimArea.Protocol, "Base path", ScimEvidenceState.SourceVerified, $"MapGroup(\"{basePath}\").", operations.Select(o => o.Location).OfType<SourceLocation>().Take(1)));

        // ── Handlers ─────────────────────────────────────────────────────────────────────────────────────────────────
        var methods = production.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => (Code: c, Method: m))).ToList();
        var handlers = new Dictionary<string, Handler>(StringComparer.Ordinal);
        foreach (var op in operations.Where(o => o.Handler is not null))
            if (!handlers.ContainsKey(op.Handler!) && methods.Where(m => m.Method.Identifier.Text == op.Handler).OrderByDescending(m => m.Code.Project == scimProject).FirstOrDefault() is { Method: not null } found)
                handlers[op.Handler!] = AnalyzeHandler(found.Code, found.Method, consts);
        operations = operations.Select(op => op.Handler is { } h && handlers.TryGetValue(h, out var handler) ? Describe(op, handler) : op).ToList();

        var writes = operations.Where(o => o.Method is "POST" or "PATCH" or "DELETE" or "PUT").ToList();
        var writeHandlers = writes.Select(o => o.Handler).OfType<string>().Distinct().Where(handlers.ContainsKey).Select(h => handlers[h]).ToList();
        foreach (var op in operations)
            facts.Add(Fact($"scim-op-{op.Method.ToLowerInvariant()}-{Slug(op.Path)}", ScimArea.Protocol, $"{op.Method} {op.Path}", ScimEvidenceState.SourceVerified,
                string.Join(" ", op.Behaviour.Prepend(op.Responses.Count > 0 ? $"Responses: {string.Join(", ", op.Responses)}." : "")).Trim(), op.Location is { } l ? [l] : []));

        // Publish vs persistence order (the partial-success risk) — from the handlers, never inferred from names.
        var publishFirst = writeHandlers.Where(h => h.Publishes.Count > 0 && h.SaveAt is { } save && h.Publishes.Min(p => p.Position) < save).ToList();
        var saveFirst = writeHandlers.Where(h => h.Publishes.Count > 0 && h.SaveAt is { } save && h.Publishes.Min(p => p.Position) > save).ToList();
        if (publishFirst.Count > 0)
            facts.Add(Fact("scim-order-publish-before-commit", ScimArea.Reliability, "Publish happens before the KjentBruker write is committed", ScimEvidenceState.NeedsReview,
                $"{string.Join(", ", publishFirst.Select(h => h.Name))} await the Service Bus publish and only then call SaveChanges/Commit{(publishFirst.Any(h => h.Transaction) ? " inside a database transaction" : "")}. " +
                "A publish failure therefore rolls back and returns 5xx (no false 2xx, no state without event). The reverse partial success is possible: " +
                "if SaveChanges or Commit fails after the publish succeeded, the event is already on the topic while KjentBruker keeps the old state; " +
                "the 5xx makes Entra retry, and the retry publishes the event again" + (publishFirst.Any(h => h.EventIdPerCall) ? " with a NEW event id, so the consumer cannot de-duplicate it by id." : "."),
                publishFirst.SelectMany(h => h.Publishes.Take(1).Select(p => p.Location).Append(Loc(h.Code, h.Method)))));
        if (saveFirst.Count > 0)
            facts.Add(Fact("scim-order-commit-before-publish", ScimArea.Reliability, "KjentBruker is written before the publish", ScimEvidenceState.NeedsReview,
                $"{string.Join(", ", saveFirst.Select(h => h.Name))} save before publishing. If the publish then fails the state is persisted without its event" +
                (saveFirst.Any(h => h.NoOp) ? "; because an identical retry is a no-op for an already-stored state, the event can be lost permanently." : "."),
                saveFirst.Select(h => Loc(h.Code, h.Method))));
        if (writeHandlers.Any(h => h.Strategy && h.Publishes.Any(p => p.InStrategy)))
            facts.Add(Fact("scim-strategy-replays-publish", ScimArea.Reliability, "Database retry strategy re-runs the publish", ScimEvidenceState.NeedsReview,
                "The publish runs inside the EF Core execution strategy (EnableRetryOnFailure). A transient SQL failure at SaveChanges/Commit re-executes the whole delegate, including an already-successful publish — the event can be sent more than once within one request.",
                writeHandlers.Where(h => h.Strategy).Select(h => Loc(h.Code, h.Method))));
        if (writeHandlers.Any(h => h.NoOp))
            facts.Add(Fact("scim-idempotent-noop", ScimArea.Reliability, "Repeated identical request is a no-op", ScimEvidenceState.SourceVerified,
                "When the stored active state already equals the requested state the handler returns without publishing (no duplicate event for a repeat after a completed request).",
                writeHandlers.Where(h => h.NoOp).Select(h => Loc(h.Code, h.Method))));
        if (writeHandlers.FirstOrDefault(h => h.GeneratedIdWhenMissing) is { } generated)
            facts.Add(Fact("scim-post-generated-id", ScimArea.DataQuality, "POST without a GUID id gets a new random EntraObjectId", ScimEvidenceState.NeedsReview,
                $"{generated.Name} uses the request 'id' as EntraObjectId only when it parses as a GUID; otherwise Guid.NewGuid() is used and the lookup cannot find an earlier row. " +
                "SCIM (RFC 7643) treats 'id' as server-assigned, so a client that does not send it creates a new KjentBruker and a new BrukerAktivert on every repeated POST.",
                [Loc(generated.Code, generated.Method)]));
        if (writeHandlers.Any(h => h.EventIdPerCall))
            facts.Add(Fact("scim-event-id-per-attempt", ScimArea.Contract, "Event id is generated per attempt", ScimEvidenceState.NeedsReview,
                "HendelsesId is Guid.NewGuid() at the time the event is built; a retried request produces a different id for the same state change.",
                writeHandlers.Where(h => h.EventIdPerCall).Select(h => Loc(h.Code, h.Method))));
        var upserts = operations.Where(o => o.UnknownUser is not null && o.Method is "PATCH" or "DELETE").ToList();
        if (upserts.Count > 0)
            facts.Add(Fact("scim-unknown-user-upsert", ScimArea.Protocol, "Unknown user on PATCH/DELETE is created, not 404", ScimEvidenceState.NeedsReview,
                string.Join(" ", upserts.Select(o => $"{o.Method}: {o.UnknownUser}")), upserts.Select(o => o.Location).OfType<SourceLocation>()));
        if (operations.FirstOrDefault(o => o.Method == "DELETE") is { } delete && delete.Handler is { } dh && handlers.TryGetValue(dh, out var deleteHandler))
            facts.Add(Fact("scim-delete-semantics", ScimArea.Protocol, "DELETE semantics", deleteHandler.SoftDelete && !deleteHandler.HardDelete ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NeedsReview,
                deleteHandler.SoftDelete && !deleteHandler.HardDelete ? "Soft delete: IsActive = false, the row is kept, BrukerDeaktivert is published; HTTP 204 in every case." : deleteHandler.HardDelete ? "The handler removes the row." : "No IsActive = false assignment found in the DELETE handler.",
                [Loc(deleteHandler.Code, deleteHandler.Method)]));

        // ── PATCH parser ─────────────────────────────────────────────────────────────────────────────────────────────
        var scimMethods = methods.Where(m => m.Code.Project == scimProject).ToList();
        facts.AddRange(PatchParser(production, scimMethods));

        // ── Filter / enumeration ─────────────────────────────────────────────────────────────────────────────────────
        facts.AddRange(ListBehaviour(scimCode, scimMethods));

        // ── KjentBruker persistence ──────────────────────────────────────────────────────────────────────────────────
        facts.AddRange(Persistence(production, writeHandlers));

        // ── Authentication ───────────────────────────────────────────────────────────────────────────────────────────
        var scimSettings = files.Settings.Where(f => f.Path.StartsWith(routeCode.ProjectDir, StringComparison.Ordinal)).ToList();
        facts.AddRange(Authentication(production, scimCode, program, policy, scimSettings, operations.FirstOrDefault()?.Location));

        // ── Publisher, events, disabled fallback ─────────────────────────────────────────────────────────────────────
        var (publisherFacts, events) = Publishers(production, scimCode, program, writeHandlers, consts, scimSettings);
        facts.AddRange(publisherFacts);

        // ── Retry ────────────────────────────────────────────────────────────────────────────────────────────────────
        facts.AddRange(Retry(scimCode, writeHandlers));

        // ── Health, metrics, logging ─────────────────────────────────────────────────────────────────────────────────
        facts.AddRange(Health(production, program));
        facts.AddRange(Metrics(scimCode, production));
        facts.AddRange(Logging(scimCode, production, writeHandlers));

        // ── Downstream consumer and use of the synchronized state ────────────────────────────────────────────────────
        var topic = events.Select(e => e.Topic).OfType<string>().FirstOrDefault();
        var eventTypes = events.Select(e => e.EventType).ToList();
        var consumers = Consumers(production, scimProject, topic, eventTypes, consts, files.Documents);
        var stateUsage = StateUsage(production, scimProject);
        facts.AddRange(DownstreamFacts(topic, consumers, stateUsage, events));

        var scimTests = code.Where(c => c.IsTest && (c.Path.Contains("Scim", StringComparison.OrdinalIgnoreCase) || c.Text.Contains("/scim/v2", StringComparison.Ordinal) || c.Text.Contains("ScimUserService", StringComparison.Ordinal))).ToList();
        var coverage = ScimRequirementRules.TestCoverage(scimTests, facts);
        var (requirements, specSource) = ScimRequirementRules.Classify(files.Documents, facts, operations, events);
        if (specSource is null) limitations.Add("No SCIM specification (spec.md with FR-requirements) in the uploaded archives: requirements are not classified.");
        limitations.Add("Syntax-only analysis: behaviour of referenced libraries whose source was not uploaded is not assessed.");
        if (!production.Any(c => c.Text.Contains("MapHealthChecks", StringComparison.Ordinal)) && facts.Any(f => f.Id == "scim-health-mapping" && f.State == ScimEvidenceState.NotAssessed))
            limitations.Add("The health endpoint mapping comes from a shared library whose source was not uploaded; upload it (e.g. M2LB.Common) to resolve paths and predicates.");

        return new ScimSourceEvidence
        {
            EnvironmentId = environmentId, AnalyzedAt = now, AnalyzerVersion = Version, Archives = archives.ToList(), Detected = true, Project = scimProject, BasePath = basePath,
            Operations = operations, Facts = facts, Events = events, Requirements = requirements, SpecificationSource = specSource, TestCoverage = coverage,
            Consumers = consumers, StateUsage = stateUsage, Limitations = limitations,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static ScimSourceFact Fact(string id, ScimArea area, string title, ScimEvidenceState state, string detail, IEnumerable<SourceLocation>? locations = null) =>
        new() { Id = id, Area = area, Title = title, State = state, Detail = detail, Locations = locations?.Distinct().Take(6).ToList() ?? [] };

    internal static SourceLocation Loc(Code code, SyntaxNode node) => new(code.Path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static string Dir(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    /// <summary>A source line for display: string literals masked (they can hold names, e-mails or secrets) except an allowed entity name, bounded.</summary>
    internal static string Snippet(string text, string? keep)
    {
        var masked = Regex.Replace(Regex.Replace(text, @"\s+", " ").Trim(), @"@?""(?:[^""\\]|\\.)*""", m => keep is not null && m.Value.Trim('@', '"') == keep ? m.Value : "\"…\"");
        return masked.Length > 160 ? masked[..160] + "…" : masked;
    }

    private static string Slug(string path) => Regex.Replace(path.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string? Literal(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal ? literal.Token.ValueText : null;

    /// <summary><c>const string</c> fields by bare name and by <c>Class.Name</c>.</summary>
    private static Dictionary<string, string> Constants(List<Code> code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in code.SelectMany(c => c.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()).Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))))
            foreach (var variable in field.Declaration.Variables)
                if (Literal(variable.Initializer?.Value) is { } value)
                {
                    map.TryAdd(variable.Identifier.Text, value);
                    if (field.Parent is TypeDeclarationSyntax type) map.TryAdd($"{type.Identifier.Text}.{variable.Identifier.Text}", value);
                }
        return map;
    }

    private static string? Resolve(ExpressionSyntax? expression, Dictionary<string, string> consts) => expression switch
    {
        null => null,
        LiteralExpressionSyntax => Literal(expression),
        MemberAccessExpressionSyntax member when consts.TryGetValue($"{(member.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text ?? (member.Expression as IdentifierNameSyntax)?.Identifier.Text}.{member.Name.Identifier.Text}", out var qualified) => qualified,
        MemberAccessExpressionSyntax member when consts.TryGetValue(member.Name.Identifier.Text, out var bare) => bare,
        IdentifierNameSyntax identifier when consts.TryGetValue(identifier.Identifier.Text, out var value) => value,
        _ => null,
    };

    private static string Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        _ => "",
    };

    /// <summary>The fluent chain an invocation is the receiver of (e.g. MapGroup(..).RequireAuthorization(..).WithTags(..)).</summary>
    private static IEnumerable<InvocationExpressionSyntax> Chain(InvocationExpressionSyntax invocation)
    {
        SyntaxNode current = invocation;
        while (current.Parent is MemberAccessExpressionSyntax member && member.Parent is InvocationExpressionSyntax outer)
        {
            yield return outer;
            current = outer;
        }
    }

    // ── Routes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static (List<ScimOperation> Operations, string? BasePath, string? Policy, Code? Code) Routes(List<Code> code, Dictionary<string, string> consts)
    {
        var operations = new List<ScimOperation>();
        string? basePath = null, policy = null;
        Code? owner = null;
        foreach (var c in code)
        {
            var groups = new Dictionary<string, (string Path, string? Policy)>(StringComparer.Ordinal);
            foreach (var invocation in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Name(i) == "MapGroup"))
            {
                if (Resolve(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression, consts) is not { } path) continue;
                var groupPolicy = Chain(invocation).Where(i => Name(i) == "RequireAuthorization").Select(i => Resolve(i.ArgumentList.Arguments.FirstOrDefault()?.Expression, consts) ?? "(default policy)").FirstOrDefault();
                var declarator = invocation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                if (declarator is not null) groups[declarator.Identifier.Text] = (path, groupPolicy);
            }
            foreach (var invocation in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MapMethods.Contains(Name(i))))
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member || Resolve(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression, consts) is not { } route) continue;
                var receiver = (member.Expression as IdentifierNameSyntax)?.Identifier.Text;
                var prefix = receiver is not null && groups.TryGetValue(receiver, out var group) ? group : ("", (string?)null);
                var full = (prefix.Item1.TrimEnd('/') + "/" + route.TrimStart('/')).Replace("//", "/");
                // A SCIM route surface is any mapped route with a "scim" path segment; its actual base path is reported, never assumed.
                if (!full.Split('/').Any(s => s.Equals("scim", StringComparison.OrdinalIgnoreCase))) continue;
                var lambda = invocation.ArgumentList.Arguments.Skip(1).Select(a => a.Expression).OfType<LambdaExpressionSyntax>().FirstOrDefault();
                var handler = lambda?.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(i => i.Expression is MemberAccessExpressionSyntax m && m.Expression is IdentifierNameSyntax id && id.Identifier.Text is not ("Results" or "TypedResults" or "Math" or "StatusCodes"))
                    .Select(Name).FirstOrDefault();
                var chainText = string.Join(" ", Chain(invocation).Select(i => i.ToString()));
                var responses = Regex.Matches((lambda?.ToString() ?? "") + " " + chainText, @"Status(\d{3})\w*|Results\.(Created|Ok|NoContent|NotFound|BadRequest|Unauthorized)")
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value switch { "Created" => "201", "Ok" => "200", "NoContent" => "204", "NotFound" => "404", "BadRequest" => "400", _ => "401" })
                    .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                operations.Add(new ScimOperation
                {
                    Method = Name(invocation)[3..].ToUpperInvariant(), Path = full, Handler = handler, Responses = responses, Location = Loc(c, invocation),
                });
                if (prefix.Item1.Length > 0) { basePath ??= prefix.Item1; policy ??= prefix.Item2; }
                owner ??= c;
            }
        }
        return (operations, basePath, policy, owner);
    }

    // ── Handler behaviour ───────────────────────────────────────────────────────────────────────────────────────────

    private static Handler AnalyzeHandler(Code code, MethodDeclarationSyntax method, Dictionary<string, string> consts)
    {
        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var publishes = invocations.Where(i => Name(i) is "PublishAsync" or "SendMessageAsync" or "SendAsync" && i.ArgumentList.Arguments.Count > 0).Select(i =>
        {
            var args = i.ArgumentList.Arguments;
            var executes = i.Ancestors().OfType<InvocationExpressionSyntax>().Where(a => Name(a) == "ExecuteAsync").ToList();
            return new Publish(Resolve(args[0].Expression, consts), args.Count > 2 ? Resolve(args[2].Expression, consts) : null, i.SpanStart, Loc(code, i),
                executes.Any(a => a.Expression.ToString().Contains("pipeline", StringComparison.OrdinalIgnoreCase)),
                executes.Any(a => a.Expression.ToString().Contains("strategy", StringComparison.OrdinalIgnoreCase)));
        }).ToList();
        int? First(string name) => invocations.Where(i => Name(i) == name).Select(i => (int?)i.SpanStart).Min();
        var text = method.ToString();
        var ifs = method.DescendantNodes().OfType<IfStatementSyntax>().ToList();
        var noOp = ifs.Any(s => Regex.IsMatch(s.Condition.ToString(), @"IsActive\s*==|==\s*\w*[Aa]ctive|!\s*\w+\.IsActive") && s.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());
        var upsert = ifs.Any(s => s.Condition.ToString().Contains("is null", StringComparison.Ordinal) && s.Statement.ToString().Contains("new ", StringComparison.Ordinal) && s.Statement.ToString().Contains(".Add(", StringComparison.Ordinal));
        var generated = method.DescendantNodes().OfType<ConditionalExpressionSyntax>().Any(c => c.ToString().Contains(".Id", StringComparison.Ordinal) && c.ToString().Contains("Guid.NewGuid()", StringComparison.Ordinal));
        var softDelete = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.Left.ToString().EndsWith("IsActive", StringComparison.Ordinal) && a.Right.ToString() == "false");
        var eventIdPerCall = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Any(o => o.Type.ToString().EndsWith("Event", StringComparison.Ordinal) && o.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Contains("Guid.NewGuid()", StringComparison.Ordinal) == true);
        var logs = invocations.Count(i => Name(i).StartsWith("Log", StringComparison.Ordinal) && i.Expression.ToString().Contains("logger", StringComparison.OrdinalIgnoreCase));
        var metrics = invocations.Where(i => Name(i).StartsWith("Record", StringComparison.Ordinal)).Select(Name).Distinct().ToList();
        return new Handler(method.Identifier.Text, code, method, publishes, First("SaveChangesAsync") ?? First("SaveChanges"), First("CommitAsync"),
            text.Contains("BeginTransaction", StringComparison.Ordinal), text.Contains("CreateExecutionStrategy", StringComparison.Ordinal), noOp, upsert, generated, softDelete,
            invocations.Any(i => Name(i) is "Remove" or "RemoveRange" or "ExecuteDeleteAsync"), eventIdPerCall, logs, metrics);
    }

    private static ScimOperation Describe(ScimOperation op, Handler h)
    {
        var behaviour = new List<string>();
        var eventTypes = h.Publishes.Select(p => p.EventType).OfType<string>().Distinct().ToList();
        if (h.Publishes.Count > 0)
        {
            var order = h.SaveAt is { } save ? h.Publishes.Min(p => p.Position) < save ? "before SaveChanges/Commit" : "after SaveChanges" : "without a database write in the handler";
            behaviour.Add($"Publishes {string.Join(" / ", eventTypes.DefaultIfEmpty("an event"))} to {h.Publishes.Select(p => p.Topic).OfType<string>().FirstOrDefault() ?? "(unresolved topic)"} {order}{(h.Transaction ? " inside a database transaction" : "")}{(h.Publishes.All(p => p.RetryWrapped) ? ", through the retry pipeline" : "")}.");
        }
        else if (op.Method is not "GET") behaviour.Add("No publish in the handler.");
        if (h.NoOp) behaviour.Add("No-op (no event) when the stored active state already equals the requested state.");
        if (h.GeneratedIdWhenMissing) behaviour.Add("EntraObjectId = request id when it is a GUID, otherwise a new random GUID.");
        if (op.Method == "DELETE") behaviour.Add(h.SoftDelete && !h.HardDelete ? "Soft delete (IsActive = false); the row is kept." : h.HardDelete ? "Removes the row." : "");
        string? unknown = null;
        if (h.UpsertWhenMissing && op.Method is "PATCH" or "DELETE")
            unknown = $"an unknown id is created as a KjentBruker{(eventTypes.Count > 0 ? $" and {string.Join("/", eventTypes)} is published" : "")} (no 404).";
        return op with { Behaviour = behaviour.Where(b => b.Length > 0).ToList(), UnknownUser = unknown };
    }

    // ── PATCH parser ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ScimSourceFact> PatchParser(List<Code> code, List<(Code Code, MethodDeclarationSyntax Method)> methods)
    {
        var parser = methods.FirstOrDefault(m => m.Method.ToString() is var t && t.Contains("Operations", StringComparison.Ordinal) && t.Contains("GetBoolean", StringComparison.Ordinal));
        if (parser.Method is null)
        {
            yield return Fact("scim-patch-parser", ScimArea.Protocol, "PATCH parser", ScimEvidenceState.NotFound, "No PatchOp parser (Operations with a boolean active value) was found.");
            yield break;
        }
        var text = parser.Method.ToString();
        var ops = Regex.Matches(text, @"\.Op\??\.Equals\(""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        var paths = Regex.Matches(text, @"\.Path\??\.Equals\(""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        var swallowed = parser.Method.DescendantNodes().OfType<CatchClauseSyntax>().Any(c => !c.Block.Statements.Any());
        var nullGuard = Regex.IsMatch(text, @"Operations\s*(is\s+null|==\s*null|\?\.|\?\?)");
        var pathless = Regex.IsMatch(text, @"Path\s*(is\s+null|==\s*null)|IsNullOrEmpty\(\w*\.Path");
        var stringValue = text.Contains("GetString", StringComparison.Ordinal) || text.Contains("bool.TryParse", StringComparison.Ordinal) || text.Contains("Boolean.TryParse", StringComparison.Ordinal);
        var location = Loc(parser.Code, parser.Method);
        yield return Fact("scim-patch-parser", ScimArea.Protocol, "PATCH is SCIM PatchOp", ScimEvidenceState.SourceVerified,
            $"Reads the Operations array; acts only on op {string.Join("/", ops.DefaultIfEmpty("(none)"))} with path {string.Join("/", paths.DefaultIfEmpty("(none)"))} and a JSON boolean value. JSON Merge Patch is not parsed.", [location]);
        if (!stringValue)
            yield return Fact("scim-patch-string-boolean", ScimArea.Security, "PATCH active value as a string is ignored", ScimEvidenceState.NeedsReview,
                "The value is read with GetBoolean()" + (swallowed ? " inside a try with an empty catch" : "") + ": a value sent as the string \"False\"/\"True\" is not applied. " +
                "Microsoft documents that Entra can send PATCH boolean values as strings unless the application's SCIM compliance flag is set — not verified for this tenant.", [location]);
        if (!pathless)
            yield return Fact("scim-patch-pathless", ScimArea.Security, "PATCH without a path is ignored", ScimEvidenceState.NeedsReview,
                "An operation without 'path' (value {\"active\": false}) is not recognised. Together with the handler rule below, such a deactivation returns HTTP 200 without changing state.", [location]);
        if (!nullGuard)
            yield return Fact("scim-patch-missing-operations", ScimArea.ErrorHandling, "Missing Operations is not validated", ScimEvidenceState.NeedsReview,
                "The parser iterates Operations without a null check: a PatchOp body without Operations fails with an unhandled exception (HTTP 500), not a SCIM 400 error.", [location]);
        var handler = methods.FirstOrDefault(m => m.Method.ToString() is var t && t.Contains(parser.Method.Identifier.Text + "(", StringComparison.Ordinal) && m.Method != parser.Method);
        if (handler.Method is not null && Regex.IsMatch(handler.Method.ToString(), @"is\s+null\)\s*\{[^}]*return\s+existing\s+is\s+null\s*\?\s*null", RegexOptions.Singleline))
            yield return Fact("scim-patch-unrecognised-200", ScimArea.Security, "Unrecognised PATCH returns 200 unchanged", ScimEvidenceState.NeedsReview,
                "When no active operation is recognised the handler returns the stored user unchanged (HTTP 200) — the caller sees success although nothing was applied.", [Loc(handler.Code, handler.Method)]);
    }

    private static IEnumerable<ScimSourceFact> ListBehaviour(List<Code> scimCode, List<(Code Code, MethodDeclarationSyntax Method)> methods)
    {
        var list = methods.FirstOrDefault(m => m.Method.ToString() is var t && t.Contains("filter", StringComparison.Ordinal) && t.Contains("Skip(", StringComparison.Ordinal));
        if (list.Method is null) yield break;
        var text = list.Method.ToString();
        var fields = Regex.Matches(text, @"Equals\(""(\w+)"",\s*StringComparison").Select(m => m.Groups[1].Value).Distinct().ToList();
        yield return Fact("scim-list", ScimArea.Protocol, "GET /Users pagination and filter", ScimEvidenceState.SourceVerified,
            $"startIndex/count pagination; 'eq' filter on {string.Join(", ", fields.DefaultIfEmpty("(no field)"))}. Returns every KjentBruker (active and inactive) with its current active state.", [Loc(list.Code, list.Method)]);
        if (Regex.IsMatch(text, @"Unsupported", RegexOptions.IgnoreCase) || !text.Contains("BadRequest", StringComparison.Ordinal))
            yield return Fact("scim-list-unsupported-filter", ScimArea.Privacy, "Unsupported filter returns the unfiltered list", ScimEvidenceState.NeedsReview,
                "A filter on another attribute or operator is ignored (logged at debug), so the response is the full paginated user list instead of a SCIM 400 invalidFilter error.", [Loc(list.Code, list.Method)]);
        var cap = scimCode.Select(c => Regex.Match(c.Text, @"Math\.Min\(count,\s*(\d+)\)")).FirstOrDefault(m => m.Success);
        if (cap is not null) yield return Fact("scim-list-cap", ScimArea.Protocol, "Page size cap", ScimEvidenceState.SourceVerified, $"count is capped at {cap.Groups[1].Value}.");
    }

    // ── Authentication ──────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ScimSourceFact> Authentication(List<Code> production, List<Code> scimCode, Code? program, string? policy, List<ScimSettingsFile> settings, SourceLocation? groupLocation)
    {
        var policies = scimCode.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Name(i) == "AddPolicy")
            .Where(i => policy is null || Literal(i.ArgumentList.Arguments.FirstOrDefault()?.Expression) == policy)
            .Select(i => (Code: c, Invocation: i, Method: i.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "(top level)"))).ToList();
        var jwt = scimCode.FirstOrDefault(c => c.Text.Contains("AddMicrosoftIdentityWebApi", StringComparison.Ordinal) || c.Text.Contains("AddJwtBearer", StringComparison.Ordinal));
        if (policy is not null)
            yield return Fact("scim-auth-required", ScimArea.Security, "Authorization required on the SCIM group", ScimEvidenceState.SourceVerified,
                $"RequireAuthorization(\"{policy}\") on the route group: requests without an accepted identity are rejected (401) before the handler runs, so no state change or publish can follow.",
                groupLocation is null ? null : [groupLocation]);
        foreach (var (code, invocation, method) in policies)
        {
            var body = invocation.ToString();
            var claims = Regex.Matches(body, @"RequireClaim\(([^,]+),\s*([^)]+)\)").Select(m => $"{m.Groups[1].Value.Trim()} = {m.Groups[2].Value.Trim()}").ToList();
            var allowAll = Regex.IsMatch(body, @"RequireAssertion\(\s*_\s*=>\s*true\s*\)");
            var local = program is not null && program.Root.DescendantNodes().OfType<IfStatementSyntax>().Any(s => s.Condition.ToString().Contains("IsLocal()", StringComparison.Ordinal) && s.Statement.ToString().Contains(method, StringComparison.Ordinal));
            if (allowAll)
                yield return Fact($"scim-auth-policy-{Slug(method)}", ScimArea.Security, "Development policy allows every caller", local ? ScimEvidenceState.SourceVerified : ScimEvidenceState.IssueDetected,
                    $"{method} registers the policy with RequireAssertion(_ => true){(local ? " and Program.cs selects it only when the environment is Local" : " — not guarded by a Local-only condition in Program.cs")}.", [Loc(code, invocation)]);
            else if (claims.Count > 0)
                yield return Fact($"scim-auth-policy-{Slug(method)}", ScimArea.Security, "Token claims required", ScimEvidenceState.SourceVerified,
                    $"{method}: {string.Join("; ", claims)}{(jwt is not null ? ". Tokens are Entra ID JWTs validated by Microsoft.Identity.Web (issuer, signature, audience)." : ".")}", [Loc(code, invocation)]);
        }
        var devHandler = scimCode.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => t.BaseList?.ToString().Contains("AuthenticationHandler", StringComparison.Ordinal) == true).Select(t => (Code: c, Type: t))).FirstOrDefault();
        if (devHandler.Type is not null)
            yield return Fact("scim-auth-dev-handler", ScimArea.Security, "Development authentication handler", devHandler.Type.ToString().Contains("IsLocal()", StringComparison.Ordinal) ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NeedsReview,
                $"{devHandler.Type.Identifier.Text} accepts an unvalidated bearer value{(devHandler.Type.ToString().Contains("IsLocal()", StringComparison.Ordinal) ? " and returns NoResult outside the Local environment" : " — no Local-only guard found")}.", [Loc(devHandler.Code, devHandler.Type)]);
        var secretProperty = scimCode.SelectMany(c => c.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => p.Identifier.Text.Contains("Secret", StringComparison.Ordinal)).Select(p => (Code: c, Property: p))).FirstOrDefault();
        if (secretProperty.Property is not null)
        {
            var name = secretProperty.Property.Identifier.Text;
            var uses = production.Sum(c => c.Root.DescendantTokens().Count(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == name)) - 1;
            yield return Fact("scim-auth-provisioning-secret", ScimArea.Security, "Provisioning secret option", uses > 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NeedsReview,
                uses > 0 ? $"{name} is referenced {uses} time(s)." : $"{name} is declared but never read: authentication does not use a shared provisioning secret (the API description still mentions one).",
                [Loc(secretProperty.Code, secretProperty.Property)]);
        }
        var keyVault = scimCode.FirstOrDefault(c => Regex.IsMatch(c.Text, @"AddAzureKeyVault|SecretClient|KeyVaultSecret"));
        yield return Fact("scim-keyvault", ScimArea.Security, "Key Vault at startup", keyVault is null ? ScimEvidenceState.NotFound : ScimEvidenceState.SourceVerified,
            keyVault is null ? "No Key Vault configuration provider or secret client in the SCIM adapter project — there is no Key Vault read at startup and so no Key Vault fail-fast path." : $"Key Vault is referenced in {keyVault.Path}.",
            keyVault is null ? null : [new SourceLocation(keyVault.Path, 1)]);
        var tenants = settings.Where(s => s.Values.TryGetValue("AzureAd:TenantId", out var t) && t.Equals("common", StringComparison.OrdinalIgnoreCase)).Select(s => s.Path).ToList();
        if (tenants.Count > 0)
            yield return Fact("scim-auth-multitenant", ScimArea.Security, "Token issuer from any tenant", ScimEvidenceState.NeedsReview,
                $"AzureAd:TenantId is \"common\" in {string.Join(", ", tenants.Select(System.IO.Path.GetFileName))}: tokens from any Entra tenant pass issuer validation; the required appid and oid claims are what restrict the caller.",
                tenants.Select(t => new SourceLocation(t, 1)));
        if (program is not null)
        {
            if (Regex.IsMatch(program.Text, @"AllowAnyOrigin\(\)"))
                yield return Fact("scim-cors-any-origin", ScimArea.Security, "CORS allows any origin", ScimEvidenceState.NeedsReview, "UseCors(...AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()). A server-to-server provisioning endpoint does not need browser cross-origin access.", [new SourceLocation(program.Path, Line(program.Text, "AllowAnyOrigin"))]);
            if (Regex.IsMatch(program.Text, @"MapOpenApi\(\)\s*;") || Regex.IsMatch(program.Text, @"MapScalarApiReference\(\)\s*;"))
                yield return Fact("scim-openapi-anonymous", ScimArea.Security, "API description is anonymous", ScimEvidenceState.NeedsReview, "MapOpenApi()/MapScalarApiReference() are mapped without RequireAuthorization: the route surface is readable without a token (metadata only, no user data).", [new SourceLocation(program.Path, Line(program.Text, "MapOpenApi"))]);
        }
    }

    internal static int Line(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 1 : text.AsSpan(0, index).Count('\n') + 1;
    }

    // ── KjentBruker persistence ─────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ScimSourceFact> Persistence(List<Code> production, List<Handler> handlers)
    {
        var handlerText = string.Join("\n", handlers.Select(h => h.Method.ToString()));
        var set = production.SelectMany(c => c.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Type is GenericNameSyntax { Identifier.Text: "DbSet" } && handlerText.Contains("." + p.Identifier.Text, StringComparison.Ordinal)).Select(p => (Code: c, Property: p)))
            .FirstOrDefault();
        if (set.Property is null)
        {
            yield return Fact("scim-persistence", ScimArea.Configuration, "User state persistence", ScimEvidenceState.NotFound, "No DbSet used by the SCIM handlers was found.");
            yield break;
        }
        var entity = ((GenericNameSyntax)set.Property.Type).TypeArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "?";
        var context = (set.Property.Parent as TypeDeclarationSyntax)?.Identifier.Text ?? "?";
        var entityType = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => t.Identifier.Text == entity).Select(t => (Code: c, Type: t))).FirstOrDefault();
        var properties = entityType.Type?.Members.OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToList() ?? [];
        var configuration = production.Select(c => (Code: c, Match: Regex.Match(c.Text, $@"EntityTypeBuilder<{Regex.Escape(entity)}>[\s\S]*?ToTable\(""([^""]+)""\)[\s\S]*?HasKey\(\w+\s*=>\s*\w+\.(\w+)\)"))).FirstOrDefault(x => x.Match.Success);
        yield return Fact("scim-persistence", ScimArea.Configuration, $"{entity} persistence", ScimEvidenceState.SourceVerified,
            $"{context}.{set.Property.Identifier.Text} (DbSet<{entity}>){(configuration.Code is not null ? $", table {configuration.Match.Groups[1].Value}, key {configuration.Match.Groups[2].Value}" : "")}; stored fields {string.Join(", ", properties)}.",
            new[] { Loc(set.Code, set.Property) }.Concat(entityType.Type is null ? [] : [Loc(entityType.Code, entityType.Type)]));
        var identity = properties.Where(p => p.Contains("UserName", StringComparison.OrdinalIgnoreCase) || p.Contains("Email", StringComparison.OrdinalIgnoreCase) || p.Contains("ExternalId", StringComparison.OrdinalIgnoreCase)).ToList();
        if (identity.Count > 0 && entityType.Type is not null)
            yield return Fact("scim-persistence-identity", ScimArea.Privacy, "Identity attributes are stored", ScimEvidenceState.NeedsReview,
                $"{entity} keeps {string.Join(" and ", identity)} from the SCIM request besides the object id and active state. In Entra provisioning these usually carry a user principal name or e-mail; they are returned by GET /Users.",
                [Loc(entityType.Code, entityType.Type)]);
    }

    // ── Publisher and event contracts ───────────────────────────────────────────────────────────────────────────────

    private static (List<ScimSourceFact> Facts, List<ScimEventContract> Events) Publishers(List<Code> production, List<Code> scimCode, Code? program, List<Handler> handlers,
        Dictionary<string, string> consts, List<ScimSettingsFile> settings)
    {
        var facts = new List<ScimSourceFact>();
        var publisher = production.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(t => (Code: c, Type: t)))
            .FirstOrDefault(t => t.Type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "PublishAsync" && m.ToString().Contains("new ServiceBusMessage", StringComparison.Ordinal)));
        string? relation = null, properties = null;
        bool? session = null;
        var camel = false;
        if (publisher.Type is not null)
        {
            var method = publisher.Type.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "PublishAsync" && m.ToString().Contains("new ServiceBusMessage", StringComparison.Ordinal));
            var text = method.ToString();
            var messageId = Regex.Match(text, @"MessageId\s*=\s*(\w+)");
            var generatedHere = messageId.Success && Regex.IsMatch(text, $@"var\s+{Regex.Escape(messageId.Groups[1].Value)}\s*=\s*Guid\.NewGuid\(\)");
            relation = !messageId.Success ? "The transport MessageId is not set (Service Bus assigns one)."
                : generatedHere ? "MessageId (and the HendelsesId application property) is a new GUID generated by the publisher for every send — it is NOT the event body's HendelsesId."
                : $"MessageId = {messageId.Groups[1].Value}.";
            properties = string.Join(", ", Regex.Matches(text, @"\[""(\w+)""\]\s*=").Select(m => m.Groups[1].Value).Distinct());
            session = text.Contains("SessionId", StringComparison.Ordinal);
            camel = publisher.Type.ToString().Contains("JsonNamingPolicy.CamelCase", StringComparison.Ordinal);
            facts.Add(Fact("scim-publisher", ScimArea.Contract, "Service Bus publisher", ScimEvidenceState.SourceVerified,
                $"{publisher.Type.Identifier.Text}.PublishAsync creates a sender per call and awaits SendMessageAsync (JSON body{(camel ? ", camelCase" : "")}, Subject = event type, application properties {properties}). {relation}" +
                (session == true ? " SessionId is set." : " No SessionId is set."), [Loc(publisher.Code, method)]));
            if (generatedHere)
                facts.Add(Fact("scim-message-id-mismatch", ScimArea.Contract, "Body HendelsesId differs from MessageId", ScimEvidenceState.NeedsReview,
                    "The event body carries the HendelsesId the handler generated, while MessageId and the HendelsesId application property carry a second GUID from the publisher. A consumer de-duplicating on one of them cannot correlate it with the other, and Service Bus duplicate detection (by MessageId) never sees a retry as a duplicate.",
                    [Loc(publisher.Code, method)]));
        }
        var disabled = scimCode.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(t => (Code: c, Type: t)))
            .FirstOrDefault(t => t.Type.BaseList?.ToString().Contains("EventPublisher", StringComparison.Ordinal) == true
                && t.Type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "PublishAsync" && m.ToString().Contains("Task.CompletedTask", StringComparison.Ordinal) && !m.ToString().Contains("Send", StringComparison.Ordinal)));
        if (disabled.Type is not null)
        {
            var keys = program is null ? [] : Regex.Matches(program.Text, @"""(ServiceBus:[\w:]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
            var empty = settings.Where(s => s.Values.TryGetValue("ServiceBus:FQDN", out var v) && v == "(empty)")
                .Select(s => System.IO.Path.GetFileName(s.Path)).ToList();
            facts.Add(Fact("scim-disabled-publisher", ScimArea.ErrorHandling, "Service Bus disabled fallback returns success without sending", ScimEvidenceState.NeedsReview,
                $"{disabled.Type.Identifier.Text} is registered when {string.Join(" or ", keys.Select(k => k.EndsWith("Disabled", StringComparison.Ordinal) ? $"{k} is true" : $"{k} is empty"))}. It logs at debug level and returns without publishing, " +
                "so every SCIM request would get HTTP 2xx with no BrukerAktivert/BrukerDeaktivert event." +
                (empty.Count > 0 ? $" ServiceBus:FQDN is empty in {string.Join(", ", empty)} — the deployed value must come from the environment." : ""),
                [Loc(disabled.Code, disabled.Type)]));
        }
        var records = production.SelectMany(c => c.Root.DescendantNodes().OfType<RecordDeclarationSyntax>().Select(r => (Code: c, Record: r))).ToList();
        var events = new List<ScimEventContract>();
        foreach (var group in handlers.SelectMany(h => h.Publishes.Select(p => (Handler: h, Publish: p))).Where(x => x.Publish.EventType is not null).GroupBy(x => x.Publish.EventType!))
        {
            var record = records.FirstOrDefault(r => r.Record.Identifier.Text == group.Key || r.Record.Identifier.Text == group.Key + "Event");
            var fields = record.Record?.ParameterList?.Parameters.Select(p => camel ? JsonNamingPolicy.CamelCase.ConvertName(p.Identifier.Text) : p.Identifier.Text).ToList() ?? [];
            events.Add(new ScimEventContract
            {
                EventType = group.Key, Topic = group.Select(x => x.Publish.Topic).OfType<string>().FirstOrDefault(), BodyFields = fields,
                MessageProperties = properties?.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
                Operations = group.Select(x => x.Handler.Name).Distinct().ToList(), IdentifierRelation = relation, SessionIdSet = session,
                IdentityFields = fields.Where(f => f.Contains("ObjectId", StringComparison.OrdinalIgnoreCase) || f.Contains("userName", StringComparison.OrdinalIgnoreCase) || f.Contains("email", StringComparison.OrdinalIgnoreCase)).ToList(),
                Location = record.Record is null ? null : Loc(record.Code, record.Record),
            });
        }
        foreach (var e in events)
            facts.Add(Fact($"scim-event-{Slug(e.EventType)}", ScimArea.Contract, $"{e.EventType} contract", e.BodyFields.Count > 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NotFound,
                e.BodyFields.Count > 0 ? $"Topic {e.Topic ?? "(unresolved)"}; body fields {string.Join(", ", e.BodyFields)}; identity field(s) {string.Join(", ", e.IdentityFields.DefaultIfEmpty("none"))} — no name or e-mail in the event. Published by {string.Join(", ", e.Operations)}."
                    : $"The {e.EventType} record was not found in the analyzed source.", e.Location is { } l ? [l] : null));
        return (facts, events);
    }

    // ── Retry ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ScimSourceFact> Retry(List<Code> scimCode, List<Handler> handlers)
    {
        foreach (var c in scimCode)
            foreach (var creation in c.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => o.Type.ToString() == "RetryStrategyOptions"))
            {
                var values = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().ToDictionary(a => a.Left.ToString(), a => a.Right.ToString()) ?? [];
                var attempts = values.TryGetValue("MaxRetryAttempts", out var a) && int.TryParse(a, out var n) ? n : 3;
                var delayMs = values.TryGetValue("Delay", out var d) && Regex.Match(d, @"From(Milliseconds|Seconds)\((\d+)\)") is { Success: true } dm
                    ? int.Parse(dm.Groups[2].Value) * (dm.Groups[1].Value == "Seconds" ? 1000 : 1) : 2000;
                var exponential = values.TryGetValue("BackoffType", out var b) && b.Contains("Exponential", StringComparison.Ordinal);
                var waits = Enumerable.Range(0, attempts).Select(i => exponential ? delayMs * (int)Math.Pow(2, i) : delayMs).ToList();
                var wrapped = handlers.Count(h => h.Publishes.Count > 0 && h.Publishes.All(p => p.RetryWrapped));
                yield return Fact("scim-retry-policy", ScimArea.Reliability, "Publish retry policy", ScimEvidenceState.SourceVerified,
                    $"Polly: {attempts} retries after the first attempt, {(exponential ? "exponential" : "constant")} delay {string.Join(" / ", waits.Select(w => $"{w} ms"))}" +
                    $"{(values.TryGetValue("UseJitter", out var j) ? $", jitter {j}" : "")}; {(values.ContainsKey("ShouldHandle") ? "handles the configured exceptions" : "no ShouldHandle — every exception except cancellation is retried")}. " +
                    $"{wrapped} publishing handler(s) run the publish through it. After the last failure the exception propagates and ASP.NET Core returns 500. Configured, not observed.",
                    [Loc(c, creation)]);
            }
        var ef = scimCode.Select(c => (Code: c, Match: Regex.Match(c.Text, @"EnableRetryOnFailure\(([^)]*)\)"))).FirstOrDefault(x => x.Match.Success);
        if (ef.Code is not null)
            yield return Fact("scim-retry-sql", ScimArea.Reliability, "SQL retry (EF Core execution strategy)", ScimEvidenceState.SourceVerified,
                $"EnableRetryOnFailure({ef.Match.Groups[1].Value}) — transient SQL errors re-run the transaction delegate. Configured, not observed.", [new SourceLocation(ef.Code.Path, Line(ef.Code.Text, "EnableRetryOnFailure"))]);
        yield return Fact("scim-dlq-sender", ScimArea.ErrorHandling, "Dead-letter semantics", ScimEvidenceState.SourceVerified,
            "The adapter only sends. A failed send never reaches a dead-letter queue: it is retried and then surfaces as HTTP 5xx to Entra. Service Bus dead-letters only messages a subscription could not deliver or a consumer rejected — that is the consumer side of the topic.");
    }

    // ── Health, metrics, logging ────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ScimSourceFact> Health(List<Code> production, Code? program)
    {
        if (program is null) yield break;
        var add = program.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Name(i) == "AddHealthChecks");
        var checks = add is null ? [] : Chain(add).Select(Name).Where(n => n.StartsWith("Add", StringComparison.Ordinal)).ToList();
        var dbCheck = checks.Any(c => c.Contains("DbContext", StringComparison.Ordinal) || c.Contains("SqlServer", StringComparison.Ordinal));
        var busCheck = checks.Any(c => c.Contains("ServiceBus", StringComparison.Ordinal)) || production.Any(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(t => t.BaseList?.ToString().Contains("IHealthCheck", StringComparison.Ordinal) == true && t.ToString().Contains("ServiceBus", StringComparison.Ordinal)));
        yield return Fact("scim-health-checks", ScimArea.Observability, "Registered health checks", checks.Count == 0 ? ScimEvidenceState.IssueDetected : dbCheck && busCheck ? ScimEvidenceState.SourceVerified : ScimEvidenceState.Partial,
            add is null ? "AddHealthChecks() is not called." : checks.Count == 0 ? "AddHealthChecks() registers no check: neither SQL Server nor Service Bus connectivity is checked." : $"Checks: {string.Join(", ", checks)}.",
            add is null ? null : [Loc(program, add)]);
        var direct = program.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Name(i) == "MapHealthChecks").Select(i => Literal(i.ArgumentList.Arguments.FirstOrDefault()?.Expression)).OfType<string>().ToList();
        var shared = program.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Name(i).StartsWith("Map", StringComparison.Ordinal) && Name(i).Contains("Health", StringComparison.Ordinal) && Name(i) != "MapHealthChecks");
        if (direct.Count > 0)
        {
            yield return Fact("scim-health-mapping", ScimArea.Observability, "Health endpoints", ScimEvidenceState.SourceVerified, $"MapHealthChecks: {string.Join(", ", direct)}.");
            if (checks.Count == 0)
                yield return Fact("scim-health-ready-empty", ScimArea.Observability, "Readiness reports Healthy without checking dependencies", ScimEvidenceState.IssueDetected,
                    $"No check is registered, so {string.Join(", ", direct)} returns HTTP 200 \"Healthy\" whatever the state of SQL Server or Service Bus. HTTP 200 there is not dependency health.", [Loc(program, add!)]);
        }
        else if (shared is not null)
        {
            var library = production.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == Name(shared)).Select(m => (Code: c, Method: m))).FirstOrDefault();
            if (library.Method is null)
                yield return Fact("scim-health-mapping", ScimArea.Observability, "Health endpoints", ScimEvidenceState.NotAssessed, $"{Name(shared)}() comes from a library whose source was not uploaded: paths and predicates are unknown.", [Loc(program, shared)]);
            else
            {
                var text = library.Method.ToString();
                var paths = Regex.Matches(text, @"MapHealthChecks\(""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
                var readyTag = Regex.Match(text, @"Tags\.Contains\(""(\w+)""\)");
                yield return Fact("scim-health-mapping", ScimArea.Observability, "Health endpoints", ScimEvidenceState.SourceVerified,
                    $"{Name(shared)}() maps {string.Join(", ", paths)} (anonymous){(readyTag.Success ? $"; the readiness endpoint runs only checks tagged \"{readyTag.Groups[1].Value}\"" : "")}.", [Loc(program, shared), Loc(library.Code, library.Method)]);
                if (readyTag.Success && !checks.Any())
                    yield return Fact("scim-health-ready-empty", ScimArea.Observability, "Readiness reports Healthy without checking dependencies", ScimEvidenceState.IssueDetected,
                        $"No check is registered, so no check carries the \"{readyTag.Groups[1].Value}\" tag: the readiness endpoint returns HTTP 200 \"Healthy\" whatever the state of SQL Server or Service Bus. HTTP 200 there is not dependency health.",
                        [Loc(program, shared), Loc(library.Code, library.Method)]);
            }
        }
    }

    private static IEnumerable<ScimSourceFact> Metrics(List<Code> scimCode, List<Code> production)
    {
        var meter = scimCode.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(t => t.ToString().Contains("CreateCounter", StringComparison.Ordinal)).Select(t => (Code: c, Type: t))).FirstOrDefault();
        if (meter.Type is null) yield break;
        var counters = Regex.Matches(meter.Type.ToString(), @"CreateCounter<\w+>\(""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        var recorders = meter.Type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text.StartsWith("Record", StringComparison.Ordinal)).Select(m => m.Identifier.Text).ToList();
        var calls = recorders.ToDictionary(r => r, r => production.Where(c => c.Path != meter.Code.Path).SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => Name(i) == r).Select(i => i.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "?")).Distinct().ToList());
        var never = calls.Where(c => c.Value.Count == 0).Select(c => c.Key).ToList();
        yield return Fact("scim-metrics", ScimArea.Observability, "Operational metrics", never.Count == 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NeedsReview,
            $"Meter counters {string.Join(", ", counters)}. " + string.Join(" ", calls.Select(c => c.Value.Count == 0 ? $"{c.Key} is never called." : $"{c.Key} is called from {string.Join(", ", c.Value)}.")),
            [Loc(meter.Code, meter.Type)]);
    }

    private static IEnumerable<ScimSourceFact> Logging(List<Code> scimCode, List<Code> production, List<Handler> handlers)
    {
        var templates = production.Where(c => scimCode.Contains(c) || c.Text.Contains("ServiceBusMessage", StringComparison.Ordinal))
            .SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Name(i).StartsWith("Log", StringComparison.Ordinal) && i.Expression.ToString().Contains("ogger", StringComparison.Ordinal))
                .Select(i => (Code: c, Invocation: i, Template: i.ArgumentList.Arguments.Select(a => Literal(a.Expression)).OfType<string>().FirstOrDefault() ?? ""))).ToList();
        var sensitive = templates.Where(t => Regex.IsMatch(t.Template, @"\{(Authorization|Token|Secret|Password|Bearer)\}", RegexOptions.IgnoreCase)).ToList();
        var identity = templates.Where(t => Regex.IsMatch(t.Template, @"\{(UserName|Email|Mail|DisplayName|Name)\}", RegexOptions.IgnoreCase)).ToList();
        var requestLogs = handlers.Sum(h => h.Logs);
        yield return Fact("scim-logging", ScimArea.Privacy, "Logging privacy (source)", sensitive.Count > 0 ? ScimEvidenceState.IssueDetected : identity.Count > 0 ? ScimEvidenceState.NeedsReview : ScimEvidenceState.SourceVerified,
            (sensitive.Count > 0 ? $"Templates log a credential placeholder: {string.Join("; ", sensitive.Select(t => t.Template))}. " : "No log template carries an Authorization header, token or secret. ")
            + (identity.Count > 0 ? $"Templates carry user identity: {string.Join("; ", identity.Select(t => t.Template))}. " : "No log template carries a user name or e-mail. ")
            + $"{templates.Count} log statement(s) in the adapter and publisher; {requestLogs} in the SCIM operation handlers (a received SCIM request is not logged by the handlers). Runtime log content is not inspected.",
            sensitive.Concat(identity).Select(t => Loc(t.Code, t.Invocation)));
    }

    // ── Downstream ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<ScimDownstreamReference> Consumers(List<Code> production, string? scimProject, string? topic, List<string> eventTypes, Dictionary<string, string> consts, List<SourceFile> documents)
    {
        var references = new List<ScimDownstreamReference>();
        var constName = consts.FirstOrDefault(c => c.Value == topic && !c.Key.Contains('.')).Key;
        foreach (var c in production.Where(c => c.Project != scimProject))
        {
            foreach (var invocation in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Name(invocation);
                if (name is "PublishAsync" or "CreateSender" or "SendMessageAsync" or "PublishBatchAsync") continue;
                var args = invocation.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
                var matches = topic is not null && args.Any(a => a.Trim('"') == topic || constName is not null && a.EndsWith("." + constName, StringComparison.Ordinal));
                if (matches) references.Add(new ScimDownstreamReference { Kind = $"{name}() on the topic", Detail = Snippet(invocation.ToString(), topic), Location = Loc(c, invocation) });
            }
            foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                if (method.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString() is { } type && eventTypes.Any(e => type == e || type == e + "Event"))
                    references.Add(new ScimDownstreamReference { Kind = "Handler of the event type", Detail = $"{(method.Parent as TypeDeclarationSyntax)?.Identifier.Text}.{method.Identifier.Text}({type})", Location = Loc(c, method) });
        }
        foreach (var doc in documents.Where(d => d.Path.EndsWith("servicebus-config.json", StringComparison.OrdinalIgnoreCase)))
            foreach (var (subscription, session) in EmulatorSubscriptions(doc.Content, topic))
                references.Add(new ScimDownstreamReference { Kind = "Local emulator subscription", Detail = $"{topic}/{subscription} (RequiresSession {session?.ToString().ToLowerInvariant() ?? "unset"}) — emulator topology for local development, not the deployed namespace.", Location = new SourceLocation(doc.Path, Line(doc.Content, $"\"{subscription}\"")) });
        foreach (var doc in documents.Where(d => d.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            if (Regex.Match(doc.Content, @"Subscription \(konsument\)\s*\|\s*`([^`]+)`") is { Success: true } documented)
                references.Add(new ScimDownstreamReference { Kind = "Documented consumer subscription", Detail = $"{documented.Groups[1].Value} (documentation only)", Location = new SourceLocation(doc.Path, Line(doc.Content, documented.Value)) });
        return references;
    }

    private static IEnumerable<(string Name, bool? Session)> EmulatorSubscriptions(string json, string? topic)
    {
        if (topic is null) yield break;
        List<(string, bool?)> found = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("Name", out var name) && name.GetString() == topic && e.TryGetProperty("Subscriptions", out var subs) && subs.ValueKind == JsonValueKind.Array)
                        foreach (var s in subs.EnumerateArray())
                            found.Add((s.GetProperty("Name").GetString() ?? "", s.TryGetProperty("Properties", out var p) && p.TryGetProperty("RequiresSession", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null));
                    foreach (var property in e.EnumerateObject()) Walk(property.Value);
                }
                else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Walk(item);
            }
            Walk(doc.RootElement);
        }
        catch (JsonException) { }
        foreach (var item in found) yield return item;
    }

    private static List<ScimDownstreamReference> StateUsage(List<Code> production, string? scimProject)
    {
        var usage = new List<ScimDownstreamReference>();
        foreach (var c in production.Where(c => c.Project != scimProject && c.Text.Contains("KjentBruker", StringComparison.Ordinal)))
        {
            var lines = c.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("IsActive", StringComparison.Ordinal)) continue;
                // Only a reference that is visibly about KjentBruker (same or the few lines above), not any other IsActive in the file.
                if (!Enumerable.Range(Math.Max(0, i - 5), i - Math.Max(0, i - 5) + 1).Any(j => lines[j].Contains("KjentBruker", StringComparison.Ordinal))) continue;
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(line, @"\bbool\s+IsActive\s*\{\s*get")) continue;
                var kind = c.Path.Contains("Seeder", StringComparison.OrdinalIgnoreCase) ? "Seed data"
                    : line.Contains("HasDefaultValue", StringComparison.Ordinal) || c.Path.Contains("Configuration", StringComparison.Ordinal) ? "Persistence configuration"
                    : line.Contains("Dto(", StringComparison.Ordinal) || line.Contains("bool IsActive)", StringComparison.Ordinal) || line.Contains("bool IsActive", StringComparison.Ordinal) ? "Projection into a read model"
                    : "Other reference";
                usage.Add(new ScimDownstreamReference { Kind = kind, Detail = Snippet(line, null), Location = new SourceLocation(c.Path, i + 1) });
            }
        }
        return usage;
    }

    private static IEnumerable<ScimSourceFact> DownstreamFacts(string? topic, List<ScimDownstreamReference> consumers, List<ScimDownstreamReference> usage, List<ScimEventContract> events)
    {
        var code = consumers.Where(c => c.Kind is not ("Local emulator subscription" or "Documented consumer subscription")).ToList();
        var emulator = consumers.Where(c => c.Kind == "Local emulator subscription").ToList();
        var documented = consumers.Where(c => c.Kind == "Documented consumer subscription").ToList();
        yield return Fact("scim-downstream-consumer", ScimArea.Security, "Consumer of the provisioning topic", code.Count > 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NotFound,
            code.Count > 0 ? $"{code.Count} reference(s) consume {topic}: {string.Join("; ", code.Take(3).Select(c => c.Detail))}."
                : $"No analyzed source receives {topic ?? "the topic"} or handles {string.Join("/", events.Select(e => e.EventType))}" +
                  (documented.Count > 0 ? $" (documentation names subscription {string.Join(", ", documented.Select(d => d.Detail))})" : "") +
                  (emulator.Count > 0 ? $"; only the local emulator topology defines a subscription ({string.Join(", ", emulator.Select(e => e.Detail.Split(' ')[0]))})" : "") +
                  ". Deactivation reaching Autorisasjon is not established by source — upload the consumer's source if it lives elsewhere.",
            code.Concat(emulator).Concat(documented).Select(c => c.Location).OfType<SourceLocation>());
        var access = usage.Where(u => u.Kind == "Other reference").ToList();
        yield return Fact("scim-state-usage", ScimArea.Security, "Use of KjentBruker.IsActive outside the adapter", access.Count > 0 ? ScimEvidenceState.SourceVerified : usage.Count > 0 ? ScimEvidenceState.NeedsReview : ScimEvidenceState.NotFound,
            usage.Count == 0 ? "No analyzed source outside the adapter reads KjentBruker.IsActive."
                : access.Count == 0 ? $"KjentBruker.IsActive is read outside the adapter only as {string.Join(", ", usage.Select(u => u.Kind.ToLowerInvariant()).Distinct())} — no access decision based on it was found. A deactivated KjentBruker does not by itself remove a user's access in the analyzed source."
                : $"{access.Count} other reference(s) — review whether they gate access: {string.Join("; ", access.Take(3).Select(a => a.Detail))}.",
            usage.Select(u => u.Location).OfType<SourceLocation>());
        if (emulator.Any(e => e.Detail.Contains("RequiresSession true", StringComparison.Ordinal)) && events.Any(e => e.SessionIdSet == false))
            yield return Fact("scim-session-mismatch", ScimArea.Contract, "Session-enabled subscription, no SessionId", ScimEvidenceState.NeedsReview,
                "The local emulator subscription requires sessions, but the publisher sets no SessionId. How that subscription treats these messages is not assessed; the deployed subscription's settings come from infrastructure.",
                emulator.Select(e => e.Location).OfType<SourceLocation>());
    }
}
