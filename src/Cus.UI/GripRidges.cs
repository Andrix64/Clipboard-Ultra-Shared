using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cus.UI;

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
