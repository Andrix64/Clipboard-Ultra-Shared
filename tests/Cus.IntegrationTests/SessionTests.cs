using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

public sealed class SessionTests : IAsyncLifetime
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
    public async Task TwoNodes_ShareClipboardBothWays_WithoutBounce()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));

        var result = await andrea.InviteAsync(luca);

        Assert.Equal(InviteOutcome.Accepted, result.Outcome);
        Assert.Equal("andrea", Assert.Single(luca.Consent.Requests).HostName);
        await Eventually.TrueAsync(() => luca.Node.Session?.Role == SessionRole.Member, "luca è membro");
        Assert.True(andrea.Overlay.IsVisible);
        Assert.True(luca.Overlay.IsVisible);
        Assert.Equal(2, andrea.Node.Session!.Participants.Count);

        luca.Copy("file da Luca");
        await Eventually.TrueAsync(() => andrea.Text == "file da Luca", "andrea riceve la copia di luca");

        andrea.Copy("risposta di Andrea");
        await Eventually.TrueAsync(() => luca.Text == "risposta di Andrea", "luca riceve la copia di andrea");

        await Task.Delay(300);
        Assert.Equal("risposta di Andrea", andrea.Text);
        Assert.Equal("risposta di Andrea", luca.Text);
        Assert.Equal(2, andrea.ClipboardEvents.Count);
        Assert.Equal(2, luca.ClipboardEvents.Count);
    }

    [Fact]
    public async Task Rejected_StartsNoSession()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        luca.Consent.Decision = ConsentDecision.Reject;

        var result = await andrea.InviteAsync(luca);

        Assert.Equal(InviteOutcome.Rejected, result.Outcome);
        Assert.Null(andrea.Node.Session);
        Assert.Null(luca.Node.Session);
        Assert.False(andrea.Overlay.IsVisible);
    }

    [Fact]
    public async Task ReadOnlyMember_SendsButDoesNotReceive()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        luca.Consent.Decision = ConsentDecision.AcceptReadOnly;

        var result = await andrea.InviteAsync(luca);
        Assert.Equal(InviteOutcome.AcceptedReadOnly, result.Outcome);
        Assert.Contains(andrea.Node.Session!.Participants, p => p.Direction == ShareDirection.ReadOnly);

        andrea.Copy("non deve arrivare a Luca");
        luca.Copy("da Luca in sola lettura");

        await Eventually.TrueAsync(() => andrea.Text == "da Luca in sola lettura", "l'host riceve dal membro in sola lettura");
        await Task.Delay(300);
        Assert.Equal("da Luca in sola lettura", luca.Text);
    }

    [Fact]
    public async Task Group_HostDistributes_AndLastArrivedWinsEverywhere()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.InviteAsync(b);
        await host.InviteAsync(c);
        await Eventually.TrueAsync(() => b.Node.Session?.Participants.Count == 3, "b vede tre partecipanti");

        b.Copy("da b");
        await Eventually.TrueAsync(() => host.Text == "da b" && c.Text == "da b", "host e c ricevono da b");

        for (var i = 0; i < 10; i++)
        {
            b.Copy($"b-{i}");
            c.Copy($"c-{i}");
        }

        await Eventually.TrueAsync(
            () => host.Text is { } t && b.Text == t && c.Text == t && (t == "b-9" || t == "c-9"),
            "tutti convergono sull'ultima copia arrivata all'host");
    }

    [Fact]
    public async Task MemberLeaving_EndsTwoPersonSession_ButNotGroup()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.InviteAsync(b);
        await host.InviteAsync(c);

        await c.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => host.Node.Session?.Participants.Count == 2, "c è uscito");
        Assert.Null(c.Node.Session);
        Assert.False(c.Overlay.IsVisible);

        b.Overlay.Click();
        await Eventually.TrueAsync(() => host.Node.Session is null, "senza membri la sessione termina");
        Assert.False(host.Overlay.IsVisible);
    }

    [Fact]
    public async Task HostDisconnecting_EndsSessionForEveryone()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.InviteAsync(b);
        await host.InviteAsync(c);

        await host.StopAsync();

        await Eventually.TrueAsync(() => b.Node.Session is null && c.Node.Session is null, "i membri escono");
        Assert.False(b.Overlay.IsVisible);
        Assert.Contains(b.Notifier.Infos, m => m.Contains("terminata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SensitiveContent_IsNeverSent()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.InviteAsync(luca);

        andrea.Clipboard.Copy(ClipboardContent.FromText("password-segreta", isMarkedSensitive: true));
        andrea.Copy("testo normale");

        await Eventually.TrueAsync(() => luca.Text == "testo normale", "il testo normale arriva");
        Assert.DoesNotContain(luca.ClipboardEvents, e => e.Content.Text == "password-segreta");
        Assert.DoesNotContain(await andrea.Node.GetHistoryAsync(), h => h.Content.Text == "password-segreta");
    }

    [Fact]
    public async Task Images_AreShared()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.InviteAsync(luca);
        var png = Core.Imaging.PngCodec.Encode(new Core.Imaging.RgbaImage(1, 1, [1, 2, 3, 4]));

        andrea.Clipboard.Copy(ClipboardContent.FromImage(png));

        await Eventually.TrueAsync(() => luca.Clipboard.Current?.ImagePng?.SequenceEqual(png) == true, "l'immagine arriva");
    }

    [Fact]
    public async Task History_Last20_OnHost_VisibleToMember_AndKeptAfterSession()
    {
        var (host, member) = (await Start("host"), await Start("member"));
        await host.InviteAsync(member);

        for (var i = 1; i <= 25; i++)
        {
            member.Copy($"voce {i}");
            await Eventually.TrueAsync(() => host.Text == $"voce {i}", $"voce {i} arrivata");
        }

        var seenByMember = await member.Node.GetHistoryAsync();
        Assert.Equal(20, seenByMember.Count);
        Assert.Equal("voce 25", seenByMember[0].Content.Text);

        await member.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => host.Node.Session is null, "sessione terminata");
        Assert.Equal(20, (await host.Node.GetHistoryAsync()).Count);

        await host.Node.UseHistoryEntryAsync((await host.Node.GetHistoryAsync())[^1]);
        Assert.Equal("voce 6", host.Text);
    }

    [Fact]
    public async Task SecondInvite_WhileInSession_IsRejected()
    {
        var (a, b, c) = (await Start("a"), await Start("b"), await Start("c"));
        await a.InviteAsync(b);

        var result = await c.InviteAsync(b);

        Assert.Equal(InviteOutcome.Rejected, result.Outcome);
        Assert.Contains("altra sessione", result.Reason);
    }

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }
}
