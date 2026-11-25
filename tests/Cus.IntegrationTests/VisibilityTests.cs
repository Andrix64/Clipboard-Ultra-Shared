using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>Ingresso stile AirDrop: ci si offre come host, gli altri chiedono di entrare, l'host decide i permessi.</summary>
public sealed class VisibilityTests : IAsyncLifetime
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
    public async Task HostNotVisible_RejectsJoin_WithoutAsking()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Contains("non si sta offrendo", result.Reason);
        Assert.Empty(andrea.Consent.Requests);
        Assert.Null(andrea.Node.Session);
        Assert.Null(luca.Node.Session);
    }

    [Fact]
    public async Task VisibleHost_HasAnEmptySession_AndTheIndicatorAppearsOnlyWhenSomeoneJoins()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));

        await andrea.ShowAsync();

        Assert.True(andrea.Node.IsVisible);
        var session = andrea.Node.Session!;
        Assert.Equal(SessionRole.Host, session.Role);
        Assert.True(session.IsVisible);
        Assert.False(session.IsSharing);
        Assert.False(andrea.Overlay.IsVisible);

        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Overlay.IsVisible, "l'indicatore compare");
        Assert.True(luca.Overlay.IsVisible);
        var participants = andrea.Overlay.State!.Participants;
        Assert.Contains(participants, p => p.IsSelf && p.IsHost && p.DisplayName == "andrea");
        Assert.Contains(luca.Overlay.State!.Participants, p => p.IsSelf && !p.IsHost && p.DisplayName == "luca");
    }

    [Fact]
    public async Task Hiding_KeepsTheSession_AndAnEmptyHiddenSessionEnds()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);

        await andrea.Node.SetVisibleAsync(false);

        Assert.False(andrea.Node.Session!.IsVisible);
        Assert.Equal(2, andrea.Node.Session.Participants.Count);
        andrea.Copy("dopo essersi nascosto");
        await Eventually.TrueAsync(() => luca.Text == "dopo essersi nascosto", "la sessione continua");

        var marco = await Start("marco");
        Assert.Equal(JoinOutcome.Rejected, (await marco.JoinAsync(andrea)).Outcome);

        await luca.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => andrea.Node.Session is null, "nascosto e senza membri: sessione finita");

        // Visibile e vuoto, poi nascosto: niente sessione.
        await andrea.ShowAsync();
        Assert.NotNull(andrea.Node.Session);
        await andrea.Node.SetVisibleAsync(false);
        Assert.Null(andrea.Node.Session);
    }

    [Fact]
    public async Task JoinRequests_CanBeAnsweredFromTheList()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        andrea.Consent.Delay = TimeSpan.FromSeconds(30);

        var join = luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.JoinRequests.Count == 1, "richiesta in elenco");
        var request = Assert.Single(andrea.Node.JoinRequests);
        Assert.Equal("luca", request.DisplayName);
        Assert.Equal(luca.Node.Identity.DeviceId, request.DeviceId);
        Assert.Equal(luca.Node.Identity.Info.Fingerprint, request.Fingerprint);

        Assert.True(andrea.Node.AnswerJoinRequest(request.RequestId, ConsentDecision.Accept));

        Assert.Equal(JoinOutcome.Accepted, (await join).Outcome);
        Assert.Empty(andrea.Node.JoinRequests);
        Assert.False(andrea.Node.AnswerJoinRequest(request.RequestId, ConsentDecision.Reject));
    }

    [Fact]
    public async Task Hiding_RejectsPendingRequests()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        andrea.Consent.Delay = TimeSpan.FromSeconds(30);

        var join = luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.JoinRequests.Count == 1, "richiesta in elenco");
        await andrea.Node.SetVisibleAsync(false);

        var result = await join;
        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Contains("smesso di offrirsi", result.Reason);
    }

    [Fact]
    public async Task MemberCannotBecomeVisible_AndJoiningStopsBeingVisible()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.ShowAsync();

        await luca.JoinAsync(andrea);

        Assert.False(luca.Node.IsVisible);
        Assert.Equal(SessionRole.Member, luca.Node.Session?.Role);
        await Assert.ThrowsAsync<CusOperationException>(() => luca.Node.SetVisibleAsync(true));
    }

    [Fact]
    public async Task RejectedJoin_LeavesTheRequesterVisible_AsBefore()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.ShowAsync();
        andrea.Consent.Decision = ConsentDecision.Reject;

        Assert.Equal(JoinOutcome.Rejected, (await luca.JoinAsync(andrea)).Outcome);

        Assert.True(luca.Node.IsVisible);
        Assert.Equal(SessionRole.Host, luca.Node.Session?.Role);
    }

    [Fact]
    public async Task BecomingVisible_IsRefused_WhileAJoinRequestIsPending()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        andrea.Consent.Delay = TimeSpan.FromSeconds(30);

        var join = luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.JoinRequests.Count == 1, "richiesta in elenco");
        await Assert.ThrowsAsync<CusOperationException>(() => luca.Node.SetVisibleAsync(true));
        Assert.True(andrea.Node.AnswerJoinRequest(andrea.Node.JoinRequests[0].RequestId, ConsentDecision.Accept));

        Assert.Equal(JoinOutcome.Accepted, (await join).Outcome);
        Assert.False(luca.Node.IsVisible);
    }

    [Fact]
    public async Task RejectedRequester_MustWait_BeforeAskingAgain()
    {
        var andrea = await Start("andrea", TestNode.DefaultOptions() with { JoinRetryCooldown = TimeSpan.FromSeconds(30) });
        var luca = await Start("luca");
        await andrea.ShowAsync();
        andrea.Consent.Decision = ConsentDecision.Reject;
        Assert.Equal(JoinOutcome.Rejected, (await luca.JoinAsync(andrea)).Outcome);
        andrea.Consent.Decision = ConsentDecision.Accept;

        var again = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, again.Outcome);
        Assert.Contains("riprova", again.Reason);
        Assert.Single(andrea.Consent.Requests);

        // Chi non è mai stato rifiutato entra normalmente.
        var marco = await Start("marco");
        Assert.Equal(JoinOutcome.Accepted, (await marco.JoinAsync(andrea)).Outcome);
    }

    [Fact]
    public async Task HostWithMembers_CannotJoinAnotherSession()
    {
        var (andrea, luca, marco) = (await Start("andrea"), await Start("luca"), await Start("marco"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.IsSharing == true, "luca è dentro");
        await marco.ShowAsync();

        await Assert.ThrowsAsync<CusOperationException>(() => andrea.JoinAsync(marco));
    }

    [Fact]
    public async Task HostCanChangeAMembersGrant()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.IsSharing == true, "luca è dentro");

        await andrea.Node.SetMemberGrantAsync(luca.Node.Identity.DeviceId, ShareDirection.ReadOnly);
        await Eventually.TrueAsync(() => luca.Node.Session?.MyGranted == ShareDirection.ReadOnly, "luca sa di essere in sola lettura");
        Assert.Equal(ShareDirection.ReadOnly, luca.Node.Session!.MyDirection);
        Assert.False(luca.Overlay.State!.CanToggleDirection);

        luca.Copy("resta da luca");
        andrea.Copy("dall'host");
        await Eventually.TrueAsync(() => luca.Text == "dall'host", "luca riceve ancora");
        await Task.Delay(300);
        Assert.Equal("dall'host", andrea.Text);
        await Assert.ThrowsAsync<CusOperationException>(() => luca.Node.SetMyDirectionAsync(ShareDirection.Bidirectional));

        await andrea.Node.SetMemberGrantAsync(luca.Node.Identity.DeviceId, ShareDirection.Bidirectional);
        await Eventually.TrueAsync(() => luca.Node.Session?.MyDirection == ShareDirection.Bidirectional, "luca torna bidirezionale");
        luca.Copy("di nuovo da luca");
        await Eventually.TrueAsync(() => andrea.Text == "di nuovo da luca", "l'host riceve di nuovo");
    }

    [Fact]
    public async Task Member_CanSwitchToReadOnlyAndBack()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => luca.Overlay.State?.CanToggleDirection == true, "il membro bidirezionale può cambiare modalità");

        luca.Overlay.ClickDirection();
        await Eventually.TrueAsync(() => luca.Node.Session?.MyDirection == ShareDirection.ReadOnly, "luca in sola lettura");
        await Eventually.TrueAsync(
            () => andrea.Node.Session!.Participants.Any(p => !p.IsHost && p.Direction == ShareDirection.ReadOnly && p.Granted == ShareDirection.Bidirectional),
            "l'host vede la scelta di luca");
        Assert.Equal(ShareDirection.Bidirectional, luca.Node.Session!.MyGranted);

        luca.Copy("resta da luca");
        await Task.Delay(300);
        Assert.Null(andrea.Text);

        await luca.Node.SetMyDirectionAsync(ShareDirection.Bidirectional);
        luca.Copy("condivisa");
        await Eventually.TrueAsync(() => andrea.Text == "condivisa", "di nuovo bidirezionale");
    }

    private async Task<TestNode> Start(string name, CusNodeOptions? options = null)
    {
        var node = await TestNode.StartAsync(_root.Path, name, options);
        _nodes.Add(node);
        return node;
    }
}
