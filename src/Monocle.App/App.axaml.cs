using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Monocle.App.ViewModels;
using Monocle.App.Views;

namespace Monocle.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Optional: `Monocle.App <folder>` opens straight into a shoot and auto-scans.
            // Used by the /cull "launch the viewer" flow so ratings can be watched live.
            var arg = desktop.Args?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(arg) && System.IO.Directory.Exists(arg))
            {
                vm.FolderPath = arg;
                if (vm.ScanCommand.CanExecute(null))
                    vm.ScanCommand.Execute(null);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}