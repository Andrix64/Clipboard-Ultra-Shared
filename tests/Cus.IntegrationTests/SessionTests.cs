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
    public async Task AcceptingAfterMoreThanTheIdleTimeout_KeepsTheMemberConnected()
    {
        var patient = TestNode.DefaultOptions() with { ConsentTimeout = TimeSpan.FromSeconds(60) };
        var (andrea, luca) = (await Start("andrea", patient), await Start("luca", patient));
        await andrea.ShowAsync();
        // L'host risponde dopo IdleTimeout: il silenzio durante l'attesa del consenso non è inattività.
        andrea.Consent.Delay = Core.Net.PeerConnection.IdleTimeout + TimeSpan.FromSeconds(2);

        var result = await luca.JoinAsync(andrea);
        Assert.Equal(JoinOutcome.Accepted, result.Outcome);
        await Task.Delay(Core.Net.PeerConnection.PingInterval * 2 + TimeSpan.FromSeconds(1));

        Assert.Equal(2, andrea.Node.Session?.Participants.Count);
        luca.Copy("ancora collegato");
        await Eventually.TrueAsync(() => andrea.Text == "ancora collegato", "la sessione funziona ancora");
    }

    [Fact]
    public async Task GuestWhoLeavesWhileWaiting_DisappearsFromTheHostsRequests()
    {
        var patient = TestNode.DefaultOptions() with { ConsentTimeout = TimeSpan.FromSeconds(60) };
        var (andrea, luca) = (await Start("andrea", patient), await Start("luca", patient));
        await andrea.ShowAsync();
        andrea.Consent.Delay = TimeSpan.FromSeconds(60);
        var join = luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.JoinRequests.Count == 1, "andrea vede la richiesta");

        await luca.StopAsync();

        // Ben prima della scadenza del consenso.
        await Eventually.TrueAsync(() => andrea.Node.JoinRequests.Count == 0, "la richiesta sparisce");
        await Assert.ThrowsAnyAsync<Exception>(() => join);
        Assert.Null(andrea.Node.Session?.Participants.FirstOrDefault(p => p.DisplayName == "luca"));
    }

    [Fact]
    public async Task HostClearsHistory_ForItselfAndTheMembers_MemberCannot()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 2, "andrea vede luca");
        andrea.Copy("da dimenticare");
        await Eventually.TrueAsync(() => luca.Text == "da dimenticare", "luca riceve la copia");
        var entry = Assert.Single(await luca.Node.GetHistoryAsync());

        // La cronologia sta sull'host: il membro non può cancellarla.
        Assert.Throws<CusOperationException>(() => luca.Node.ClearHistory());
        Assert.Single(await andrea.Node.GetHistoryAsync());

        andrea.Node.ClearHistory();

        Assert.Empty(await andrea.Node.GetHistoryAsync());
        Assert.Empty(await luca.Node.GetHistoryAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => luca.Node.UseHistoryEntryAsync(entry));
    }

    [Fact]
    public async Task LateJoiner_SeesOnlyTheHistoryFromWhenItJoined()
    {
        var (andrea, luca, sara) = (await Start("andrea"), await Start("luca"), await Start("sara"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 2, "andrea vede luca");
        andrea.Copy("prima di sara");
        await Eventually.TrueAsync(() => luca.Text == "prima di sara", "luca riceve la copia");
        var before = Assert.Single(await luca.Node.GetHistoryAsync());

        // Sara entra (in sola lettura): la cronologia di prima non è sua.
        andrea.Consent.Decision = ConsentDecision.AcceptReadOnly;
        await sara.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 3, "andrea vede sara");
        Assert.Empty(await sara.Node.GetHistoryAsync());
        // Neanche chiedendo una voce precisa di prima.
        await Assert.ThrowsAnyAsync<Exception>(() => sara.Node.UseHistoryEntryAsync(before));
        Assert.NotEqual("prima di sara", sara.Text);

        andrea.Copy("dopo sara");
        await Eventually.TrueAsync(() => sara.Text == "dopo sara", "sara riceve la copia nuova");
        Assert.Contains("dopo sara", Assert.Single(await sara.Node.GetHistoryAsync()).Describe(), StringComparison.Ordinal);
        Assert.Equal(2, (await luca.Node.GetHistoryAsync()).Count);
    }

    [Fact]
    public async Task Rename_IsSeenByTheOthersInTheSession_AndByTheHost()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 2, "andrea vede luca");

        // Un membro cambia nome: lo vede l'host.
        Assert.Equal("Luca Rossi", await luca.Node.SetDisplayNameAsync("  Luca   Rossi "));
        await Eventually.TrueAsync(() => andrea.Node.Session!.Participants.Any(p => p.DisplayName == "Luca Rossi"), "l'host vede il nuovo nome");
        await Eventually.TrueAsync(() => luca.Node.Session!.Participants.Any(p => p.IsSelf && p.DisplayName == "Luca Rossi"), "il membro vede il proprio nome");

        // L'host cambia nome: lo vede il membro.
        await andrea.Node.SetDisplayNameAsync("Andrea Host");
        await Eventually.TrueAsync(() => luca.Node.Session!.Participants.Any(p => p.IsHost && p.DisplayName == "Andrea Host"), "il membro vede il nuovo nome dell'host");

        // Un nome vuoto non cambia nulla.
        Assert.Null(await luca.Node.SetDisplayNameAsync("   "));
        Assert.Equal("Luca Rossi", luca.Node.Identity.DisplayName);
    }

    [Fact]
    public async Task Rename_ToALookAlikeOfTheHost_IsRefused_AndRenamesAreRateLimited()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.ShowAsync();
        await luca.JoinAsync(andrea);
        await Eventually.TrueAsync(() => andrea.Node.Session?.Participants.Count == 2, "andrea vede luca");
        var hostName = andrea.Node.Identity.DisplayName;

        // Il nome dell'host (anche con altre maiuscole) non è accettato: all'host resta il nome vecchio.
        await luca.Node.SetDisplayNameAsync(hostName.ToUpperInvariant() + " ");
        // Subito dopo, un secondo cambio è oltre il limite: non passa subito, ma allo scadere dell'intervallo.
        await luca.Node.SetDisplayNameAsync("Troppo Presto");
        await Task.Delay(TimeSpan.FromSeconds(0.5));
        Assert.DoesNotContain(andrea.Node.Session!.Participants, p => !p.IsHost && p.DisplayName == "Troppo Presto");
        await Eventually.TrueAsync(() => andrea.Node.Session!.Participants.Any(p => !p.IsHost && p.DisplayName == "Troppo Presto"), "il nome arriva a intervallo scaduto");

        // Più cambi ravvicinati: vale l'ultimo.
        await luca.Node.SetDisplayNameAsync("Luca Uno");
        await luca.Node.SetDisplayNameAsync("Luca Onesto");
        await Eventually.TrueAsync(() => andrea.Node.Session!.Participants.Any(p => !p.IsHost && p.DisplayName == "Luca Onesto"), "l'host vede l'ultimo nome");
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Contains(andrea.Node.Session!.Participants, p => !p.IsHost && p.DisplayName == "Luca Onesto");
        Assert.Single(andrea.Node.Session!.Participants, p => DisplayText.LooksAlike(p.DisplayName, hostName));
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
    public async Task MemberLeaving_KeepsTheGroup_AndHiddenHostStaysOpenWithoutMembers()
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

        // ...e senza membri resta aperta (raggiungibile dall'indirizzo) finché l'host non la chiude.
        b.Overlay.Click();
        await Eventually.TrueAsync(() => host.Node.Session is { Participants.Count: 1 }, "senza membri la sessione resta aperta");
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
    public async Task Join_ToSomeoneAlreadyMemberElsewhere_IsRefused_WithoutRevealingWhoTheyAre()
    {
        var (a, b, c) = (await Start("a"), await Start("b"), await Start("c"));
        await a.ShowAsync();
        await b.JoinAsync(a);

        // b non si offre e c non è suo amico: b non si presenta nemmeno (il caso con un amico è in FriendTests).
        var error = await Assert.ThrowsAsync<CusOperationException>(() => c.JoinAsync(b));

        Assert.Contains("non accetta connessioni", error.Message);
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

    private async Task<TestNode> Start(string name, CusNodeOptions? options = null)
    {
        var node = await TestNode.StartAsync(_root.Path, name, options);
        _nodes.Add(node);
        return node;
    }
}
