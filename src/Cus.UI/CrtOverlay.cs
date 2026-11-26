using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Cus.UI;

/// <summary>Un pallino del radar. <paramref name="Key"/> è l'identità (ID dispositivo): decide dove sta.</summary>
public sealed record RadarBlip(string Key, string Label, bool Connected);

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

    /// <summary>Pallini del radar: uno per ogni dispositivo visto in rete o collegato.</summary>
    public static readonly StyledProperty<IReadOnlyList<RadarBlip>?> BlipsProperty =
        AvaloniaProperty.Register<CrtOverlay, IReadOnlyList<RadarBlip>?>(nameof(Blips));

    static CrtOverlay()
    {
        AffectsRender<CrtOverlay>(RadarProperty, CornerRadiusProperty, StrengthProperty, BlipsProperty);
        RadarProperty.Changed.AddClassHandler<CrtOverlay>((o, _) => o.SyncClock());
    }

    public IReadOnlyList<RadarBlip>? Blips { get => GetValue(BlipsProperty); set => SetValue(BlipsProperty, value); }

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
            DrawRadar(context, w, h, t, Blips);

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
    private static void DrawRadar(DrawingContext context, double w, double h, double t, IReadOnlyList<RadarBlip>? blips)
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

        if (blips is null || blips.Count == 0)
            return;

        // Un pallino per dispositivo. La posizione deriva dall'identità: lo stesso dispositivo sta sempre
        // nello stesso punto e due dispositivi non finiscono mai uno sull'altro. Il pallino si accende
        // quando la lancetta lo ha appena superato e poi svanisce, come su un radar vero.
        var placed = new List<Point>();
        foreach (var blip in blips.OrderBy(b => b.Connected))
        {
            var spot = Place(origin, h, from, to, blip.Key, placed, out var angle);
            placed.Add(spot);

            var behind = (sweep - angle) * dir;
            var flash = behind >= 0 ? Math.Exp(-behind / 28) : 0;
            var glow = 0.4 + 0.6 * flash;
            IBrush Ink(double a) => new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(a, 0, 255), 17, 36, 10));

            if (!blip.Connected)
            {
                context.DrawEllipse(Ink(130 * glow), null, spot, 2.6, 2.6);
                continue;
            }

            // Collegato: disco pieno con anello e l'iniziale del nome accanto, così si distingue dagli altri.
            context.DrawEllipse(Ink(30 + 40 * flash), new Pen(Ink(190), 1.2), spot, 7, 7);
            context.DrawEllipse(Ink(235), null, spot, 3.6, 3.6);
            if (blip.Label.Length == 0)
                continue;
            var text = new FormattedText(blip.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10, Ink(215));
            context.DrawText(text, new Point(spot.X + 9, spot.Y - text.Height / 2));
        }
    }

    /// <summary>Punto stabile nel ventaglio per una chiave, spostato se cade troppo vicino a uno già piazzato.</summary>
    private static Point Place(Point origin, double h, double from, double to, string key, List<Point> placed, out double angle)
    {
        var hash = Fnv(key);
        var a = 0.08 + 0.84 * ((hash & 0xFFFF) / 65535.0);
        var r = 0.30 + 0.62 * (((hash >> 16) & 0xFFFF) / 65535.0);
        var point = default(Point);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            angle = from + (to - from) * a;
            point = OnArc(origin, h * r, angle);
            if (placed.All(p => Math.Abs(p.X - point.X) > 16 || Math.Abs(p.Y - point.Y) > 16))
                return point;
            r = 0.30 + (r - 0.30 + 0.17) % 0.62;
            a = 0.08 + (a - 0.08 + 0.11) % 0.84;
        }

        angle = from + (to - from) * a;
        return OnArc(origin, h * r, angle);
    }

    /// <summary>Hash stabile (string.GetHashCode cambia a ogni avvio).</summary>
    private static uint Fnv(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key)
            hash = (hash ^ c) * 16777619u;
        return hash;
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
