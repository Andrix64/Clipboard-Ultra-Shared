using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cus.UI;

/// <summary>
/// Icona dell'applicazione con un numerino in basso a destra: quanti utenti di CUS sono visibili in rete.
/// La usano la finestra principale (quindi la barra delle applicazioni) e l'icona nell'area di notifica.
/// </summary>
internal static class AppIconBadge
{
    private const int Size = 64;

    /// <summary>Oltre questo numero si scrive "9+": nel tondino non ci stanno più cifre leggibili.</summary>
    private const int MaxShown = 9;

    private static readonly Dictionary<int, WindowIcon> s_cache = [];
    private static readonly IBrush s_badgeBrush = new SolidColorBrush(Color.Parse("#E5261A"));
    private static Bitmap? s_base;
    private static bool s_baseFailed;

    /// <summary>L'icona normale con 0 (o se l'icona di base non si lascia ridisegnare); altrimenti col numero.</summary>
    public static WindowIcon For(int count)
    {
        if (count <= 0)
            return Skin.AppIcon;

        count = Math.Min(count, MaxShown + 1);
        if (s_cache.TryGetValue(count, out var cached))
            return cached;
        if (Base() is not { } image)
            return Skin.AppIcon;

        var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(image, new Rect(0, 0, Size, Size));

            var text = count > MaxShown ? $"{MaxShown}+" : count.ToString(CultureInfo.InvariantCulture);
            const double radius = 18;
            var center = new Point(Size - radius - 1, Size - radius - 1);
            context.DrawEllipse(s_badgeBrush, new Pen(Brushes.White, 3), center, radius, radius);

            var label = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                text.Length > 1 ? 19 : 26,
                Brushes.White);
            context.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
        }

        // La bitmap resta viva quanto l'icona (in cache): non si libera.
        var icon = new WindowIcon(target);
        s_cache[count] = icon;
        return icon;
    }

    private static Bitmap? Base()
    {
        if (s_base is not null || s_baseFailed)
            return s_base;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Cus.UI/Assets/cus.ico"));
            s_base = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            // Senza decodifica dell'.ico si resta all'icona normale: il numero è un di più.
            s_baseFailed = true;
        }

        return s_base;
    }
}
