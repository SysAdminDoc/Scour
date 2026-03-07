using System.Windows;
using Scour.App.ViewModels;
using Scour.App.Views;

namespace Scour.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Handle --scan "path" from context menu integration
        if (e.Args.Length >= 2 && e.Args[0] == "--scan")
        {
            var scanPath = e.Args[1];
            var window = new MainWindow();
            if (window.DataContext is MainViewModel vm)
            {
                vm.SetRootPath(scanPath);
            }
            window.Show();
        }
    }
}
