using BirkNext.Api.Controllers;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// «Testgjennomføring i BirkNext» is optional (2026-11-25): off unless System settings turn it on (Azure DevOps Test Plans is the default). While off,
/// nothing can be recorded — start, save, complete, abandon, attach, defect follow-up and withdrawal are refused with a clear message — and the
/// availability says so. Reading stays possible, so runs recorded while it was on never disappear.
/// </summary>
public sealed class ManualExecutionGateTests
{
    private static ManualTestRunsController Controller(string? value) =>
        new(null!, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ManualExecutionSettings.Key] = value }).Build());

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("not-a-bool", false)]
    [InlineData("true", true)]
    public void Availability_is_on_only_when_the_setting_is_explicitly_true(string? value, bool expected) =>
        ((ManualExecutionAvailability)((OkObjectResult)Controller(value).Availability()).Value!).Enabled.Should().Be(expected);

    [Fact]
    public async Task While_off_every_recording_call_is_refused_before_anything_is_read_or_written()
    {
        var off = Controller("false"); // the service is null: a refused call never reaches it
        var results = new[]
        {
            await off.Start(new StartManualRunRequest("p", Guid.NewGuid(), "c1", null), default),
            await off.Save(Guid.NewGuid(), "p", new SaveManualRunRequest(), default),
            await off.Complete(Guid.NewGuid(), "p", new CompleteManualRunRequest(1), default),
            await off.Abandon(Guid.NewGuid(), "p", new AbandonManualRunRequest(1, "x"), default),
            await off.UpdateDefect(Guid.NewGuid(), "d1", "p", new UpdateManualDefectRequest(1, ManualDefectStatus.Closed, null, null, null), default),
            await off.Withdraw(Guid.NewGuid(), Guid.NewGuid(), "p", new WithdrawAttachmentRequest("x"), default),
            await off.Attach(Guid.NewGuid(), "p", 1, null, null, null, default),
        };
        results.Select(r => (r as ObjectResult)?.StatusCode).Should().OnlyContain(code => code == 409);
        results.Cast<ObjectResult>().Select(r => r.Value!.ToString()).Should().OnlyContain(v => v!.Contains("manual-execution-off") && v.Contains("Azure DevOps Test Plans"));
    }
}
