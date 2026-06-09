using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Monocle.Core.Model;

namespace Monocle.App.Views;

/// <summary>
/// Interactive crop editor (#25): drag on the full (uncropped, rotated) preview to select a
/// crop, optionally locked to an aspect ratio, with a rule-of-thirds overlay. Apply stores a
/// normalised crop; the original file is never modified.
/// </summary>
public partial class CropWindow : Window
{
    private readonly Bitmap? _bitmap;
    private readonly CropRect? _initialCrop;
    private readonly Action<CropRect?> _onApply;
    private double? _aspect;
    private Point _start;
    private bool _dragging;
    private bool _initialised;

    public CropWindow() : this(null, null, _ => { }) { }

    public CropWindow(Bitmap? bitmap, CropRect? initialCrop, Action<CropRect?> onApply)
    {
        InitializeComponent();
        _bitmap = bitmap;
        _initialCrop = initialCrop;
        _onApply = onApply;

        if (_bitmap is not null)
            Img.Source = _bitmap;

        AspectBox.ItemsSource = new[] { "Free", "1:1", "4:3", "3:2", "16:9" };
        AspectBox.SelectionChanged += (_, _) => _aspect = ParseAspect(AspectBox.SelectedItem as string);

        Overlay.PointerPressed += OnPressed;
        Overlay.PointerMoved += OnMoved;
        Overlay.PointerReleased += (_, _) => _dragging = false;
        Overlay.LayoutUpdated += (_, _) => InitOnce();
        KeyDown += OnKeyDown;
    }

    private void InitOnce()
    {
        if (_initialised || _bitmap is null || Overlay.Bounds.Width <= 0)
            return;
        _initialised = true;
        if (_initialCrop is { } c)
        {
            var ir = ImageRect();
            SetSelection(ir.X + c.X * ir.Width, ir.Y + c.Y * ir.Height, c.W * ir.Width, c.H * ir.Height);
        }
    }

    private Rect ImageRect()
    {
        if (_bitmap is null)
            return default;
        double cw = Overlay.Bounds.Width, ch = Overlay.Bounds.Height;
        double iw = _bitmap.Size.Width, ih = _bitmap.Size.Height;
        if (cw <= 0 || ch <= 0 || iw <= 0 || ih <= 0)
            return default;
        var scale = Math.Min(cw / iw, ch / ih);
        double dw = iw * scale, dh = ih * scale;
        return new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _start = Clamp(e.GetPosition(Overlay), ImageRect());
        _dragging = true;
        SetSelection(_start.X, _start.Y, 0, 0);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        var ir = ImageRect();
        var cur = Clamp(e.GetPosition(Overlay), ir);
        double dx = cur.X - _start.X, dy = cur.Y - _start.Y;
        double w = Math.Abs(dx), h = Math.Abs(dy);
        if (_aspect is { } a && a > 0)
            h = w / a;

        double x = dx >= 0 ? _start.X : _start.X - w;
        double y = dy >= 0 ? _start.Y : _start.Y - h;

        // Keep inside the image.
        x = Math.Clamp(x, ir.X, ir.Right);
        y = Math.Clamp(y, ir.Y, ir.Bottom);
        w = Math.Min(w, ir.Right - x);
        h = Math.Min(h, ir.Bottom - y);
        SetSelection(x, y, w, h);
    }

    private void SetSelection(double x, double y, double w, double h)
    {
        Canvas.SetLeft(Sel, x);
        Canvas.SetTop(Sel, y);
        Sel.Width = w;
        Sel.Height = h;
        Sel.IsVisible = w > 1 && h > 1;

        // Rule-of-thirds guides.
        foreach (var (line, vertical, frac) in new[] { (V1, true, 1.0 / 3), (V2, true, 2.0 / 3), (H1, false, 1.0 / 3), (H2, false, 2.0 / 3) })
        {
            line.IsVisible = Sel.IsVisible;
            if (vertical)
            {
                Canvas.SetLeft(line, x + w * frac);
                Canvas.SetTop(line, y);
                line.Height = h;
            }
            else
            {
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, y + h * frac);
                line.Width = w;
            }
        }
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!Sel.IsVisible)
        {
            Close();
            return;
        }
        var ir = ImageRect();
        if (ir.Width <= 0)
        {
            Close();
            return;
        }
        var crop = new CropRect(
            (Canvas.GetLeft(Sel) - ir.X) / ir.Width,
            (Canvas.GetTop(Sel) - ir.Y) / ir.Height,
            Sel.Width / ir.Width,
            Sel.Height / ir.Height).Normalized();
        _onApply(crop.IsFullFrame ? null : crop);
        Close();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _onApply(null);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter) OnApply(this, e);
    }

    private static Point Clamp(Point p, Rect r) =>
        new(Math.Clamp(p.X, r.X, r.Right), Math.Clamp(p.Y, r.Y, r.Bottom));

    private static double? ParseAspect(string? s) => s switch
    {
        "1:1" => 1.0,
        "4:3" => 4.0 / 3,
        "3:2" => 3.0 / 2,
        "16:9" => 16.0 / 9,
        _ => null,
    };
}
