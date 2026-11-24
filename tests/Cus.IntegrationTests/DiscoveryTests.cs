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
    public async Task TwoInstancesOnTheSamePc_SeeEachOther()
    {
        var port = Random.Shared.Next(40000, 45000);
        var options = TestNode.DefaultOptions() with { EnableDiscovery = true, DiscoveryPort = port };
        var a = await TestNode.StartAsync(_root.Path, "a", options);
        var b = await TestNode.StartAsync(_root.Path, "b", options);
        _nodes.AddRange([a, b]);

        await Eventually.TrueAsync(
            () => a.Node.Peers.Any(p => p.DeviceId == b.Node.Identity.DeviceId && p.Port == b.Node.Port)
                && b.Node.Peers.Any(p => p.DeviceId == a.Node.Identity.DeviceId),
            "le due istanze si vedono", timeoutMs: 15000);

        var peerB = a.Node.Peers.First(p => p.DeviceId == b.Node.Identity.DeviceId);
        var result = await a.Node.InviteAsync(peerB.Host, peerB.Port);
        Assert.Equal(InviteOutcome.Accepted, result.Outcome);
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
