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
        await andrea.ShowAsync();

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Accepted, result.Outcome);
        Assert.Equal("luca", Assert.Single(andrea.Consent.Requests).RequesterName);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 2, "andrea vede luca");
        Assert.Equal(SessionRole.Member, luca.Node.Session?.Role);
        Assert.True(andrea.Overlay.IsVisible);
        Assert.True(luca.Overlay.IsVisible);

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
    public async Task Rejected_StartsNoSessionForTheRequester()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        andrea.Consent.Decision = ConsentDecision.Reject;

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Null(luca.Node.Session);
        Assert.False(andrea.Node.Session!.IsSharing);
        Assert.False(andrea.Overlay.IsVisible);
        Assert.False(luca.Overlay.IsVisible);
    }

    [Fact]
    public async Task ReadOnlyGrant_MemberReceives_ButDoesNotSend()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        andrea.Consent.Decision = ConsentDecision.AcceptReadOnly;

        var result = await luca.JoinAsync(andrea);

        Assert.Equal(JoinOutcome.AcceptedReadOnly, result.Outcome);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Any(p => p.Direction == ShareDirection.ReadOnly) == true, "l'host vede la sola lettura");
        Assert.Equal(ShareDirection.ReadOnly, luca.Node.Session!.MyDirection);

        andrea.Copy("dall'host");
        await Eventually.TrueAsync(() => luca.Text == "dall'host", "il membro in sola lettura riceve");

        luca.Copy("resta da Luca");
        await Task.Delay(300);
        Assert.Equal("dall'host", andrea.Text);
        Assert.DoesNotContain(await andrea.Node.GetHistoryAsync(), h => h.Content.Text == "resta da Luca");
    }

    [Fact]
    public async Task ReadOnlyMember_ReceivesOtherMembersCopies_AndHistory()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.ShowAsync();
        await b.JoinAsync(host);
        host.Consent.Decision = ConsentDecision.AcceptReadOnly;
        await c.JoinAsync(host);
        await Eventually.TrueAsync(() => c.Node.Session?.Participants.Count == 3, "c vede tutti i partecipanti");

        b.Copy("da b");
        await Eventually.TrueAsync(() => c.Text == "da b", "c riceve la copia di b");
        Assert.Contains(await c.Node.GetHistoryAsync(), h => h.Preview == "da b");

        c.Copy("resta da c");
        await Task.Delay(300);
        Assert.Equal("da b", host.Text);
        Assert.Equal("da b", b.Text);
    }

    [Fact]
    public async Task Group_HostDistributes_AndLastArrivedWinsEverywhere()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.ShowAsync();
        await b.JoinAsync(host);
        await c.JoinAsync(host);
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
    public async Task MemberLeaving_KeepsTheGroup_AndHiddenHostWithoutMembersEndsTheSession()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.ShowAsync();
        await b.JoinAsync(host);
        await c.JoinAsync(host);

        await c.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => host.Node.Session?.Participants.Count == 2, "c è uscito");
        Assert.Null(c.Node.Session);
        Assert.False(c.Overlay.IsVisible);

        // L'host smette di offrirsi: la sessione resta per chi c'è già...
        await host.Node.SetVisibleAsync(false);
        Assert.Equal(2, host.Node.Session?.Participants.Count);

        // ...e senza membri termina.
        b.Overlay.Click();
        await Eventually.TrueAsync(() => host.Node.Session is null, "senza membri e non visibile la sessione termina");
        Assert.False(host.Overlay.IsVisible);
    }

    [Fact]
    public async Task HostDisconnecting_EndsSessionForEveryone()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.ShowAsync();
        await b.JoinAsync(host);
        await c.JoinAsync(host);

        await host.StopAsync();

        await Eventually.TrueAsync(() => b.Node.Session is null && c.Node.Session is null, "i membri escono");
        Assert.False(b.Overlay.IsVisible);
        Assert.Contains(b.Notifier.Infos, m => m.Contains("terminata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SensitiveContent_IsNotSentToUntrustedMembers()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);

        andrea.CopySensitive("password-segreta");
        andrea.Copy("testo normale");

        await Eventually.TrueAsync(() => luca.Text == "testo normale", "il testo normale arriva");
        Assert.DoesNotContain(luca.ClipboardEvents, e => e.Content.Text == "password-segreta");
        Assert.DoesNotContain(await andrea.Node.GetHistoryAsync(), h => h.Content.Text == "password-segreta");
    }

    [Fact]
    public async Task Images_AreShared()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        var png = Core.Imaging.PngCodec.Encode(new Core.Imaging.RgbaImage(1, 1, [1, 2, 3, 4]));

        andrea.Clipboard.Copy(ClipboardContent.FromImage(png));

        await Eventually.TrueAsync(() => luca.Clipboard.Current?.ImagePng?.SequenceEqual(png) == true, "l'immagine arriva");
    }

    [Fact]
    public async Task History_Last20_OnHost_VisibleToMember_AndKeptAfterSession()
    {
        var (host, member) = (await Start("host"), await Start("member"));
        await host.ShowAsync();
        await member.JoinAsync(host);

        for (var i = 1; i <= 25; i++)
        {
            member.Copy($"voce {i}");
            await Eventually.TrueAsync(() => host.Text == $"voce {i}", $"voce {i} arrivata");
        }

        // Il membro riceve anteprime (contenuto vuoto); la voce completa arriva quando la usa.
        var seenByMember = await member.Node.GetHistoryAsync();
        Assert.Equal(20, seenByMember.Count);
        Assert.True(seenByMember[0].IsPreview);
        Assert.Equal("voce 25", seenByMember[0].Describe());
        await member.Node.UseHistoryEntryAsync(seenByMember[^1]);
        Assert.Equal("voce 6", member.Text);

        await member.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing != true, "il membro è uscito");
        await host.Node.LeaveSessionAsync();
        Assert.Null(host.Node.Session);
        Assert.Equal(20, (await host.Node.GetHistoryAsync()).Count);

        await host.Node.UseHistoryEntryAsync((await host.Node.GetHistoryAsync())[^1]);
        Assert.Equal("voce 6", host.Text);
    }

    [Fact]
    public async Task Join_ToSomeoneAlreadyMemberElsewhere_IsRejected()
    {
        var (a, b, c) = (await Start("a"), await Start("b"), await Start("c"));
        await a.ShowAsync();
        await b.JoinAsync(a);

        var result = await c.JoinAsync(b);

        Assert.Equal(JoinOutcome.Rejected, result.Outcome);
        Assert.Contains("è già in una condivisione", result.Reason);
        Assert.Null(c.Node.Session);
    }

    [Fact]
    public async Task CurrentClipboard_DescribesTextAndFiles_WithTheOriginName()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);

        andrea.Copy("ciao!");
        await Eventually.TrueAsync(() => luca.Node.CurrentClipboard?.Title == "testo di 5 caratteri", "luca vede il riepilogo del testo");
        Assert.Equal("andrea", luca.Node.CurrentClipboard!.OriginName);
        Assert.Equal(5, luca.Node.CurrentClipboard.SizeBytes);
        Assert.Equal("andrea", andrea.Node.CurrentClipboard?.OriginName);
        Assert.True(andrea.Node.CurrentClipboard!.IsShared);
        Assert.Equal("testo di 5 caratteri", luca.Overlay.State?.Current?.Title);

        var files = TestFiles.Create(_root.Path, "luca-files", ("progetto/a.txt", 10), ("progetto/b.txt", 20));
        luca.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => andrea.Node.CurrentClipboard?.Title == "progetto", "andrea vede il nome della cartella");
        Assert.Equal("luca", andrea.Node.CurrentClipboard!.OriginName);
        Assert.Equal(30, andrea.Node.CurrentClipboard.SizeBytes);
    }

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }
}
