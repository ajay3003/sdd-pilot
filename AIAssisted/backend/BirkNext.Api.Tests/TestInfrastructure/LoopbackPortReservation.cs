using System.Net;
using System.Net.Sockets;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// A loopback port this test owns, held by the operating system (2026-11-25). Replaces the "bind port 0, read the port, release it, use the number
/// later" pattern: between the release and the later use, any parallel test (or process) could take the port — a "closed" port then answers, or the
/// listener that should have it fails to bind. CI_DEF_3's loopback server once answered another test's probe that way.
/// <para>The socket is <b>bound but not listening</b>: connections to the port are refused (exactly what "nothing listens here" means), and no other
/// bind can take the port, in this process or any other, until the reservation is disposed. When a test's own server should listen there later,
/// <see cref="Listen"/> starts listening on the same socket — the port is never released in between.</para>
/// </summary>
public sealed class LoopbackPortReservation : IDisposable
{
    private Socket? _socket;

    private LoopbackPortReservation(Socket socket)
    {
        _socket = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Reserves a free loopback port (the OS chooses it; an in-use port is never chosen).</summary>
    public static LoopbackPortReservation Reserve()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Windows: without exclusive use another socket that sets SO_REUSEADDR could still bind the port. The option does not exist elsewhere.
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return new LoopbackPortReservation(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Starts listening on the reserved port and hands the socket over (the caller owns and disposes it); the port is never released.</summary>
    public Socket Listen(int backlog = 16)
    {
        var socket = Interlocked.Exchange(ref _socket, null) ?? throw new InvalidOperationException("The reservation was already handed over or disposed.");
        try { socket.Listen(backlog); return socket; }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Releases the port, unless <see cref="Listen"/> handed it over (then the listener's owner releases it).</summary>
    public void Dispose() => Interlocked.Exchange(ref _socket, null)?.Dispose();
}
