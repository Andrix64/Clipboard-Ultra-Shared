using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Cus.UI;

/// <summary>
/// Skin "lettore retrò": telaio in metallo scuro, display LCD verde lime, pulsanti tondi lucidi.
/// Unica fonte di colori e pennelli: la usano sia gli stili XAML (x:Static) sia le finestre
/// costruite in codice (indicatore, consenso, notifiche).
/// </summary>
public static class Skin
{
    public static readonly Color Lime = Color.Parse("#9CF02A");
    public static readonly Color LimeBright = Color.Parse("#D6FF6E");
    public static readonly Color LimeDeep = Color.Parse("#3F7A06");
    public static readonly Color Ink = Color.Parse("#11240A");
    public static readonly Color Metal = Color.Parse("#3A3E43");
    public static readonly Color MetalEdge = Color.Parse("#121417");
    public static readonly Color Engraving = Color.Parse("#C4CCBA");
    public static readonly Color Alert = Color.Parse("#FF5A3C");

    public static readonly FontFamily LcdFont = new("Cascadia Mono, Consolas, Courier New, monospace");

    /// <summary>
    /// Font Awesome Free (solido, licenza SIL OFL, Assets/fa-solid-900.ttf): tutte le icone dell'interfaccia.
    /// I glifi stanno nell'area d'uso privato di Unicode, vedi <see cref="Icons"/>.
    /// </summary>
    public static readonly FontFamily IconFont = new("avares://Cus.UI/Assets#Font Awesome 6 Free");

    /// <summary>Icona delle finestre che compaiono nella barra delle applicazioni.</summary>
    public static WindowIcon AppIcon => s_appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://Cus.UI/Assets/cus.ico")));
    private static WindowIcon? s_appIcon;

