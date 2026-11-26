using Avalonia.Controls;

namespace Cus.UI;

/// <summary>
/// Finestra modale di conferma per le azioni irreversibili (per esempio sciogliere un patto). Invio ed Esc
/// annullano: confermare richiede un clic sul pulsante dell'azione.
/// </summary>
public static class ConfirmDialog
{
    /// <returns>True solo se l'utente ha premuto il pulsante di conferma.</returns>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel) =>
        await DialogShell.AskAsync(owner, title, 440, Skin.LcdText(message, 12), [Loc.T("common.cancel"), confirmLabel], seed: 6) == 1;
}
