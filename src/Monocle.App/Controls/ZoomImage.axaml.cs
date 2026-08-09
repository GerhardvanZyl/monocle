using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Monocle.App.Controls;

/// <summary>
/// Reusable image viewer with fit-to-centre, mouse-wheel zoom (around the cursor) and drag-to-pan
/// (#6, #27). Used by the in-app enlarged pane. The image is pinned top-left and positioned purely
/// by a render-transform matrix, so <see cref="Fit"/> centres it reliably regardless of viewport
/// size — fixing the off-centre enlarge.
/// </summary>
public partial class ZoomImage : UserControl
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Raised on a genuine click (press+release under <see cref="ClickTolerance"/> pixels of
    /// movement) that started outside the photo itself — i.e. over the letterboxed/zoomed-out margin,
    /// never over the image. A drag that starts on the photo and happens to release past its edge is
    /// a pan, not a backdrop click, and must not raise this (checked at press time, not release time).</summary>
    public event EventHandler? BackdropClicked;

    private const double ClickTolerance = 4;

    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private bool _pressedOnImage;
    private Point _pressPoint;
    private Point _lastPointer;
    private bool _fitted;

    public ZoomImage()
    {
        InitializeComponent();

        Viewport.PointerWheelChanged += OnWheel;
        Viewport.PointerPressed += OnPressed;
        Viewport.PointerMoved += OnMoved;
        Viewport.PointerReleased += OnReleased;
        Viewport.LayoutUpdated += (_, _) => FitOnce();
    }

    static ZoomImage()
    {
        SourceProperty.Changed.AddClassHandler<ZoomImage>((c, _) => c.OnSourceChanged());
    }

    private void OnSourceChanged()
    {
        Img.Source = Source;
        Img.Width = Source?.Size.Width ?? 0;
        Img.Height = Source?.Size.Height ?? 0;
        _fitted = false;          // re-centre the new image on the next layout pass
        FitOnce();
    }

    private void FitOnce()
    {
        if (_fitted || Source is null || Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0)
            return;
        _fitted = true;
        var sx = Viewport.Bounds.Width / Source.Size.Width;
        var sy = Viewport.Bounds.Height / Source.Size.Height;
        _scale = System.Math.Min(sx, sy);
        _offsetX = (Viewport.Bounds.Width - Source.Size.Width * _scale) / 2;
        _offsetY = (Viewport.Bounds.Height - Source.Size.Height * _scale) / 2;
        Apply();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Source is null)
            return;
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
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _lastPointer = e.GetPosition(Viewport);
        _pressPoint = _lastPointer;
        // Decided once, at press time: whether this gesture started on the photo itself (Img) or on
        // the backdrop around it (the rest of Viewport — letterbox bars, or empty margin once zoomed
        // out). Where the pointer is later released doesn't change this, so a pan that starts on the
        // photo and releases past its edge is never mistaken for a backdrop click.
        _pressedOnImage = ReferenceEquals(e.Source, Img);
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

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        if (_pressedOnImage)
            return;
        var p = e.GetPosition(Viewport);
        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        if (dx * dx + dy * dy <= ClickTolerance * ClickTolerance)
            BackdropClicked?.Invoke(this, EventArgs.Empty);
    }

    private void Apply() =>
        Img.RenderTransform = new MatrixTransform(new Matrix(_scale, 0, 0, _scale, _offsetX, _offsetY));
}
