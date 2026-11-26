using Avalonia;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>Lampada spia tonda con alone: la rossa pulsa piano, la bianca resta ferma.</summary>
public sealed class Lamp : FxControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<Lamp, Color>(nameof(Color), Colors.Red);

    public static readonly StyledProperty<bool> PulseProperty =
        AvaloniaProperty.Register<Lamp, bool>(nameof(Pulse));

    static Lamp()
    {
        AffectsRender<Lamp>(ColorProperty, PulseProperty);
        PulseProperty.Changed.AddClassHandler<Lamp>((o, _) => o.SyncClock());
    }

    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public bool Pulse { get => GetValue(PulseProperty); set => SetValue(PulseProperty, value); }

    protected override bool Animated => Pulse;

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 6)
            return;

        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var level = Pulse ? 0.65 + 0.35 * (0.5 + 0.5 * Math.Sin(FxClock.Seconds * 2.2)) : 0.8;
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(150 * level), Color.R, Color.G, Color.B), 0),
                new GradientStop(Color.FromArgb(0, Color.R, Color.G, Color.B), 1),
            },
        };
        context.DrawEllipse(glow, null, c, size / 2, size / 2);

        var body = new RadialGradientBrush
        {
            Center = new RelativePoint(0.4, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Blend(Colors.White, Color, 0.55 + 0.25 * level), 0),
                new GradientStop(Blend(Colors.Black, Color, 0.45 + 0.55 * level), 0.75),
                new GradientStop(Blend(Colors.Black, Color, 0.25), 1),
            },
        };
        var r = size * 0.28;
        context.DrawEllipse(body, new Pen(new SolidColorBrush(Color.Parse("#0A0A0B")), 1), c, r, r);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
