using System.Security.Cryptography;
using System.Text;
using Cus.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Cus.Core.Tests;

/// <summary>
/// Smoke test di SQLite3MC su .NET 10: il DB è davvero cifrato e la chiave sbagliata è rifiutata.
/// </summary>
public sealed class EncryptedDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cus-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_directory, "cus.db");

    [Fact]
    public void Reopen_WithSameKey_ReadsData()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        using (var connection = EncryptedDatabase.Open(DbPath, key))
            Execute(connection, "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('segreto-di-prova');");

        using (var connection = EncryptedDatabase.Open(DbPath, key))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT v FROM t;";
            Assert.Equal("segreto-di-prova", command.ExecuteScalar());
        }
    }

    [Fact]
    public void Open_WithWrongKey_Fails()
    {
        using (var connection = EncryptedDatabase.Open(DbPath, RandomNumberGenerator.GetBytes(32)))
            Execute(connection, "CREATE TABLE t (v TEXT);");

        Assert.Throws<SqliteException>(() => EncryptedDatabase.Open(DbPath, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void File_IsNotPlainSqlite()
    {
        using (var connection = EncryptedDatabase.Open(DbPath, RandomNumberGenerator.GetBytes(32)))
            Execute(connection, "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('testo-in-chiaro-da-non-trovare');");

        var bytes = File.ReadAllBytes(DbPath);
        var content = Encoding.ASCII.GetString(bytes);

        Assert.False(content.StartsWith("SQLite format 3", StringComparison.Ordinal));
        Assert.DoesNotContain("testo-in-chiaro-da-non-trovare", content);
    }

    [Fact]
    public void Open_WithShortKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EncryptedDatabase.Open(DbPath, new byte[16]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
