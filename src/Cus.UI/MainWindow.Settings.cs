using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Cus.Core.Node;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ impostazioni

    private void WireSettingsPage(CusNode node)
    {
        var languageButtons = new List<(string Code, Button Button)>();
        foreach (var language in Loc.Available)
        {
            var button = new Button { Content = language.Name.ToUpper(CultureInfo.GetCultureInfo(language.Code)), Margin = new Thickness(0, 0, 6, 6) };
            button.Classes.Add("pill");
            button.Classes.Add("page");
            button.Click += (_, _) =>
            {
                // Salvata nel profilo: da qui in poi vale questa, non più quella dell'installer.
                node.SavedLanguage = language.Code;
                Loc.Current = language.Code;
            };
            languageButtons.Add((language.Code, button));
            LanguagesPanel.Children.Add(button);
        }

        void Update()
        {
            var on = node.KeepRunningInBackground;
            BackgroundToggle.Content = on ? Loc.T("main.yes") : Loc.T("main.no");
            BackgroundToggle.Classes.Set("active", on);
            var atLogin = node.LaunchAtLogin;
            LaunchAtLoginToggle.Content = atLogin ? Loc.T("main.yes") : Loc.T("main.no");
            LaunchAtLoginToggle.Classes.Set("active", atLogin);
            foreach (var (code, button) in languageButtons)
                button.Classes.Set("active", Loc.Current == code);
        }

        LanguageChanged += Update;

        BackgroundToggle.Click += (_, _) =>
        {
            node.KeepRunningInBackground = !node.KeepRunningInBackground;
            Update();
        };
        BackgroundToggle.IsEnabled = _model.HasTrayIcon;

        LaunchAtLoginToggle.Click += (_, _) =>
        {
            if (_model.AutoStart is not { } autoStart)
                return;
            var on = !node.LaunchAtLogin;
            try
            {
                if (on)
                    autoStart.Enable();
                else
                    autoStart.Disable();
                node.LaunchAtLogin = on;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Registro bloccato da criteri o antivirus: l'impostazione resta com'era.
                AddActivity("! " + Loc.T("main.launchAtLoginFailed", ex.Message));
            }
            Update();
        };
        // Solo il profilo predefinito con la clipboard reale: le istanze di prova non partono all'accesso.
        LaunchAtLoginToggle.IsEnabled = _model.AutoStart is not null;
        Update();

        async Task SaveName()
        {
            var applied = await node.SetDisplayNameAsync(NameInput.Text);
            // Vuoto: si torna al nome attuale; altrimenti si mostra quello ripulito.
            NameInput.Text = applied ?? node.Identity.DisplayName;
        }

        RenameButton.Click += async (_, _) => await RunFrom(RenameButton, SaveName);
        NameInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await RunFrom(RenameButton, SaveName);
        };
    }
}
