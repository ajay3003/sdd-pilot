using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Port ownership for parallel tests (2026-11-25). The old pattern — bind port 0, read it, release it, use the number later — let another parallel
/// test take the port in between (CI_DEF_3's loopback server answered another test's probe). A reservation is held by the OS: a "closed" port
/// refuses connections and cannot be bound by anyone else, and a test's own server takes the port over without releasing it. All on loopback;
/// nothing touches the running application's ports.
/// </summary>
public sealed class LoopbackPortReservationTests
{
    private static async Task<SocketError?> ConnectAsync(int port)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(TimeSpan.FromSeconds(10)); return null; }
        catch (SocketException e) { return e.SocketErrorCode; }
    }

    [Fact]
    public async Task P01_a_reserved_port_refuses_connections_like_a_closed_port()
    {
        using var reservation = LoopbackPortReservation.Reserve();
        reservation.Port.Should().BeInRange(1, 65535);
        (await ConnectAsync(reservation.Port)).Should().Be(SocketError.ConnectionRefused);
    }

    [Fact]
    public void P02_no_other_listener_can_claim_a_reserved_port()
    {
        using var reservation = LoopbackPortReservation.Reserve();
        var steal = () => { var l = new TcpListener(IPAddress.Loopback, reservation.Port); l.Start(); l.Stop(); };
        steal.Should().Throw<SocketException>().Which.SocketErrorCode.Should().Be(SocketError.AddressAlreadyInUse);
    }

    [Fact]
    public async Task P03_parallel_reservations_and_ephemeral_listeners_never_share_a_port()
    {
        var reservations = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(LoopbackPortReservation.Reserve)));
        try
        {
            var reserved = reservations.Select(r => r.Port).ToHashSet();
            reserved.Should().HaveCount(64, "every reservation owns its own port");
            // What a parallel test does: listen on port 0 (the OS never hands out a reserved port).
            var listeners = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return l; })));
            try { listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).Should().NotIntersectWith(reserved); }
            finally { foreach (var l in listeners) l.Stop(); }
        }
        finally { foreach (var r in reservations) r.Dispose(); }
    }

    [Fact]
    public async Task P04_the_tests_own_server_takes_the_port_over_and_the_connection_reaches_it()
    {
        using var reservation = LoopbackPortReservation.Reserve();
        var port = reservation.Port;
        using var listener = reservation.Listen();
        ((IPEndPoint)listener.LocalEndPoint!).Port.Should().Be(port, "the same socket listens: the port was never released");

        var accept = listener.AcceptAsync();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        await client.SendAsync("ping"u8.ToArray());
        var buffer = new byte[4];
        (await accepted.ReceiveAsync(buffer)).Should().Be(4, "the connection reached the intended server");

        var again = () => reservation.Listen();
        again.Should().Throw<InvalidOperationException>("a reservation is handed over once");
    }

    [Fact]
    public async Task P05_disposing_releases_the_port_and_is_safe_twice_also_after_a_failure()
    {
        int port;
        try
        {
            using var reservation = LoopbackPortReservation.Reserve();
            port = reservation.Port;
            throw new InvalidOperationException("a failing test");
        }
        catch (InvalidOperationException) { }
        var reservation2 = LoopbackPortReservation.Reserve();
        reservation2.Dispose();
        reservation2.Dispose();

        // After a reservation is disposed its port can be bound again (cleanup really happened).
        var released = LoopbackPortReservation.Reserve();
        var number = released.Port;
        released.Dispose();
        var listener = new TcpListener(IPAddress.Loopback, number);
        var bind = () => listener.Start();
        bind.Should().NotThrow();
        listener.Stop();
        (await ConnectAsync(number)).Should().Be(SocketError.ConnectionRefused, "nothing is left listening");
    }

    [Fact]
    public async Task P06_a_handed_over_listener_is_released_by_its_owner_not_by_the_reservation()
    {
        var reservation = LoopbackPortReservation.Reserve();
        var listener = reservation.Listen();
        reservation.Dispose();
        var accept = listener.AcceptAsync();
        (await ConnectAsync(reservation.Port)).Should().BeNull("disposing the handed-over reservation does not close the listener");
        (await accept.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        listener.Dispose();
        (await ConnectAsync(reservation.Port)).Should().Be(SocketError.ConnectionRefused);
    }
}
