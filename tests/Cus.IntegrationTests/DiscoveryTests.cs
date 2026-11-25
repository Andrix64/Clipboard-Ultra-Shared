using System.Net;
using System.Net.Sockets;
using System.Text;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>
/// Scoperta via UDP multicast tra istanze sullo stesso PC. Usa una porta di test diversa da
/// quella reale (23701) per non vedere istanze di CUS eventualmente aperte.
/// </summary>
public sealed class DiscoveryTests : IAsyncLifetime
{
    private readonly TestRoot _root = new();
    private readonly List<TestNode> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task OnlyVisibleHosts_AreSeen_WithTheirParticipantCount()
    {
        var port = Random.Shared.Next(40000, 45000);
        var options = TestNode.DefaultOptions() with { EnableDiscovery = true, DiscoveryPort = port };
        var a = await TestNode.StartAsync(_root.Path, "a", options);
        var b = await TestNode.StartAsync(_root.Path, "b", options);
        var c = await TestNode.StartAsync(_root.Path, "c", options);
        _nodes.AddRange([a, b, c]);

        // Nessuno si offre: nessuno si vede (come AirDrop, si è visibili solo se lo si sceglie).
        await Task.Delay(1500);
        Assert.DoesNotContain(a.Node.Peers, p => p.IsAnnounced);

        await b.ShowAsync();
        await Eventually.TrueAsync(
            () => a.Node.Peers.Any(p => p.DeviceId == b.Node.Identity.DeviceId && p.Port == b.Node.Port && p.IsAnnounced && p.Participants == 1),
            "a vede b che si offre", timeoutMs: 15000);
        Assert.DoesNotContain(b.Node.Peers, p => p.DeviceId == a.Node.Identity.DeviceId);

        var peerB = a.Node.Peers.First(p => p.DeviceId == b.Node.Identity.DeviceId && p.IsAnnounced);
        Assert.True(peerB.CanJoin);
        var result = await a.Node.JoinAsync(peerB.Host, peerB.Port, peerB.DeviceId);
        Assert.Equal(JoinOutcome.Accepted, result.Outcome);

        // Chi guarda da fuori vede quante persone ci sono nella sessione di b.
        await Eventually.TrueAsync(
            () => c.Node.Peers.Any(p => p.DeviceId == b.Node.Identity.DeviceId && p.Participants == 2),
            "c vede due persone nella sessione di b", timeoutMs: 15000);

        // b smette di offrirsi: sparisce subito dall'elenco, senza aspettare la scadenza.
        await b.Node.SetVisibleAsync(false);
        await Eventually.TrueAsync(() => !c.Node.Peers.Any(p => p.DeviceId == b.Node.Identity.DeviceId), "b sparisce", timeoutMs: 3000);
        Assert.Equal(2, b.Node.Session?.Participants.Count);
    }

    [Fact]
    public async Task ForeignPackets_AreIgnored()
    {
        var port = Random.Shared.Next(45000, 50000);
        var seen = new List<DiscoveryAnnouncement>();
        var identity = DeviceIdentity.Generate("n", "u");
        await using var discovery = new Discovery(identity, () => new AnnouncementPayload("id", "n", "u", 1234), port);
        discovery.Announced += a => { lock (seen) seen.Add(a); };
        discovery.Start();

        using var udp = new UdpClient();
        await udp.SendAsync("non json"u8.ToArray(), new IPEndPoint(Discovery.Group, port));
        await udp.SendAsync("{\"App\":\"ALTRO\",\"DeviceId\":\"x\",\"DisplayName\":\"x\",\"UserName\":\"x\",\"Port\":1}"u8.ToArray(), new IPEndPoint(Discovery.Group, port));
        // Annuncio in chiaro (formato senza firma) con un ID valido: scartato.
        await udp.SendAsync(Encoding.UTF8.GetBytes($"{{\"App\":\"CUS\",\"DeviceId\":\"{new string('A', 32)}\",\"DisplayName\":\"x\",\"UserName\":\"x\",\"Port\":1}}"), new IPEndPoint(Discovery.Group, port));
        await Task.Delay(500);

        lock (seen)
            Assert.Empty(seen); // anche il proprio annuncio è escluso
    }

