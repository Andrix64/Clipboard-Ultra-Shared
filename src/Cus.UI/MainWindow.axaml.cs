using Avalonia.Controls;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

public partial class MainWindow : Window
{
    private readonly MainWindowModel _model;

    /// <summary>Usato solo dal designer di Avalonia.</summary>
    public MainWindow()
        : this(MainWindowModel.Empty)
    {
    }

    public MainWindow(MainWindowModel model)
    {
        _model = model;
        InitializeComponent();

        Title = model.Title;
        InfoText.Text = string.Join(Environment.NewLine, model.InfoLines);

        if (model.SimulateCopy is { } simulateCopy)
        {
            SimulatedPanel.IsVisible = true;
            CopyButton.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(CopyInput.Text))
                    simulateCopy(CopyInput.Text);
            };
        }

        if (model.Clipboard is { } clipboard)
        {
            clipboard.Changed += OnClipboardChanged;
            clipboard.Start();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model.Clipboard is { } clipboard)
            clipboard.Changed -= OnClipboardChanged;
        base.OnClosed(e);
    }

    private void OnClipboardChanged(object? sender, ClipboardChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => CurrentText.Text = Describe(e.Content));

    private static string Describe(ClipboardContent content) => content.Kind switch
    {
        ClipboardContentKind.Text => content.IsMarkedSensitive ? "(testo sensibile, non condiviso)" : content.Text ?? "",
        ClipboardContentKind.Image => $"(immagine, {content.ImagePng?.Length ?? 0} byte)",
        ClipboardContentKind.Files => $"({content.Files?.Count ?? 0} file)",
        _ => "(contenuto sconosciuto)",
    };
}
