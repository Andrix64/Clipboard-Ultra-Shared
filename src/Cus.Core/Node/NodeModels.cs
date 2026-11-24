using System.Net;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;

namespace Cus.Core.Node;

public sealed record CusNodeOptions
{
    /// <summary>Porta TCP fissa. Null: la prima libera tra 23700 e 23709.</summary>
    public int? Port { get; init; }

    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    public bool EnableDiscovery { get; init; } = true;

    public int DiscoveryPort { get; init; } = Net.Discovery.DefaultPort;

    public IReadOnlyList<PeerEndpoint> ManualPeers { get; init; } = [];

    /// <summary>Quanto attendere la risposta dell'utente a una richiesta di accesso.</summary>
    public TimeSpan ConsentTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PairingCodeLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public int PairingAttempts { get; init; } = 3;
}

/// <summary>Peer visibile: trovato in rete o inserito a mano.</summary>
public sealed record PeerView(string? DeviceId, string DisplayName, string UserName, string Host, int Port, bool IsTrusted, bool IsManual)
{
    public string Address => new PeerEndpoint(Host, Port).ToString();
}

public enum SessionRole
{
    Host,
    Member,
}

public sealed record SessionView(string SessionId, SessionRole Role, IReadOnlyList<SessionParticipant> Participants);

public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt, int AttemptsLeft);

public enum InviteOutcome
{
    Accepted,
    AcceptedReadOnly,
    Rejected,
}

public sealed record InviteResult(InviteOutcome Outcome, string PeerName, string? Reason);

public sealed class PairingFailedException(string message) : Exception(message);

/// <summary>Errore mostrabile all'utente così com'è.</summary>
public sealed class CusOperationException(string message, Exception? inner = null) : Exception(message, inner);
