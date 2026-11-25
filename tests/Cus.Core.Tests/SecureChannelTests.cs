using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cus.Core.Identity;
using Cus.Core.Net;

namespace Cus.Core.Tests;

public class SecureChannelTests
{
    [Fact]
    public async Task Handshake_AuthenticatesBothSides_AndTrafficIsEncrypted()
    {
        var alice = DeviceIdentity.Generate("alice", "u");
        var bob = DeviceIdentity.Generate("bob", "u");
        var (aliceStream, bobStream, wire) = await RecordingLoopbackAsync();

        var (aliceChannel, bobChannel) = await EstablishAsync(aliceStream, alice, bobStream, bob);

        Assert.Equal(bob.SignPublicKey, aliceChannel.RemoteIdentityKey);
        Assert.Equal(alice.SignPublicKey, bobChannel.RemoteIdentityKey);

        await aliceChannel.SendAsync(Encoding.UTF8.GetBytes("testo-in-chiaro-segreto"), default);
        Assert.Equal("testo-in-chiaro-segreto", Encoding.UTF8.GetString(await bobChannel.ReceiveAsync(default)));
        await bobChannel.SendAsync(Encoding.UTF8.GetBytes("risposta-segreta"), default);
        Assert.Equal("risposta-segreta", Encoding.UTF8.GetString(await aliceChannel.ReceiveAsync(default)));

        var captured = Encoding.UTF8.GetString(wire.ToArray());
        Assert.DoesNotContain("testo-in-chiaro-segreto", captured);
        Assert.DoesNotContain("risposta-segreta", captured);
    }

    [Fact]
    public async Task TamperedMessage_IsRejected()
    {
        var (a, b) = (DeviceIdentity.Generate("a", "u"), DeviceIdentity.Generate("b", "u"));
        var aToB = new Pipe();
        var bToA = new Pipe();
        var tamper = new TamperingStream(bToA.Reader.AsStream(), aToB.Writer.AsStream());
        var bStream = new DuplexStream(aToB.Reader.AsStream(), bToA.Writer.AsStream());

        var (aChannel, bChannel) = await EstablishAsync(tamper, a, bStream, b);
        tamper.FlipNextWrite = true;
        await aChannel.SendAsync(new byte[] { 1, 2, 3, 4 }, default);

        await Assert.ThrowsAsync<ProtocolException>(() => bChannel.ReceiveAsync(default));
    }

    [Fact]
    public async Task OversizedMessage_IsRefusedBeforeEncrypting_AndTheChannelKeepsWorking()
    {
        var (a, b) = (DeviceIdentity.Generate("a", "u"), DeviceIdentity.Generate("b", "u"));
        var (s1, s2, _) = await RecordingLoopbackAsync();
        var (aChannel, bChannel) = await EstablishAsync(s1, a, s2, b);

        await Assert.ThrowsAsync<MessageTooLargeException>(
            () => aChannel.SendAsync(new byte[SecureChannel.MaxMessageBytes + 1], default));

        // Prima della correzione lo stato di cifratura avanzava comunque: il messaggio successivo
        // risultava "alterato o fuori ordine".
        await aChannel.SendAsync(new byte[] { 7, 8, 9 }, default);
        Assert.Equal(new byte[] { 7, 8, 9 }, await bChannel.ReceiveAsync(default));
        await aChannel.SendAsync(new byte[SecureChannel.MaxMessageBytes], default);
        Assert.Equal(SecureChannel.MaxMessageBytes, (await bChannel.ReceiveAsync(default)).Length);
    }

