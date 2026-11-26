using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Storage;
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
    public void ClearHistory_RemovesEveryEntry_OfEverySession()
    {
        using var db = CusDatabase.Open(Paths("host"), _secrets);
        for (var i = 1; i <= 3; i++)
            db.AddHistory(Entry(i, ClipboardContent.FromText($"voce {i}")));

        Assert.Equal(3, db.ClearHistory());

        Assert.Empty(db.GetHistory());
        db.AddHistory(Entry(4, ClipboardContent.FromText("dopo")));
        Assert.Equal("dopo", Assert.Single(db.GetHistory()).Content.Text);
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
    public void Friends_RoundTrip_AndDelete()
    {
        using var db = CusDatabase.Open(Paths("alice"), _secrets);
        var id = DeviceIdentity.Generate("luca", "u").DeviceId;
        var friend = new Friend(id, "Luca", AgreementLevel.Anytime, TrustedWithSensitive: true, "192.168.1.5", 23701, DateTimeOffset.UtcNow);

        db.UpsertFriend(friend);
        db.UpsertFriend(friend with { DisplayName = "Luca (fisso)" });

        var loaded = db.GetFriend(id)!;
        Assert.Equal("Luca (fisso)", loaded.DisplayName);
        Assert.Equal(AgreementLevel.Anytime, loaded.Agreement);
        Assert.True(loaded.TrustedWithSensitive);
        Assert.Equal(new PeerEndpoint("192.168.1.5", 23701), loaded.LastEndpoint);
        Assert.Equal(DeviceIdentity.FingerprintOf(id), loaded.Fingerprint);
        Assert.Single(db.GetFriends());

        db.DeleteFriend(id);
        Assert.Null(db.GetFriend(id));
        Assert.Empty(db.GetFriends());
    }

    [Fact]
    public void Migration4_DropsTheTrustListAndPairedEndpoints_AndKeepsTheRest()
    {
        var paths = Paths("vecchio");
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        _secrets.Save(CusDatabase.DatabaseKeyName, key);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

        // DB alla versione 3 (prima degli amici), con una voce di fiducia e un indirizzo memorizzato.
        using (var connection = EncryptedDatabase.Open(paths.DatabasePath, key))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE device_identity (id INTEGER PRIMARY KEY CHECK (id = 1), display_name TEXT NOT NULL, user_name TEXT NOT NULL,
                    sign_public BLOB NOT NULL, sign_secret BLOB NOT NULL, created_utc TEXT NOT NULL);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE manual_peers (address TEXT PRIMARY KEY);
                CREATE TABLE trust_entries (entry_id TEXT PRIMARY KEY, kind TEXT NOT NULL, actor_public BLOB NOT NULL, subject_public BLOB NOT NULL,
                    subject_name TEXT NOT NULL, created_utc TEXT NOT NULL, signature BLOB NOT NULL);
                CREATE TABLE history (id INTEGER PRIMARY KEY AUTOINCREMENT, item_id TEXT NOT NULL, session_id TEXT NOT NULL, seq INTEGER NOT NULL,
                    origin_device_id TEXT NOT NULL, origin_name TEXT NOT NULL, kind TEXT NOT NULL, text TEXT, image_png BLOB, files_json TEXT,
                    created_utc TEXT NOT NULL);
                INSERT INTO trust_entries VALUES ('x', 'pair', x'01', x'02', 'vecchio PC', '2026-11-24T10:00:00.000Z', x'03');
                INSERT INTO settings VALUES ('endpoint/ABC', '192.168.1.9:23700'), ('display-name', 'tenuto');
                INSERT INTO manual_peers VALUES ('192.168.1.20:23700');
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        using (var db = CusDatabase.Open(paths, _secrets))
        {
            Assert.Equal(4, db.CurrentSchemaVersion());
            Assert.Null(db.GetSetting("endpoint/ABC"));
            Assert.Equal("tenuto", db.GetSetting("display-name"));
            Assert.Equal(["192.168.1.20:23700"], db.GetManualPeers());
            Assert.Empty(db.GetFriends());
        }

        using var check = EncryptedDatabase.Open(paths.DatabasePath, key);
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'trust_entries';";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    public void Dispose() => _temp.Dispose();

    private ProfileAppPaths Paths(string profile) => new(profile, _temp.Path);

    private static HistoryEntry Entry(int seq, ClipboardContent content) =>
        new($"item-{seq}", "session", seq, "device", "Andrea", content, DateTimeOffset.UtcNow);
}
