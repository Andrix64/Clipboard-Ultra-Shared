using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cus.UI;

// Abbellimenti "scanner portatile da fantascienza": schermo CRT con linee di scansione e radar,
// viti, graffi, strisce di LED rossi, impugnatura a coste. Sono solo disegno: nessuno prende il
// mouse (IsHitTestVisible = false) e nessuno cambia il layout.

/// <summary>Orologio condiviso a ~15 fotogrammi al secondo: gira solo finché c'è almeno un abbellimento animato visibile.</summary>
internal static class FxClock
{
    private static readonly DispatcherTimer s_timer = CreateTimer();
    private static readonly System.Diagnostics.Stopwatch s_watch = System.Diagnostics.Stopwatch.StartNew();
    private static event Action? Ticked;
    private static int s_users;

    public static double Seconds => s_watch.Elapsed.TotalSeconds;

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        timer.Tick += (_, _) => Ticked?.Invoke();
        return timer;
    }

    public static void Subscribe(Action handler)
    {
        Ticked += handler;
        if (++s_users == 1)
            s_timer.Start();
    }

    public static void Unsubscribe(Action handler)
    {
        Ticked -= handler;
        if (--s_users <= 0)
        {
            s_users = 0;
            s_timer.Stop();
        }
    }

    /// <summary>Vero se il controllo si vede davvero (finestra aperta e non ridotta a icona): altrimenti non serve ridisegnare.</summary>
    public static bool IsShowing(Visual visual) =>
        visual.IsEffectivelyVisible && TopLevel.GetTopLevel(visual) is not Window { WindowState: WindowState.Minimized };
}

/// <summary>Base dei disegni animati: si iscrive all'orologio solo quando serve e solo mentre è nell'albero visuale.</summary>
public abstract class FxControl : Control
{
    private bool _subscribed;

    protected FxControl() => IsHitTestVisible = false;

    protected abstract bool Animated { get; }

    private void OnTick()
    {
        if (FxClock.IsShowing(this))
            InvalidateVisual();
    }

    protected void SyncClock()
    {
        var want = Animated && VisualRoot is not null;
        if (want == _subscribed)
            return;
        _subscribed = want;
        if (want)
            FxClock.Subscribe(OnTick);
        else
            FxClock.Unsubscribe(OnTick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed)
            FxClock.Unsubscribe(OnTick);
        _subscribed = false;
    }
}

/// <summary>
/// Vetro del monitor CRT da mettere sopra un display: linee di scansione, bordi scuri, riflesso
/// curvo e, se richiesto, ventaglio radar con linea di scansione e banda di aggiornamento.
/// </summary>
public sealed class CrtOverlay : FxControl
{
    public static readonly StyledProperty<bool> RadarProperty =
        AvaloniaProperty.Register<CrtOverlay, bool>(nameof(Radar));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<CrtOverlay, double>(nameof(CornerRadius), 8);

    /// <summary>Intensità delle linee di scansione (1 = normale).</summary>
    public static readonly StyledProperty<double> StrengthProperty =
        AvaloniaProperty.Register<CrtOverlay, double>(nameof(Strength), 1);

    static CrtOverlay()
    {
        AffectsRender<CrtOverlay>(RadarProperty, CornerRadiusProperty, StrengthProperty);
        RadarProperty.Changed.AddClassHandler<CrtOverlay>((o, _) => o.SyncClock());
    }

