namespace BirkNext.Web.Services;

/// <summary>
/// The exporter's check messages in the tester's words (2026-11-25): what is wrong, what to do and where to fix it. The exporter's own text stays
/// available as the technical detail; an unknown message is shown as it is, never hidden.
/// </summary>
public static class AdoIssueText
{
    public const string SettingsLink = "admin/system-settings?section=azure-devops-test-plans";

    /// <param name="Text">Plain Norwegian.</param>
    /// <param name="FixInSettings">The value is a project default: fix it in System Settings → Azure DevOps – Test Plans → Field defaults.</param>
    public sealed record Explained(string Text, bool FixInSettings, bool Translated);

    public static Explained Explain(string message) => message switch
    {
        var m when m.StartsWith("Azure DevOps Area Path is missing", StringComparison.Ordinal) =>
            new("Område (Area Path) mangler. Velg standardområde i Azure DevOps-innstillingene, eller endre feltet for denne eksporten.", true, true),
        var m when m.StartsWith("The Area Path is not a valid path", StringComparison.Ordinal) =>
            new("Område (Area Path) har feil form. Skriv det som Prosjekt\\Område.", true, true),
        var m when m.StartsWith("Assigned To is not a valid", StringComparison.Ordinal) =>
            new("«Tildelt til» er ikke et gyldig brukernavn eller en gyldig e-postadresse.", true, true),
        var m when m.StartsWith("The initial State is missing", StringComparison.Ordinal) =>
            new("Starttilstand (State) mangler, for eksempel Design.", true, true),
        var m when m.StartsWith("The State is not a valid", StringComparison.Ordinal) =>
            new("Tilstanden (State) er ikke et gyldig navn.", true, true),
        var m when m.StartsWith("No test case is ready for export", StringComparison.Ordinal) =>
            new("Ingen testtilfeller er klare til eksport. Velg testtilfeller, eller rett dem som trenger oppfølging.", false, true),
        var m when m.StartsWith("Confirm that you checked every Azure DevOps test case id", StringComparison.Ordinal) =>
            new("Bekreft at du har kontrollert hver Azure DevOps-id, og at stegene kan erstattes.", false, true),
        _ => new(message, false, false),
    };
}
