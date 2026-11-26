namespace Cus.Platform.Windows.Tests;

/// <summary>Usa un nome di valore unico nella chiave Run reale dell'utente e lo rimuove sempre.</summary>
public sealed class RegistryAutoStartTests : IDisposable
{
    private readonly RegistryAutoStart _autoStart =
        new(@"C:\Program Files\CUS test\cus.exe", $"CUS-test-{Guid.NewGuid():N}");

    [Fact]
    public void Enable_RegistersQuotedPathWithTrayArgument_DisableRemovesIt()
    {
        Assert.Null(_autoStart.RegisteredCommandLine());

        _autoStart.Enable();
        Assert.Equal(@"""C:\Program Files\CUS test\cus.exe"" --tray", _autoStart.RegisteredCommandLine());

        _autoStart.Enable(); // idempotente
        _autoStart.Disable();
        Assert.Null(_autoStart.RegisteredCommandLine());

        _autoStart.Disable(); // nessun errore se assente
    }

    public void Dispose() => _autoStart.Disable();
}
