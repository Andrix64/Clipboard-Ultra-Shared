using Cus.Core.Abstractions;

namespace Cus.Platform.Simulated;

/// <summary>
/// Clipboard virtuale, una per istanza. Si comporta come quella di sistema: sia una copia
/// dell'utente (<see cref="Copy"/>) sia una scrittura di CUS (<see cref="WriteAsync"/>)
/// notificano il monitor; le scritture di CUS sono marcate come tali.
/// </summary>
public sealed class SimulatedClipboard : IClipboardMonitor, IClipboardWriter
{
    private readonly Lock _gate = new();
    private ClipboardContent? _current;
    private IRemoteFileSource? _fileSource;
    private bool _started;

    public event EventHandler<ClipboardChangedEventArgs>? Changed;

    public ClipboardContent? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Start()
    {
        lock (_gate) _started = true;
    }

    /// <summary>Simula una copia dell'utente (Ctrl+C).</summary>
    public void Copy(ClipboardContent content) => Set(content, null, isOwnWrite: false);

    public Task WriteAsync(ClipboardContent content, IRemoteFileSource? files = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Kind == ClipboardContentKind.Files && files is null)
            throw new ArgumentException("Per scrivere file serve la sorgente remota.", nameof(files));
        Set(content, files, isOwnWrite: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Simula l'incolla dei file ricevuti: li scarica dal PC d'origine nella cartella indicata,
    /// come farebbe Esplora risorse con i virtual files.
    /// </summary>
    /// <exception cref="RemoteFileUnavailableException">Il PC d'origine non è disponibile.</exception>
    public async Task<IReadOnlyList<string>> PasteFilesAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        ClipboardContent? content;
        IRemoteFileSource? source;
        lock (_gate)
        {
            content = _current;
            source = _fileSource;
        }

        if (content is not { Kind: ClipboardContentKind.Files, Files: { } files })
            throw new InvalidOperationException("La clipboard non contiene file.");

        if (!ClipboardFileNames.TryValidate(files, out var reason))
            throw new InvalidDataException($"Elenco di file non valido: {reason}.");

        var written = new List<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory)) + Path.DirectorySeparatorChar;
        for (var i = 0; i < files.Count; i++)
        {
            var target = Path.GetFullPath(Path.Combine(root, files[i].Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Nome di file non valido: '{files[i].Name}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var output = File.Create(target);
            if (source is null && files[i].LocalPath is { } local)
            {
                await using var input = File.OpenRead(local);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                for (long offset = 0; offset < files[i].SizeBytes;)
                {
                    var count = (int)Math.Min(1024 * 1024, files[i].SizeBytes - offset);
                    var chunk = await source!.ReadAsync(i, offset, count, cancellationToken);
                    if (chunk.Length > count)
                        chunk = chunk[..count];
                    if (chunk.Length == 0)
                        throw new RemoteFileUnavailableException("Il PC d'origine ha restituito un blocco vuoto.");
                    await output.WriteAsync(chunk, cancellationToken);
                    offset += chunk.Length;
                }
            }

            written.Add(target);
        }

        return written;
    }

    public void Dispose()
    {
        lock (_gate) _started = false;
    }

    private void Set(ClipboardContent content, IRemoteFileSource? files, bool isOwnWrite)
    {
        ArgumentNullException.ThrowIfNull(content);
        bool notify;
        lock (_gate)
        {
            _current = content;
            _fileSource = files;
            notify = _started;
        }

        if (notify)
            Changed?.Invoke(this, new ClipboardChangedEventArgs(content, isOwnWrite));
    }
}
