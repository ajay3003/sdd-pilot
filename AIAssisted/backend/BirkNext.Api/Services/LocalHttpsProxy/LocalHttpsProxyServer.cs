using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Opens outbound connections for the proxy. Never routes through the proxy itself.</summary>
public interface IUpstreamConnector
{
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
    /// <summary>Null means default OS certificate validation for upstream TLS.</summary>
    RemoteCertificateValidationCallback? CertificateValidation { get; }
}

/// <summary>Direct TCP (default) or through a configured corporate upstream proxy via CONNECT. No credentials are sent to the upstream proxy.</summary>
public sealed class DirectUpstreamConnector(string? upstreamProxy = null) : IUpstreamConnector
{
    private readonly (string Host, int Port)? _proxy = Parse(upstreamProxy);

    public RemoteCertificateValidationCallback? CertificateValidation => null;

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (_proxy is { } proxy)
            {
                await tcp.ConnectAsync(proxy.Host, proxy.Port, cancellationToken);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"), cancellationToken);
                using var reader = new BufferedNetworkReader(stream, 4096);
                var head = await reader.ReadHeadAsync(16384, cancellationToken) ?? throw new IOException("The upstream proxy closed the connection.");
                if (!HttpHead.TryParse(head, out var response) || response.StatusCode is < 200 or >= 300) throw new IOException("The upstream proxy refused the tunnel.");
                return stream;
            }
            await tcp.ConnectAsync(host, port, cancellationToken);
            return tcp.GetStream();
        }
        catch { tcp.Dispose(); throw; }
    }

    private static (string, int)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out var port) || port is < 1 or > 65535 || value.Contains('@'))
            throw new ArgumentException("LocalHttpsProxy:UpstreamProxy must be host:port without credentials.");
        return (value[..colon].Trim(), port);
    }
}

/// <summary>One intercepted request/response pair on an approved host. The bearer reference is cleared right after the observer ran.</summary>
internal sealed class ProxyExchange
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Method { get; init; }
    public int StatusCode { get; init; }
    public string? BearerToken { get; set; }
    public override string ToString() => $"{Method} {Host}:{Port} -> HTTP {StatusCode}";
}

internal interface IProxyTrafficObserver
{
    void OnPassThrough(string host, int port);
    void OnInterceptedConnection(string host);
    void OnTlsHandshakeFailed(string host);
    void OnExchange(ProxyExchange exchange);
}

/// <summary>
/// Loopback-only HTTP CONNECT proxy. Approved <c>host:port</c> authorities are intercepted (TLS terminated with a leaf from the
/// dedicated inspection root, HTTP/1.1 relayed verbatim while only the request line, method and Authorization scheme are inspected);
/// every other authority is tunnelled byte-for-byte without decryption. Nothing about a message is logged.
/// </summary>
internal sealed class LocalHttpsProxyServer(ApprovedHostSet scope, IProxyCertificateAuthority authority, IUpstreamConnector upstream, IProxyTrafficObserver observer, ILogger? logger = null) : IAsyncDisposable
{
    private static readonly byte[] ConnectionEstablished = "HTTP/1.1 200 Connection Established\r\nProxy-Agent: BirkNext-LocalHttpsProxy\r\n\r\n"u8.ToArray();
    private static readonly byte[] BadRequest = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
    private static readonly byte[] BadGateway = "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _connections;

    public IPEndPoint? Endpoint { get; private set; }
    public bool Faulted { get; private set; }
    public int ActiveConnections => _connections;

