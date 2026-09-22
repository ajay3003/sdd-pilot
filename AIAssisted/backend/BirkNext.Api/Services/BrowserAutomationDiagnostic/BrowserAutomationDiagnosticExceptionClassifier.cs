using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>What kind of failure a browser operation produced. The distinction drives the whole result.</summary>
public enum BrowserAutomationFailureKind
{
    /// <summary>The page, context or browser was closed underneath the automation. The spike's signature failure.</summary>
    TargetClosed,
    /// <summary>The operation did not complete within its timeout. An absence of a response, not an observation.</summary>
    Timeout,
    /// <summary>The user stopped the diagnostic.</summary>
    Cancelled,
    /// <summary>Anything else: reported by type, never collapsed into "diagnostic failed".</summary>
    Other,
}

/// <summary>
/// Classifies a browser failure by what it tells us.
///
/// Playwright for .NET does not expose <c>TargetClosedException</c> publicly — it surfaces as a
/// <see cref="PlaywrightException"/> whose message says the target was closed — so the type name alone cannot be
/// caught or trusted. Classification therefore reads the message, and the name reported back to the user is the one
/// they will see in Playwright's own output and recognise in a support conversation.
/// </summary>
public static class BrowserAutomationDiagnosticExceptionClassifier
{
    /// <summary>The name IT will recognise, even though the type is not public in the .NET binding.</summary>
    public const string TargetClosedExceptionName = "TargetClosedException";

    public static BrowserAutomationFailureKind Classify(Exception exception) => exception switch
    {
        OperationCanceledException => BrowserAutomationFailureKind.Cancelled,
        TimeoutException => BrowserAutomationFailureKind.Timeout,
        PlaywrightException pw when IsTargetClosed(pw.Message) => BrowserAutomationFailureKind.TargetClosed,
        PlaywrightException pw when IsTimeout(pw.Message) => BrowserAutomationFailureKind.Timeout,
        _ when IsTargetClosed(exception.Message) => BrowserAutomationFailureKind.TargetClosed,
        _ => BrowserAutomationFailureKind.Other,
    };

    /// <summary>The exception type to put in the report. Types only — a message can carry state, a type cannot.</summary>
    public static string TypeName(Exception exception) =>
        Classify(exception) == BrowserAutomationFailureKind.TargetClosed &&
        !exception.GetType().Name.Contains("TargetClosed", StringComparison.Ordinal)
            ? TargetClosedExceptionName
            : exception.GetType().Name;

    private static bool IsTargetClosed(string? message) =>
        message is not null &&
        (message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("TargetClosedException", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("has been closed", StringComparison.OrdinalIgnoreCase));

    private static bool IsTimeout(string? message) =>
        message is not null &&
        message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("exceeded", StringComparison.OrdinalIgnoreCase);
}