    [Fact]
    public async Task ForgedHiddenAnnouncement_DoesNotRemoveTheHost()
    {
        var port = Random.Shared.Next(50000, 52500);
        var options = TestNode.DefaultOptions() with { EnableDiscovery = true, DiscoveryPort = port };
        var host = await TestNode.StartAsync(_root.Path, "host", options);
        var viewer = await TestNode.StartAsync(_root.Path, "viewer", options);
        _nodes.AddRange([host, viewer]);

        await host.ShowAsync();
        await Eventually.TrueAsync(() => viewer.Node.Peers.Any(p => p.DeviceId == host.Node.Identity.DeviceId), "viewer vede host", timeoutMs: 15000);

        // Un attaccante manda congedi a nome dell'host: in chiaro, e firmati con la propria chiave.
        using var udp = new UdpClient();
        var hostId = host.Node.Identity.DeviceId;
        await udp.SendAsync(Encoding.UTF8.GetBytes(
            $"{{\"App\":\"CUS\",\"DeviceId\":\"{hostId}\",\"DisplayName\":\"\",\"UserName\":\"\",\"Port\":{host.Node.Port},\"Participants\":0,\"Hidden\":true}}"),
            new IPEndPoint(Discovery.Group, port));
        await using (var forger = new Discovery(DeviceIdentity.Generate("x", "x"), () => null, port))
        {
            forger.Start();
            await forger.AnnounceHiddenAsync(new AnnouncementPayload(hostId, "", "", host.Node.Port));
        }

        // Più a lungo dell'intervallo tra due annunci no: basta che non sparisca mai nel frattempo.
        for (var i = 0; i < 10; i++)
        {
            Assert.Contains(viewer.Node.Peers, p => p.DeviceId == hostId && p.IsAnnounced);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task ReplayedHiddenAnnouncement_DoesNotRemoveTheHost()
    {
        var port = Random.Shared.Next(52500, 55000);
        var options = TestNode.DefaultOptions() with { EnableDiscovery = true, DiscoveryPort = port };
        var host = await TestNode.StartAsync(_root.Path, "host", options);
        var viewer = await TestNode.StartAsync(_root.Path, "viewer", options);
        _nodes.AddRange([host, viewer]);
        var hostId = host.Node.Identity.DeviceId;

        // L'attaccante registra tutto ciò che passa sul gruppo multicast.
        using var sniffer = new UdpClient(AddressFamily.InterNetwork);
        sniffer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sniffer.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        sniffer.JoinMulticastGroup(Discovery.Group);
        var captured = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        using var stop = new CancellationTokenSource();
        var sniffing = Task.Run(async () =>
        {
            try
            {
                while (true)
                    captured.Enqueue((await sniffer.ReceiveAsync(stop.Token)).Buffer);
            }
            catch (OperationCanceledException) { }
        });

        await host.ShowAsync();
        await Eventually.TrueAsync(() => viewer.Node.Peers.Any(p => p.DeviceId == hostId), "viewer vede host", timeoutMs: 15000);
        await host.Node.SetVisibleAsync(false);
        await Eventually.TrueAsync(() => !viewer.Node.Peers.Any(p => p.DeviceId == hostId), "il congedo vero funziona", timeoutMs: 3000);
        await Task.Delay(200);
        var recorded = captured.Where(p => Encoding.UTF8.GetString(p).Contains("\"Body\"")).ToArray();
        Assert.NotEmpty(recorded);

        await host.ShowAsync();
        await Eventually.TrueAsync(() => viewer.Node.Peers.Any(p => p.DeviceId == hostId), "host di nuovo visibile", timeoutMs: 15000);

        // Ripete tutti i pacchetti registrati prima (congedo compreso): nessun effetto.
        await stop.CancelAsync();
        await sniffing;
        using var udp = new UdpClient();
        foreach (var packet in recorded)
            await udp.SendAsync(packet, new IPEndPoint(Discovery.Group, port));
        for (var i = 0; i < 10; i++)
        {
            Assert.Contains(viewer.Node.Peers, p => p.DeviceId == hostId && p.IsAnnounced);
            await Task.Delay(100);
        }
    }
}
