using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cus.Core.Abstractions;
using Cus.Core.Identity;

namespace Cus.Core.Net;

/// <summary>Annuncio ricevuto da un host che si offre in rete.</summary>
/// <param name="Participants">Persone nella sua sessione, lui compreso.</param>
/// <param name="Hidden">Congedo: l'host ha smesso di offrirsi e va tolto subito dall'elenco.</param>
public sealed record DiscoveryAnnouncement(string DeviceId, string DisplayName, string UserName, string Host, int Port, int Participants = 1, bool Hidden = false);

/// <summary>
/// Scoperta dei peer con annunci UDP multicast (239.255.23.70, porta 23701). Più istanze sullo
/// stesso PC condividono la porta grazie a SO_REUSEADDR e ricevono tutte gli annunci. Si annuncia
/// solo chi si offre come host (come AirDrop: si è visibili solo quando lo si sceglie); tutti ascoltano.
/// Ogni annuncio (anche il congedo) è firmato con la chiave Ed25519 del dispositivo e porta un
/// contatore crescente: chi ascolta scarta gli annunci con firma non valida, con un ID che non
/// corrisponde alla chiave o con un contatore non più alto dell'ultimo visto da quel dispositivo.
/// Così nessuno in rete può far sparire (o ricomparire) un host altrui, nemmeno ripetendo un suo
/// vecchio congedo. L'identità completa la verifica comunque il canale cifrato alla connessione.
/// </summary>
public sealed class Discovery : IAsyncDisposable
{
    public static readonly IPAddress Group = IPAddress.Parse("239.255.23.70");
    public const int DefaultPort = 23701;
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);

    private const int MaxPacket = 4096;
    private const int MaxBody = 2048;

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly DeviceIdentity _identity;
    private readonly Func<AnnouncementPayload?> _payload;
    private readonly AnnouncementFreshness _freshness = new();
    private readonly Lock _sequenceGate = new();
    private long _lastSequence;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private UdpClient? _client;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    /// <param name="identity">Identità con cui firmare gli annunci; l'ID annunciato è sempre il suo.</param>
    /// <param name="payload">Dati da annunciare, letti a ogni annuncio; null: niente annuncio (non visibile).</param>
    public Discovery(DeviceIdentity identity, Func<AnnouncementPayload?> payload, int port = DefaultPort)
    {
        _identity = identity;
        _payload = payload;
        _port = port;
    }

    public event Action<DiscoveryAnnouncement>? Announced;

    public void Start()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            client.JoinMulticastGroup(Group);
            client.MulticastLoopback = true;
            client.Ttl = 1;
        }
        catch
        {
            // Nessuna scheda di rete, porta occupata: il socket non deve restare aperto.
            client.Dispose();
            throw;
        }

        _client = client;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _announceLoop = Task.Run(AnnounceLoopAsync);
    }

    /// <summary>Annuncia subito, oltre agli annunci periodici.</summary>
    public Task AnnounceNowAsync() => SendAsync(_lifetime.Token);

    /// <summary>Congedo: chi ascolta toglie subito questo host dall'elenco invece di aspettare che scada.</summary>
    public async Task AnnounceHiddenAsync(AnnouncementPayload payload)
    {
        if (_client is null)
            return;
        var bytes = Seal(payload with { Hidden = true, DisplayName = "", UserName = "", Participants = 0 });
        await _client.SendAsync(bytes, new IPEndPoint(Group, _port), _lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _client?.Dispose();
        foreach (var task in new[] { _receiveLoop, _announceLoop })
        {
            if (task is null)
                continue;
            try { await task; }
            catch (Exception) { /* chiusura */ }
        }
    }

    private async Task AnnounceLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await SendAsync(_lifetime.Token);
                await Task.Delay(AnnounceInterval, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Rete assente o interfaccia giù: si riprova al prossimo giro.
                System.Diagnostics.Trace.WriteLine($"CUS: annuncio fallito: {ex.Message}");
                try { await Task.Delay(AnnounceInterval, _lifetime.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _payload() is not { } current)
            return;
        await _client.SendAsync(Seal(current), new IPEndPoint(Group, _port), cancellationToken);
    }

    /// <summary>Busta firmata: ID, istanza e contatore li mette sempre questa istanza.</summary>
    private byte[] Seal(AnnouncementPayload payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload with { DeviceId = _identity.DeviceId, Instance = _instanceId, Sequence = NextSequence() });
        var signature = _identity.Sign(SignedMessage(body));
        return JsonSerializer.SerializeToUtf8Bytes(new SignedAnnouncement(body, _identity.SignPublicKey, signature));
    }

    /// <summary>
    /// Contatore = millisecondi Unix, ma sempre più alto del precedente: cresce anche tra un avvio e
    /// l'altro senza doverlo salvare.
    /// </summary>
    private long NextSequence()
    {
        lock (_sequenceGate)
        {
            _lastSequence = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastSequence + 1);
            return _lastSequence;
        }
    }

    /// <summary>Prefisso del messaggio firmato: una firma di annuncio non vale per nient'altro.</summary>
    private static byte[] SignedMessage(ReadOnlySpan<byte> body) => [.. "CUS-discovery-v2\0"u8, .. body];

    /// <summary>Annuncio verificato (firma, ID uguale alla chiave, contatore fresco), oppure null.</summary>
    private AnnouncementPayload? Open(byte[] packet)
    {
        var envelope = JsonSerializer.Deserialize<SignedAnnouncement>(packet);
        if (envelope is not { App: "CUS", Version: SignedAnnouncement.CurrentVersion, Body: { Length: > 0 and <= MaxBody } body, Key: { } key, Signature: { } signature }
            || !DeviceIdentity.Verify(signature, SignedMessage(body), key))
            return null;

        var payload = JsonSerializer.Deserialize<AnnouncementPayload>(body);
        if (payload is null || payload.App != "CUS" || payload.Instance == _instanceId
            || payload.Port is < 1 or > 65535 || !IsDeviceId(payload.DeviceId)
            || !string.Equals(payload.DeviceId, DeviceIdentity.ComputeDeviceId(key), StringComparison.OrdinalIgnoreCase))
            return null;

        // Solo dopo la firma: un pacchetto falso non deve spostare il contatore di nessuno.
        return _freshness.TryAccept(payload.DeviceId.ToUpperInvariant(), payload.Sequence) ? payload : null;
    }

    private static bool IsLocalAddress(IPAddress address) => LocalAddresses.Contains(address);

    /// <summary>Un ID dispositivo è di 32 cifre esadecimali (vedi DeviceIdentity.ComputeDeviceId).</summary>
    private static bool IsDeviceId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    private async Task ReceiveLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested && _client is not null)
        {
            try
            {
                var result = await _client.ReceiveAsync(_lifetime.Token);
                if (result.Buffer.Length > MaxPacket || Open(result.Buffer) is not { } payload)
                    continue;
                // Un'istanza sullo stesso PC si raggiunge sempre in loopback (anche se ascolta solo lì).
                var host = IsLocalAddress(result.RemoteEndPoint.Address) ? "127.0.0.1" : result.RemoteEndPoint.Address.ToString();
                Announced?.Invoke(new DiscoveryAnnouncement(
                    payload.DeviceId.ToUpperInvariant(), DisplayText.Sanitize(payload.DisplayName, 64), DisplayText.Sanitize(payload.UserName, 64, ""), host, payload.Port,
                    Math.Clamp(payload.Participants, 1, 999), payload.Hidden));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is JsonException or SocketException)
            {
                // Pacchetto estraneo o errore transitorio: si ignora.
            }
            catch (Exception ex)
            {
                // Un errore imprevisto (anche in chi ascolta l'evento) non deve fermare la scoperta.
                System.Diagnostics.Trace.WriteLine($"CUS: errore nella scoperta: {ex}");
            }
        }
    }
}