    public static IBrush ChassisBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#5B6168"), 0),
            new GradientStop(Color.Parse("#3B3F44"), 0.08),
            new GradientStop(Color.Parse("#2C2F33"), 0.55),
            new GradientStop(Color.Parse("#3A3E43"), 1),
        },
    };

    public static IBrush ChassisEdgeBrush { get; } = new SolidColorBrush(MetalEdge);

    public static IBrush BevelBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#7A8088"), 0), new GradientStop(Color.Parse("#1C1E21"), 1) },
    };

    /// <summary>Display principale: lime acceso con alone verso il basso.</summary>
    public static IBrush LcdBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#C8F55A"), 0),
            new GradientStop(Color.Parse("#8ED31F"), 0.45),
            new GradientStop(Color.Parse("#5FA20E"), 1),
        },
    };

    /// <summary>Pannelli secondari (liste): LCD spento, scritte lime.</summary>
    public static IBrush DarkLcdBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#16240C"), 0), new GradientStop(Color.Parse("#0B1406"), 1) },
    };

    public static IBrush LimeGlossBrush { get; } = new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.38, 0.3, RelativeUnit.Relative),
        Center = new RelativePoint(0.45, 0.4, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#EAFFA0"), 0),
            new GradientStop(Color.Parse("#A6EE2C"), 0.35),
            new GradientStop(Color.Parse("#5EA40C"), 0.8),
            new GradientStop(Color.Parse("#2F5C03"), 1),
        },
    };

    public static IBrush LimeGlossHoverBrush { get; } = new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.38, 0.3, RelativeUnit.Relative),
        Center = new RelativePoint(0.45, 0.4, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F6FFC8"), 0),
            new GradientStop(Color.Parse("#C2FF4A"), 0.35),
            new GradientStop(Color.Parse("#74C214"), 0.8),
            new GradientStop(Color.Parse("#3A6E05"), 1),
        },
    };

    public static IBrush HighlightBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(170, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
    };

    public static IBrush PillBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#C9FA5C"), 0), new GradientStop(Color.Parse("#6CB812"), 1) },
    };

    public static IBrush PillOffBrush { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#4B5056"), 0), new GradientStop(Color.Parse("#26292D"), 1) },
    };

    public static IBrush LimeTextBrush { get; } = new SolidColorBrush(Lime);

    public static IBrush InkBrush { get; } = new SolidColorBrush(Ink);

    public static IBrush EngravingBrush { get; } = new SolidColorBrush(Engraving);

    public static IBrush AlertBrush { get; } = new SolidColorBrush(Alert);

    /// <summary>Telaio con bordo scuro e riflesso in alto, per le finestre costruite in codice.</summary>
    public static Border Chassis(Control child, double cornerRadius = 14) => new()
    {
        Background = ChassisBrush,
        BorderBrush = ChassisEdgeBrush,
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(cornerRadius),
        Padding = new Thickness(8),
        BoxShadow = BoxShadows.Parse("0 4 14 0 #99000000"),
        Child = child,
    };

    /// <summary>Display LCD lime con testo scuro.</summary>
    public static Border Lcd(Control child, bool dark = false) => new()
    {
        Background = dark ? DarkLcdBrush : LcdBrush,
        BorderBrush = new SolidColorBrush(Color.Parse("#0C1505")),
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6),
        BoxShadow = BoxShadows.Parse("inset 0 2 6 0 #66000000"),
        Child = child,
    };

    /// <summary>I glifi di <see cref="Icons"/> (area d'uso privato) si disegnano con Font Awesome, il resto col font del testo.</summary>
    public static bool IsIcon(char c) => c is >= '\uE000' and <= '\uF8FF';

    /// <summary>Imposta il testo di un blocco: le icone nel testo diventano parti scritte in Font Awesome.</summary>
    public static void SetRich(TextBlock block, string? text)
    {
        block.Inlines?.Clear();
        if (string.IsNullOrEmpty(text) || !text.Any(IsIcon))
        {
            block.Text = text;
            return;
        }

        block.Text = null;
        var inlines = block.Inlines ??= new InlineCollection();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            // Un'icona è un pezzo a sé; il testo tra due icone è un pezzo unico.
            var boundary = i == text.Length || IsIcon(text[i]) || (i > start && IsIcon(text[start]));
            if (!boundary)
                continue;
            if (i > start)
            {
                var piece = text[start..i];
                inlines.Add(IsIcon(piece[0])
                    ? new Run(piece) { FontFamily = IconFont, FontWeight = FontWeight.Black }
                    : new Run(piece));
            }

            start = i;
        }
    }

    /// <summary>Testo senza icone, per i dialoghi che mostrano il messaggio per intero.</summary>
    public static string PlainText(TextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
            return block.Text ?? "";
        return new string(string.Concat(inlines.OfType<Run>().Select(r => r.Text)).Where(c => !IsIcon(c)).ToArray()).Trim();
    }

    /// <summary>Contenuto di un pulsante o di una voce di menu: testo, con le icone in Font Awesome.</summary>
    public static void SetContent(ContentControl control, string text) => control.Content = Rich(text);

    /// <summary>Intestazione di una voce di menu, con le icone in Font Awesome.</summary>
    public static void SetHeader(MenuItem control, string text) => control.Header = Rich(text);

    private static object Rich(string text)
    {
        if (!text.Any(IsIcon))
            return text;
        var block = new TextBlock();
        SetRich(block, text);
        return block;
    }

    public static TextBlock LcdText(string text, double size = 13, bool bold = false, bool dark = false)
    {
        var block = new TextBlock
        {
            FontFamily = LcdFont,
            FontSize = size,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            Foreground = dark ? LimeTextBrush : InkBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        SetRich(block, text);
        return block;
    }

    public static TextBlock Engraved(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        LetterSpacing = 1.5,
        Foreground = EngravingBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Pulsante a pastiglia lime (stile "PRESETS").</summary>
    public static Button Pill(string text)
    {
        var button = new Button();
        SetContent(button, text.ToUpperInvariant());
        button.Classes.Add("pill");
        return button;
    }
}
