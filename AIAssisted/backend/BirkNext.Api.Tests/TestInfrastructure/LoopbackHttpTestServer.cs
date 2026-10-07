using System.Collections.Concurrent;
using System.Net;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Test-only loopback HTTP server with a deterministic shutdown. <see cref="HttpListener"/> unblocks a pending
/// <c>GetContextAsync</c> differently per platform when it is stopped: http.sys (Windows) throws
/// <see cref="HttpListenerException"/>, the managed listener (Linux, the CI agents) throws <see cref="ObjectDisposedException"/>
/// for 'listener'. Both mean "stopped" only when this server asked to stop; at any other time they are real failures and are
/// rethrown from <see cref="StopAsync"/> so the test that owns the server fails.
/// </summary>
public sealed class LoopbackHttpTestServer : IAsyncDisposable
{
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ConcurrentQueue<Exception> _faults = new();
    private readonly object _gate = new();
    private HttpListener? _listener;
    private Task? _loop;
    private Task? _stop;

    public LoopbackHttpTestServer(Func<HttpListenerContext, Task> handler) => _handler = handler;

    public int Port { get; private set; }

    public string Url(string path) => $"http://localhost:{Port}{path}";

    /// <summary>
    /// Binds a free port and starts accepting. Binding is the check (no "find a free port, bind later" race): a port in use
    /// fails <see cref="HttpListener.Start"/> and the next candidate is tried. Accepting starts synchronously — no warm-up delay.
    /// </summary>
    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("The server is already started.");
        var first = Random.Shared.Next(20000, 60000);
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var port = 20000 + (first - 20000 + attempt * 97) % 40000;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                listener.Close();
                continue;
            }
            _listener = listener;
            Port = port;
            _loop = AcceptAsync(listener);
            return;
        }
        throw new InvalidOperationException("No free loopback port could be bound for the test server.", last);
    }

    /// <summary>
    /// Stops the server: signal stopping → stop accepting (unblocks the pending accept) → await the accept loop → await
    /// in-flight requests → release the listener → dispose the token. Idempotent: every call returns the same stop.
    /// Unexpected accept or handler failures are rethrown here.
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate) return _stop ??= StopCoreAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task StopCoreAsync()
    {
        _stopping.Cancel();
        try
        {
            if (_listener is { IsListening: true } listener) listener.Stop();
            if (_loop is not null) await _loop;
            await Task.WhenAll(_inFlight.Keys);
        }
        finally
        {
            _listener?.Close();
            _stopping.Dispose();
        }
        if (!_faults.IsEmpty) throw new AggregateException("The loopback test server failed while serving requests.", _faults);
    }

    private async Task AcceptAsync(HttpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (_stopping.IsCancellationRequested && IsStopSignal(ex))
            {
                return;
            }
            var request = HandleAsync(context);
            _inFlight.TryAdd(request, 0);
            _ = request.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await _handler(context);
        }
        catch (Exception ex) when (_stopping.IsCancellationRequested && (IsStopSignal(ex) || ex is IOException))
        {
            // The response was cut off because the server is stopping.
        }
        catch (Exception ex)
        {
            // A bug in a test page: record it (StopAsync rethrows it) and answer 500, so the client sees a failure on every
            // platform (an aborted response is not reported to the client the same way by http.sys and the managed listener).
            _faults.Enqueue(ex);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception closeFailure) when (IsStopSignal(closeFailure) || closeFailure is IOException)
            {
                context.Response.Abort();
            }
        }
    }

    /// <summary>What a stopped listener throws on each platform.</summary>
    private static bool IsStopSignal(Exception ex) =>
        ex is HttpListenerException or ObjectDisposedException or InvalidOperationException;
}
