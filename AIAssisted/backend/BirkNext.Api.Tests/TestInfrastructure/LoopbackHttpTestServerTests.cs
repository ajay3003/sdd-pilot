using System.Net;
using System.Text;
using Xunit;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// The shutdown contract the Playwright integration tests rely on. Before it, stopping the listener while an accept was pending
/// threw ObjectDisposedException('listener') on Linux, which the server loop did not expect, so test teardown failed in CI.
/// </summary>
public sealed class LoopbackHttpTestServerTests
{
    // A hang guard, not a performance limit: under a full parallel test run the listener and client can be starved well past 15 s
    // (seen at 21–38 s), so the guard is generous and only fails a test that really hangs.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);

    private static Task Ok(HttpListenerContext context)
    {
        var body = Encoding.UTF8.GetBytes("ok");
        context.Response.StatusCode = 200;
        context.Response.OutputStream.Write(body);
        context.Response.Close();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StopAsync_CompletesEvenWhenTheListenerNeverEndsThePendingAccept()
    {
        // The managed listener (Linux CI) does not always complete a pending accept when it is stopped. Before this fix the stop
        // then never completed, and an owner awaiting DisposeAsync without a bound (the Playwright integration tests) hung until
        // the 5-minute blame-hang timeout aborted the test host. Here the accept never completes at all.
        var neverAccepted = new TaskCompletionSource<HttpListenerContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoopbackHttpTestServer(Ok, _ => neverAccepted.Task);
        server.Start();

        await server.StopAsync().WaitAsync(Bound);
    }

    [Fact]
    public async Task AnAcceptCompletingAfterTheStop_IsObserved_NotLeftUnobserved()
    {
        var late = new TaskCompletionSource<HttpListenerContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoopbackHttpTestServer(Ok, _ => late.Task);
        server.Start();
        await server.StopAsync().WaitAsync(Bound);

        late.SetException(new ObjectDisposedException("listener"));

        Assert.True(late.Task.IsFaulted);
        Assert.NotNull(late.Task.Exception); // observed by the server's continuation as well; no UnobservedTaskException
    }

    [Fact]
    public async Task RepeatedStartStop_WithPendingAccepts_AlwaysCompletes()
    {
        for (var i = 0; i < 25; i++)
        {
            var server = new LoopbackHttpTestServer(Ok);
            server.Start();
            await server.StopAsync().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task StopAsync_WhileAnAcceptIsPending_CompletesCleanly()
    {
        var server = new LoopbackHttpTestServer(Ok);
        server.Start();

        await server.StopAsync().WaitAsync(Bound);
    }

    [Fact]
    public async Task DisposeAsync_AfterStopAsync_AndRepeatedStops_AreTheSameStop()
    {
        var server = new LoopbackHttpTestServer(Ok);
        server.Start();

        var first = server.StopAsync();
        var second = server.StopAsync();
        Assert.Same(first, second);
        await first.WaitAsync(Bound);
        await server.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task DisposeAsync_Directly_LeavesNoFaultedBackgroundWork()
    {
        await using (var server = new LoopbackHttpTestServer(Ok))
        {
            server.Start();
        }
    }

    [Fact]
    public async Task ServesRequests_ThenReleasesItsPort()
    {
        var server = new LoopbackHttpTestServer(Ok);
        server.Start();
        using (var client = new HttpClient())
            Assert.Equal("ok", await client.GetStringAsync(server.Url("/")).WaitAsync(Bound));
        await server.StopAsync().WaitAsync(Bound);

        // The prefix registration is released: the same port binds again.
        using var again = new HttpListener();
        again.Prefixes.Add($"http://localhost:{server.Port}/");
        again.Start();
        again.Stop();
    }

    [Fact]
    public async Task StartServeStop_Repeatedly_NeverFaults()
    {
        using var client = new HttpClient();
        for (var i = 0; i < 50; i++)
        {
            var server = new LoopbackHttpTestServer(Ok);
            server.Start();
            if (i % 2 == 0) Assert.Equal("ok", await client.GetStringAsync(server.Url($"/{i}")).WaitAsync(Bound));
            await server.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task UnexpectedHandlerFailure_StillFailsTheOwningTest()
    {
        var server = new LoopbackHttpTestServer(_ => throw new InvalidDataException("bug in the test page"));
        server.Start();
        using (var client = new HttpClient())
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(server.Url("/")).WaitAsync(Bound));

        var failure = await Assert.ThrowsAsync<AggregateException>(() => server.StopAsync().WaitAsync(Bound));
        Assert.Contains(failure.InnerExceptions, e => e is InvalidDataException);
    }

    [Fact]
    public async Task KeepsServing_WhileEveryThreadOfTheContextThatStartedItIsBlocked()
    {
        // Regression (partition-6 timeouts, docs/audits/birknext-backend-test-stall-root-cause-2026-11-09.md): the server was started on an xUnit
        // worker and its accept loop continued on xUnit's limited-concurrency synchronization context. While every worker was blocked in a
        // synchronous WebApplicationFactory host start, no request was accepted (seen: 51 s), so a client under test timed out against a
        // server that never answered. A server that stands in for a remote system must not depend on the test framework's threads.
        using var context = new SingleThreadContext();
        var started = new TaskCompletionSource<LoopbackHttpTestServer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        context.Post(_ =>
        {
            var s = new LoopbackHttpTestServer(Ok);
            s.Start();
            started.SetResult(s);
            release.Wait(); // the context's only thread is now blocked, as an xUnit worker is inside WebApplicationFactory.EnsureServer()
        }, null);
        var server = await started.Task.WaitAsync(Bound);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal("ok", await client.GetStringAsync(server.Url("/")));
        }
        finally
        {
            release.Set();
            await server.StopAsync().WaitAsync(Bound);
        }
    }

    /// <summary>A synchronization context with exactly one thread, like xUnit's MaxConcurrencySyncContext with every other worker busy.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _work.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true, Name = "single-thread-context" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            // After Dispose nothing runs here any more; a late post is dropped instead of throwing on a foreign thread.
            try { _work.Add((d, state)); }
            catch (InvalidOperationException) when (_work.IsAddingCompleted) { }
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(Bound);
        }
    }

    [Fact]
    public void Start_Twice_IsRejected()
    {
        var server = new LoopbackHttpTestServer(Ok);
        server.Start();
        try { Assert.Throws<InvalidOperationException>(server.Start); }
        finally { server.StopAsync().GetAwaiter().GetResult(); }
    }
}
