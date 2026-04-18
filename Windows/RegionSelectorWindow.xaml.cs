using System.Drawing;
using System.Windows;
using System.Windows.Media;
using ScreenshotOCR.Services;
using WpfRect = System.Windows.Rect;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ScreenshotOCR.Windows;

public partial class RegionSelectorWindow : Window
{
    private readonly System.Drawing.Bitmap _frozen;
    private readonly CaptureService.VirtualScreen _vs;
    private System.Windows.Point? _dragStart;
    private bool _confirmed;

    public Rectangle? SelectedRegion { get; private set; }
    public System.Drawing.Bitmap FrozenBitmap => _frozen;

    public RegionSelectorWindow(System.Drawing.Bitmap frozen, CaptureService.VirtualScreen vs)
    {
        InitializeComponent();

        _frozen = frozen;
        _vs = vs;

        // Use DIP (1/96 inch) coordinates for WPF. We'll base window on screen pixels using PerMonitorV2 awareness.
        // Place window at virtual screen origin with its exact pixel size mapped to DIPs at 96.
        // Since manifest declares PerMonitorV2, we need to convert.
        // Simplest: use WindowStartupLocation=Manual and size/position in DIPs computed from the primary DPI.

        var src = CaptureService.ToBitmapSource(frozen);
        FrozenImage.Source = src;

        Left = vs.X;
        Top = vs.Y;
        Width = vs.Width;
        Height = vs.Height;

        Loaded += (_, _) =>
        {
            OuterRect.Rect = new WpfRect(0, 0, ActualWidth, ActualHeight);
            Focus();
        };

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            _confirmed = false;
            Close();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        SelectionBorder.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;

        var p = e.GetPosition(this);
        var r = new WpfRect(_dragStart.Value, p);

        SelectionHole.Rect = r;

        SelectionBorder.Margin = new Thickness(r.X, r.Y, 0, 0);
        SelectionBorder.Width = r.Width;
        SelectionBorder.Height = r.Height;

        var pxRect = DipRectToPixels(r);
        SizeText.Text = $"{(int)pxRect.Width} × {(int)pxRect.Height}";
        SizeBox.Visibility = Visibility.Visible;
        SizeBox.Margin = new Thickness(r.X + r.Width + 8, r.Y + r.Height + 8, 0, 0);

        HintBox.Visibility = Visibility.Collapsed;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        ReleaseMouseCapture();

        var p = e.GetPosition(this);
        var r = new WpfRect(_dragStart.Value, p);
        _dragStart = null;

        if (r.Width < 4 || r.Height < 4)
        {
            SelectedRegion = null;
            _confirmed = false;
            Close();
            return;
        }

        var pxRect = DipRectToPixels(r);
        SelectedRegion = new Rectangle(
            (int)Math.Round(pxRect.X),
            (int)Math.Round(pxRect.Y),
            (int)Math.Round(pxRect.Width),
            (int)Math.Round(pxRect.Height));
        _confirmed = true;
        Close();
    }

    private WpfRect DipRectToPixels(WpfRect dip)
    {
        var src = PresentationSource.FromVisual(this);
        double sx = 1.0, sy = 1.0;
        if (src?.CompositionTarget is not null)
        {
            sx = src.CompositionTarget.TransformToDevice.M11;
            sy = src.CompositionTarget.TransformToDevice.M22;
        }
        return new WpfRect(dip.X * sx, dip.Y * sy, dip.Width * sx, dip.Height * sy);
    }

    public bool WasConfirmed => _confirmed;
}
