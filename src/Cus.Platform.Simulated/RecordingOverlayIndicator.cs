using Cus.Core.Abstractions;

namespace Cus.Platform.Simulated;

/// <summary>
/// Indicatore senza finestre: registra lo stato e lo espone a UI e test. Con più istanze
/// sullo stesso PC evita che gli indicatori topmost reali si sovrappongano.
/// </summary>
public sealed class RecordingOverlayIndicator : IOverlayIndicator
{
    public event EventHandler? LeaveRequested;

    public event EventHandler? TransfersRequested;

    public event EventHandler? DirectionToggleRequested;

    public event EventHandler? StateChanged;

    public SessionIndicatorState? State { get; private set; }

    public bool IsVisible => State is not null;

    public void Show(SessionIndicatorState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Hide()
    {
        State = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simula il doppio clic dell'utente sull'indicatore (uscita dalla sessione).</summary>
    public void Click() => LeaveRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Simula il pulsante dei trasferimenti.</summary>
    public void ClickTransfers() => TransfersRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Simula il pulsante sola lettura / bidirezionale del membro.</summary>
    public void ClickDirection() => DirectionToggleRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Hide();
}
