using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>Patti reciproci proposti dall'host: entrare senza conferma, collegarsi in qualsiasi momento.</summary>
public sealed class FriendTests : IAsyncLifetime
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
    public async Task AutoJoinAgreement_IsMutual_AndSkipsTheConsentNextTime()
    {
        var (andrea, luca) = await StartSessionAsync();

        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.AutoJoin));

        var request = Assert.Single(luca.Consent.AgreementRequests);
        Assert.Equal("andrea", request.HostName);
        Assert.Equal(AgreementLevel.AutoJoin, request.Level);
        Assert.Equal(AgreementLevel.AutoJoin, andrea.Node.Friends.AgreementWith(luca.Node.Identity.DeviceId));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.AutoJoin, "luca salva il patto");

        await luca.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => andrea.Node.Session?.IsSharing == false, "luca è uscito");
        andrea.Consent.Decision = ConsentDecision.Reject;

        var again = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Accepted, again.Outcome);
        Assert.Single(andrea.Consent.Requests);
        Assert.Contains(andrea.Notifier.Infos, m => m.Contains("patto", StringComparison.Ordinal));

        // Vale nei due sensi: ora è luca a offrirsi e andrea entra senza conferma.
        await luca.Node.LeaveSessionAsync();
        await andrea.Node.LeaveSessionAsync();
        await luca.ShowAsync();
        luca.Consent.Decision = ConsentDecision.Reject;
        Assert.Equal(JoinOutcome.Accepted, (await andrea.JoinAsync(luca)).Outcome);
        Assert.Empty(luca.Consent.Requests);
    }

    [Fact]
    public async Task AutoJoinAgreement_DoesNotWork_WhenTheHostIsNotVisible()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.AutoJoin));
        await luca.Node.LeaveSessionAsync();
        await andrea.Node.LeaveSessionAsync();

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Contains("non si sta offrendo", result.Reason);
    }

    [Fact]
    public async Task AnytimeAgreement_ConnectsEvenWhenTheHostIsNotVisible()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.Anytime, "luca salva il patto");
        await luca.Node.LeaveSessionAsync();
        await andrea.Node.LeaveSessionAsync();
        Assert.False(andrea.Node.IsVisible);

        // L'amico compare nell'elenco all'ultimo indirizzo noto, anche se andrea non si annuncia.
        var peer = Assert.Single(luca.Node.Peers, p => p.DeviceId == andrea.Node.Identity.DeviceId);
        Assert.False(peer.IsAnnounced);
        Assert.True(peer.CanJoin);
        Assert.Equal(andrea.Node.Port, peer.Port);

        var result = await luca.Node.JoinAsync(peer.Host, peer.Port, peer.DeviceId);

        Assert.Equal(JoinOutcome.Accepted, result.Outcome);
        Assert.Empty(andrea.Consent.Requests.Skip(1));
        await Eventually.TrueAsync(() => andrea.Node.Session?.IsSharing == true, "andrea diventa host");
        Assert.False(andrea.Node.IsVisible);
        luca.Copy("ciao");
        await Eventually.TrueAsync(() => andrea.Text == "ciao", "la condivisione funziona");

        // Senza membri e non visibile, la sessione finisce.
        await luca.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => andrea.Node.Session is null, "sessione finita");
    }

    [Fact]
    public async Task AnytimeAgreement_IsRejected_WhenTheFriendIsAlreadyMemberElsewhere()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));
        await luca.Node.LeaveSessionAsync();
        await andrea.Node.LeaveSessionAsync();

        var marco = await Start("marco");
        await marco.ShowAsync();
        await andrea.JoinAsync(marco);

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Contains("andrea è già in una condivisione", result.Reason);
    }

    [Fact]
    public async Task RejectedProposal_StoresNothing()
    {
        var (andrea, luca) = await StartSessionAsync();
        luca.Consent.AgreementDecision = false;

        Assert.False(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));

        Assert.Equal(AgreementLevel.None, andrea.Node.Friends.AgreementWith(luca.Node.Identity.DeviceId));
        Assert.Equal(AgreementLevel.None, luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId));
        Assert.Empty(andrea.Node.Friends.All);
        Assert.Empty(luca.Node.Friends.All);
    }

    [Fact]
    public async Task Revoking_DropsTheAgreementOnBothSides_OverALiveConnection()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.Anytime, "luca salva il patto");

        await luca.Node.RevokeAgreementAsync(andrea.Node.Identity.DeviceId);

        Assert.Equal(AgreementLevel.None, luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId));
        await Eventually.TrueAsync(() => andrea.Node.Friends.AgreementWith(luca.Node.Identity.DeviceId) == AgreementLevel.None, "il patto cade anche per andrea");
        Assert.Contains(andrea.Notifier.Infos, m => m.Contains("sciolto", StringComparison.Ordinal));
        Assert.NotNull(luca.Node.Session);
    }

    [Fact]
    public async Task Revoking_WhileTheOtherPcIsOff_DropsTheAgreementAtTheNextConnection()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.Anytime, "luca salva il patto");
        await luca.StopAsync();

        await andrea.Node.LeaveSessionAsync();
        await andrea.Node.RevokeAgreementAsync(luca.Node.Identity.DeviceId);
        await luca.RestartAsync();
        Assert.Equal(AgreementLevel.Anytime, luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId));

        // Luca prova a usare il patto: lo scambio iniziale lo fa cadere anche da lui.
        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Equal(AgreementLevel.None, luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId));
        Assert.Empty(luca.Node.Friends.All);
    }

    [Fact]
    public async Task Revoking_ReachesTheOtherPc_AtItsLastAddress()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.AutoJoin));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.AutoJoin, "luca salva il patto");
        await luca.Node.LeaveSessionAsync();
        await andrea.Node.LeaveSessionAsync();

        // Nessuna connessione aperta: andrea contatta luca all'ultimo indirizzo noto.
        await andrea.Node.RevokeAgreementAsync(luca.Node.Identity.DeviceId);

        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.None, "il patto cade anche per luca");
    }

    [Fact]
    public async Task OnlyTheHost_CanPropose_ToCurrentMembers()
    {
        var (andrea, luca) = await StartSessionAsync();

        await Assert.ThrowsAsync<CusOperationException>(() => luca.Node.ProposeAgreementAsync(andrea.Node.Identity.DeviceId, AgreementLevel.AutoJoin));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.None));
        Assert.Empty(luca.Node.Friends.All);
    }

    [Fact]
    public async Task RemovingAFriend_DropsAgreementAndTrust_OnThisSide_AndTheAgreementOnTheOther()
    {
        var (andrea, luca) = await StartSessionAsync();
        Assert.True(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.Anytime));
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.Anytime, "luca salva il patto");
        luca.Node.SetSensitiveTrust(andrea.Node.Identity.DeviceId, "andrea", trusted: true);
        andrea.Node.SetSensitiveTrust(luca.Node.Identity.DeviceId, "luca", trusted: true);

        await luca.Node.RemoveFriendAsync(andrea.Node.Identity.DeviceId);

        Assert.Null(luca.Node.Friends.Get(andrea.Node.Identity.DeviceId));
        await Eventually.TrueAsync(() => andrea.Node.Friends.AgreementWith(luca.Node.Identity.DeviceId) == AgreementLevel.None, "il patto cade anche per andrea");

        // La fiducia per le password è una decisione solo di andrea: resta.
        Assert.True(andrea.Node.Friends.TrustsWithSensitive(luca.Node.Identity.DeviceId));
    }

    [Fact]
    public async Task UnansweredProposal_CountsAsRejected_AfterTheMembersConsentTimeout()
    {
        var andrea = await Start("andrea");
        var luca = await Start("luca", TestNode.DefaultOptions() with { ConsentTimeout = TimeSpan.FromSeconds(1) });
        await andrea.ShowAsync();
        Assert.Equal(JoinOutcome.Accepted, (await luca.JoinAsync(andrea)).Outcome);
        luca.Consent.Delay = TimeSpan.FromSeconds(30);

        Assert.False(await andrea.Node.ProposeAgreementAsync(luca.Node.Identity.DeviceId, AgreementLevel.AutoJoin));

        Assert.Empty(andrea.Node.Friends.All);
        Assert.Empty(luca.Node.Friends.All);
    }

    [Fact]
    public async Task SecondProposal_WhileTheFirstIsStillOnScreen_IsRejectedWithoutAsking()
    {
        var (andrea, luca) = await StartSessionAsync();
        luca.Consent.Delay = TimeSpan.FromMilliseconds(500);
        var lucaId = luca.Node.Identity.DeviceId;

        var first = andrea.Node.ProposeAgreementAsync(lucaId, AgreementLevel.AutoJoin);
        await Eventually.TrueAsync(() => !luca.Consent.AgreementRequests.IsEmpty, "la prima proposta è sullo schermo di luca");
        var second = await andrea.Node.ProposeAgreementAsync(lucaId, AgreementLevel.Anytime);

        Assert.False(second);
        Assert.True(await first);
        Assert.Single(luca.Consent.AgreementRequests);
        await Eventually.TrueAsync(() => luca.Node.Friends.AgreementWith(andrea.Node.Identity.DeviceId) == AgreementLevel.AutoJoin, "vale la prima");
        Assert.Equal(AgreementLevel.AutoJoin, andrea.Node.Friends.AgreementWith(lucaId));
    }

    [Fact]
    public async Task ManualPeers_AppearInTheList_UntilRemoved()
    {
        var andrea = await Start("andrea");

        andrea.Node.AddManualPeer("192.168.1.20");

        var peer = Assert.Single(andrea.Node.Peers);
        Assert.True(peer.IsManual);
        Assert.True(peer.CanJoin);
        Assert.Null(peer.DeviceId);
        Assert.Equal(NetworkDefaults.DefaultPort, peer.Port);

        andrea.Node.RemoveManualPeer("192.168.1.20");

        Assert.Empty(andrea.Node.Peers);
    }

    private async Task<(TestNode Host, TestNode Member)> StartSessionAsync()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        Assert.Equal(JoinOutcome.Accepted, (await luca.JoinAsync(andrea)).Outcome);
        await Eventually.TrueAsync(() => andrea.Node.Session?.IsSharing == true, "luca è dentro");
        return (andrea, luca);
    }

    private async Task<TestNode> Start(string name, CusNodeOptions? options = null)
    {
        var node = await TestNode.StartAsync(_root.Path, name, options);
        _nodes.Add(node);
        return node;
    }
}
