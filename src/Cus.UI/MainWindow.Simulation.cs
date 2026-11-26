using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Cus.Core.Abstractions;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ simulazione

    private void WireSimulationPage(ISimulationControls? simulation)
    {
        if (simulation is null)
            return;

        PageSimButton.IsVisible = true;
        SimCurrentText.Text = simulation.DescribeCurrent();
        simulation.ContentChanged += () => Post(() => SimCurrentText.Text = simulation.DescribeCurrent());
        SimCopyTextButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(SimTextInput.Text))
                simulation.CopyText(SimTextInput.Text);
        };
        SimCopySensitiveButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(SimTextInput.Text))
                simulation.CopySensitiveText(SimTextInput.Text);
        };
        SimCopyFilesButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = Loc.T("main.filesToCopy") });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimCopyFolderButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("main.folderToCopy") });
            var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                simulation.CopyPaths(paths);
        };
        SimPasteFilesButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("main.whereToPasteTheFiles") });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
                return;
            await Run(async () =>
            {
                try
                {
                    var written = await simulation.PasteFilesAsync(folder);
                    AddActivity(Loc.T("main.pastedFilesInto", written.Count, folder));
                }
                catch (RemoteFileUnavailableException ex)
                {
                    AddActivity(Loc.T("main.cannotPaste", ex.Message));
                }
            });
        };
    }
}
