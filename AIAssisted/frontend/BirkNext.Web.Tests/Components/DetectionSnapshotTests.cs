using System.Linq;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The last successful public detection is persisted as a safe snapshot in a SEPARATE evidence store (never in the saved profile), so a
/// restart restores it as "Previously detected" (revalidated to Current or Stale) without forcing a fresh Detect and without modifying
/// or "saving" the Target Environment configuration. No token/cookie/session is ever persisted.
/// </summary>
public sealed partial class AuthenticationApplyDraftTests
{
    private static string Row(IRenderedComponent<Component> cut, string testId) => cut.Find($"[data-testid='{testId}']").TextContent.Trim();
    private static bool Has(IRenderedComponent<Component> cut, string testId) => cut.FindAll($"[data-testid='{testId}']").Count > 0;

    private void SetupSnapshotStore(string? mapJson)
    {
        JSInterop.SetupVoid("birkNextStorage.setSnapshots", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getSnapshots").SetResult(mapJson);
    }

    private static string SnapshotMap(string detectedUrl, string state, string detectedAtIso) =>
        "{\"dev\":{\"result\":{\"originalUrl\":\"" + detectedUrl + "\",\"success\":true,\"reachability\":\"Reachable\"," +
        "\"detectedClientFramework\":\"BlazorWebAssembly\",\"suggestedEnvironmentType\":\"Development\",\"state\":\"" + state + "\"," +
        "\"authenticationRequired\":false,\"detectedAuthenticationType\":\"None\",\"isActivationReady\":true}," +
        "\"detectedProfileId\":\"dev\",\"detectedUrl\":\"" + detectedUrl + "\",\"detectedAt\":\"" + detectedAtIso + "\",\"source\":\"Detect Settings\"}}";

    private IRenderedComponent<Component> OpenRestored(string targetUrl, string? snapshotMapJson)
    {
        var settingsJson =
            "{\"activeProfileId\":\"dev\",\"profiles\":[" +
            "{\"id\":\"local\",\"name\":\"Local\",\"targetUrl\":\"https://local.example.com\"}," +
            "{\"id\":\"dev\",\"name\":\"M2LB DEV\",\"environmentType\":\"Development\",\"targetUrl\":\"" + targetUrl + "\"," +
            "\"authentication\":{\"authenticationType\":\"None\"}}]}";
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(settingsJson);
        SetupSnapshotStore(snapshotMapJson);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    [Fact]
    public void Detect_PersistsASafeSnapshotSeparatelyWithoutModifyingTheProfileOrSavingConfig()
    {
        SetupSnapshotStore(null);
        var cut = Open();
        var profileBefore = System.Text.Json.JsonSerializer.Serialize(Persisted());
        Click(cut, "Detect settings");
        cut.WaitForAssertion(() => _settings.GetDetectionSnapshot("dev").Should().NotBeNull());
        var snap = _settings.GetDetectionSnapshot("dev")!;
        snap.DetectedProfileId.Should().Be("dev");
        snap.Result.DetectedAuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId);
        snap.DetectedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        // The saved profile is NOT modified and the settings config is NOT saved by Detect.
        System.Text.Json.JsonSerializer.Serialize(Persisted()).Should().Be(profileBefore);
        SaveCalls().Should().Be(0);
        // The snapshot payload contains no credential.
        var snapshotWrite = JSInterop.Invocations.Where(i => i.Identifier == "birkNextStorage.setSnapshots").Select(i => (string)i.Arguments[0]!).Last();
        foreach (var forbidden in new[] { "eyJ", "Bearer ", "Authorization", "Cookie", "bearer" })
            snapshotWrite.Should().NotContain(forbidden);
    }

    [Fact]
    public void Restart_RestoresPreviousDetectionAsCurrentWhenUrlUnchangedAndFresh()
    {
        var cut = OpenRestored("https://dev.example.com/", SnapshotMap("https://dev.example.com/", "Complete", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")));
        Has(cut, "detection-restored").Should().BeTrue();
        Row(cut, "detection-restored").Should().Contain("Previously detected — current");
        cut.Markup.Should().Contain("Reachable");
        cut.Markup.Should().NotContain("changed since the last detection");
        SaveCalls().Should().Be(0); // restoring evidence never writes settings config
    }

    [Fact]
    public void Restart_MarksStaleWhenTargetUrlChanged()
    {
        var cut = OpenRestored("https://new.example.com/", SnapshotMap("https://dev.example.com/", "Complete", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")));
        Has(cut, "detection-restored").Should().BeTrue();
        Row(cut, "detection-restored").Should().Contain("stale, re-detect required");
        cut.Find(".fa-result-message").TextContent.Should().Contain("target URL changed");
    }

    [Fact]
    public void Restart_MarksStaleWhenSnapshotOlderThanFreshnessLimit()
    {
        var cut = OpenRestored("https://dev.example.com/", SnapshotMap("https://dev.example.com/", "Complete", DateTimeOffset.UtcNow.AddDays(-30).ToString("O")));
        Row(cut, "detection-restored").Should().Contain("stale, re-detect required");
        cut.Find(".fa-result-message").TextContent.Should().Contain("older than the freshness limit");
    }

    [Fact]
    public void Restart_WithNoSnapshotShowsNoRestoredBadge()
    {
        var cut = OpenRestored("https://dev.example.com/", null);
        Has(cut, "detection-restored").Should().BeFalse();
    }
}
