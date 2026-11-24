using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

public sealed class PairingTests : IAsyncLifetime
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
    public async Task Pairing_WithCorrectCode_TrustsBothWays_AndSkipsConsent()
    {
        var (portatile, fisso) = (await Start("andrea-portatile"), await Start("andrea-fisso"));

        await Pair(portatile, fisso);

        Assert.True(portatile.Node.Trust.State.IsTrusted(fisso.Node.Identity.DeviceId));
        Assert.True(fisso.Node.Trust.State.IsTrusted(portatile.Node.Identity.DeviceId));
        Assert.Null(portatile.Node.ActivePairingCode);

        fisso.Consent.Decision = ConsentDecision.Reject; // non deve essere chiesto
        var result = await portatile.InviteAsync(fisso);

        Assert.Equal(InviteOutcome.Accepted, result.Outcome);
        Assert.Empty(fisso.Consent.Requests);
    }

    [Fact]
    public async Task PairedDevice_ReadOnlyInvite_IsAcceptedReadOnly_WithoutConsent()
    {
        var (portatile, fisso) = (await Start("andrea-portatile"), await Start("andrea-fisso"));
        await Pair(portatile, fisso);
        fisso.Consent.Decision = ConsentDecision.Reject; // non deve essere chiesto

        var result = await portatile.InviteAsync(fisso, ShareDirection.ReadOnly);

        Assert.Equal(InviteOutcome.AcceptedReadOnly, result.Outcome);
        Assert.Empty(fisso.Consent.Requests);
        await Eventually.TrueAsync(() => fisso.Node.Session is not null, "fisso nella sessione");
        portatile.Copy("dal portatile");
        await Eventually.TrueAsync(() => fisso.Text == "dal portatile", "fisso riceve");
        fisso.Copy("resta sul fisso");
        await Task.Delay(300);
        Assert.Equal("dal portatile", portatile.Text);
    }

    [Fact]
    public async Task WrongCode_ThreeTimes_ExhaustsTheCode()
    {
        var (a, b) = (await Start("a"), await Start("b"));
        var code = a.Node.StartPairing();
        var wrong = code.Code == "000000" ? "111111" : "000000";

        var first = await Assert.ThrowsAsync<PairingFailedException>(() => b.Node.PairWithAsync("127.0.0.1", a.Node.Port, wrong));
        Assert.Contains("Tentativi rimasti: 2", first.Message);
        await Assert.ThrowsAsync<PairingFailedException>(() => b.Node.PairWithAsync("127.0.0.1", a.Node.Port, wrong));
        var third = await Assert.ThrowsAsync<PairingFailedException>(() => b.Node.PairWithAsync("127.0.0.1", a.Node.Port, wrong));
        Assert.Contains("esauriti", third.Message);

        var afterwards = await Assert.ThrowsAsync<PairingFailedException>(() => b.Node.PairWithAsync("127.0.0.1", a.Node.Port, code.Code));
        Assert.Contains("Nessun codice", afterwards.Message);
        Assert.False(a.Node.Trust.State.IsTrusted(b.Node.Identity.DeviceId));
    }

    [Fact]
    public async Task ExpiredCode_IsRefused()
    {
        var a = await Start("a", TestNode.DefaultOptions() with { PairingCodeLifetime = TimeSpan.FromMilliseconds(200) });
        var b = await Start("b");
        var code = a.Node.StartPairing();
        await Task.Delay(400);

        var error = await Assert.ThrowsAsync<PairingFailedException>(() => b.Node.PairWithAsync("127.0.0.1", a.Node.Port, code.Code));
        Assert.Contains("scaduto", error.Message);
    }

    [Fact]
    public async Task Revocation_ReachesADeviceThatWasOff()
    {
        var (a, b, c) = (await Start("a"), await Start("b"), await Start("c"));
        await Pair(a, b);
        await Pair(a, c);
        await Eventually.TrueAsync(() => c.Node.Trust.State.IsTrusted(b.Node.Identity.DeviceId), "c si fida di b tramite a");

        await c.StopAsync();
        await a.Node.RevokeAsync(b.Node.Identity.DeviceId);
        Assert.True(a.Node.Trust.State.IsRevoked(b.Node.Identity.DeviceId));

        await c.RestartAsync();
        Assert.True(c.Node.Trust.State.IsTrusted(b.Node.Identity.DeviceId)); // non lo sa ancora
        await c.Node.SyncWithAsync("127.0.0.1", a.Node.Port);
        Assert.True(c.Node.Trust.State.IsRevoked(b.Node.Identity.DeviceId));

        // b non riesce più a collegarsi a c, in nessun ordine.
        await Assert.ThrowsAsync<CusOperationException>(() => b.Node.InviteAsync("127.0.0.1", c.Node.Port));
        await Assert.ThrowsAsync<CusOperationException>(() => c.Node.InviteAsync("127.0.0.1", b.Node.Port));
    }

    [Fact]
    public async Task MutualRevocation_RevokesBoth()
    {
        var (a, b, c) = (await Start("a"), await Start("b"), await Start("c"));
        await Pair(a, b);
        await Pair(a, c);
        await Eventually.TrueAsync(() => b.Node.Trust.State.IsTrusted(c.Node.Identity.DeviceId), "b si fida di c");

        await c.StopAsync();
        await a.StopAsync();
        await b.Node.RevokeAsync(a.Node.Identity.DeviceId);
        await b.StopAsync();
        await a.RestartAsync();
        await a.Node.RevokeAsync(b.Node.Identity.DeviceId);

        await b.RestartAsync();
        await c.RestartAsync();
        await c.Node.SyncWithAsync("127.0.0.1", a.Node.Port);
        await c.Node.SyncWithAsync("127.0.0.1", b.Node.Port);

        var state = c.Node.Trust.State;
        Assert.True(state.IsRevoked(a.Node.Identity.DeviceId));
        Assert.True(state.IsRevoked(b.Node.Identity.DeviceId));
    }

    [Fact]
    public async Task Revocation_ClosesActiveSessionImmediately()
    {
        var (a, b) = (await Start("a"), await Start("b"));
        await Pair(a, b);
        await a.InviteAsync(b);
        await Eventually.TrueAsync(() => b.Node.Session is not null, "sessione attiva");

        await a.Node.RevokeAsync(b.Node.Identity.DeviceId);

        await Eventually.TrueAsync(() => a.Node.Session is null && b.Node.Session is null, "sessione chiusa per la revoca");
    }

    [Fact]
    public async Task SelfRevocation_IsDelivered_AndBlocksThisDevice()
    {
        var (a, b) = (await Start("a"), await Start("b"));
        await Pair(a, b);

        await b.Node.RevokeAsync(b.Node.Identity.DeviceId);

        Assert.True(b.Node.Trust.State.SelfRevoked);
        await Eventually.TrueAsync(() => a.Node.Trust.State.IsRevoked(b.Node.Identity.DeviceId), "a riceve l'auto-revoca");
        Assert.Throws<CusOperationException>(() => b.Node.StartPairing());
    }

    private static async Task Pair(TestNode shows, TestNode enters)
    {
        var code = shows.Node.StartPairing();
        await enters.Node.PairWithAsync("127.0.0.1", shows.Node.Port, code.Code);
        await Eventually.TrueAsync(() => shows.Node.Trust.State.IsTrusted(enters.Node.Identity.DeviceId), "abbinamento completato");
    }

    private async Task<TestNode> Start(string name, CusNodeOptions? options = null)
    {
        var node = await TestNode.StartAsync(_root.Path, name, options);
        _nodes.Add(node);
        return node;
    }
}
