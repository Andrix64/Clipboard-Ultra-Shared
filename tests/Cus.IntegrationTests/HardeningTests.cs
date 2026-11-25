using System.Net;
using System.Net.Sockets;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>Regressioni dei problemi trovati nella revisione di sicurezza (2026-11-24).</summary>
public sealed class HardeningTests : IAsyncLifetime
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
    public async Task LongNonAsciiText_IsShared()
    {
        var (host, member) = await StartSessionAsync();
        var text = new string('è', 1_500_000);

        member.Copy(text);

        await Eventually.TrueAsync(() => host.Text == text, "il testo arriva all'host", 10_000);
        host.Copy("dopo");
        await Eventually.TrueAsync(() => member.Text == "dopo", "il membro continua a ricevere");
    }

    [Fact]
    public async Task CopyTooLargeForAFrame_IsRefused_AndTheSessionSurvives()
    {
        var (host, member) = await StartSessionAsync();

        // Sotto il limite di caratteri, ma i caratteri di controllo diventano \u0001 (6 byte) nel JSON.
        member.Copy(new string('\u0001', CusNode.MaxTextChars));

        await Eventually.TrueAsync(() => member.Notifier.Errors.Any(e => e.Contains("troppo grande")), "errore chiaro per l'utente");
        host.Copy("dopo");
        await Eventually.TrueAsync(() => member.Text == "dopo", "il membro non resta bloccato in attesa della conferma");
        member.Copy("ancora");
        await Eventually.TrueAsync(() => host.Text == "ancora", "il canale cifrato è ancora sincronizzato");
        await Task.Delay(PeerConnection.PingInterval + TimeSpan.FromSeconds(1));
        Assert.NotNull(member.Node.Session);
        Assert.NotNull(host.Node.Session);
    }

    [Fact]
    public async Task HistoryWithLargeImages_DoesNotDropTheMember_AndFullEntriesAreFetchedOnUse()
    {
        var (host, member) = await StartSessionAsync();
        var first = RandomBytes(4 * 1024 * 1024);
        var second = RandomBytes(4 * 1024 * 1024);
        host.Clipboard.Copy(ClipboardContent.FromImage(first));
        await Eventually.TrueAsync(() => member.Clipboard.Current?.ImagePng?.SequenceEqual(first) == true, "prima immagine");
        host.Clipboard.Copy(ClipboardContent.FromImage(second));
        await Eventually.TrueAsync(() => member.Clipboard.Current?.ImagePng?.SequenceEqual(second) == true, "seconda immagine");

        var history = await member.Node.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.True(h.IsPreview));
        await member.Node.UseHistoryEntryAsync(history[^1]);
        Assert.Equal(first, member.Clipboard.Current?.ImagePng);
        Assert.NotNull(member.Node.Session);
    }


    [Fact]
    public async Task Member_SeesOnlyTheHistoryOfTheCurrentSession()
    {
        var host = await Start("host");
        var previous = await Start("precedente");
        await host.ShowAsync();
        await previous.JoinAsync(host);
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == true, "prima sessione");
        host.Copy("segreto della sessione precedente");
        await Eventually.TrueAsync(() => previous.Text == "segreto della sessione precedente", "copia nella prima sessione");
        await host.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => previous.Node.Session is null, "prima sessione chiusa");

        var next = await Start("successivo");
        await host.ShowAsync();
        await next.JoinAsync(host);
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == true, "seconda sessione");
        host.Copy("nuova");
        await Eventually.TrueAsync(() => next.Text == "nuova", "copia nella seconda sessione");

        var seen = await next.Node.GetHistoryAsync();

        Assert.Equal("nuova", Assert.Single(seen).Describe());
        Assert.Equal(2, (await host.Node.GetHistoryAsync()).Count);
    }

    [Fact]
    public async Task Join_ToAnAddressWithADifferentIdentity_IsCancelledBeforeAsking()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await luca.ShowAsync();
        var announcedId = DeviceIdentity.Generate("altro", "u").DeviceId;

        var error = await Assert.ThrowsAsync<CusOperationException>(
            () => andrea.Node.JoinAsync("127.0.0.1", luca.Node.Port, announcedId));

        Assert.Contains("non corrisponde", error.Message);
        Assert.Empty(luca.Consent.Requests);
        Assert.Null(andrea.Node.Session);
        Assert.Equal(JoinOutcome.Accepted, (await andrea.Node.JoinAsync("127.0.0.1", luca.Node.Port, luca.Node.Identity.DeviceId)).Outcome);
    }

    [Fact]
    public async Task OnlyOneConsentPrompt_IsShownAtATime_OtherRequestsWaitInTheList()
    {
        var (luca, andrea, marco) = (await Start("luca"), await Start("andrea"), await Start("marco"));
        await luca.ShowAsync();
        luca.Consent.Delay = TimeSpan.FromSeconds(2);

        var fromAndrea = andrea.JoinAsync(luca);
        await Eventually.TrueAsync(() => !luca.Consent.Requests.IsEmpty, "prima richiesta mostrata");
        var fromMarco = marco.JoinAsync(luca);
        await Eventually.TrueAsync(() => luca.Node.JoinRequests.Count == 2, "la seconda richiesta è nell'elenco");
        Assert.Contains(luca.Notifier.Infos, m => m.Contains("marco chiede di entrare", StringComparison.Ordinal));

        var marcoRequest = luca.Node.JoinRequests.Single(r => r.DisplayName == "marco");
        Assert.True(luca.Node.AnswerJoinRequest(marcoRequest.RequestId, ConsentDecision.AcceptReadOnly));

        Assert.Equal(JoinOutcome.AcceptedReadOnly, (await fromMarco).Outcome);
        Assert.Equal(JoinOutcome.Accepted, (await fromAndrea).Outcome);
        Assert.Single(luca.Consent.Requests);
        Assert.Empty(luca.Node.JoinRequests);
    }

    [Fact]
    public async Task PushWithDangerousFileNames_IsIgnored_ButAcknowledged()
    {
        var host = await Start("host");
        await host.ShowAsync();
        await using var connection = await JoinRawAsync(host);

        await connection.SendAsync(new ClipboardPushMessage("0123456789ABCDEF01234567",
            ClipboardContent.FromFiles([new ClipboardFileInfo("..\\..\\Start Menu\\Programs\\Startup\\x.bat", 10)])));
        var ack = await ReceiveAsync<ClipboardAckMessage>(connection);

        Assert.Equal("0123456789ABCDEF01234567", ack.ItemId);
        Assert.Null(host.Clipboard.Current);
        Assert.Empty(await host.Node.GetHistoryAsync());
    }

    [Fact]
    public async Task ReadOnlyMember_SendsNothing_AndRefusesFileReads_EvenIfTheHostAsks()
    {
        var member = await Start("member");
        var (listener, port) = Listen();
        try
        {
            var join = member.Node.JoinAsync("127.0.0.1", port);
            await using var connection = await AcceptRawJoinAsync(listener, port, new JoinReplyMessage(true, "SESSIONE", ShareDirection.ReadOnly, null));
            Assert.Equal(JoinOutcome.AcceptedReadOnly, (await join).Outcome);

            member.Copy("segreto del membro");
            member.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "ro", ("x.bin", 10))));
            await Task.Delay(500);
            // L'host non conosce l'identificativo (nulla è uscito), ma ci prova comunque.
            await connection.SendAsync(new FileReadRequestMessage(1, "0123456789ABCDEF01234567", 0, 0, 10));

            CusMessage message;
            var pushes = 0;
            while ((message = await connection.ReceiveAsync(TimeSpan.FromSeconds(5))) is not FileReadReplyMessage)
            {
                if (message is ClipboardPushMessage)
                    pushes++;
            }

            Assert.Equal(0, pushes);
            var fileReply = (FileReadReplyMessage)message;
            Assert.Null(fileReply.Data);
            Assert.NotNull(fileReply.Error);
            Assert.False(member.Node.CurrentClipboard!.IsShared);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FilesCopiedWhileReadOnly_StayOnThePc()
    {
        var (host, member) = await StartSessionAsync();
        await member.Node.SetMyDirectionAsync(ShareDirection.ReadOnly);
        await Eventually.TrueAsync(() => host.Node.Session!.Participants.Any(p => !p.IsHost && p.Direction == ShareDirection.ReadOnly), "l'host vede la sola lettura");

        member.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "ro-files", ("x.bin", 10))));
        await Eventually.TrueAsync(() => member.Node.CurrentClipboard?.Kind == ClipboardContentKind.Files, "copia registrata");
        await Task.Delay(300);

        Assert.Null(host.Clipboard.Current);
        Assert.False(member.Node.CurrentClipboard!.IsShared);
    }

    [Fact]
    public async Task ReadOnlyGrant_PushFromTheMember_IsIgnored_ButAcknowledged()
    {
        var host = await Start("host");
        await host.ShowAsync();
        host.Consent.Decision = ConsentDecision.AcceptReadOnly;
        await using var connection = await JoinRawAsync(host, ShareDirection.ReadOnly);

        // Il membro manda comunque le sue copie, e prova pure a dichiararsi bidirezionale.
        await connection.SendAsync(new SetDirectionMessage(ShareDirection.Bidirectional));
        await connection.SendAsync(new ClipboardPushMessage("0123456789ABCDEF01234567", ClipboardContent.FromText("dal membro")));
        var ack = await ReceiveAsync<ClipboardAckMessage>(connection);

        Assert.Equal("0123456789ABCDEF01234567", ack.ItemId);
        Assert.Null(host.Clipboard.Current);
        Assert.Empty(await host.Node.GetHistoryAsync());
        Assert.Contains(host.Node.Session!.Participants, p => !p.IsHost && p.Direction == ShareDirection.ReadOnly);
    }

    [Fact]
    public async Task UnknownDirections_AreTreatedAsReadOnlyOrIgnored()
    {
        // Host che concede una modalità sconosciuta: il membro la tratta come sola lettura.
        var member = await Start("member");
        var (listener, port) = Listen();
        try
        {
            var join = member.Node.JoinAsync("127.0.0.1", port);
            await using var connection = await AcceptRawJoinAsync(listener, port, new JoinReplyMessage(true, "SESSIONE", (ShareDirection)7, null));
            Assert.Equal(JoinOutcome.AcceptedReadOnly, (await join).Outcome);
            Assert.Equal(ShareDirection.ReadOnly, member.Node.Session!.MyDirection);
        }
        finally
        {
            listener.Stop();
        }

        // Membro che chiede una modalità sconosciuta: l'host la ignora.
        var host = await Start("host");
        await host.ShowAsync();
        await using var evil = await JoinRawAsync(host);
        await evil.SendAsync(new SetDirectionMessage((ShareDirection)7));
        await evil.SendAsync(new ClipboardPushMessage("0123456789ABCDEF01234567", ClipboardContent.FromText("ancora bidirezionale")));
        await Eventually.TrueAsync(() => host.Text == "ancora bidirezionale", "la copia entra: la modalità non è cambiata");
    }

    [Fact]
    public async Task PushReusingAnotherItemId_IsIgnored_ButAcknowledged()
    {
        var host = await Start("host");
        await host.ShowAsync();
        await using var connection = await JoinRawAsync(host);

        host.Copy("dell'host");
        var item = await ReceiveAsync<ClipboardItemMessage>(connection);

        // Stesso identificativo, contenuto diverso: sostituirebbe la voce dell'host nella cronologia.
        await connection.SendAsync(new ClipboardPushMessage(item.ItemId, ClipboardContent.FromText("contraffatta")));
        var ack = await ReceiveAsync<ClipboardAckMessage>(connection);

        Assert.Equal(item.ItemId, ack.ItemId);
        Assert.Equal("dell'host", host.Text);
        var history = await host.Node.GetHistoryAsync();
        Assert.Equal("dell'host", Assert.Single(history, h => h.ItemId == item.ItemId).Content.Text);
        Assert.DoesNotContain(history, h => h.Content.Text == "contraffatta");
    }

    [Fact]
    public async Task Member_CannotSpoofTheRequesterOfAFileRead()
    {
        var host = await Start("host");
        await host.ShowAsync();
        await using var connection = await JoinRawAsync(host);
        connection.Start();
        var files = TestFiles.Create(_root.Path, "host-files", ("a.bin", 100));
        host.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => host.Node.CurrentClipboard?.Kind == ClipboardContentKind.Files, "copia dell'host");
        var itemId = (await host.Node.GetHistoryAsync())[0].ItemId;

        // Solo l'host può inoltrare letture per conto di altri: da un membro il campo si ignora.
        var reply = await connection.RequestAsync<FileReadReplyMessage>(
            id => new FileReadRequestMessage(id, itemId, 0, 0, 100, DeviceIdentity.Generate("x", "u").DeviceId, "Finto"),
            TimeSpan.FromSeconds(5));

        Assert.Equal(100, reply.Data?.Length);
        var transfer = Assert.Single(host.Node.Transfers);
        Assert.Equal("evil", transfer.RequesterName);
        Assert.Equal(TransferState.Completed, transfer.State);
    }

    [Fact]
    public async Task JoinRequest_ClaimingAnAgreement_IsRejected_WhenTheHostIsNotVisible()
    {
        var host = await Start("host");
        var evil = DeviceIdentity.Generate("evil", "u");
        await using var connection = await PeerConnection.ConnectAsync("127.0.0.1", host.Node.Port, evil, 0, CancellationToken.None);
        await connection.SendAsync(new FriendSyncMessage(AgreementLevel.Anytime));
        Assert.Equal(AgreementLevel.None, (await connection.ReceiveAsync<FriendSyncMessage>(TimeSpan.FromSeconds(5))).Level);
        await connection.SendAsync(new JoinRequestMessage());
        var reply = await connection.ReceiveAsync<JoinReplyMessage>(TimeSpan.FromSeconds(5));

        // Dichiarare un patto che l'host non ha non serve a nulla: vale il livello più basso.
        Assert.False(reply.Accepted);
        Assert.Contains("non si sta offrendo", reply.Reason);
        Assert.Empty(host.Consent.Requests);
        Assert.Null(host.Node.Session);
        Assert.Equal(AgreementLevel.None, host.Node.Friends.AgreementWith(evil.DeviceId));
    }

    [Fact]
    public async Task OversizedHello_FromAnUnknownPeer_ClosesTheConnection()
    {
        var host = await Start("host");
        await host.ShowAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Node.Port);
        var evil = DeviceIdentity.Generate("evil", "u");
        await using var channel = await SecureChannel.EstablishAsync(client.GetStream(), evil, isInitiator: true, CancellationToken.None);
        Assert.IsType<HelloMessage>(CusMessage.Deserialize(await channel.ReceiveAsync(CancellationToken.None)));

        // Un saluto da 64 KB: ben oltre il limite per chi non si è ancora presentato.
        await channel.SendAsync(new HelloMessage(new string('x', 64 * 1024), "u", 0).Serialize(), CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => channel.ReceiveAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested, "l'host deve chiudere, non restare in attesa");
        Assert.Empty(host.Consent.Requests);
    }

    /// <summary>Un "membro" scritto a mano: handshake, livello del patto, richiesta di ingresso.</summary>
    private static async Task<PeerConnection> JoinRawAsync(TestNode host, ShareDirection expected = ShareDirection.Bidirectional)
    {
        var evil = DeviceIdentity.Generate("evil", "u");
        var connection = await PeerConnection.ConnectAsync("127.0.0.1", host.Node.Port, evil, 0, CancellationToken.None);
        await connection.SendAsync(new FriendSyncMessage(AgreementLevel.None));
        await connection.ReceiveAsync<FriendSyncMessage>(TimeSpan.FromSeconds(5));
        await connection.SendAsync(new JoinRequestMessage());
        var reply = await connection.ReceiveAsync<JoinReplyMessage>(TimeSpan.FromSeconds(5));
        Assert.True(reply.Accepted, reply.Reason);
        Assert.Equal(expected, reply.Granted);
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == true, "membro registrato");
        return connection;
    }

    /// <summary>Un "host" scritto a mano: accetta la connessione di chi chiede di entrare e risponde.</summary>
    private static async Task<PeerConnection> AcceptRawJoinAsync(TcpListener listener, int port, JoinReplyMessage reply)
    {
        var evil = DeviceIdentity.Generate("evil", "u");
        var client = await listener.AcceptTcpClientAsync();
        var connection = await PeerConnection.AcceptAsync(client, evil, port, CancellationToken.None);
        await connection.SendAsync(new FriendSyncMessage(AgreementLevel.None));
        await connection.ReceiveAsync<FriendSyncMessage>(TimeSpan.FromSeconds(5));
        await connection.ReceiveAsync<JoinRequestMessage>(TimeSpan.FromSeconds(5));
        await connection.SendAsync(reply);
        return connection;
    }

    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static async Task<T> ReceiveAsync<T>(PeerConnection connection) where T : CusMessage
    {
        while (true)
        {
            if (await connection.ReceiveAsync(TimeSpan.FromSeconds(5)) is T message)
                return message;
        }
    }

    private async Task<(TestNode Host, TestNode Member)> StartSessionAsync()
    {
        var (host, member) = (await Start("host"), await Start("member"));
        await host.ShowAsync();
        Assert.Equal(JoinOutcome.Accepted, (await member.JoinAsync(host)).Outcome);
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == true, "membro nella sessione");
        return (host, member);
    }

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
