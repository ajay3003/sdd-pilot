using Microsoft.Extensions.Configuration;

namespace BirkNext.Web.Tests.Configuration;

public sealed class BackendUrlConfigurationTests
{
    [Fact]
    public void DevelopmentRuntimeConfiguration_UsesHttpLoopbackBackend()
    {
        var frontendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web"));
        var configuration = new ConfigurationBuilder()
            .SetBasePath(frontendRoot)
            .AddJsonFile("wwwroot/appsettings.json", optional: false)
            .Build();

        Assert.Equal("http://localhost:5000", configuration["BackendUrl"]);
    }

    [Fact]
    public void RuntimeOverride_TakesPrecedenceOverPackagedDefault()
    {
        var frontendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web"));
        var configuration = new ConfigurationBuilder()
            .SetBasePath(frontendRoot)
            .AddJsonFile("wwwroot/appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackendUrl"] = "https://api.example.test"
            })
            .Build();

        Assert.Equal("https://api.example.test", configuration["BackendUrl"]);
    }
}
