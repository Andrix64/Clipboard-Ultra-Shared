using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Cus.UI;

/// <summary>
/// Testo tradotto nello XAML: <c>Text="{ui:Tr main.history}"</c>. Restituisce il testo nella
/// lingua corrente e, quando la lingua cambia, lo riscrive sulla proprietà di destinazione.
/// </summary>
/// <param name="key">Chiave nei file di lingua (vedi <see cref="Loc"/>).</param>
public sealed class Tr(string key) : MarkupExtension
{
    /// <summary>Destinazioni ancora vive da aggiornare al cambio di lingua.</summary>
    private static readonly List<(WeakReference<AvaloniaObject> Target, AvaloniaProperty Property, Tr Text)> s_targets = [];

    static Tr() => Loc.Changed += () => Dispatcher.UIThread.Post(UpdateAll);

    public string Key { get; } = key;

    public string Text => Loc.T(Key);

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            {
                TargetObject: AvaloniaObject target,
                TargetProperty: AvaloniaProperty property,
            })
        {
            lock (s_targets)
                s_targets.Add((new WeakReference<AvaloniaObject>(target), property, this));
        }

        return Text;
    }

    private static void UpdateAll()
    {
        lock (s_targets)
        {
            s_targets.RemoveAll(t => !t.Target.TryGetTarget(out _));
            foreach (var (reference, property, text) in s_targets)
                if (reference.TryGetTarget(out var target))
                    target.SetValue(property, text.Text);
        }
    }
}
