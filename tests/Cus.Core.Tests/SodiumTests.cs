using Cus.Core.Crypto;

namespace Cus.Core.Tests;

/// <summary>Smoke test di libsodium: la libreria nativa si carica e si inizializza.</summary>
public class SodiumTests
{
    [Fact]
    public void Initializes()
    {
        Sodium.EnsureInitialized();
    }
}
