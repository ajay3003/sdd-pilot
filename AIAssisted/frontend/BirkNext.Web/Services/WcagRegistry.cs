using BirkNext.Web.Models;
using BirkNext.BrowserCompanion;

namespace BirkNext.Web.Services;

/// <summary>All A/AA criteria in 2.1 and 2.2. Automation describes implemented capability, not theoretical potential.</summary>
public static class WcagRegistry
{
    private static WcagCriterionDefinition D(string id, WcagLevel level, string title,
        string checks = "", bool interaction = false, bool crossPage = false, bool manual = true,
        WcagVersion since = WcagVersion.Wcag21) => new(id, level, title,
            checks.Length == 0 && !AxeRuleCatalog.CanEvaluate(id) ? WcagAutomation.Manual : manual ? WcagAutomation.Partial : WcagAutomation.Automatic,
            checks.Split(',', StringSplitOptions.RemoveEmptyEntries), interaction, crossPage, manual,
            checks.Length == 0 && !AxeRuleCatalog.CanEvaluate(id) ? "No reliable native automated proof; human review required." : "Results cover tested elements only; semantic adequacy and unobserved states require review.", since);

    public static IReadOnlyList<WcagCriterionDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        D("1.1.1", WcagLevel.A, "Non-text Content", "a11y-image-alt,a11y-image-name"),
        D("1.2.1", WcagLevel.A, "Audio-only and Video-only (Prerecorded)", "media-presence"),
        D("1.2.2", WcagLevel.A, "Captions (Prerecorded)", "media-captions"),
        D("1.2.3", WcagLevel.A, "Audio Description or Media Alternative (Prerecorded)"),
        D("1.2.4", WcagLevel.AA, "Captions (Live)"),
        D("1.2.5", WcagLevel.AA, "Audio Description (Prerecorded)"),
        D("1.3.1", WcagLevel.A, "Info and Relationships", "a11y-control-label,a11y-heading-order,a11y-heading-h1,a11y-main-landmark"),
        D("1.3.2", WcagLevel.A, "Meaningful Sequence", "a11y-positive-tabindex"),
        D("1.3.3", WcagLevel.A, "Sensory Characteristics", "sensory-instructions"),
        D("1.3.4", WcagLevel.AA, "Orientation", interaction: true),
        D("1.3.5", WcagLevel.AA, "Identify Input Purpose", "input-purpose"),
        D("1.4.1", WcagLevel.A, "Use of Color"),
        D("1.4.2", WcagLevel.A, "Audio Control", "autoplay-audio"),
        D("1.4.3", WcagLevel.AA, "Contrast (Minimum)", "text-contrast"),
        D("1.4.4", WcagLevel.AA, "Resize Text", "resize-text", interaction: true),
        D("1.4.5", WcagLevel.AA, "Images of Text"),
        D("1.4.10", WcagLevel.AA, "Reflow", "reflow-snapshot"),
        D("1.4.11", WcagLevel.AA, "Non-text Contrast"),
        D("1.4.12", WcagLevel.AA, "Text Spacing", "text-spacing", interaction: true),
        D("1.4.13", WcagLevel.AA, "Content on Hover or Focus", interaction: true),
        D("2.1.1", WcagLevel.A, "Keyboard", "mouse-only", interaction: true),
        D("2.1.2", WcagLevel.A, "No Keyboard Trap", "keyboard-traversal", interaction: true),
        D("2.1.4", WcagLevel.A, "Character Key Shortcuts", interaction: true),
        D("2.2.1", WcagLevel.A, "Timing Adjustable", "timing-presence"),
        D("2.2.2", WcagLevel.A, "Pause, Stop, Hide", "moving-content"),
        D("2.3.1", WcagLevel.A, "Three Flashes or Below Threshold"),
        D("2.4.1", WcagLevel.A, "Bypass Blocks", "bypass-structure"),
        D("2.4.2", WcagLevel.A, "Page Titled", "a11y-page-title"),
        D("2.4.3", WcagLevel.A, "Focus Order", "a11y-positive-tabindex,a11y-hidden-focusable", interaction: true),
        D("2.4.4", WcagLevel.A, "Link Purpose (In Context)", "a11y-link-name"),
        D("2.4.5", WcagLevel.AA, "Multiple Ways", "navigation-structure", crossPage: true),
        D("2.4.6", WcagLevel.AA, "Headings and Labels", "a11y-control-label,a11y-heading-order"),
        D("2.4.7", WcagLevel.AA, "Focus Visible", "focus-indicator", interaction: true),
        D("2.5.1", WcagLevel.A, "Pointer Gestures", "gesture-presence"),
        D("2.5.2", WcagLevel.A, "Pointer Cancellation", "pointerdown-presence"),
        D("2.5.3", WcagLevel.A, "Label in Name", "label-in-name"),
        D("2.5.4", WcagLevel.A, "Motion Actuation"),
        D("3.1.1", WcagLevel.A, "Language of Page", "a11y-document-lang"),
        D("3.1.2", WcagLevel.AA, "Language of Parts", "language-parts"),
        D("3.2.1", WcagLevel.A, "On Focus", interaction: true),
        D("3.2.2", WcagLevel.A, "On Input", interaction: true),
        D("3.2.3", WcagLevel.AA, "Consistent Navigation", "navigation-structure", crossPage: true),
        D("3.2.4", WcagLevel.AA, "Consistent Identification", "component-structure", crossPage: true),
        D("3.3.1", WcagLevel.A, "Error Identification", "invalid-association"),
        D("3.3.2", WcagLevel.A, "Labels or Instructions", "a11y-control-label"),
        D("3.3.3", WcagLevel.AA, "Error Suggestion", "invalid-association"),
        D("3.3.4", WcagLevel.AA, "Error Prevention (Legal, Financial, Data)"),
        D("4.1.1", WcagLevel.A, "Parsing", "a11y-duplicate-id"),
        D("4.1.2", WcagLevel.A, "Name, Role, Value", "a11y-button-name,a11y-dialog-name,a11y-aria-reference"),
        D("4.1.3", WcagLevel.AA, "Status Messages", "status-presence"),
        D("2.4.11", WcagLevel.AA, "Focus Not Obscured (Minimum)", interaction: true, since: WcagVersion.Wcag22),
        D("2.5.7", WcagLevel.AA, "Dragging Movements", "gesture-presence", since: WcagVersion.Wcag22),
        D("2.5.8", WcagLevel.AA, "Target Size (Minimum)", since: WcagVersion.Wcag22),
        D("3.2.6", WcagLevel.A, "Consistent Help", crossPage: true, since: WcagVersion.Wcag22),
        D("3.3.7", WcagLevel.A, "Redundant Entry", since: WcagVersion.Wcag22),
        D("3.3.8", WcagLevel.AA, "Accessible Authentication (Minimum)", since: WcagVersion.Wcag22),
    });

    public static IEnumerable<WcagCriterionDefinition> For(WcagSettings settings) => All
        .Where(d => settings.Profile.CriterionIds.Contains(d.CriterionId))
        .OrderBy(d => Version.Parse(d.CriterionId));
}
