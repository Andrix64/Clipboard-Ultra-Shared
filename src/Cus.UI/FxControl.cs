using Avalonia;
using Avalonia.Controls;
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
