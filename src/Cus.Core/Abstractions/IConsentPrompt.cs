using Cus.Core.Friends;

namespace Cus.Core.Abstractions;

public enum ConsentDecision
{
    Reject,

    /// <summary>Fa entrare con accesso bidirezionale: le sue copie arrivano a tutti e le altre a lui.</summary>
    Accept,

    /// <summary>Fa entrare in sola lettura: riceve le copie della sessione ma le sue restano sul suo PC.</summary>
    AcceptReadOnly,
}

/// <param name="RequesterName">Nome del dispositivo che chiede di entrare.</param>
/// <param name="RequesterUserName">Utente dichiarato da chi chiede.</param>
/// <param name="RequesterFingerprint">Impronta della chiave di chi chiede, da confrontare a voce se serve.</param>
public sealed record ConsentRequest(string RequesterName, string RequesterUserName, string RequesterFingerprint);

/// <summary>L'host propone un patto: vale nei due sensi e uno qualsiasi dei due può scioglierlo.</summary>
public sealed record AgreementRequest(string HostName, string HostFingerprint, AgreementLevel Level);

/// <summary>Chiede all'utente le decisioni che riguardano l'accesso alla sua clipboard.</summary>
public interface IConsentPrompt
{
    /// <summary>Host: qualcuno chiede di entrare nella sessione. L'host sceglie anche la modalità.</summary>
    Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken);

    /// <summary>Membro: l'host propone un patto reciproco.</summary>
    Task<bool> AskAgreementAsync(AgreementRequest request, CancellationToken cancellationToken);
}

/// <summary>Messaggi per l'utente (errori chiari, avvisi). In UI diventano notifiche.</summary>
public interface IUserNotifier
{
    void Info(string message);

    void Error(string message);
}