    [Fact]
    public async Task Handshake_RejectsOversizedFrame_WithoutAllocatingIt()
    {
        var identity = DeviceIdentity.Generate("a", "u");
        var toUs = new Pipe();
        var fromUs = new Pipe();
        var stream = new DuplexStream(toUs.Reader.AsStream(), fromUs.Writer.AsStream());

        // Chi si collega dichiara un frame da 8 MB e poi non manda nulla.
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, SecureChannel.MaxFrameBytes);
        await toUs.Writer.WriteAsync(header);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ProtocolException>(
            () => SecureChannel.EstablishAsync(stream, identity, isInitiator: false, timeout.Token));
    }

    [Fact]
    public async Task ImpersonatingAKey_WithoutThePrivateKey_Fails()
    {
        var honest = DeviceIdentity.Generate("onesto", "u");
        var victim = DeviceIdentity.Generate("vittima", "u");
        // L'impostore dichiara la chiave pubblica della vittima ma firma con la propria privata.
        var impostor = new DeviceIdentity("impostore", "u", victim.SignPublicKey, DeviceIdentity.Generate("x", "u").SignSecretKey);
        var (s1, s2, _) = await RecordingLoopbackAsync();

        var honestSide = SecureChannel.EstablishAsync(s1, honest, isInitiator: true, CancellationToken.None);
        var impostorSide = SecureChannel.EstablishAsync(s2, impostor, isInitiator: false, CancellationToken.None);

        await Assert.ThrowsAsync<ProtocolException>(() => honestSide);

        // Il lato onesto si ferma: chiudendo gli stream anche l'impostore smette di attendere.
        await s1.DisposeAsync();
        await s2.DisposeAsync();
        try { await impostorSide.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* fallisce comunque: conta il lato onesto */ }
    }

    [Fact]
    public async Task Identities_NeverTravelInClear()
    {
        var alice = DeviceIdentity.Generate("alice", "u");
        var bob = DeviceIdentity.Generate("bob", "u");
        var (aliceStream, bobStream, wire) = await RecordingBothWaysLoopbackAsync();

        await EstablishAsync(aliceStream, alice, bobStream, bob);

        var captured = wire.ToArray();
        Assert.False(Contains(captured, alice.SignPublicKey), "la chiave di chi si collega è passata in chiaro");
        Assert.False(Contains(captured, bob.SignPublicKey), "la chiave di chi risponde è passata in chiaro");
    }

    [Fact]
    public async Task Responder_ThatDoesNotReveal_SaysOnlyRefused_AndKeepsItsKeyHidden()
    {
        var stranger = DeviceIdentity.Generate("sconosciuto", "u");
        var hidden = DeviceIdentity.Generate("nascosto", "u");
        var (strangerStream, hiddenStream, wire) = await RecordingBothWaysLoopbackAsync();
        string? asked = null;

        var initiator = SecureChannel.EstablishAsync(strangerStream, stranger, isInitiator: true, CancellationToken.None);
        var responder = SecureChannel.EstablishAsync(hiddenStream, hidden, isInitiator: false, id => { asked = id; return false; }, CancellationToken.None);

        await Assert.ThrowsAsync<HandshakeRefusedException>(() => initiator);
        await Assert.ThrowsAsync<HandshakeRefusedException>(() => responder);
        Assert.Equal(stranger.DeviceId, asked);
        Assert.False(Contains(wire.ToArray(), hidden.SignPublicKey));
    }

    [Fact]
    public async Task ClaimingAFriendsKey_WithoutItsPrivateKey_FailsBeforeTheResponderDecides()
    {
        var friend = DeviceIdentity.Generate("amico", "u");
        var hidden = DeviceIdentity.Generate("nascosto", "u");
        var impostor = new DeviceIdentity("impostore", "u", friend.SignPublicKey, DeviceIdentity.Generate("x", "u").SignSecretKey);
        var (s1, s2, wire) = await RecordingBothWaysLoopbackAsync();
        var asked = false;

        // Chi risponde smette di parlare: l'impostore resta in attesa fino al proprio timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var initiator = SecureChannel.EstablishAsync(s1, impostor, isInitiator: true, timeout.Token);
        var responder = SecureChannel.EstablishAsync(s2, hidden, isInitiator: false, id => { asked = true; return id == friend.DeviceId; }, CancellationToken.None);

        await Assert.ThrowsAsync<ProtocolException>(() => responder);
        Assert.False(asked);
        await Assert.ThrowsAnyAsync<Exception>(() => initiator);
        Assert.False(Contains(wire.ToArray(), hidden.SignPublicKey));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>Come <see cref="RecordingLoopbackAsync"/>, ma registra quello che scrivono entrambi i lati.</summary>
    private static async Task<(Stream, Stream, MemoryStream)> RecordingBothWaysLoopbackAsync()
    {
        var (client, server, wire) = await RecordingLoopbackAsync();
        return (client, new RecordingStream(server, wire), wire);
    }

    [Fact]
    public void Messages_RoundTripThroughJson()
    {
        CusMessage message = new ClipboardItemMessage("item", 7, "dev", "Andrea", Abstractions.ClipboardContent.FromText("ciao"));

        var back = Assert.IsType<ClipboardItemMessage>(CusMessage.Deserialize(message.Serialize()));

        Assert.Equal("ciao", back.Content.Text);
        Assert.Equal(7, back.Sequence);
        Assert.Throws<ProtocolException>(() => CusMessage.Deserialize("{\"$t\":\"sconosciuto\"}"u8.ToArray()));
    }

    private static async Task<(SecureChannel, SecureChannel)> EstablishAsync(Stream s1, DeviceIdentity i1, Stream s2, DeviceIdentity i2)
    {
        var first = SecureChannel.EstablishAsync(s1, i1, isInitiator: true, CancellationToken.None);
        var second = SecureChannel.EstablishAsync(s2, i2, isInitiator: false, CancellationToken.None);
        return (await first, await second);
    }

    /// <summary>Due stream TCP in loopback; tutto ciò che il primo scrive viene anche registrato.</summary>
    private static async Task<(Stream, Stream, MemoryStream)> RecordingLoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await accept;
        listener.Stop();
        var wire = new MemoryStream();
        return (new RecordingStream(client.GetStream(), wire), server.GetStream(), wire);
    }

    private sealed class RecordingStream(Stream inner, MemoryStream record) : DuplexStream(inner, inner)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (record)
                record.Write(buffer.Span);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class TamperingStream(Stream read, Stream write) : DuplexStream(read, write)
    {
        public bool FlipNextWrite { get; set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FlipNextWrite && buffer.Length > 8)
            {
                FlipNextWrite = false;
                var copy = buffer.ToArray();
                copy[^1] ^= 0xFF;
                buffer = copy;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            write.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
