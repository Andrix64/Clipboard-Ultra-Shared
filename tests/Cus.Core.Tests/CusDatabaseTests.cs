using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Storage;
using Cus.Core.Trust;
using Cus.Platform.Simulated;

namespace Cus.Core.Tests;

public sealed class CusDatabaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly InMemorySecretStore _secrets = new();

    [Fact]
    public void Open_CreatesKeyAndAppliesAllMigrations()
    {
        using var db = CusDatabase.Open(Paths("alice"), _secrets);

        Assert.Equal(CusDatabase.SchemaVersion, db.CurrentSchemaVersion());
        Assert.Equal(32, _secrets.Load(CusDatabase.DatabaseKeyName)!.Length);
    }

    [Fact]
    public void Reopen_KeepsIdentitySettingsAndPeers()
    {
        var paths = Paths("alice");
        var identity = DeviceIdentity.Generate("portatile", "andrea");
        using (var db = CusDatabase.Open(paths, _secrets))
        {
            db.SaveIdentity(identity);
            db.SetSetting("display-name", "Portatile di Andrea");
            db.AddManualPeer("192.168.1.20:23700");
            db.AddManualPeer("192.168.1.20:23700");
        }

        using (var db = CusDatabase.Open(paths, _secrets))
        {
            var loaded = db.LoadIdentity()!;
            Assert.Equal(identity.DeviceId, loaded.DeviceId);
            Assert.Equal(identity.SignPublicKey, loaded.SignPublicKey);
            Assert.Equal("Portatile di Andrea", db.GetSetting("display-name"));
            Assert.Equal(["192.168.1.20:23700"], db.GetManualPeers());
            Assert.Equal(CusDatabase.SchemaVersion, db.CurrentSchemaVersion());
        }
    }

    [Fact]
    public void OtherProfile_CannotOpenTheDatabaseWithItsOwnKey()
    {
        var alice = Paths("alice");
        using (CusDatabase.Open(alice, _secrets)) { }

        var otherSecrets = new InMemorySecretStore();
        otherSecrets.Save(CusDatabase.DatabaseKeyName, new byte[32]);

        Assert.ThrowsAny<Exception>(() => CusDatabase.Open(alice, otherSecrets));
    }

    [Fact]
    public void ExistingDatabase_WithoutKey_IsNotRecreated()
    {
        var alice = Paths("alice");
        using (CusDatabase.Open(alice, _secrets)) { }

        Assert.Throws<InvalidOperationException>(() => CusDatabase.Open(alice, new InMemorySecretStore()));
    }

    [Fact]
    public void History_KeepsOnlyTheLast20_NewestFirst()
    {
        using var db = CusDatabase.Open(Paths("host"), _secrets);
        for (var i = 1; i <= 25; i++)
            db.AddHistory(Entry(i, ClipboardContent.FromText($"voce {i}")));

        var history = db.GetHistory();

        Assert.Equal(CusDatabase.HistoryLimit, history.Count);
        Assert.Equal("voce 25", history[0].Content.Text);
        Assert.Equal("voce 6", history[^1].Content.Text);
    }

    [Fact]
    public void History_StoresImagesAndFileReferences()
    {
        using var db = CusDatabase.Open(Paths("host"), _secrets);
        db.AddHistory(Entry(1, ClipboardContent.FromImage([1, 2, 3])));
        db.AddHistory(Entry(2, ClipboardContent.FromFiles([new ClipboardFileInfo("a/b.txt", 42) { LocalPath = @"C:\segreto\b.txt" }])));

        var history = db.GetHistory();

        Assert.Equal(new ClipboardFileInfo("a/b.txt", 42), history[0].Content.Files!.Single());
        Assert.Null(history[0].Content.Files!.Single().LocalPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, history[1].Content.ImagePng);
    }

    [Fact]
    public void TrustEntries_AreStoredOnceAndRoundTrip()
    {
        using var db = CusDatabase.Open(Paths("alice"), _secrets);
        var actor = DeviceIdentity.Generate("a", "u");
        var subject = DeviceIdentity.Generate("b", "u");
        var entry = TrustEntry.Create(actor, TrustEntryKind.Pair, subject.Info, DateTimeOffset.UtcNow);

        Assert.True(db.AddTrustEntry(entry));
        Assert.False(db.AddTrustEntry(entry));

        var loaded = db.GetTrustEntries().Single();
        Assert.Equal(entry.EntryId, loaded.EntryId);
        Assert.True(loaded.HasValidSignature());
    }

    public void Dispose() => _temp.Dispose();

    private ProfileAppPaths Paths(string profile) => new(profile, _temp.Path);

    private static HistoryEntry Entry(int seq, ClipboardContent content) =>
        new($"item-{seq}", "session", seq, "device", "Andrea", content, DateTimeOffset.UtcNow);
}
