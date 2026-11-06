using System.Net.Http.Json;
using BirkNext.Api.Services.LocalSecurity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>
/// An in-process client that went through the real local-session bootstrap (docs/security/localhost-session-design-2026-11-06.md): it reads the
/// launch code from the running service — as the launcher reads it from App_Data — exchanges it for the session cookie (kept by the factory's cookie
/// handler) and identifies itself as the BirkNext client. No production bypass exists for tests.
/// </summary>
public static class LocalSessionTestClient
{
    public const string FrontendOrigin = "http://localhost:5173";

    /// <param name="withOrigin">Send the frontend Origin on every request (false for tests that set Origin per request themselves).</param>
    public static HttpClient CreateLocalSessionClient<TEntry>(this WebApplicationFactory<TEntry> factory, WebApplicationFactoryClientOptions? options = null, bool withOrigin = true)
        where TEntry : class
    {
        var client = options is null ? factory.CreateClient() : factory.CreateClient(options);
        client.DefaultRequestHeaders.Add(LocalSessionService.ClientHeader, "tests");
        if (withOrigin) client.DefaultRequestHeaders.Add("Origin", FrontendOrigin);
        var code = factory.Services.GetRequiredService<LocalSessionService>().CurrentCode;
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "api/local-session") { Content = JsonContent.Create(new { code }) };
        if (!withOrigin) exchange.Headers.Add("Origin", FrontendOrigin);
        // On the thread pool: blocking an xUnit worker on a continuation that needs that worker would deadlock under limited parallelism.
        var (status, body) = Task.Run(async () =>
        {
            using var response = await client.SendAsync(exchange);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }).GetAwaiter().GetResult();
        if ((int)status is < 200 or > 299)
            throw new InvalidOperationException($"Local-session bootstrap failed in the test host: {(int)status} {body}");
        return client;
    }
}
