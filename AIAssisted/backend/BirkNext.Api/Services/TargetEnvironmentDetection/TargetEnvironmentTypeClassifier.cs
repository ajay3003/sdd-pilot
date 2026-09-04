using BirkNext.Api.Models;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

public static class TargetEnvironmentTypeClassifier
{
    public static FrontendEnvironmentType? Infer(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var host = hostname.Trim('[', ']').ToLowerInvariant();
        if (host.Contains("prod") || host.Contains("production")) return FrontendEnvironmentType.Production;
        if (host.Contains("dev") || host.Contains("development")) return FrontendEnvironmentType.Development;
        if (host.Contains("qa") || host.Contains("test")) return FrontendEnvironmentType.QA;
        if (host.Contains("rc") || host.Contains("staging")) return FrontendEnvironmentType.RC;
        if (host == "localhost" || host == "::1" || host == "127.0.0.1" || host.StartsWith("127.") || host.EndsWith(".localhost"))
            return FrontendEnvironmentType.Local;
        return null;
    }
}
