using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Cus.Core.Hosting;

public enum ClipboardMode
{
    /// <summary>Clipboard di sistema.</summary>
    Real,

    /// <summary>Clipboard virtuale per istanza: più istanze sullo stesso PC non si disturbano.</summary>
    Simulated,
}

/// <summary>Indirizzo di un peer inserito a mano: IP o nome host, porta opzionale.</summary>
public sealed record PeerEndpoint(string Host, int Port)
{
    public override string ToString() =>
        Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>Accetta <c>host</c>, <c>host:porta</c>, <c>[ipv6]</c> e <c>[ipv6]:porta</c>.</summary>
    public static PeerEndpoint Parse(string value, int defaultPort = NetworkDefaults.DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Indirizzo del peer vuoto.", nameof(value));

        value = value.Trim();
        string host;
        string? portText = null;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
                throw new ArgumentException($"Indirizzo IPv6 senza ']': '{value}'.", nameof(value));
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                    throw new ArgumentException($"Indirizzo del peer non valido: '{value}'.", nameof(value));
                portText = rest[1..];
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            var colon = value.IndexOf(':');
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        else if (value.Contains(':'))
        {
            throw new ArgumentException(
                $"Indirizzo IPv6 ambiguo: '{value}'. Scrivilo tra parentesi quadre, per esempio [::1]:23700.",
                nameof(value));
        }
        else
        {
            host = value;
        }

        if (host.Length == 0)
            throw new ArgumentException($"Host mancante in '{value}'.", nameof(value));

        var port = portText is null ? defaultPort : CusOptions.ParsePort(portText);
        return new PeerEndpoint(host, port);
    }
}

public static class NetworkDefaults
{
    /// <summary>Prima porta del range riservato a CUS (23700–23709, vedi IDEA.md).</summary>
    public const int DefaultPort = 23700;

    public const int PortRangeEnd = 23709;
}

/// <summary>
/// Opzioni da riga di comando. Pensate anche per avviare più istanze sullo stesso PC:
/// <code>cus --profile alice --port 23700 --clipboard simulated --peer 127.0.0.1:23702</code>
/// </summary>
public sealed partial record CusOptions
{
    public const string DefaultProfile = "default";

    public const string Usage =
        "Uso: cus [--profile <nome>] [--name <nome del PC>] [--port <porta>] [--bind <ip>] [--clipboard real|simulated] "
        + "[--peer <host[:porta]>]... [--no-discovery]";

    public string Profile { get; init; } = DefaultProfile;

    /// <summary>Porta TCP di ascolto. Null: la prima libera del range 23700–23709.</summary>
    public int? Port { get; init; }

    public ClipboardMode ClipboardMode { get; init; } = ClipboardMode.Real;

    public IReadOnlyList<PeerEndpoint> Peers { get; init; } = [];

    /// <summary>Nome del dispositivo mostrato agli altri. Null: nome del PC (più il profilo, se non è quello di default).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Indirizzo di ascolto. 127.0.0.1 evita l'avviso del firewall nelle prove su un solo PC.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    public bool EnableDiscovery { get; init; } = true;

    /// <exception cref="ArgumentException">Argomento sconosciuto o valore non valido.</exception>
    public static CusOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CusOptions();
        var peers = new List<PeerEndpoint>();

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            switch (name)
            {
                case "--profile":
                    var profile = ValueOf(args, ref i, name);
                    ValidateProfileName(profile);
                    options = options with { Profile = profile };
                    break;
                case "--port":
                    options = options with { Port = ParsePort(ValueOf(args, ref i, name)) };
                    break;
                case "--clipboard":
                    options = options with { ClipboardMode = ParseClipboardMode(ValueOf(args, ref i, name)) };
                    break;
                case "--peer":
                    peers.Add(PeerEndpoint.Parse(ValueOf(args, ref i, name)));
                    break;
                case "--name":
                    var displayName = ValueOf(args, ref i, name).Trim();
                    if (displayName.Length is 0 or > 64)
                        throw new ArgumentException("Il nome del PC deve avere da 1 a 64 caratteri.");
                    options = options with { DisplayName = displayName };
                    break;
                case "--bind":
                    var bind = ValueOf(args, ref i, name);
                    if (!IPAddress.TryParse(bind, out var address))
                        throw new ArgumentException($"Indirizzo di --bind non valido: '{bind}'.");
                    options = options with { BindAddress = address };
                    break;
                case "--no-discovery":
                    options = options with { EnableDiscovery = false };
                    break;
                default:
                    throw new ArgumentException($"Argomento sconosciuto: '{name}'. {Usage}");
            }
        }

        return options with { Peers = peers };
    }

    public static void ValidateProfileName(string profile)
    {
        if (!ProfileNameRegex().IsMatch(profile))
            throw new ArgumentException(
                $"Nome di profilo non valido: '{profile}'. Ammessi lettere, cifre, '-' e '_', massimo 32 caratteri.");
    }

    internal static int ParsePort(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Porta non valida: '{text}'. Deve essere un numero tra 1 e 65535.");
        return port;
    }

    private static ClipboardMode ParseClipboardMode(string text) => text switch
    {
        "real" => ClipboardMode.Real,
        "simulated" => ClipboardMode.Simulated,
        _ => throw new ArgumentException($"Valore di --clipboard non valido: '{text}'. Usa 'real' o 'simulated'."),
    };

    private static string ValueOf(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count)
            throw new ArgumentException($"Manca il valore di {name}. {Usage}");
        return args[++i];
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ProfileNameRegex();
}
