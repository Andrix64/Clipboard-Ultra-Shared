using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Cus.Platform.Linux.Tests;

/// <summary>Secret Service reale tramite libsecret (in CI: gnome-keyring-daemon in una sessione D-Bus di prova).</summary>
[SupportedOSPlatform("linux")]
public sealed class SecretServiceStoreTests
{
    [SecretServiceFact]
    public void SavedSecret_IsLoadedBack()
    {
        var store = new SecretServiceStore(UniqueProfile());
        var secret = RandomNumberGenerator.GetBytes(32);

        store.Save("db-key", secret);

        Assert.Equal(secret, store.Load("db-key"));
    }

    [SecretServiceFact]
    public void Save_ReplacesThePreviousValue()
    {
        var store = new SecretServiceStore(UniqueProfile());
        store.Save("db-key", [1, 2, 3]);

        store.Save("db-key", [4, 5, 6]);

        Assert.Equal([4, 5, 6], store.Load("db-key"));
    }

    [SecretServiceFact]
    public void Load_ReturnsNull_ForAMissingSecret() =>
        Assert.Null(new SecretServiceStore(UniqueProfile()).Load("db-key"));

    [SecretServiceFact]
    public void Profiles_DoNotShareSecrets()
    {
        var first = new SecretServiceStore(UniqueProfile());
        var second = new SecretServiceStore(UniqueProfile());

        first.Save("db-key", [7]);

        Assert.Null(second.Load("db-key"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../key")]
    [InlineData("a b")]
    public void InvalidNames_AreRejected_BeforeTouchingTheKeyring(string name) =>
        Assert.Throws<ArgumentException>(() => new SecretServiceStore("/tmp/cus").Load(name));

    private static string UniqueProfile() => Path.Combine(Path.GetTempPath(), "cus-secret-tests", Guid.NewGuid().ToString("N"));
}