    public bool Radar { get => GetValue(RadarProperty); set => SetValue(RadarProperty, value); }

    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public double Strength { get => GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

    protected override bool Animated => Radar;

    private static readonly IBrush s_vignette = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = RelativeScalar.Parse("75%"),
        RadiusY = RelativeScalar.Parse("75%"),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
            new GradientStop(Color.FromArgb(105, 0, 0, 0), 1),
        },
    };

    private static readonly IBrush s_glare = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.6, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(58, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
        },
    };

    private static readonly IBrush s_band = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(26, 235, 255, 190), 0.5),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
        },
    };

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8)
            return;

        var area = new Rect(0, 0, w, h);
        using var clip = context.PushClip(new RoundedRect(area, CornerRadius));
        var t = FxClock.Seconds;

        if (Radar)
            DrawRadar(context, w, h, t);

        // Linee di scansione: una riga scura ogni tre pixel.
        var alpha = (byte)Math.Clamp(34 * Strength, 0, 255);
        var lines = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), 1);
        for (var y = 1.5; y < h; y += 3)
            context.DrawLine(lines, new Point(0, y), new Point(w, y));

        context.DrawRectangle(s_vignette, null, area);

        // Riflesso curvo del vetro, in alto a sinistra.
        var glare = new StreamGeometry();
        using (var g = glare.Open())
        {
            g.BeginFigure(new Point(0, 0), true);
            g.LineTo(new Point(w * 0.62, 0));
            g.QuadraticBezierTo(new Point(w * 0.30, h * 0.22), new Point(0, h * 0.42));
            g.EndFigure(true);
        }

        using (context.PushOpacity(Math.Clamp(Strength, 0, 1)))
            context.DrawGeometry(s_glare, null, glare);

        if (Radar)
        {
            // Banda di aggiornamento che scende lentamente, come sui vecchi monitor.
            var bandH = Math.Max(24, h * 0.22);
            var y = ((t * 0.16) % 1.5 - 0.25) * h;
            using var _ = context.PushTransform(Matrix.CreateTranslation(0, y));
            context.DrawRectangle(s_band, null, new Rect(0, 0, w, bandH));
        }
    }

    private static Point OnArc(Point origin, double radius, double degrees)
    {
        var r = degrees * Math.PI / 180;
        return new Point(origin.X + radius * Math.Cos(r), origin.Y + radius * Math.Sin(r));
    }

    /// <summary>Ventaglio radar in basso a destra (sullo schermo di riferimento: archi tratteggiati, linea piena esterna, lancetta).</summary>
    private static void DrawRadar(DrawingContext context, double w, double h, double t)
    {
        var origin = new Point(w * 0.8, h + 10);
        const double from = -140, to = -40;
        var ink = Color.FromArgb(46, 17, 36, 10);
        var thin = new Pen(new SolidColorBrush(Color.FromArgb(30, 17, 36, 10)), 1);
        var dashed = new Pen(new SolidColorBrush(ink), 1, new DashStyle([4, 4], 0));
        var bold = new Pen(new SolidColorBrush(Color.FromArgb(62, 17, 36, 10)), 2);

        // Bordi e raggi del ventaglio.
        var reach = h * 1.1;
        context.DrawLine(bold, origin, OnArc(origin, reach, from));
        context.DrawLine(bold, origin, OnArc(origin, reach, to));
        for (var a = from + 20; a < to; a += 20)
            context.DrawLine(thin, origin, OnArc(origin, reach, a));

        // Archi: due tratteggiati e uno pieno.
        foreach (var (radius, pen) in new[] { (h * 0.55, dashed), (h * 0.82, dashed), (h * 1.02, bold) })
            context.DrawGeometry(null, pen, Arc(origin, radius, from, to));

        // Lancetta che va avanti e indietro, con la scia.
        var phase = t / 3.2 % 2;
        var sweep = from + (to - from) * (phase < 1 ? phase : 2 - phase);
        var dir = phase < 1 ? 1 : -1;
        var wedge = new StreamGeometry();
        using (var g = wedge.Open())
        {
            g.BeginFigure(origin, true);
            g.LineTo(OnArc(origin, h * 1.02, sweep - dir * 16));
            g.ArcTo(OnArc(origin, h * 1.02, sweep), new Size(h * 1.02, h * 1.02), 0, false,
                dir > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(26, 17, 36, 10)), null, wedge);
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(95, 17, 36, 10)), 1.5), origin, OnArc(origin, h * 1.02, sweep));

        // Un puntino che pulsa dentro il ventaglio.
        var blip = OnArc(origin, h * 0.62, -76);
        var glow = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * 2.4));
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(130 * glow), 17, 36, 10)), null, blip, 2.6, 2.6);
    }

    private static StreamGeometry Arc(Point origin, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(OnArc(origin, radius, from), false);
        g.ArcTo(OnArc(origin, radius, to), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        g.EndFigure(false);
        return geometry;
    }
}

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
        AvaloniaProperty.Register<ChassisDecor, double>(nameof(CornerRadius), 12);

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

/// <summary>Impugnatura: file di coste di gomma nere, come sul manico dello scanner di riferimento.</summary>
public sealed class GripRidges : Control
{
    private static readonly IBrush s_rib = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#4A4F55"), 0),
            new GradientStop(Color.Parse("#0C0D0F"), 0.35),
            new GradientStop(Color.Parse("#050506"), 1),
        },
    };

    public GripRidges() => IsHitTestVisible = false;

    public override void Render(DrawingContext context)
    {
        const double rib = 5, gap = 3;
        var h = Bounds.Height;
        if (h < 4)
            return;
        for (var x = 0.0; x + rib <= Bounds.Width; x += rib + gap)
            context.DrawRectangle(s_rib, null, new RoundedRect(new Rect(x, 0, rib, h), 2.5));
    }
}
