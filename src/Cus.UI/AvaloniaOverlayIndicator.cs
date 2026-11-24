using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>
/// Indicatore sempre in primo piano, una finestrella per monitor in alto a destra. Non ruba il
/// focus (la piattaforma lo rende non attivabile tramite <paramref name="configureWindow"/>) e
/// occupa solo la propria area: i clic altrove arrivano alle finestre sotto. Il doppio clic
/// sull'indicatore chiede di uscire dalla sessione: un clic singolo (per esempio mirando alla
/// barra di una finestra sotto) non fa nulla.
/// </summary>
public sealed class AvaloniaOverlayIndicator(Func<Screens?> screens, Action<Window>? configureWindow = null) : IOverlayIndicator
{
    private const int Margin = 12;

    private readonly List<Window> _windows = [];
    private SessionIndicatorState? _state;
    private Screens? _subscribedScreens;

    public event EventHandler? LeaveRequested;

    public void Show(SessionIndicatorState state) => Dispatcher.UIThread.Post(() =>
    {
        _state = state;
        Rebuild();
    });

    public void Hide() => Dispatcher.UIThread.Post(() =>
    {
        _state = null;
        CloseAll();
    });

    public void Dispose() => Hide();

    private void Rebuild()
    {
        CloseAll();
        if (_state is null || screens() is not { } all)
            return;

        if (!ReferenceEquals(_subscribedScreens, all))
        {
            all.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
            _subscribedScreens = all;
        }

        foreach (var screen in all.All)
        {
            var window = CreateWindow(_state);
            _windows.Add(window);
            // La dimensione definitiva arriva dopo il layout (SizeToContent): si riallinea a destra
            // a ogni cambio di dimensione.
            void Place(double width)
            {
                var scaling = screen.Scaling;
                var area = screen.WorkingArea;
                window.Position = new PixelPoint(
                    area.Right - (int)Math.Ceiling(width * scaling) - (int)(Margin * scaling),
                    area.Y + (int)(Margin * scaling));
            }

            window.SizeChanged += (_, e) => Place(e.NewSize.Width);
            Place(360);
            window.Show();
            configureWindow?.Invoke(window);
        }
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
    }

    private Window CreateWindow(SessionIndicatorState state)
    {
        var participants = string.Join(" · ", state.Participants.Select(p =>
            p.DisplayName + (p.IsHost ? " (host)" : "") + (p.Direction == ShareDirection.ReadOnly ? " (sola lettura)" : "")));

        var content = Skin.Chassis(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Skin.Lcd(new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        Skin.LcdText("● CLIPBOARD CONDIVISA", 13, bold: true),
                        new TextBlock { Text = participants, FontFamily = Skin.LcdFont, FontSize = 11, Foreground = Skin.InkBrush, MaxWidth = 340, TextWrapping = TextWrapping.Wrap },
                    },
                }),
                Skin.Engraved("doppio clic per uscire dalla sessione"),
            },
        }, cornerRadius: 12);
        content.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(content, "Doppio clic per uscire dalla sessione");
        content.PointerPressed += (_, e) =>
        {
            // Solo un doppio clic sinistro deliberato: i clic accidentali sull'angolo in alto a destra
            // (dove stanno i pulsanti delle finestre) non devono chiudere la condivisione.
            if (e.ClickCount >= 2 && e.GetCurrentPoint(content).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                LeaveRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        return new Window
        {
            Title = "CUS — clipboard condivisa",
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            HorizontalContentAlignment = HorizontalAlignment.Right,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
    }
}