    public static bool IsLoopbackPortFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Binds 127.0.0.1 only (port 0 = OS-assigned). Throws <see cref="SocketException"/> when the port is occupied; never frees it.</summary>
    public void Start(int port)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start(64);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        if (!IPAddress.IsLoopback(endpoint.Address)) { listener.Stop(); throw new InvalidOperationException("The local HTTPS proxy must bind a loopback address only."); }
        Endpoint = endpoint;
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex)
        {
            Faulted = true;
            logger?.LogWarning("Local HTTPS proxy accept loop stopped with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken sessionToken)
    {
        Interlocked.Increment(ref _connections);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        var ct = lifetime.Token;
        try
        {
            using var owned = client;
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is IPEndPoint peer && !IPAddress.IsLoopback(peer.Address)) return;
            var stream = client.GetStream();
            using var reader = new BufferedNetworkReader(stream);
            var raw = await reader.ReadHeadAsync(65536, ct);
            if (raw is null) return;
            if (!HttpHead.TryParse(raw, out var request) || !request.IsRequest) { await stream.WriteAsync(BadRequest, ct); return; }
            if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase)) await HandleConnectAsync(stream, reader, request.Target, ct);
            else await HandlePlainAsync(stream, reader, request, ct);
        }
        catch (Exception ex) when (IsExpected(ex)) { logger?.LogDebug("Proxy connection ended with {ExceptionType}.", ex.GetType().Name); }
        catch (Exception ex) { logger?.LogWarning("Proxy connection failed with {ExceptionType}.", ex.GetType().Name); }
        finally { Interlocked.Decrement(ref _connections); }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException
        or AuthenticationException or InvalidDataException or InvalidOperationException or TimeoutException or System.Security.Cryptography.CryptographicException;

    private async Task HandleConnectAsync(NetworkStream client, BufferedNetworkReader reader, string target, CancellationToken ct)
    {
        if (!TryParseAuthority(target, out var host, out var port)) { await client.WriteAsync(BadRequest, ct); return; }
        if (scope.Contains(host, port)) await InterceptAsync(client, reader, host, port, ct);
        else await PassThroughAsync(client, reader, host, port, ct);
    }

    private static bool TryParseAuthority(string target, out string host, out int port)
    {
        host = ""; port = 443;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 300) return false;
        var colon = target.LastIndexOf(':');
        if (colon > 0 && !target.EndsWith(']'))
        {
            if (!int.TryParse(target[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            host = target[..colon];
        }
        else host = target;
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    /// <summary>Non-approved authority: opaque tunnel, no TLS termination, no inspection.</summary>
    private async Task PassThroughAsync(NetworkStream client, BufferedNetworkReader reader, string host, int port, CancellationToken ct)
    {
        Stream upstreamStream;
        try { upstreamStream = await upstream.ConnectAsync(host, port, ct).WaitAsync(ConnectTimeout, ct); }
        catch (Exception ex) when (IsExpected(ex)) { await client.WriteAsync(BadGateway, ct); return; }
        observer.OnPassThrough(host, port);
        await using (upstreamStream)
        {
            await client.WriteAsync(ConnectionEstablished, ct);
            await reader.FlushBufferedAsync(upstreamStream, ct);
            await PumpAsync(client, upstreamStream, ct);
        }
    }

    /// <summary>Plain absolute-form HTTP request (e.g. an http:// redirect hop): forwarded opaquely to its own host, never inspected.</summary>
    private async Task HandlePlainAsync(NetworkStream client, BufferedNetworkReader reader, HttpHead request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0) { await client.WriteAsync(BadRequest, ct); return; }
        Stream upstreamStream;
        try { upstreamStream = await upstream.ConnectAsync(uri.IdnHost, uri.Port, ct).WaitAsync(ConnectTimeout, ct); }
        catch (Exception ex) when (IsExpected(ex)) { await client.WriteAsync(BadGateway, ct); return; }
        observer.OnPassThrough(uri.IdnHost, uri.Port);
        await using (upstreamStream)
        {
            await upstreamStream.WriteAsync(request.Raw, ct);
            await reader.FlushBufferedAsync(upstreamStream, ct);
            await PumpAsync(client, upstreamStream, ct);
        }
    }

    private static async Task PumpAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ab = CopyAsync(a, b, cts.Token);
        var ba = CopyAsync(b, a, cts.Token);
        await Task.WhenAny(ab, ba);
        cts.Cancel();
        try { await Task.WhenAll(ab, ba); } catch (Exception ex) when (IsExpected(ex)) { }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 16384, ct); }
        catch (Exception ex) when (IsExpected(ex)) { }
    }

    private async Task InterceptAsync(NetworkStream client, BufferedNetworkReader plainReader, string host, int port, CancellationToken ct)
    {
        X509Certificate2 leaf;
        try { leaf = authority.IssueLeaf(host); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { await client.WriteAsync(BadGateway, ct); return; }

        await client.WriteAsync(ConnectionEstablished, ct);
        Stream clientBase = plainReader.HasBuffered ? new PrefixedStream(plainReader.TakeBuffered(), client) : client;
        var sslClient = new SslStream(clientBase, false);
        try
        {
            // Present the leaf together with the inspection root as the offered chain, so SChannel does not need to locate the CA in a
            // machine store to build the server chain (which yields an "unknown chain building error" when it cannot). The browser still
            // validates against its own trusted copy of the root.
            var chain = new X509Certificate2Collection();
            if (authority.AuthorityPublicCertificate is { } root) chain.Add(root);
            await sslClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(leaf, chain, offline: true),
                ClientCertificateRequired = false,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct).WaitAsync(HandshakeTimeout, ct);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Typical cause: the browser does not trust the inspection root yet and aborted the handshake.
            observer.OnTlsHandshakeFailed(host);
            sslClient.Dispose();
            return;
        }
        observer.OnInterceptedConnection(host);

        SslStream sslUpstream;
        try
        {
            var upstreamRaw = await upstream.ConnectAsync(host, port, ct).WaitAsync(ConnectTimeout, ct);
            sslUpstream = new SslStream(upstreamRaw, false, upstream.CertificateValidation);
            try
            {
                await sslUpstream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    EnabledSslProtocols = SslProtocols.None
                }, ct).WaitAsync(HandshakeTimeout, ct);
            }
            catch { sslUpstream.Dispose(); throw; }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            try { await sslClient.WriteAsync(BadGateway, ct); } catch (Exception inner) when (IsExpected(inner)) { }
            sslClient.Dispose();
            return;
        }

        await using (sslClient)
        await using (sslUpstream)
            await RelayAsync(sslClient, sslUpstream, host, port, ct);
    }

    private sealed class PendingRequest(string method, string? bearer, bool upgrade)
    {
        public string Method { get; } = method;
        public string? Bearer { get; set; } = bearer;
        public bool IsUpgrade { get; } = upgrade;
        public TaskCompletionSource<bool> UpgradeDecision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RelayAsync(SslStream client, SslStream server, string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var clientReader = new BufferedNetworkReader(client);
        using var serverReader = new BufferedNetworkReader(server);
        var pending = Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var responses = PumpResponsesAsync(client, serverReader, pending.Reader, host, port, cts.Token);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var raw = await clientReader.ReadHeadAsync(65536, cts.Token);
                if (raw is null) break;
                if (!HttpHead.TryParse(raw, out var request) || !request.IsRequest) break;
                var record = new PendingRequest(request.Method, ExtractBearer(request), request.IsUpgrade);
                await pending.Writer.WriteAsync(record, cts.Token);
                await server.WriteAsync(raw, cts.Token);
                await clientReader.CopyBodyAsync(server, BodyFraming.ForRequest(request), cts.Token);
                await server.FlushAsync(cts.Token);
                if (record.IsUpgrade)
                {
                    var upgraded = await record.UpgradeDecision.Task.WaitAsync(cts.Token);
                    if (upgraded) { await clientReader.CopyToEndAsync(server, cts.Token); break; }
                }
                if (request.ConnectionClose) break;
            }
            pending.Writer.TryComplete();
            try { await responses.WaitAsync(TimeSpan.FromSeconds(60), cts.Token); } catch (TimeoutException) { }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally
        {
            pending.Writer.TryComplete();
            cts.Cancel();
            try { await responses; } catch (Exception ex) when (IsExpected(ex)) { }
        }
    }

    private async Task PumpResponsesAsync(SslStream client, BufferedNetworkReader serverReader, ChannelReader<PendingRequest> pending, string host, int port, CancellationToken ct)
    {
        PendingRequest? current = null;
        try
        {
            while (true)
            {
                var raw = await serverReader.ReadHeadAsync(65536, ct);
                if (raw is null) break;
                if (!HttpHead.TryParse(raw, out var response) || response.StatusCode == 0) break;
                if (response.StatusCode is >= 100 and < 200 and not 101)
                {
                    await client.WriteAsync(raw, ct);
                    await client.FlushAsync(ct);
                    continue;
                }
                if (!await pending.WaitToReadAsync(ct) || !pending.TryRead(out current)) break;
                await client.WriteAsync(raw, ct);
                if (response.StatusCode == 101)
                {
                    current.UpgradeDecision.TrySetResult(true);
                    Report(current, host, port, 101);
                    await serverReader.CopyToEndAsync(client, ct);
                    break;
                }
                current.UpgradeDecision.TrySetResult(false);
                var framing = BodyFraming.ForResponse(response, current.Method);
                await serverReader.CopyBodyAsync(client, framing, ct);
                await client.FlushAsync(ct);
                Report(current, host, port, response.StatusCode);
                current = null;
                if (response.ConnectionClose || framing.Kind == BodyKind.UntilClose) break;
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally
        {
            current?.UpgradeDecision.TrySetResult(false);
            while (pending.TryRead(out var leftover)) { leftover.Bearer = null; leftover.UpgradeDecision.TrySetResult(false); }
        }
    }

    private void Report(PendingRequest request, string host, int port, int statusCode)
    {
        var exchange = new ProxyExchange { Host = host, Port = port, Method = request.Method, StatusCode = statusCode, BearerToken = request.Bearer };
        request.Bearer = null;
        try { observer.OnExchange(exchange); }
        catch (Exception ex) { logger?.LogWarning("Proxy traffic observer failed with {ExceptionType}.", ex.GetType().Name); }
        finally { exchange.BearerToken = null; }
    }

    private static string? ExtractBearer(HttpHead request)
    {
        var authorization = request.Header("Authorization");
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[7..].Trim();
        return BearerTokenInspector.LooksLikeBearerToken(token) ? token : null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        if (_acceptLoop is { } loop) { try { await loop; } catch (Exception ex) when (IsExpected(ex)) { } }
        _cts.Dispose();
    }

    /// <summary>Replays bytes a head reader over-read before delegating to the live stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, take).CopyTo(buffer);
                _offset += take;
                return take;
            }
            return inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length) return new(Read(buffer.Span));
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
