using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Monocle.App.Diagnostics;
using Monocle.App.ViewModels;

namespace Monocle.App.Views;

public partial class MainWindow : Window
{
    private const double TileWidth = 200; // tile incl. padding/border

    private ListBox? _grid;

    public MainWindow()
    {
        InitializeComponent();
        _grid = this.FindControl<ListBox>("PhotoGrid");
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Vm is not null)
            Vm.Columns = Math.Max(1, (int)((e.NewSize.Width - 20) / TileWidth));
    }

    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PhotoTileViewModel tile } && Vm is not null)
            Vm.SelectedPhoto = tile;
    }

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

    private void OnCloseEnlargedClick(object? sender, RoutedEventArgs e) => Vm?.CloseEnlarged();

    private async void OpenFullscreen()
    {
        // async void event handler: an unguarded throw (preview decode) would escape to the sync
        // context as an unhandled exception and crash the app.
        try
        {
            if (Vm?.SelectedPhoto is { } tile)
            {
                var bmp = await Vm.GetDetailBitmapAsync(tile);
                if (bmp is not null)
                    Vm.OpenEnlarged(bmp);   // in-app overlay; keeps toolbar + grid visible
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't enlarge photo", ex);
            if (Vm is not null)
                Vm.StatusText = $"Couldn't enlarge photo: {ex.Message}";
        }
    }

    private void OnCropClick(object? sender, RoutedEventArgs e) => OpenCrop();

    private async void OpenCrop()
    {
        try
        {
            if (Vm?.SelectedPhoto is { } tile)
            {
                var bmp = await Vm.GetUncroppedBitmapAsync(tile);
                if (bmp is not null)
                    new CropWindow(bmp, tile.Item.Crop, crop => _ = Vm.ApplyCropAsync(tile, crop)).Show(this);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open crop editor", ex);
            if (Vm is not null)
                Vm.StatusText = $"Couldn't open crop editor: {ex.Message}";
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
            case Key.Escape when Vm.IsEnlarged: Vm.CloseEnlarged(); e.Handled = true; break;
            case Key.F:
                if (Vm.IsEnlarged) Vm.CloseEnlarged(); else OpenFullscreen();
                e.Handled = true;
                break;
            case Key.C: OpenCrop(); e.Handled = true; break;
            case Key.V: Vm.ToggleVariantCommand.Execute(null); e.Handled = true; break;
            case Key.OemOpenBrackets: Vm.RotateLeftCommand.Execute(null); e.Handled = true; break;
            case Key.OemCloseBrackets: Vm.RotateRightCommand.Execute(null); e.Handled = true; break;
            case Key.Left or Key.H: MoveSelection(-1); e.Handled = true; break;
            case Key.Right or Key.L: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-Math.Max(1, Vm.Columns)); e.Handled = true; break;
            case Key.Down: MoveSelection(Math.Max(1, Vm.Columns)); e.Handled = true; break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Vm is null || Vm.VisiblePhotos.Count == 0)
            return;
        var index = Vm.SelectedPhoto is null ? -1 : Vm.VisiblePhotos.IndexOf(Vm.SelectedPhoto);
        var next = Math.Clamp(index + delta, 0, Vm.VisiblePhotos.Count - 1);
        Vm.SelectedPhoto = Vm.VisiblePhotos[next];

        // Scroll the row containing the new selection into view (the ListBox virtualizes rows).
        var cols = Math.Max(1, Vm.Columns);
        var rowIndex = next / cols;
        if (rowIndex >= 0 && rowIndex < Vm.PhotoRows.Count)
            _grid?.ScrollIntoView(Vm.PhotoRows[rowIndex]);
    }

    protected override void OnClosed(EventArgs e)
    {
        Vm?.Cleanup();
        base.OnClosed(e);
    }
}
