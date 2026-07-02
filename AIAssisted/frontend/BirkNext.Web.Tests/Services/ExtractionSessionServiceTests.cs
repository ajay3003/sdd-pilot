using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

public class ExtractionSessionServiceTests : BunitContext
{
    private const string StorageKey = "birknext:extraction:session";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private ExtractionSessionService CreateService() => new(JSInterop.JSRuntime, Mock.Of<IWorkspaceStateManager>());

    private static ExtractionSessionSnapshot MakeSnapshot(DateTimeOffset? timestamp = null) => new()
    {
        SessionId = "test-session-id",
        Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        Profile = ExtractionProfile.Speckit,
        PipelineStatus = PipelineStatus.Success,
        Candidates =
        [
            new CandidateSnapshot(
                CandidateId: Guid.NewGuid(),
                Title: "The system shall allow login",
                Classification: ScenarioKind.Requirement,
                ClassificationSignal: ClassificationSignal.Rfc2119Uppercase,
                ContextHeading: null,
                SourceBlockType: BlockType.UnorderedListItem,
                Confidence: null,
                IsSelected: false,
                ReviewStatus: CandidateReviewStatus.New,
                SaveState: CandidateSaveState.Pending,
                SaveError: null,
                SavedScenarioId: null)
        ],
    };

    [Fact]
    public async Task LoadAsync_WhenGetItemReturnsNull_ReturnsNull()
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(null);

        var result = await CreateService().LoadAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenGetItemReturnsEmptyString_ReturnsNull()
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(string.Empty);

        var result = await CreateService().LoadAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenSnapshotIsRecent_ReturnsSnapshot()
    {
        var snapshot = MakeSnapshot();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(json);

        var result = await CreateService().LoadAsync();

        result.Should().NotBeNull();
        result!.SessionId.Should().Be("test-session-id");
        result.Profile.Should().Be(ExtractionProfile.Speckit);
        result.PipelineStatus.Should().Be(PipelineStatus.Success);
    }

    [Fact]
    public async Task LoadAsync_WhenSnapshotIsExpired_ReturnsNull()
    {
        var expiredSnapshot = MakeSnapshot(DateTimeOffset.UtcNow.AddDays(-8));
        var json = JsonSerializer.Serialize(expiredSnapshot, JsonOptions);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(json);

        var result = await CreateService().LoadAsync();

        result.Should().BeNull("snapshot older than 7 days must be treated as expired");
    }

    [Fact]
    public void SessionArtifacts_SurviveRefresh()
    {
        // A 5-day-old snapshot must NOT be expired (session should survive browser restart / refresh)
        var recentSnapshot = MakeSnapshot(DateTimeOffset.UtcNow.AddDays(-5));
        var service = CreateService();

        service.IsExpired(recentSnapshot).Should().BeFalse(
            "session artifacts must survive at least 7 days to cover browser restart scenarios");
    }

    [Fact]
    public async Task LoadAsync_WhenGetItemThrows_ReturnsNull()
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true)
            .SetException(new Exception("storage unavailable"));

        var result = await CreateService().LoadAsync();

        result.Should().BeNull("storage errors must be swallowed gracefully");
    }

    [Fact]
    public async Task SaveAsync_CallsSetItemWithStorageKey()
    {
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true);

        await CreateService().SaveAsync(MakeSnapshot());

        var invocation = JSInterop.VerifyInvoke("birkNextStorage.setItem");
        invocation.Arguments[0].Should().Be(StorageKey);
    }

    [Fact]
    public async Task SaveAsync_SerializedJson_ContainsSessionId()
    {
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true);

        await CreateService().SaveAsync(MakeSnapshot());

        var invocation = JSInterop.VerifyInvoke("birkNextStorage.setItem");
        invocation.Arguments[1]?.ToString().Should().NotBeNullOrEmpty().And.Contain("test-session-id");
    }

    [Fact]
    public async Task ClearAsync_CallsRemoveItemWithStorageKey()
    {
        JSInterop.SetupVoid("birkNextStorage.removeItem", _ => true);

        await CreateService().ClearAsync();

        var invocation = JSInterop.VerifyInvoke("birkNextStorage.removeItem");
        invocation.Arguments[0].Should().Be(StorageKey);
    }

    [Fact]
    public void IsExpired_WhenSnapshotIsRecent_ReturnsFalse()
    {
        var snapshot = MakeSnapshot(DateTimeOffset.UtcNow.AddMinutes(-30));

        CreateService().IsExpired(snapshot).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_WhenSnapshotIsOlderThanSevenDays_ReturnsTrue()
    {
        var snapshot = MakeSnapshot(DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(-1));

        CreateService().IsExpired(snapshot).Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WhenSnapshotIsExactlyTwoHoursOld_ReturnsFalse()
    {
        var snapshot = MakeSnapshot(DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(1));

        CreateService().IsExpired(snapshot).Should().BeFalse("2 hours is well within the 7-day expiry window");
    }
}
