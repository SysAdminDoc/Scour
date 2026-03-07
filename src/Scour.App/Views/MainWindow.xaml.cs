using System.Windows;
using System.Windows.Input;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ApplyWindowSettings(this);
                if (vm.Scanners.Count > 0)
                    vm.ActiveScanner = vm.Scanners[0];
            }
        };

        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.SaveWindowSettings(this);
        };
    }

    // Custom title bar drag
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            if (WindowState == WindowState.Maximized)
            {
                var point = PointToScreen(e.GetPosition(this));
                var ratio = point.X / SystemParameters.PrimaryScreenWidth;

                WindowState = WindowState.Normal;

                Left = point.X - (Width * ratio);
                Top = point.Y - 20;
            }
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ScannerNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            rb.Tag is ScannerViewModel scannerVm &&
            DataContext is MainViewModel mainVm)
        {
            mainVm.ActiveScanner = scannerVm;
        }
    }

    // Drag-and-drop folder support
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0 && System.IO.Directory.Exists(files[0]))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0 && System.IO.Directory.Exists(files[0]) && DataContext is MainViewModel vm)
            {
                vm.SetRootPath(files[0]);
            }
        }
    }
}
