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
        await using var discovery = new Discovery(() => new AnnouncementPayload("id", "n", "u", 1234), port);
        discovery.Announced += a => { lock (seen) seen.Add(a); };
        discovery.Start();

        using var udp = new System.Net.Sockets.UdpClient();
        await udp.SendAsync("non json"u8.ToArray(), new System.Net.IPEndPoint(Discovery.Group, port));
        await udp.SendAsync("{\"App\":\"ALTRO\",\"DeviceId\":\"x\",\"DisplayName\":\"x\",\"UserName\":\"x\",\"Port\":1}"u8.ToArray(), new System.Net.IPEndPoint(Discovery.Group, port));
        await Task.Delay(500);

        lock (seen)
            Assert.Empty(seen); // anche il proprio annuncio è escluso
    }
}
