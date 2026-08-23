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
                vm.FolderPath = arg;

            // Otherwise reopen the last shoot (the constructor restores FolderPath from settings,
            // and only when that folder still exists). Everything the previous session computed —
            // metrics, EXIF, model scores, preview JPEGs — comes back out of .monocle-cache, so this
            // is a reload rather than a re-scan; it also picks up files added since (#1).
            if (vm.ScanCommand.CanExecute(null))
                vm.ScanCommand.Execute(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}