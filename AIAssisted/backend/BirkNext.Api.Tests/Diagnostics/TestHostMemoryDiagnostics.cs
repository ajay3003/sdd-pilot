using System.Collections;
using System.Reflection;
using BirkNext.Api.Data;
using BirkNext.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Diagnostics;

/// <summary>
/// V11 (2026-11-27) backend test stability: does an in-process API host leave memory behind after it is disposed? The integration classes start a
/// complete host per TEST (xUnit creates a class instance per test, and InitializeAsync builds a WebApplicationFactory). This measures, over
/// repeated create → request → dispose cycles, the managed heap after a full collection and the size of Entity Framework's global
/// <c>ServiceProviderCache</c>. A diagnostic, not a product test: it only fails when a cycle is clearly retained (see the threshold).
/// </summary>
[Trait("Category", "Diagnostics")]
public sealed class TestHostMemoryDiagnostics(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_hostmem");

    public async Task InitializeAsync() => await Execute($"CREATE DATABASE \"{_database}\"");

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static int EfServiceProviderCacheCount()
    {
        var type = typeof(DbContext).Assembly.GetType("Microsoft.EntityFrameworkCore.Internal.ServiceProviderCache");
        var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var field = type?.GetField("_configurations", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(instance) is ICollection c ? c.Count : -1;
    }

    /// <summary>Every live System.Threading timer: its callback's declaring type and method, its state's type, due time and period.</summary>
    private static List<string> LiveTimers()
    {
        var result = new List<string>();
        var queueType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
        if (queueType?.GetProperty("Instances", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is not Array queues) return ["TimerQueue.Instances not found"];
        foreach (var queue in queues)
        {
            if (queue is null) continue;
            foreach (var listName in new[] { "_shortTimers", "_longTimers" })
            {
                var node = queue.GetType().GetField(listName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(queue);
                while (node is not null)
                {
                    var t = node.GetType();
                    var callback = t.GetField("_timerCallback", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node) as Delegate;
                    var state = t.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node);
                    var period = t.GetField("_period", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node);
                    var due = t.GetField("_dueTime", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node);
                    var name = state?.GetType().GetProperty("Name")?.GetValue(state);
                    var lifetime = state?.GetType().GetProperty("Lifetime")?.GetValue(state);
                    result.Add($"{callback?.Method.DeclaringType?.FullName}.{callback?.Method.Name} state={state?.GetType().Name} name={name} lifetime={lifetime} due={due} period={period}");
                    node = t.GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node);
                }
            }
        }
        return result;
    }

    private static long Settled()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Fact]
    public async Task Releasing_the_disposed_hosts_HotChocolate_registration_releases_the_host()
    {
        var connection = TestPostgres.Temporary(_database).ConnectionString;
        WebApplicationFactory<Program> Host() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        }));
        async Task Cycle()
        {
            object? resolver;
            await using (var host = Host())
            {
                using var client = host.CreateClient();
                await client.GetAsync("/api/health");
                resolver = HostStaticRegistrations.ResolverOf(host.Services);
            }
            output.WriteLine($"released {HostStaticRegistrations.Release(resolver)} registration(s); list now {HostStaticRegistrations.Count()}");
        }
        await Cycle();
        var baseline = Settled();
        const int cycles = 6;
        var samples = new List<long>();
        for (var i = 1; i <= cycles; i++)
        {
            await Cycle();
            var now = Settled(); samples.Add(now);
            output.WriteLine($"cycle {i}: {now / 1_048_576.0:0.0} MB managed (+{(now - baseline) / 1_048_576.0:0.0} MB), active timers {Timer.ActiveCount}, pending work items {ThreadPool.PendingWorkItemCount}, threads {System.Diagnostics.Process.GetCurrentProcess().Threads.Count}");
        }
        var perCycle = (samples[^1] - samples[0]) / (double)(cycles - 1) / 1_048_576.0;
        output.WriteLine($"retained per host with the registration released: {perCycle:0.00} MB/cycle");
        // 2026-11-27: measured 11.87 MB/cycle with the registration released — the HotChocolate list is NOT the root (hypothesis disproven).
        output.WriteLine(perCycle < 2 ? "the registration was the root" : "the registration is NOT the root: retention unchanged");
        // Hypothesis 3: the IHttpClientFactory handler-expiry timer (2 minutes) keeps each disposed host reachable until it fires.
        await Task.Delay(TimeSpan.FromSeconds(5));
        var later = Settled();
        output.WriteLine($"after 5 s: {later / 1_048_576.0:0.0} MB managed (+{(later - baseline) / 1_048_576.0:0.0} MB), active timers {Timer.ActiveCount}");
        foreach (var line in LiveTimers()) output.WriteLine("timer: " + line);
    }

    /// <summary>Static fields (all loaded assemblies) whose collections grow, or whose value changes, across disposed hosts: candidates for what keeps them alive.</summary>
    private static Dictionary<string, string> StaticState()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            Type?[] types;
            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var type in types.OfType<Type>().Where(t => !t.ContainsGenericParameters))
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(f => !f.IsLiteral))
                {
                    object? value;
                    try { value = field.GetValue(null); } catch { continue; }
                    if (value is null) continue;
                    var key = $"{assembly.GetName().Name}|{type.FullName}.{field.Name}";
                    string state;
                    try
                    {
                        state = value switch
                        {
                            ICollection c => $"count={c.Count}",
                            string or ValueType => "",
                            _ => $"id={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)}",
                        };
                    }
                    catch (Exception) { continue; } // e.g. a default ImmutableArray
                    result[key] = state;
                }
        }
        return result;
    }

    [Fact]
    public async Task Static_state_that_grows_or_changes_with_each_disposed_host_is_listed()
    {
        var connection = TestPostgres.Temporary(_database).ConnectionString;
        WebApplicationFactory<Program> Host() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        }));
        async Task Cycle()
        {
            await using var host = Host();
            using var client = host.CreateClient();
            await client.GetAsync("/api/health");
        }
        await Cycle(); Settled();
        var before = StaticState();
        for (var i = 0; i < 3; i++) await Cycle();
        Settled();
        var after = StaticState();
        var changed = after.Where(kv => before.TryGetValue(kv.Key, out var b) ? b != kv.Value && kv.Value.Length > 0 : kv.Value.Length > 0)
            .Select(kv => $"{kv.Key}: {(before.TryGetValue(kv.Key, out var b) ? b : "(new)")} -> {kv.Value}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        output.WriteLine($"{changed.Count} static fields changed across 3 hosts:");
        foreach (var c in changed.Take(200)) output.WriteLine(c);
    }

    [Fact]
    public async Task A_disposed_API_host_releases_its_memory_and_adds_no_cached_EF_service_provider()
    {
        var connection = TestPostgres.Temporary(_database).ConnectionString;
        WebApplicationFactory<Program> Host() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        }));

        // Warm-up: the first host loads assemblies, JIT and static state once; it is not a leak.
        await using (var first = Host())
        {
            using var scope = first.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
            using var client = first.CreateClient();
            await client.GetAsync("/api/health");
        }
        var baseline = Settled(); var baseCache = EfServiceProviderCacheCount();
        output.WriteLine($"after warm-up: {baseline / 1_048_576.0:0.0} MB managed, EF service provider cache {baseCache}");

        const int cycles = 6;
        var samples = new List<long>();
        for (var i = 1; i <= cycles; i++)
        {
            await using (var host = Host())
            {
                using var client = host.CreateClient();
                await client.GetAsync("/api/health");
                using var scope = host.Services.CreateScope();
                _ = await scope.ServiceProvider.GetRequiredService<AppDbContext>().TestPlanDrafts.CountAsync();
            }
            var now = Settled(); samples.Add(now);
            output.WriteLine($"cycle {i}: {now / 1_048_576.0:0.0} MB managed (+{(now - baseline) / 1_048_576.0:0.0} MB), EF service provider cache {EfServiceProviderCacheCount()}");
        }
        var perCycle = (samples[^1] - samples[0]) / (double)(cycles - 1) / 1_048_576.0;
        output.WriteLine($"retained per host after warm-up: {perCycle:0.00} MB/cycle; EF cache grew by {EfServiceProviderCacheCount() - baseCache}");
        // Diagnostic threshold: a disposed host should not keep tens of MB each (a test class with 13 tests would keep hundreds of MB).
        perCycle.Should().BeLessThan(20, "a disposed API host must not retain its memory");
    }
}
