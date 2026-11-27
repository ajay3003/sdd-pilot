using System.Reflection;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// V11 (2026-11-27, backend test stability): HotChocolate 14 registers every GraphQL request-executor resolver in a STATIC hot-reload list
/// (<c>RequestExecutorResolver.ApplicationUpdateHandler._actions</c>) and never removes it. An in-process API host disposed by a test therefore
/// stays reachable through that list, with its schema and service provider (measured: about 11.7 MB per host; see TestHostMemoryDiagnostics).
/// Hot reload never happens in a test run, so removing a DISPOSED host's entry is safe; live hosts keep theirs. Never used by the product.
/// </summary>
public static class HostStaticRegistrations
{
    private static readonly FieldInfo? Actions = typeof(IRequestExecutorResolver).Assembly
        .GetType("HotChocolate.Execution.RequestExecutorResolver+ApplicationUpdateHandler")
        ?.GetField("_actions", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>The resolver of a host, captured BEFORE it is disposed (null when the host has no GraphQL).</summary>
    public static object? ResolverOf(IServiceProvider services) => services.GetService<IRequestExecutorResolver>();

    /// <summary>Removes the hot-reload registrations that belong to <paramref name="resolver"/>. Returns how many were removed (-1 when the
    /// library's internals are not found: a newer HotChocolate may have fixed or renamed it, and nothing is touched).</summary>
    public static int Release(object? resolver)
    {
        if (resolver is null) return 0;
        if (Actions?.GetValue(null) is not List<Action> list) return -1;
        lock (list)
        {
            return list.RemoveAll(a => ReferenceEquals(a.Target, resolver)
                || a.Target?.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => ReferenceEquals(f.GetValue(a.Target), resolver)) == true);
        }
    }

    /// <summary>How many registrations the static list holds now (diagnostics; -1 when not found).</summary>
    public static int Count() => Actions?.GetValue(null) is List<Action> list ? list.Count : -1;

    private static readonly FieldInfo? Disposed = typeof(IRequestExecutorResolver).Assembly
        .GetType("HotChocolate.Execution.RequestExecutorResolver")?.GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Removes every registration whose resolver is disposed (its host is gone). HotChocolate locks the same list when it adds or iterates.</summary>
    public static int PruneDisposed()
    {
        if (Actions?.GetValue(null) is not List<Action> list || Disposed is null) return -1;
        lock (list)
        {
            return list.RemoveAll(a => a.Target is { } target && (Disposed.DeclaringType!.IsInstanceOfType(target) ? target
                : target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.GetValue(target)).FirstOrDefault(Disposed.DeclaringType.IsInstanceOfType))
                is { } resolver && Disposed.GetValue(resolver) is true);
        }
    }

    private static Timer? _pruner;

    /// <summary>Every test run prunes disposed hosts' registrations once a second, so no test class has to remember it.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void StartPruning() =>
        _pruner ??= new Timer(_ => { try { PruneDisposed(); } catch (Exception) { /* diagnostics only: never fail a test run */ } }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
}
