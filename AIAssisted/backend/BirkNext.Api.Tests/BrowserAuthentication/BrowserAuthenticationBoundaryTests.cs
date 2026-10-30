using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.BrowserAuthentication;

/// <summary>
/// Boundaries of the unattended browser login. Attended flows do not depend on it (they behave exactly as before), and browser login is one
/// hop only: Altinn/Skolenærvær execution, Altinn → MU, BirkNext → Altinn API credentials and service-to-service authentication never read it.
/// </summary>
public sealed class BrowserAuthenticationBoundaryTests
{
    private static readonly string Api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));

    private static IEnumerable<(string Rel, string Text)> Sources(string folder) =>
        Directory.GetFiles(Path.Combine(Api, folder), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(Api, f).Replace('\\', '/'), File.ReadAllText(f)));

    /// <summary>A reference to the unattended browser login (its namespaces or types). Altinn's own recorded browser-sign-in observation
    /// (<c>AltinnBrowserAuthenticationObservation</c>) is a different, older concept and does not count.</summary>
    private static bool ReferencesUnattendedLogin(string text) =>
        text.Contains("BirkNext.BrowserAuthentication", StringComparison.Ordinal) || text.Contains("Services.BrowserAuthentication", StringComparison.Ordinal)
        || text.Contains("UnattendedLogin", StringComparison.Ordinal) || text.Contains("BrowserAuthenticationMode", StringComparison.Ordinal);

    [Theory]
    [InlineData("Services/AuthenticatedReview")]
    [InlineData("Services/ManagedEdge")]
    [InlineData("Services/LocalHttpsProxy")]
    [InlineData("Services/BrowserCompanion")]
    [InlineData("Services/HeadlessAuthDiagnostic")]
    public void AttendedFlows_DoNotDependOnUnattendedBrowserLogin(string folder) =>
        Sources(folder).Where(f => ReferencesUnattendedLogin(f.Text))
            .Select(f => f.Rel).Should().BeEmpty();

    [Theory]
    [InlineData("Services/AltinnTesting")]
    [InlineData("Services/IntegrationJourneys")]
    [InlineData("Services/ActiveEventTesting")]
    public void ServiceAndAltinnAuthentication_NeverReadTheUnattendedBrowserLogin(string folder) =>
        Sources(folder).Where(f => ReferencesUnattendedLogin(f.Text))
            .Select(f => f.Rel).Should().BeEmpty("browser login never satisfies Altinn → MU, Altinn API or service-to-service authentication");

    [Fact]
    public void UnattendedBrowserLogin_DoesNotUseAttendedSessionsProfilesOrTestData()
    {
        var forbidden = new[] { "IAuthenticatedBrowserSessionManager", "ManagedEdge", "LaunchPersistentContext", "ManagedEdgeProfile", "ITestDataProvider", "AltinnSessionStore", "AppDbContext" };
        Sources("Services/BrowserAuthentication")
            .SelectMany(f => forbidden.Where(w => f.Text.Contains(w, StringComparison.Ordinal)).Select(w => $"{f.Rel}: {w}"))
            .Should().BeEmpty("the unattended login uses its own fresh, non-persistent browser context and its own protected credential");
    }
}
