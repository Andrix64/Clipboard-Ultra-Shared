namespace Cus.Core.Abstractions;

public enum ConsentDecision
{
    Reject,
    Accept,

    /// <summary>"Puoi solo leggere": il membro invia le proprie copie ma non riceve quelle degli altri.</summary>
    AcceptReadOnly,
}

/// <param name="HostName">Nome del dispositivo che chiede l'accesso (l'host).</param>
/// <param name="HostUserName">Utente dichiarato dall'host.</param>
/// <param name="HostFingerprint">Impronta della chiave dell'host, da confrontare a voce se serve.</param>
public sealed record ConsentRequest(string HostName, string HostUserName, string HostFingerprint);

/// <summary>Chiede all'utente se concedere l'accesso alla propria clipboard.</summary>
public interface IConsentPrompt
{
    Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken);
}

/// <summary>Messaggi per l'utente (errori chiari, avvisi). In UI diventano notifiche.</summary>
public interface IUserNotifier
{
    void Info(string message);

    void Error(string message);
}
