using Avalonia;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>Colonna di LED rossi sul bordo dello schermo; uno più acceso scorre con una piccola coda.</summary>
public sealed class LedStrip : FxControl
{
    public static readonly StyledProperty<bool> RunProperty =
        AvaloniaProperty.Register<LedStrip, bool>(nameof(Run), true);

    static LedStrip()
    {
        AffectsRender<LedStrip>(RunProperty);
        RunProperty.Changed.AddClassHandler<LedStrip>((o, _) => o.SyncClock());
    }

    /// <summary>Spenta, la colonna resta ferma con i LED fiocamente accesi.</summary>
    public bool Run { get => GetValue(RunProperty); set => SetValue(RunProperty, value); }

    protected override bool Animated => Run;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        const double pitch = 12;
        var count = (int)(h / pitch);
        if (count < 2 || w < 4)
            return;

        var cx = w / 2;
        var head = Run ? (int)(FxClock.Seconds * 5) % (count + 6) : -10;
        var top = (h - (count - 1) * pitch) / 2;
        for (var i = 0; i < count; i++)
        {
            var y = top + i * pitch;
            // Distanza dietro alla testa che scorre: 0 = acceso al massimo, poi si spegne.
            var behind = head - i;
            var level = behind is >= 0 and <= 3 ? 1 - behind / 4.0 : 0;
            // Sempre un lieve bagliore di fondo, più forte in alto e in basso come sulla cornice di riferimento.
            level = Math.Max(level, i % 4 == 0 ? 0.35 : 0.12);

            if (level > 0.3)
            {
                context.DrawEllipse(
                    new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb((byte)(110 * level), 255, 60, 40), 0),
                            new GradientStop(Color.FromArgb(0, 255, 60, 40), 1),
                        },
                    },
                    null, new Point(cx, y), 5, 5);
            }

            var core = Color.FromArgb(255,
                (byte)(58 + 197 * level), (byte)(21 + 40 * level), (byte)(18 + 24 * level));
            context.DrawEllipse(new SolidColorBrush(core), new Pen(new SolidColorBrush(Color.Parse("#0A0A0B")), 0.8),
                new Point(cx, y), 2.1, 2.1);
        }
    }
}
