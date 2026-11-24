using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cus.Core.Abstractions;
using Cus.Core.Identity;
using Cus.Core.Trust;
using Microsoft.Data.Sqlite;

namespace Cus.Core.Storage;

/// <summary>
/// DB cifrato dell'istanza: identità, impostazioni, peer manuali, lista di fiducia, cronologia.
/// Thread-safe: un'unica connessione protetta da un lock.
/// </summary>
public sealed class CusDatabase : IDisposable
{
    public const string DatabaseKeyName = "db-key";
    public const int HistoryLimit = 20;

    /// <summary>Migrazioni in ordine. La posizione (1-based) è la versione dello schema.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE device_identity (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            display_name TEXT NOT NULL,
            user_name TEXT NOT NULL,
            sign_public BLOB NOT NULL,
            sign_secret BLOB NOT NULL,
            created_utc TEXT NOT NULL);
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE manual_peers (address TEXT PRIMARY KEY);
        """,
        """
        CREATE TABLE trust_entries (
            entry_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            actor_public BLOB NOT NULL,
            subject_public BLOB NOT NULL,
            subject_name TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            signature BLOB NOT NULL);
        """,
        """
        CREATE TABLE history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            seq INTEGER NOT NULL,
            origin_device_id TEXT NOT NULL,
            origin_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            text TEXT,
            image_png BLOB,
            files_json TEXT,
            created_utc TEXT NOT NULL);
        """,
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private CusDatabase(SqliteConnection connection)
    {
        _connection = connection;
        Migrate();
    }

    public static int SchemaVersion => Migrations.Length;

    /// <summary>
    /// Apre il DB del profilo. La chiave è casuale (32 byte), generata al primo avvio e
    /// conservata solo nel secret store del sistema operativo.
    /// </summary>
    public static CusDatabase Open(IAppPaths paths, ISecretStore secrets)
    {
        var key = secrets.Load(DatabaseKeyName);
        if (key is null)
        {
            if (File.Exists(paths.DatabasePath))
                throw new InvalidOperationException(
                    $"Il DB '{paths.DatabasePath}' esiste ma la sua chiave non è nel secret store: impossibile aprirlo.");
            key = RandomNumberGenerator.GetBytes(32);
            secrets.Save(DatabaseKeyName, key);
        }

        var connection = EncryptedDatabase.Open(paths.DatabasePath, key);
        try
        {
            return new CusDatabase(connection);
        }
        catch
        {
            // Migrazione fallita (per esempio DB di una versione più recente): il file non resta aperto.
            connection.Dispose();
            throw;
        }
    }

    public int CurrentSchemaVersion()
    {
        lock (_gate)
            return Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- identità

    public DeviceIdentity? LoadIdentity()
    {
        lock (_gate)
        {
            using var command = Command("SELECT display_name, user_name, sign_public, sign_secret FROM device_identity WHERE id = 1;");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new DeviceIdentity(reader.GetString(0), reader.GetString(1), (byte[])reader[2], (byte[])reader[3]);
        }
    }

    public void SaveIdentity(DeviceIdentity identity)
    {
        lock (_gate)
        {
            using var command = Command("""
                INSERT INTO device_identity (id, display_name, user_name, sign_public, sign_secret, created_utc)
                VALUES (1, $name, $user, $pk, $sk, $created)
                ON CONFLICT(id) DO UPDATE SET display_name = $name, user_name = $user,
                    sign_public = $pk, sign_secret = $sk, created_utc = $created;
                """);
            command.Parameters.AddWithValue("$name", identity.DisplayName);
            command.Parameters.AddWithValue("$user", identity.UserName);
            command.Parameters.AddWithValue("$pk", identity.SignPublicKey);
            command.Parameters.AddWithValue("$sk", identity.SignSecretKey);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- impostazioni

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var command = Command("SELECT value FROM settings WHERE key = $key;");
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = Command("INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;");
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- peer manuali

    public IReadOnlyList<string> GetManualPeers()
    {
        lock (_gate)
        {
            using var command = Command("SELECT address FROM manual_peers ORDER BY address;");
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        }
    }

    public void AddManualPeer(string address)
    {
        lock (_gate)
        {
            using var command = Command("INSERT OR IGNORE INTO manual_peers (address) VALUES ($a);");
            command.Parameters.AddWithValue("$a", address);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveManualPeer(string address)
    {
        lock (_gate)
        {
            using var command = Command("DELETE FROM manual_peers WHERE address = $a;");
            command.Parameters.AddWithValue("$a", address);
            command.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- lista di fiducia

    public IReadOnlyList<TrustEntry> GetTrustEntries()
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, actor_public, subject_public, subject_name, created_utc, signature FROM trust_entries ORDER BY created_utc, entry_id;");
            using var reader = command.ExecuteReader();
            var result = new List<TrustEntry>();
            while (reader.Read())
            {
                result.Add(new TrustEntry(
                    reader.GetString(0) == "pair" ? TrustEntryKind.Pair : TrustEntryKind.Revoke,
                    (byte[])reader[1],
                    (byte[])reader[2],
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    (byte[])reader[5]));
            }

            return result;
        }
    }

    /// <returns>True se la voce era nuova.</returns>
    public bool AddTrustEntry(TrustEntry entry)
    {
        lock (_gate)
        {
            using var command = Command("""
                INSERT OR IGNORE INTO trust_entries (entry_id, kind, actor_public, subject_public, subject_name, created_utc, signature)
                VALUES ($id, $kind, $actor, $subject, $name, $created, $sig);
                """);
            command.Parameters.AddWithValue("$id", entry.EntryId);
            command.Parameters.AddWithValue("$kind", entry.Kind == TrustEntryKind.Pair ? "pair" : "revoke");
            command.Parameters.AddWithValue("$actor", entry.ActorPublicKey);
            command.Parameters.AddWithValue("$subject", entry.SubjectPublicKey);
            command.Parameters.AddWithValue("$name", entry.SubjectName);
            command.Parameters.AddWithValue("$created", entry.CreatedUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$sig", entry.Signature);
            return command.ExecuteNonQuery() == 1;
        }
    }

    // ---------------------------------------------------------------- cronologia (solo sull'host)

    /// <summary>Aggiunge una voce e tiene solo le ultime <see cref="HistoryLimit"/>.</summary>
    public void AddHistory(HistoryEntry entry)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using (var insert = Command("""
                INSERT INTO history (item_id, session_id, seq, origin_device_id, origin_name, kind, text, image_png, files_json, created_utc)
                VALUES ($item, $session, $seq, $origin, $originName, $kind, $text, $image, $files, $created);
                """))
            {
                insert.Transaction = transaction;
                insert.Parameters.AddWithValue("$item", entry.ItemId);
                insert.Parameters.AddWithValue("$session", entry.SessionId);
                insert.Parameters.AddWithValue("$seq", entry.Sequence);
                insert.Parameters.AddWithValue("$origin", entry.OriginDeviceId);
                insert.Parameters.AddWithValue("$originName", entry.OriginName);
                insert.Parameters.AddWithValue("$kind", entry.Content.Kind.ToString());
                insert.Parameters.AddWithValue("$text", (object?)entry.Content.Text ?? DBNull.Value);
                insert.Parameters.AddWithValue("$image", (object?)entry.Content.ImagePng ?? DBNull.Value);
                insert.Parameters.AddWithValue("$files", entry.Content.Files is null ? DBNull.Value : JsonSerializer.Serialize(entry.Content.Files));
                insert.Parameters.AddWithValue("$created", entry.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }

            using (var trim = Command("DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT $limit);"))
            {
                trim.Transaction = transaction;
                trim.Parameters.AddWithValue("$limit", HistoryLimit);
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Le voci più recenti per prime.</summary>
    /// <param name="sessionId">Solo le voci di questa sessione; null: tutte.</param>
    public IReadOnlyList<HistoryEntry> GetHistory(string? sessionId = null)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT item_id, session_id, seq, origin_device_id, origin_name, kind, text, image_png, files_json, created_utc
                FROM history WHERE $session IS NULL OR session_id = $session ORDER BY id DESC;
                """);
            command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
            return ReadHistory(command);
        }
    }

    /// <summary>La voce più recente con questo item_id, o null.</summary>
    public HistoryEntry? GetHistoryEntry(string itemId)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT item_id, session_id, seq, origin_device_id, origin_name, kind, text, image_png, files_json, created_utc
                FROM history WHERE item_id = $item ORDER BY id DESC LIMIT 1;
                """);
            command.Parameters.AddWithValue("$item", itemId);
            return ReadHistory(command).FirstOrDefault();
        }
    }

    private static List<HistoryEntry> ReadHistory(SqliteCommand command)
    {
        {
            using var reader = command.ExecuteReader();
            var result = new List<HistoryEntry>();
            while (reader.Read())
            {
                var kind = Enum.Parse<ClipboardContentKind>(reader.GetString(5));
                var content = new ClipboardContent
                {
                    Kind = kind,
                    Text = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ImagePng = reader.IsDBNull(7) ? null : (byte[])reader[7],
                    Files = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<List<ClipboardFileInfo>>(reader.GetString(8)),
                };
                result.Add(new HistoryEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    content,
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _connection.Dispose();
    }

    private void Migrate()
    {
        lock (_gate)
        {
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > Migrations.Length)
                throw new InvalidOperationException(
                    $"Il DB ha schema {version}, più recente di quello supportato ({Migrations.Length}). Aggiorna CUS.");

            for (var next = version + 1; next <= Migrations.Length; next++)
            {
                using var transaction = _connection.BeginTransaction();
                using var command = Command(Migrations[next - 1] + $"\nPRAGMA user_version = {next};");
                command.Transaction = transaction;
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }
    }

    private object? Scalar(string sql)
    {
        using var command = Command(sql);
        return command.ExecuteScalar();
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
