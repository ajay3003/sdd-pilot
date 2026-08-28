namespace BirkNext.Api.Services.FrontendQualityEngines;

public static class FrontendQualityEngineAuthenticationSupport
{
    public static bool Supports(FrontendQualityEngineId id, ReviewAuthenticationMode mode) => (id, mode) switch
    {
        (_, ReviewAuthenticationMode.Anonymous) => true,
        (FrontendQualityEngineId.BrowserRuntime, ReviewAuthenticationMode.Authenticated) => true,
        (FrontendQualityEngineId.Accessibility, ReviewAuthenticationMode.Authenticated) => true,
        (FrontendQualityEngineId.Lighthouse, ReviewAuthenticationMode.Authenticated) => false,
        (FrontendQualityEngineId.PassiveSecurity, ReviewAuthenticationMode.Authenticated) => false,
        _ => false,
    };
}
