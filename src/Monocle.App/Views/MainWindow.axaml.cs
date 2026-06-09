using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Monocle.App.ViewModels;

namespace Monocle.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PhotoTileViewModel tile } && Vm is not null)
        {
            Vm.SelectedPhoto = tile;
            OpenFullscreen();
        }
    }

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) => OpenFullscreen();

    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => OpenFullscreen();

    private async void OpenFullscreen()
    {
        if (Vm?.SelectedPhoto is { } tile)
        {
            var bmp = await Vm.GetDetailBitmapAsync(tile);
            if (bmp is not null)
                new FullscreenWindow(bmp).Show();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
            return;

        // Don't hijack typing in the notes box (except Escape).
        if (FocusManager?.GetFocusedElement() is TextBox && e.Key != Key.Escape)
            return;

        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: Vm.SetStarsCommand.Execute("1"); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: Vm.SetStarsCommand.Execute("2"); e.Handled = true; break;
            case Key.D3 or Key.NumPad3: Vm.SetStarsCommand.Execute("3"); e.Handled = true; break;
            case Key.D4 or Key.NumPad4: Vm.SetStarsCommand.Execute("4"); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: Vm.SetStarsCommand.Execute("0"); e.Handled = true; break;
            case Key.P: Vm.SetStarsCommand.Execute("4"); e.Handled = true; break;          // pick
            case Key.R or Key.X: Vm.SetStarsCommand.Execute("1"); e.Handled = true; break;  // reject
            case Key.F: OpenFullscreen(); e.Handled = true; break;
            case Key.OemOpenBrackets: Vm.RotateLeftCommand.Execute(null); e.Handled = true; break;
            case Key.OemCloseBrackets: Vm.RotateRightCommand.Execute(null); e.Handled = true; break;
            case Key.Left or Key.H: MoveSelection(-1); e.Handled = true; break;
            case Key.Right or Key.L: MoveSelection(1); e.Handled = true; break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Vm is null || Vm.VisiblePhotos.Count == 0)
            return;
        var index = Vm.SelectedPhoto is null ? -1 : Vm.VisiblePhotos.IndexOf(Vm.SelectedPhoto);
        var next = System.Math.Clamp(index + delta, 0, Vm.VisiblePhotos.Count - 1);
        Vm.SelectedPhoto = Vm.VisiblePhotos[next];   // ListBox scrolls the selection into view
    }

    protected override void OnClosed(System.EventArgs e)
    {
        Vm?.Cleanup();
        base.OnClosed(e);
    }
}
