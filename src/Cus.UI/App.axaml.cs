using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Cus.UI;

public partial class App : Application
{
    /// <summary>Impostato dal progetto eseguibile prima dell'avvio.</summary>
    public MainWindowModel? Model { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Model ?? MainWindowModel.Empty);

        base.OnFrameworkInitializationCompleted();
    }
}
