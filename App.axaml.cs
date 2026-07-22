using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Googlook.Security;
using Googlook.ViewModels;
using Googlook.Views;

namespace Googlook;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vault = new ConfigVault();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(vault)
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