internal static class LocalAddresses
{
    private static (long Ticks, HashSet<IPAddress> Addresses) _cache = (0, []);

    public static bool Contains(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        var cache = _cache;
        if (Environment.TickCount64 - cache.Ticks > 30_000)
        {
            var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .ToHashSet();
            cache = (Environment.TickCount64, addresses);
            _cache = cache;
        }

        return cache.Addresses.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}

public sealed record AnnouncementPayload(string DeviceId, string DisplayName, string UserName, int Port, int Participants = 1, bool Hidden = false)
{
    public string App { get; init; } = "CUS";

    public string Instance { get; init; } = "";

    /// <summary>Contatore crescente del mittente: un annuncio vecchio ripetuto viene scartato.</summary>
    public long Sequence { get; init; }
}

/// <summary>
/// Busta in rete: annuncio serializzato, chiave pubblica Ed25519 del mittente e firma sull'annuncio.
/// Le versioni precedenti, con annunci in chiaro, la scartano: non vi trovano un DeviceId.
/// </summary>
internal sealed record SignedAnnouncement(byte[] Body, byte[] Key, byte[] Signature)
{
    public const int CurrentVersion = 2;

    public string App { get; init; } = "CUS";

    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// Ultimo contatore accettato per dispositivo. Un annuncio passa solo se il suo contatore è più
/// alto: un congedo (o un annuncio) registrato e ripetuto più tardi non ha effetto. I dispositivi
/// non sentiti da <see cref="ForgetAfter"/> si dimenticano, e quelli ricordati sono al più <c>capacity</c>.
/// </summary>
internal sealed class AnnouncementFreshness(int capacity = 1024)
{
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (long Sequence, long SeenTicks)> _last = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="nowTicks">Millisecondi monotoni (Environment.TickCount64); i test li impostano.</param>
    public bool TryAccept(string deviceId, long sequence, long? nowTicks = null)
    {
        var now = nowTicks ?? Environment.TickCount64;
        var forget = (long)ForgetAfter.TotalMilliseconds;
        lock (_gate)
        {
            if (_last.TryGetValue(deviceId, out var last))
            {
                if (now - last.SeenTicks < forget && sequence <= last.Sequence)
                    return false;
            }
            else if (_last.Count >= capacity)
            {
                foreach (var (id, entry) in _last)
                {
                    if (now - entry.SeenTicks >= forget)
                        _last.Remove(id);
                }

                if (_last.Count >= capacity)
                    return false;
            }

            _last[deviceId] = (sequence, now);
            return true;
        }
    }
}
