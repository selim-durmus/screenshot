using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotOCR.Models;
using ScreenshotOCR.Services;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using Rectangle = System.Windows.Shapes.Rectangle;
using Bitmap = System.Drawing.Bitmap;
using Cursors = System.Windows.Input.Cursors;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButton = System.Windows.Input.MouseButton;

namespace ScreenshotOCR.Windows;

public partial class ResultWindow : Window
{
    private readonly Bitmap _bitmap;
    private readonly OcrResult _ocr;
    private readonly List<WordHighlight> _highlights = new();
    private WpfPoint? _dragStart;
    private bool _didInitialSize;

    private record WordHighlight(OcrWord Word, Rectangle Hit, Rectangle Fill);

    public ResultWindow(Bitmap bitmap, OcrResult ocr)
    {
        InitializeComponent();
        _bitmap = bitmap;
        _ocr = ocr;

        CapturedImage.Source = CaptureService.ToBitmapSource(bitmap);

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => LayoutHighlights();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_didInitialSize) return;
        _didInitialSize = true;

        // Fit window to image aspect, max ~70% of working area.
        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.75;
        double maxH = wa.Height * 0.75;
        double imgAspect = _bitmap.Width / (double)_bitmap.Height;

        double chromeW = 64; // margins + padding
        double chromeH = 96; // title bar + margins

        double w = Math.Min(_bitmap.Width, maxW - chromeW) + chromeW;
        double h = (w - chromeW) / imgAspect + chromeH;

        if (h > maxH)
        {
            h = maxH;
            w = (h - chromeH) * imgAspect + chromeW;
        }

        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;

        Dispatcher.InvokeAsync(BuildHighlights, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BuildHighlights()
    {
        OverlayCanvas.Children.Clear();
        _highlights.Clear();

        foreach (var word in _ocr.Words)
        {
            // Semi-transparent hit target so user can see hover feedback.
            var hit = new Rectangle
            {
                Fill = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.IBeam,
                Tag = word
            };
            var fill = new Rectangle
            {
                Fill = (SolidColorBrush)FindResource("SelectionFill"),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                RadiusX = 2,
                RadiusY = 2
            };
            OverlayCanvas.Children.Add(fill);
            OverlayCanvas.Children.Add(hit);
            _highlights.Add(new WordHighlight(word, hit, fill));
        }

        LayoutHighlights();
    }

    private void LayoutHighlights()
    {
        if (_ocr.Words.Count == 0) return;
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();

        foreach (var h in _highlights)
        {
            double x = h.Word.X * scaleX + offsetX;
            double y = h.Word.Y * scaleY + offsetY;
            double w = h.Word.Width * scaleX;
            double hgt = h.Word.Height * scaleY;

            Canvas.SetLeft(h.Hit, x);
            Canvas.SetTop(h.Hit, y);
            h.Hit.Width = w;
            h.Hit.Height = hgt;

            Canvas.SetLeft(h.Fill, x);
            Canvas.SetTop(h.Fill, y);
            h.Fill.Width = w;
            h.Fill.Height = hgt;
        }
    }

    private (double scaleX, double scaleY, double offsetX, double offsetY) GetImageTransform()
    {
        // Image uses Stretch=Uniform; compute actual displayed rect inside ImageHost.
        double ctrlW = CapturedImage.ActualWidth;
        double ctrlH = CapturedImage.ActualHeight;
        if (ctrlW <= 0 || ctrlH <= 0) return (1, 1, 0, 0);

        double imgW = _bitmap.Width;
        double imgH = _bitmap.Height;
        double scale = Math.Min(ctrlW / imgW, ctrlH / imgH);
        double displayW = imgW * scale;
        double displayH = imgH * scale;
        double ox = (ctrlW - displayW) / 2;
        double oy = (ctrlH - displayH) / 2;
        return (scale, scale, ox, oy);
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayCanvas);
        ClearSelection();
        OverlayCanvas.CaptureMouse();
        UpdateSelection(_dragStart.Value, _dragStart.Value);
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        UpdateSelection(_dragStart.Value, e.GetPosition(OverlayCanvas));
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        OverlayCanvas.ReleaseMouseCapture();
        UpdateSelection(_dragStart.Value, e.GetPosition(OverlayCanvas));
        _dragStart = null;

        if (App.Settings.CopyToClipboardOnSelect)
            CopySelectionToClipboard(silent: true);
    }

    private void Overlay_RightUp(object sender, MouseButtonEventArgs e)
    {
        CopySelectionToClipboard(silent: false);
    }

    private void UpdateSelection(WpfPoint a, WpfPoint b)
    {
        var rect = new WpfRect(a, b);
        foreach (var h in _highlights)
        {
            var wordRect = new WpfRect(Canvas.GetLeft(h.Hit), Canvas.GetTop(h.Hit), h.Hit.Width, h.Hit.Height);
            bool selected = wordRect.IntersectsWith(rect);
            h.Fill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ClearSelection()
    {
        foreach (var h in _highlights) h.Fill.Visibility = Visibility.Collapsed;
    }

    private string BuildSelectedText()
    {
        var selected = _highlights
            .Where(h => h.Fill.Visibility == Visibility.Visible)
            .Select(h => h.Word)
            .OrderBy(w => w.LineIndex).ThenBy(w => w.WordIndex)
            .ToList();

        if (selected.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        int currentLine = selected[0].LineIndex;
        for (int i = 0; i < selected.Count; i++)
        {
            var w = selected[i];
            if (w.LineIndex != currentLine)
            {
                sb.Append('\n');
                currentLine = w.LineIndex;
            }
            else if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(w.Text);
        }
        return sb.ToString();
    }

    private void CopySelectionToClipboard(bool silent)
    {
        var text = BuildSelectedText();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!silent) CopyAll_Click(this, new RoutedEventArgs());
            return;
        }
        try { System.Windows.Clipboard.SetText(text); } catch { }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ocr.FullText)) return;
        try { System.Windows.Clipboard.SetText(_ocr.FullText); } catch { }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var src = CaptureService.ToBitmapSource(_bitmap);
            System.Windows.Clipboard.SetImage(src);
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            CopySelectionToClipboard(silent: false);
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            foreach (var h in _highlights) h.Fill.Visibility = Visibility.Visible;
        }
    }
}
