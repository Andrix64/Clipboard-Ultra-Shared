namespace Cus.Platform.Linux.Tests;

/// <summary>
/// Test che richiede Linux con un display X (in CI: xvfb-run) e xclip, l'altra applicazione che copia e
/// incolla. Altrove si salta: su Windows non c'è nessuna clipboard X11 da provare.
/// </summary>
public sealed class X11FactAttribute : FactAttribute
{
    public X11FactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Solo Linux.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Skip = "DISPLAY non impostata (usa xvfb-run).";
    }
}

/// <summary>
/// Test che scrive nel portachiavi dell'utente: gira solo con CUS_TEST_SECRET_SERVICE=1 (in CI, un
/// gnome-keyring-daemon usa e getta), per non lasciare voci nel portachiavi di chi lancia i test.
/// </summary>
public sealed class SecretServiceFactAttribute : FactAttribute
{
    public SecretServiceFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Solo Linux.";
        else if (Environment.GetEnvironmentVariable("CUS_TEST_SECRET_SERVICE") != "1")
            Skip = "Imposta CUS_TEST_SECRET_SERVICE=1 con un portachiavi di prova.";
    }
}
