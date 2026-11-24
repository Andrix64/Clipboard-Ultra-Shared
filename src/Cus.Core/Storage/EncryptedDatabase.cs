using Microsoft.Data.Sqlite;

namespace Cus.Core.Storage;

/// <summary>
/// Apre il DB SQLite cifrato con SQLite3 Multiple Ciphers (cipher di default).
/// </summary>
public static class EncryptedDatabase
{
    /// <summary>
    /// Apre (o crea) il DB cifrato con la chiave data. Su sistemi non Windows il file viene
    /// creato con permessi 0600.
    /// </summary>
    /// <exception cref="SqliteException">La chiave è sbagliata o il file non è un DB CUS.</exception>
    public static SqliteConnection Open(string path, ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
            throw new ArgumentException("La chiave del DB deve essere di almeno 32 byte.", nameof(key));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows() && !File.Exists(path))
        {
            using (File.Create(path)) { }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = Convert.ToHexString(key),
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            // La chiave si verifica solo alla prima lettura: forzarla qui fa fallire subito
            // una chiave sbagliata, invece che alla prima query dell'applicazione.
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT count(*) FROM sqlite_master;";
            check.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
