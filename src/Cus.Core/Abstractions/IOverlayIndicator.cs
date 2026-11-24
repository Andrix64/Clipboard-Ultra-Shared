namespace Cus.Core.Abstractions;

public enum ShareDirection
{
    Bidirectional,

    /// <summary>Il membro riceve le copie della sessione ma non invia le proprie: la sua clipboard resta sul suo PC.</summary>
    ReadOnly,
}

public sealed record SessionParticipant(string DisplayName, ShareDirection Direction, bool IsHost);

public sealed record SessionIndicatorState(IReadOnlyList<SessionParticipant> Participants);

/// <summary>
/// Indicatore sempre in primo piano su tutti i monitor che segnala la condivisione attiva.
/// Un clic sull'indicatore chiede di uscire dalla sessione.
/// </summary>
public interface IOverlayIndicator : IDisposable
{
    event EventHandler? LeaveRequested;

    void Show(SessionIndicatorState state);

    void Hide();
}
