using Cus.Core.Abstractions;
using Cus.Platform.Simulated;
using Cus.UI;

namespace Cus.App.Shared;

/// <summary>Collega la scheda "Clipboard simulata" della UI alla clipboard virtuale dell'istanza.</summary>
internal sealed class SimulationControls : ISimulationControls
{
    private readonly SimulatedClipboard _clipboard;

    public SimulationControls(SimulatedClipboard clipboard)
    {
        _clipboard = clipboard;
        _clipboard.Changed += (_, _) => ContentChanged?.Invoke();
    }

    public event Action? ContentChanged;

    public string DescribeCurrent() => _clipboard.Current?.Describe() ?? "(vuota)";

    public void CopyText(string text) => _clipboard.Copy(ClipboardContent.FromText(text));

    public void CopySensitiveText(string text) => _clipboard.Copy(ClipboardContent.FromText(text, isMarkedSensitive: true));

    public void CopyPaths(IReadOnlyList<string> paths)
    {
        var files = LocalFileList.FromPaths(paths);
        if (files.Count > 0)
            _clipboard.Copy(ClipboardContent.FromFiles(files));
    }

    public Task<IReadOnlyList<string>> PasteFilesAsync(string folder) => _clipboard.PasteFilesAsync(folder);
}
