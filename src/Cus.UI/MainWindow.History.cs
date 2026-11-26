using Avalonia.Controls;
using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ cronologia

    private void WireHistoryPage(CusNode node)
    {
        RefreshHistoryButton.Click += async (_, _) => await RefreshHistoryAsync();
        UseHistoryButton.Click += async (_, _) =>
        {
            if (HistoryList.SelectedIndex is not (var i and >= 0) || i >= _history.Count)
            {
                _model.Notifier?.Info(Loc.T("main.pickAHistoryEntryThenPress"));
                return;
            }

            // La voce si fissa prima dell'attesa: nel frattempo la lista può essere ricaricata.
            var entry = _history[i];
            await RunFrom(UseHistoryButton, async () =>
            {
                await node.UseHistoryEntryAsync(entry);
                AddActivity(Loc.T("main.copiedFromHistory", Describe(entry)));
            });
        };
        node.CurrentClipboardChanged += (_, _) => Post(() =>
        {
            if (HistoryPage.IsVisible)
                _ = RefreshHistoryAsync();
        });
    }

    private async Task RefreshHistoryAsync()
    {
        if (_model.Node is not { } node)
            return;
        HistoryTitle.Text = node.Session?.Role == SessionRole.Member
            ? Loc.T("main.copiesInThisSessionFromThe")
            : Loc.T("main.lastCopiesHostedHere");
        await Run(async () =>
        {
            var entries = await node.GetHistoryAsync();
            _history.Clear();
            _history.AddRange(entries);
            // Le voci (nomi e testi) possono arrivare da altri PC: niente a capo né caratteri bidi.
            HistoryList.ItemsSource = _history.Select(h =>
                $"{h.CreatedUtc.ToLocalTime():HH:mm} {DisplayText.Sanitize(h.OriginName, 24)}: {Describe(h)}").ToList();
        });
    }
}
