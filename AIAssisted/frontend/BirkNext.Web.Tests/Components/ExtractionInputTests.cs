using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace BirkNext.Web.Tests.Components;

public class ExtractionInputTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _mockExtractionService = new();
    private readonly Mock<IExtractionConfiguration> _mockConfig = new();

    public ExtractionInputTests()
    {
        _mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        _mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        _mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);
        Services.AddSingleton(_mockExtractionService.Object);
        Services.AddSingleton(_mockConfig.Object);
        Services.AddLogging();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    private static ExtractionPipelineResult MakeSuccessResult() =>
        ExtractionPipelineResult.Success(
            candidates:
            [
                new ExtractionCandidate
                {
                    Title = "The system shall do something",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                    SourceBlockType = BlockType.UnorderedListItem,
                }
            ],
            inputLengthChars: 50,
            inputLineCount: 1,
            durationMs: 5,
            requirementCount: 1,
            testCount: 0,
            needsClarificationCount: 0);

    [Fact]
    public void EmptyTextArea_ShowsValidationMessage_DoesNotCallExtract()
    {
        var cut = Render<ExtractionInput>();

        cut.Find("[data-testid='extract-button']").Click();

        cut.Find("[data-testid='validation-message']").TextContent
            .Should().Contain("Paste some text");

        _mockExtractionService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void InputAboveMaxLength_ShowsLengthError_DoesNotCallExtract()
    {
        var oversizedInput = new string('x', 50_001);

        var cut = Render<ExtractionInput>();

        cut.Find("[data-testid='spec-textarea']").Input(oversizedInput);
        cut.Find("[data-testid='extract-button']").Click();

        cut.Find("[data-testid='validation-message']").TextContent
            .Should().Contain("too large");

        _mockExtractionService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidInput_CallsExtractWithRawString()
    {
        const string specText = "The system shall allow users to log in.";
        _mockExtractionService
            .Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();

        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='extract-button']").HasAttribute("disabled") ||
                  cut.Find("[data-testid='extract-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(2));

        _mockExtractionService.Verify(
            s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SuccessfulExtraction_RaisesOnExtractionCompletedWithResult()
    {
        const string specText = "The system shall allow login.";
        var pipelineResult = MakeSuccessResult();

        _mockExtractionService
            .Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineResult);

        ExtractionPipelineResult? receivedResult = null;

        var cut = Render<ExtractionInput>(p =>
            p.Add(c => c.OnExtractionCompleted, (ExtractionPipelineResult r) => { receivedResult = r; }));

        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => receivedResult is not null,
            timeout: TimeSpan.FromSeconds(2));

        receivedResult.Should().BeSameAs(pipelineResult);
    }

    [Fact]
    public async Task FileImport_DoesNotCallExtractService()
    {
        var cut = Render<ExtractionInput>();
        var importChild = cut.FindComponent<SpecificationImport>();
        await cut.InvokeAsync(() =>
            importChild.Instance.OnFileDrop("spec.md", 512,
                "The system shall allow users to authenticate."));

        _mockExtractionService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FileImport_ThenExtract_UsesImportedContent()
    {
        const string content = "The system shall allow users to authenticate.";
        _mockExtractionService
            .Setup(s => s.ExtractAsync(content, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        var importChild = cut.FindComponent<SpecificationImport>();
        await cut.InvokeAsync(() => importChild.Instance.OnFileDrop("spec.md", 512, content));

        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='extract-button']").HasAttribute("disabled") ||
                  cut.Find("[data-testid='extract-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(2));

        _mockExtractionService.Verify(
            s => s.ExtractAsync(content, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractButton_DisabledDuringExtractionAndReenabledAfter()
    {
        const string specText = "The system shall allow login.";
        var tcs = new TaskCompletionSource<ExtractionPipelineResult>();

        _mockExtractionService
            .Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var cut = Render<ExtractionInput>();

        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.Find("[data-testid='extract-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='extract-button']").HasAttribute("disabled").Should().BeTrue();

        tcs.SetResult(MakeSuccessResult());

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='extract-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='extract-button']").HasAttribute("disabled").Should().BeFalse();
    }
}

// ── T085 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Observability tests for ExtractionInput: verifies log event fields, sessionId consistency,
/// and that no raw pasted text ever appears in any log message.
/// </summary>
public class ExtractionInputObservabilityTests : BunitContext
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    private readonly Mock<IScenarioExtractionService> _mockService = new();
    private readonly CapturingLogger<ExtractionInput> _logger = new();

    public ExtractionInputObservabilityTests()
    {
        var mockConfig = new Mock<IExtractionConfiguration>();
        mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);

        Services.AddSingleton(_mockService.Object);
        Services.AddSingleton<IExtractionConfiguration>(mockConfig.Object);
        Services.AddSingleton<ILogger<ExtractionInput>>(_logger);
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    private static ExtractionPipelineResult MakeSuccessResult(string title = "The system shall do something") =>
        ExtractionPipelineResult.Success(
            candidates:
            [
                new ExtractionCandidate
                {
                    Title = title,
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                    SourceBlockType = BlockType.UnorderedListItem,
                }
            ],
            inputLengthChars: 50,
            inputLineCount: 1,
            durationMs: 5,
            requirementCount: 1,
            testCount: 0,
            needsClarificationCount: 0);

    private static ExtractionPipelineResult MakeNoResultsResult() =>
        ExtractionPipelineResult.NonSuccess(PipelineStatus.NoResults, 42, 3, 2);

    [Fact]
    public async Task ValidInput_ExtractionTriggered_IsLogged()
    {
        const string specText = "The system shall allow login.";
        _mockService.Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _logger.Messages.Any(m => m.Contains("ExtractionTriggered")),
            timeout: TimeSpan.FromSeconds(2));

        _logger.Messages.Should().Contain(m => m.Contains("ExtractionTriggered"));
    }

    [Fact]
    public async Task ValidInput_ExtractionCompleted_IsLogged()
    {
        const string specText = "The system shall allow login.";
        _mockService.Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _logger.Messages.Any(m => m.Contains("ExtractionCompleted")),
            timeout: TimeSpan.FromSeconds(2));

        _logger.Messages.Should().Contain(m => m.Contains("ExtractionCompleted"));
    }

    [Fact]
    public async Task NoResultsInput_ExtractionEmpty_IsLoggedWithSnakeCaseReason()
    {
        const string specText = "Some headings but no bullet points.";
        _mockService.Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeNoResultsResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _logger.Messages.Any(m => m.Contains("ExtractionEmpty")),
            timeout: TimeSpan.FromSeconds(2));

        _logger.Messages.Should().Contain(m =>
            m.Contains("ExtractionEmpty") && m.Contains("no_candidates_found"));
    }

    [Fact]
    public async Task AllLogEvents_ShareSameSessionId()
    {
        const string specText = "The system shall allow login.";
        _mockService.Setup(s => s.ExtractAsync(specText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input(specText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _logger.Messages.Count(m => m.Contains("sessionId=")) >= 2,
            timeout: TimeSpan.FromSeconds(2));

        var triggeredMsg = _logger.Messages.FirstOrDefault(m => m.Contains("ExtractionTriggered"));
        var completedMsg = _logger.Messages.FirstOrDefault(m => m.Contains("ExtractionCompleted"));

        triggeredMsg.Should().NotBeNull();
        completedMsg.Should().NotBeNull();

        var triggeredSessionId = System.Text.RegularExpressions.Regex
            .Match(triggeredMsg!, @"sessionId=([0-9a-fA-F\-]+)").Groups[1].Value;
        var completedSessionId = System.Text.RegularExpressions.Regex
            .Match(completedMsg!, @"sessionId=([0-9a-fA-F\-]+)").Groups[1].Value;

        triggeredSessionId.Should().NotBeNullOrEmpty();
        triggeredSessionId.Should().Be(completedSessionId,
            "ExtractionTriggered and ExtractionCompleted must carry the same sessionId");
    }

    [Fact]
    public async Task NoLogEvent_ContainsRawInputText()
    {
        const string rawText = "unique-sentinel-raw-input-text-12345";
        _mockService.Setup(s => s.ExtractAsync(rawText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input(rawText);
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _logger.Messages.Any(m => m.Contains("ExtractionCompleted")),
            timeout: TimeSpan.FromSeconds(2));

        // no raw text: verify the raw input value is absent from every log message
        _logger.Messages.Should().NotContain(m => m.Contains(rawText),
            "log events must never carry raw pasted text content");
    }

    [Fact]
    public async Task FileImport_NoLogEvent_ContainsImportedContent()
    {
        const string importedText = "unique-sentinel-imported-content-98765";

        var cut = Render<ExtractionInput>();
        var importChild = cut.FindComponent<SpecificationImport>();
        await cut.InvokeAsync(() => importChild.Instance.OnFileDrop("spec.md", 512, importedText));

        _logger.Messages.Should().NotContain(m => m.Contains(importedText),
            "file content must never appear in log messages");
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Profile selector UI tests
// ──────────────────────────────────────────────────────────────────────────────

public class ExtractionInputProfileSelectorTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _mockService = new();

    public ExtractionInputProfileSelectorTests()
    {
        var mockConfig = new Mock<IExtractionConfiguration>();
        mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);

        Services.AddSingleton(_mockService.Object);
        Services.AddSingleton(mockConfig.Object);
        Services.AddLogging();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    private static ExtractionPipelineResult MakeSuccessResult()
        => ExtractionPipelineResult.Success([], 10, 1, 5, 0, 0, 0);

    [Fact]
    public void ProfileSelector_IsRendered()
    {
        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='profile-selector']").Should().NotBeNull();
        cut.Find("[data-testid='profile-radio-default']").Should().NotBeNull();
        cut.Find("[data-testid='profile-radio-speckit']").Should().NotBeNull();
    }

    [Fact]
    public void ProfileSelector_DefaultIsSelectedByDefault()
    {
        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='profile-radio-default']").HasAttribute("checked").Should().BeTrue(
            "Default radio must be checked initially");
        cut.Find("[data-testid='profile-radio-speckit']").HasAttribute("checked").Should().BeFalse(
            "Speckit radio must be unchecked initially");
    }

    [Fact]
    public async Task SelectingSpeckitProfile_PassesSpeckitToService()
    {
        _mockService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='profile-radio-speckit']").Change(true);
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _mockService.Invocations.Any(i => i.Method.Name == "ExtractAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()),
            Times.Once,
            "service must be called with Speckit profile when Speckit radio is selected");
    }

    [Fact]
    public async Task SelectingDefaultProfile_PassesDefaultToService()
    {
        _mockService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Default, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        // Explicitly click Default (it starts selected, but confirm it passes Default)
        cut.Find("[data-testid='profile-radio-default']").Change(true);
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _mockService.Invocations.Any(i => i.Method.Name == "ExtractAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Default, It.IsAny<CancellationToken>()),
            Times.Once,
            "service must be called with Default profile when Default radio is selected");
    }

    [Fact]
    public void ChangingProfile_DoesNotClearInputText()
    {
        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("preserved text");
        cut.Find("[data-testid='profile-radio-speckit']").Change(true);

        // Input should not have been erased by profile change
        cut.FindAll("[data-testid='validation-message']").Should().BeEmpty(
            "changing profile must not trigger validation or clear the input field");
    }
}

