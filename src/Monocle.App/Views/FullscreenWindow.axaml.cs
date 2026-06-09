using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Monocle.App.Views;

/// <summary>
/// Borderless fullscreen image viewer with mouse-wheel zoom (around the cursor) and
/// drag-to-pan (#6, #27). Esc, F or double-click closes it.
/// </summary>
public partial class FullscreenWindow : Window
{
    private readonly Bitmap? _bitmap;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private Point _lastPointer;
    private bool _fitted;

    public FullscreenWindow() : this(null) { }

    public FullscreenWindow(Bitmap? bitmap)
    {
        InitializeComponent();
        _bitmap = bitmap;
        if (_bitmap is not null)
            Img.Source = _bitmap;

        Img.Width = _bitmap?.Size.Width ?? 0;
        Img.Height = _bitmap?.Size.Height ?? 0;

        KeyDown += OnKeyDown;
        DoubleTapped += (_, _) => Close();
        Viewport.PointerWheelChanged += OnWheel;
        Viewport.PointerPressed += OnPressed;
        Viewport.PointerMoved += OnMoved;
        Viewport.PointerReleased += (_, _) => _dragging = false;
        Viewport.LayoutUpdated += (_, _) => FitOnce();
    }

    private void FitOnce()
    {
        if (_fitted || _bitmap is null || Viewport.Bounds.Width <= 0)
            return;
        _fitted = true;
        var sx = Viewport.Bounds.Width / _bitmap.Size.Width;
        var sy = Viewport.Bounds.Height / _bitmap.Size.Height;
        _scale = System.Math.Min(sx, sy);
        _offsetX = (Viewport.Bounds.Width - _bitmap.Size.Width * _scale) / 2;
        _offsetY = (Viewport.Bounds.Height - _bitmap.Size.Height * _scale) / 2;
        Apply();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var cursor = e.GetPosition(Viewport);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var newScale = System.Math.Clamp(_scale * factor, 0.05, 40);

        // Keep the world point under the cursor fixed while zooming.
        var worldX = (cursor.X - _offsetX) / _scale;
        var worldY = (cursor.Y - _offsetY) / _scale;
        _offsetX = cursor.X - worldX * newScale;
        _offsetY = cursor.Y - worldY * newScale;
        _scale = newScale;
        Apply();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _lastPointer = e.GetPosition(Viewport);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        var p = e.GetPosition(Viewport);
        _offsetX += p.X - _lastPointer.X;
        _offsetY += p.Y - _lastPointer.Y;
        _lastPointer = p;
        Apply();
    }

    private void Apply() =>
        Img.RenderTransform = new MatrixTransform(new Matrix(_scale, 0, 0, _scale, _offsetX, _offsetY));

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F)
            Close();
    }
}
