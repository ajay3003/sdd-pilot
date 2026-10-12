using System.Text.RegularExpressions;
using BirkNext.Web.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BirkNext.Web.Tests.Configuration;

/// <summary>
/// Entra sign-in is MSAL only (Program.cs: AddMsalAuthentication when configured, an anonymous provider otherwise). The page must load the
/// MSAL authentication runtime: the generic OIDC AuthenticationService.js crashes on MSAL settings at the first sign-in or token request
/// ("Cannot read properties of undefined (reading 'join')" in createUserManager). Missing configuration never becomes authenticated.
/// </summary>
public sealed class EntraAuthenticationStartupTests
{
    private static string FrontendRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web"));

    [Fact]
    public void IndexHtml_LoadsExactlyOneAuthenticationRuntime_TheMsalOne()
    {
        var html = File.ReadAllText(Path.Combine(FrontendRoot, "wwwroot", "index.html"));
        var scripts = Regex.Matches(html, @"<script\s+src=""([^""]*AuthenticationService\.js)""").Select(match => match.Groups[1].Value).ToArray();

        scripts.Should().Equal(["_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js"],
            "Program.cs configures MSAL; the generic OIDC runtime must not be loaded");
        html.IndexOf(scripts[0], StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("_framework/blazor.webassembly.js", StringComparison.Ordinal),
            "the authentication runtime must be loaded before Blazor starts");
    }

    [Fact]
    public void TheMsalRuntime_ComesFromAReferencedPackage_AndNoOidcProviderIsRegistered()
    {
        File.ReadAllText(Path.Combine(FrontendRoot, "BirkNext.Web.csproj")).Should().Contain("Microsoft.Authentication.WebAssembly.Msal");
        var program = File.ReadAllText(Path.Combine(FrontendRoot, "Program.cs"));
        program.Should().Contain("AddMsalAuthentication").And.NotContain("AddOidcAuthentication", "there is one sign-in mode");
    }

    [Theory]
    [InlineData(null, "client", "api://x/ActiveEventExecute")]
    [InlineData("tenant", null, "api://x/ActiveEventExecute")]
    [InlineData("tenant", "client", null)]
    [InlineData("", "", "")]
    public void IncompleteEntraConfiguration_IsNotConfigured(string? tenant, string? client, string? scope)
    {
        var settings = EntraClientSettings.From(Config(tenant, client, scope));
        settings.Configured.Should().BeFalse("missing configuration keeps the app anonymous; it never silently becomes authenticated");
    }

    [Fact]
    public void CompleteEntraConfiguration_IsConfigured_WithItsOwnValuesOnly()
    {
        var settings = EntraClientSettings.From(Config("tenant-id", "client-id", "api://birknext/ActiveEventExecute"));
        settings.Configured.Should().BeTrue();
        settings.ClientId.Should().Be("client-id");
        settings.Authority.Should().Be("https://login.microsoftonline.com/tenant-id");
        settings.ApiScopes.Should().Equal("api://birknext/ActiveEventExecute");
    }

    [Fact]
    public void TheShippedAppSettings_HaveNoEntraValues()
    {
        var settings = EntraClientSettings.From(new ConfigurationBuilder().SetBasePath(FrontendRoot).AddJsonFile("wwwroot/appsettings.json", optional: false).Build());
        settings.Configured.Should().BeFalse("no tenant, client or scope is embedded in the application");
    }

    [Fact]
    public async Task WithoutEntra_TheAuthenticationStateIsAnonymous()
    {
        var state = await new AnonymousAuthenticationStateProvider().GetAuthenticationStateAsync();
        state.User.Identity?.IsAuthenticated.Should().BeFalse();
    }

    private static IConfiguration Config(string? tenant, string? client, string? scope) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:Entra:TenantId"] = tenant, ["Authentication:Entra:ClientId"] = client, ["Authentication:Entra:ActiveEventApiScope"] = scope,
    }).Build();
}
