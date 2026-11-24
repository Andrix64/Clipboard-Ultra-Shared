using System.Net;
using System.Net.Sockets;
using Cus.Core.Abstractions;
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
        await host.InviteAsync(previous);
        await Eventually.TrueAsync(() => previous.Node.Session is not null, "prima sessione");
        host.Copy("segreto della sessione precedente");
        await Eventually.TrueAsync(() => previous.Text == "segreto della sessione precedente", "copia nella prima sessione");
        await host.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => previous.Node.Session is null, "prima sessione chiusa");

        var next = await Start("successivo");
        await host.InviteAsync(next);
        await Eventually.TrueAsync(() => next.Node.Session is not null, "seconda sessione");
        host.Copy("nuova");
        await Eventually.TrueAsync(() => next.Text == "nuova", "copia nella seconda sessione");

        var seen = await next.Node.GetHistoryAsync();

        Assert.Equal("nuova", Assert.Single(seen).Describe());
        Assert.Equal(2, (await host.Node.GetHistoryAsync()).Count);
    }

    [Fact]
    public async Task Invite_ToAnAddressWithADifferentIdentity_IsCancelledBeforeAsking()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        var announcedId = DeviceIdentity.Generate("altro", "u").DeviceId;

        var error = await Assert.ThrowsAsync<CusOperationException>(
            () => andrea.Node.InviteAsync("127.0.0.1", luca.Node.Port, announcedId));

        Assert.Contains("non corrisponde", error.Message);
        Assert.Empty(luca.Consent.Requests);
        Assert.Null(andrea.Node.Session);
        Assert.Equal(InviteOutcome.Accepted, (await andrea.Node.InviteAsync("127.0.0.1", luca.Node.Port, luca.Node.Identity.DeviceId)).Outcome);
    }

    [Fact]
    public async Task OnlyOneConsentPrompt_IsShownAtATime()
    {
        var (luca, andrea, marco) = (await Start("luca"), await Start("andrea"), await Start("marco"));
        luca.Consent.Delay = TimeSpan.FromSeconds(2);

        var fromAndrea = andrea.InviteAsync(luca);
        await Eventually.TrueAsync(() => !luca.Consent.Requests.IsEmpty, "prima richiesta mostrata");
        var fromMarco = await marco.InviteAsync(luca);

        Assert.Equal(InviteOutcome.Rejected, fromMarco.Outcome);
        Assert.Contains("altra richiesta", fromMarco.Reason);
        Assert.Equal(InviteOutcome.Accepted, (await fromAndrea).Outcome);
        Assert.Single(luca.Consent.Requests);
    }

    [Fact]
    public async Task PushWithDangerousFileNames_IsIgnored_ButAcknowledged()
    {
        var host = await Start("host");
        var evil = DeviceIdentity.Generate("evil", "u");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var invite = host.Node.InviteAsync("127.0.0.1", port);
            using var client = await listener.AcceptTcpClientAsync();
            await using var connection = await PeerConnection.AcceptAsync(client, evil, port, CancellationToken.None);
            await connection.SendAsync(new TrustSyncMessage([]));
            await connection.ReceiveAsync<TrustSyncMessage>(TimeSpan.FromSeconds(5));
            await connection.ReceiveAsync<SessionInviteMessage>(TimeSpan.FromSeconds(5));
            await connection.SendAsync(new SessionInviteReplyMessage(ConsentDecision.Accept, null));
            Assert.Equal(InviteOutcome.Accepted, (await invite).Outcome);

            await connection.SendAsync(new ClipboardPushMessage("ITEM1",
                ClipboardContent.FromFiles([new ClipboardFileInfo("..\\..\\Start Menu\\Programs\\Startup\\x.bat", 10)])));

            CusMessage message;
            do
            {
                message = await connection.ReceiveAsync(TimeSpan.FromSeconds(5));
            }
            while (message is not ClipboardAckMessage);

            Assert.Equal("ITEM1", ((ClipboardAckMessage)message).ItemId);
            Assert.Null(host.Clipboard.Current);
            Assert.Empty(await host.Node.GetHistoryAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<(TestNode Host, TestNode Member)> StartSessionAsync()
    {
        var (host, member) = (await Start("host"), await Start("member"));
        Assert.Equal(InviteOutcome.Accepted, (await host.InviteAsync(member)).Outcome);
        await Eventually.TrueAsync(() => member.Node.Session is not null, "membro nella sessione");
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
