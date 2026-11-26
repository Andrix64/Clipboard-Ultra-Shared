using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>Lato della scocca su cui mettere le viti (le altre sono coperte dal corpo vicino).</summary>
public enum ScrewSide
{
    None,
    Left,
    Right,
    Both,
}

/// <summary>Usura della scocca: viti con taglio, graffi, righe di spazzolatura e un filo di luce sul bordo.</summary>
public sealed class ChassisDecor : Control
{
    public static readonly StyledProperty<ScrewSide> ScrewsProperty =
        AvaloniaProperty.Register<ChassisDecor, ScrewSide>(nameof(Screws), ScrewSide.Both);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ChassisDecor, double>(nameof(CornerRadius), 5);

    public static readonly StyledProperty<int> SeedProperty =
        AvaloniaProperty.Register<ChassisDecor, int>(nameof(Seed), 1);

    static ChassisDecor() => AffectsRender<ChassisDecor>(ScrewsProperty, CornerRadiusProperty, SeedProperty);

    public ChassisDecor() => IsHitTestVisible = false;

    public ScrewSide Screws { get => GetValue(ScrewsProperty); set => SetValue(ScrewsProperty, value); }

    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>Cambia la disposizione dei graffi, così i tre corpi non sembrano copie.</summary>
    public int Seed { get => GetValue(SeedProperty); set => SetValue(SeedProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40)
            return;

        var area = new Rect(0, 0, w, h);
        using var clip = context.PushClip(new RoundedRect(area, CornerRadius));

        // Spazzolatura: righe orizzontali quasi invisibili.
        var brushed = new Pen(new SolidColorBrush(Color.FromArgb(7, 255, 255, 255)), 1);
        for (var y = 2.5; y < h; y += 3)
            context.DrawLine(brushed, new Point(0, y), new Point(w, y));

        // Graffi e segni d'uso.
        var rng = new Random(Seed * 7919 + 13);
        for (var i = 0; i < 26; i++)
        {
            var x = rng.NextDouble() * w;
            var y = rng.NextDouble() * h;
            var len = 6 + rng.NextDouble() * 34;
            var angle = (rng.NextDouble() - 0.5) * 0.7 + (rng.Next(4) == 0 ? Math.PI / 2 : 0);
            var light = i % 3 != 0;
            var color = light
                ? Color.FromArgb((byte)(16 + rng.Next(26)), 255, 255, 255)
                : Color.FromArgb((byte)(30 + rng.Next(30)), 0, 0, 0);
            context.DrawLine(new Pen(new SolidColorBrush(color), light ? 0.8 : 1),
                new Point(x, y), new Point(x + len * Math.Cos(angle), y + len * Math.Sin(angle)));
        }

        // Bordo interno: ombra sotto, luce sopra.
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.5),
            new RoundedRect(new Rect(1.5, 1.5, w - 3, h - 3), Math.Max(0, CornerRadius - 1.5)));
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1),
            new Point(CornerRadius, 2.5), new Point(w - CornerRadius, 2.5));

        if (Screws == ScrewSide.None)
            return;
        const double inset = 8.5;
        var n = 0;
        foreach (var (x, y, side) in new[]
                 {
                     (inset, inset, ScrewSide.Left), (inset, h - inset, ScrewSide.Left),
                     (w - inset, inset, ScrewSide.Right), (w - inset, h - inset, ScrewSide.Right),
                 })
        {
            n++;
            if (Screws == ScrewSide.Both || Screws == side)
                DrawScrew(context, new Point(x, y), (n * 37 + Seed * 11) % 180);
        }
    }

    private static void DrawScrew(DrawingContext context, Point c, double slotDegrees)
    {
        const double r = 3.6;
        var head = new RadialGradientBrush
        {
            Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#9AA0A6"), 0),
                new GradientStop(Color.Parse("#4C5156"), 0.7),
                new GradientStop(Color.Parse("#25282B"), 1),
            },
        };
        context.DrawEllipse(Brushes.Black, null, new Point(c.X + 0.4, c.Y + 0.8), r + 0.6, r + 0.6);
        context.DrawEllipse(head, new Pen(new SolidColorBrush(Color.Parse("#0E0F11")), 1), c, r, r);
        var a = slotDegrees * Math.PI / 180;
        var d = new Point(Math.Cos(a) * (r - 0.8), Math.Sin(a) * (r - 0.8));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#0E0F11")), 1.3),
            new Point(c.X - d.X, c.Y - d.Y), new Point(c.X + d.X, c.Y + d.Y));
    }
}
