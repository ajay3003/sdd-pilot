using System.Text;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ExecutionEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>Chained-call outputs: extraction (types, missing paths, credential-looking values, redacted headers), send-time resolution, and the run's
/// refusal of a call that depends on a step that does not run before it.</summary>
public sealed class ExecutionOutputsTests
{
    private static CapturedResponse Response(params CapturedHeader[] headers) => new() { StatusCode = 200, Headers = [.. headers] };

    [Fact]
    public void Outputs_are_typed_and_a_missing_wrong_typed_or_credential_looking_value_is_not_extracted()
    {
        var raw = Encoding.UTF8.GetBytes("""{"id":8,"name":"Testbarn","items":[{"code":"A"}],"note":"Bearer abcdefghijklmnop","data":{"createChild":{"id":"8"}}}""");
        var (outputs, values) = ExecutionOutputs.Extract(
        [
            new("id", OutputSource.JsonBody, "$.id", "Integer"), new("first", OutputSource.JsonBody, "items[0].code"), new("gid", OutputSource.GraphQlData, "createChild.id", "String"),
            new("missing", OutputSource.JsonBody, "$.nope"), new("wrong", OutputSource.JsonBody, "$.name", "Integer"), new("note", OutputSource.JsonBody, "$.note"),
            new("etag", OutputSource.Header, "ETag"), new("cookie", OutputSource.Header, "Set-Cookie"),
        ], Response(new("ETag", "\"v1\""), new("Set-Cookie", "[redacted]", Redacted: true)), raw);
        outputs.Single(o => o.Name == "id").Should().Be(new ExtractedOutput("id", OutputSource.JsonBody, "$.id", "Integer", true, "8", null));
        outputs.Single(o => o.Name == "first").Preview.Should().Be("A");
        outputs.Single(o => o.Name == "gid").Found.Should().BeTrue();
        outputs.Single(o => o.Name == "missing").Problem.Should().Contain("not present");
        outputs.Single(o => o.Name == "wrong").Problem.Should().Contain("is String, not Integer");
        outputs.Single(o => o.Name == "note").Problem.Should().Contain("looks like a credential");
        outputs.Single(o => o.Name == "etag").Preview.Should().Be("\"v1\"");
        outputs.Single(o => o.Name == "cookie").Problem.Should().Contain("redacted");
        values.Keys.Should().BeEquivalentTo(["id", "first", "gid", "etag"], "only found, safe values are handed to later steps");
        values["id"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number);
    }

    [Fact]
    public void Definitions_that_would_extract_credentials_are_refused()
    {
        ExecutionOutputs.DefinitionProblem(new("token", OutputSource.JsonBody, "$.token")).Should().Contain("never extracted");
        ExecutionOutputs.DefinitionProblem(new("x", OutputSource.JsonBody, "$.client_secret")).Should().Contain("never extracted");
        ExecutionOutputs.DefinitionProblem(new("x", OutputSource.Header, "Authorization")).Should().Contain("never extracted");
        ExecutionOutputs.DefinitionProblem(new("x", OutputSource.JsonBody, "$.id", "Uuid")).Should().Contain("type is one of");
        ExecutionOutputs.DefinitionProblem(new("childId", OutputSource.JsonBody, "$.id", "Integer")).Should().BeNull();
    }

    [Fact]
    public void Resolution_keeps_types_in_JSON_escapes_paths_and_reports_what_is_missing()
    {
        var outputs = new Dictionary<string, Dictionary<string, JsonNode?>>
        {
            ["create"] = new() { ["id"] = JsonValue.Create(8), ["name"] = JsonValue.Create("a b/c"), ["tags"] = new JsonArray("x", "y") },
        };
        var d = new ExecutionCallDefinition
        {
            Path = "/children/{{steps.create.name}}", Query = new() { ["id"] = "{{steps.create.id}}" }, ContentType = "application/json",
            Body = """{"childId":"{{steps.create.id}}","tags":"{{steps.create.tags}}","label":"id={{steps.create.id}}","other":"{{steps.later.x}}"}""",
        };
        var (call, missing, used) = ExecutionOutputs.Resolve(d, outputs);
        call.Path.Should().Be("/children/a%20b%2Fc");
        call.Query["id"].Should().Be("8");
        call.Body.Should().Be("""{"childId":8,"tags":["x","y"],"label":"id=8","other":"{{steps.later.x}}"}""");
        missing.Should().Equal("steps.later.x");
        used.Select(u => (u.Name, u.Type)).Should().BeEquivalentTo([("steps.create.name", "String"), ("steps.create.id", "Integer"), ("steps.create.tags", "Array")]);
    }

    [Fact]
    public async Task A_call_that_depends_on_a_later_or_unknown_step_is_blocked_before_anything_is_sent()
    {
        var services = new ServiceCollection();
        var db = $"outputs-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        await using var provider = services.BuildServiceProvider();
        var start = await provider.GetRequiredService<ExecutionRunService>().StartAsync(new ExecutionRunRequest
        {
            Name = "order", EnvironmentId = "local", EnvironmentType = "Local", TargetUrl = "http://127.0.0.1:9/",
            Calls =
            [
                new() { Name = "first", Path = "/a/{{steps.second.id}}", StepId = "first" },
                new() { Name = "second", Path = "/b", StepId = "second", Extract = [new("id", OutputSource.JsonBody, "$.id")] },
                new() { Name = "dup", Path = "/c", StepId = "second" },
                new() { Name = "ghost", Path = "/d", DependsOn = ["nobody"] },
            ],
        }, "test", default);
        for (var i = 0; i < 200 && ExecutionRunService.IsRunning(start.RunId!.Value); i++) await Task.Delay(25);
        using var scope = provider.CreateScope();
        var calls = (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(start.RunId!.Value, new(Take: 10), default))!.Calls;
        calls[0].State.Should().Be(ExecutionState.Blocked);
        calls[0].Reason.Should().Contain("do not run before it");
        calls[2].Reason.Should().Contain("used twice");
        calls[3].Reason.Should().Contain("nobody");
        calls.Where(c => c.State == ExecutionState.Blocked).Should().OnlyContain(c => c.Attempts.Count == 0);
    }
}
