namespace Cus.Core.Tests;

/// <summary>Cartella temporanea per un test, cancellata alla fine.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cus-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un file ancora aperto da SQLite non deve far fallire il test.
        }
    }
}
