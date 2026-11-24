using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cus.Core.Abstractions;

namespace Cus.Core.Net;

/// <summary>Annuncio ricevuto da un altro dispositivo in rete.</summary>
public sealed record DiscoveryAnnouncement(string DeviceId, string DisplayName, string UserName, string Host, int Port);

/// <summary>
/// Scoperta dei peer con annunci UDP multicast (239.255.23.70, porta 23701). Più istanze sullo
/// stesso PC condividono la porta grazie a SO_REUSEADDR e ricevono tutte gli annunci.
/// Gli annunci non sono autenticati: servono solo a trovare l'indirizzo, l'identità la verifica
/// il canale cifrato alla connessione.
/// </summary>
public sealed class Discovery : IAsyncDisposable
{
    public static readonly IPAddress Group = IPAddress.Parse("239.255.23.70");
    public const int DefaultPort = 23701;
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly Func<AnnouncementPayload> _payload;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private UdpClient? _client;
    private Task? _receiveLoop;
    private Task? _announceLoop;

    /// <param name="payload">Dati da annunciare, letti a ogni annuncio (nome e porta possono cambiare).</param>
    public Discovery(Func<AnnouncementPayload> payload, int port = DefaultPort)
    {
        _payload = payload;
        _port = port;
    }

    public event Action<DiscoveryAnnouncement>? Announced;

    public void Start()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        client.JoinMulticastGroup(Group);
        client.MulticastLoopback = true;
        client.Ttl = 1;
        _client = client;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _announceLoop = Task.Run(AnnounceLoopAsync);
    }

    /// <summary>Annuncia subito, oltre agli annunci periodici.</summary>
    public Task AnnounceNowAsync() => SendAsync(_lifetime.Token);

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
        if (_client is null)
            return;
        var payload = _payload() with { Instance = _instanceId };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _client.SendAsync(bytes, new IPEndPoint(Group, _port), cancellationToken);
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
                if (result.Buffer.Length > 2048)
                    continue;
                var payload = JsonSerializer.Deserialize<AnnouncementPayload>(result.Buffer);
                if (payload is null || payload.App != "CUS" || payload.Instance == _instanceId
                    || payload.Port is < 1 or > 65535 || !IsDeviceId(payload.DeviceId))
                    continue;
                // Un'istanza sullo stesso PC si raggiunge sempre in loopback (anche se ascolta solo lì).
                var host = IsLocalAddress(result.RemoteEndPoint.Address) ? "127.0.0.1" : result.RemoteEndPoint.Address.ToString();
                Announced?.Invoke(new DiscoveryAnnouncement(
                    payload.DeviceId.ToUpperInvariant(), DisplayText.Sanitize(payload.DisplayName, 64), DisplayText.Sanitize(payload.UserName, 64, ""), host, payload.Port));
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

public sealed record AnnouncementPayload(string DeviceId, string DisplayName, string UserName, int Port)
{
    public string App { get; init; } = "CUS";

    public string Instance { get; init; } = "";
}
