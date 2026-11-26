using Cus.Core.Abstractions;
using Microsoft.Win32;

namespace Cus.Platform.Windows;

/// <summary>
/// Avvio automatico all'accesso a Windows tramite la chiave Run dell'utente corrente (HKCU): niente
/// privilegi di amministratore, e compare in Gestione attività → App di avvio, dove l'utente può
/// disattivarlo. All'accesso CUS parte nell'area di notifica (<c>--tray</c>), senza aprire la finestra.
/// </summary>
public sealed class RegistryAutoStart(string executablePath, string valueName = "CUS") : IAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string TrayArgument = "--tray";

    /// <summary>Riga di comando registrata: percorso tra virgolette (può contenere spazi) più <see cref="TrayArgument"/>.</summary>
    public string CommandLine => $"\"{executablePath}\" {TrayArgument}";

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key.GetValue(valueName) as string != CommandLine)
            key.SetValue(valueName, CommandLine, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Valore attualmente registrato, null se assente (per i test).</summary>
    internal string? RegisteredCommandLine()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(valueName) as string;
    }
}
