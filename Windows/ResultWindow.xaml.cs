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
    // A single character's rect in image-pixel space.
    private record Cell(
        char Ch,
        int LineIndex,
        int WordIndex,
        double X, double Y, double W, double H,
        bool IsLastInWord);

    // A caret position between two adjacent cells in reading order. 0..Count.
    private readonly Bitmap _bitmap;
    private readonly OcrResult _ocr;
    private readonly List<Cell> _cells = new();
    private readonly List<Rectangle> _bandPool = new();

    private int _anchor = -1;
    private int _cursor = -1;
    private bool _dragging;
    private bool _didInitialSize;

    public ResultWindow(Bitmap bitmap, OcrResult ocr)
    {
        InitializeComponent();
        _bitmap = bitmap;
        _ocr = ocr;
        CapturedImage.Source = CaptureService.ToBitmapSource(bitmap);

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => RenderSelection();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_didInitialSize) return;
        _didInitialSize = true;

        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.75;
        double maxH = wa.Height * 0.75;
        double imgAspect = _bitmap.Width / (double)_bitmap.Height;
        double chromeW = 64, chromeH = 96;

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

        Dispatcher.InvokeAsync(BuildCells, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BuildCells()
    {
        _cells.Clear();
        foreach (var word in _ocr.Words.OrderBy(w => w.LineIndex).ThenBy(w => w.WordIndex))
        {
            int n = Math.Max(1, word.Text.Length);
            double charW = word.Width / n;
            for (int i = 0; i < word.Text.Length; i++)
            {
                _cells.Add(new Cell(
                    word.Text[i],
                    word.LineIndex,
                    word.WordIndex,
                    word.X + i * charW,
                    word.Y,
                    charW,
                    word.Height,
                    IsLastInWord: i == word.Text.Length - 1));
            }
        }
    }

    // --- Coordinate math ---------------------------------------------------

    private (double scale, double offsetX, double offsetY) ImageTransform()
    {
        double ctrlW = CapturedImage.ActualWidth;
        double ctrlH = CapturedImage.ActualHeight;
        if (ctrlW <= 0 || ctrlH <= 0) return (1, 0, 0);

        double imgW = _bitmap.Width;
        double imgH = _bitmap.Height;
        double scale = Math.Min(ctrlW / imgW, ctrlH / imgH);
        double displayW = imgW * scale;
        double displayH = imgH * scale;
        double ox = (ctrlW - displayW) / 2;
        double oy = (ctrlH - displayH) / 2;
        return (scale, ox, oy);
    }

    private WpfPoint CanvasToImage(WpfPoint p)
    {
        var (scale, ox, oy) = ImageTransform();
        if (scale == 0) return new WpfPoint(0, 0);
        return new WpfPoint((p.X - ox) / scale, (p.Y - oy) / scale);
    }

    // Caret index at image-space point. 0..cells.Count.
    // Caret i means "between cell i-1 and cell i", so selection range is [a, b) exclusive end.
    private int CaretAt(WpfPoint imgPt)
    {
        if (_cells.Count == 0) return 0;

        // Pick the line whose y-band contains the point, or the nearest one.
        int bestLine = -1;
        double bestLineDist = double.MaxValue;
        foreach (var group in _cells.GroupBy(c => c.LineIndex))
        {
            double yTop = group.Min(c => c.Y);
            double yBot = group.Max(c => c.Y + c.H);
            double dist = imgPt.Y < yTop ? yTop - imgPt.Y
                        : imgPt.Y > yBot ? imgPt.Y - yBot
                        : 0;
            if (dist < bestLineDist) { bestLineDist = dist; bestLine = group.Key; }
        }
        if (bestLine < 0) return 0;

        // Cells on that line in reading order, with their global indices.
        var onLine = new List<(int idx, Cell c)>();
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].LineIndex == bestLine) onLine.Add((i, _cells[i]));

        if (onLine.Count == 0) return 0;

        // Left of the first cell: caret before first; right of last cell: caret after last.
        if (imgPt.X <= onLine[0].c.X) return onLine[0].idx;
        if (imgPt.X >= onLine[^1].c.X + onLine[^1].c.W) return onLine[^1].idx + 1;

        for (int k = 0; k < onLine.Count; k++)
        {
            var (idx, c) = onLine[k];
            if (imgPt.X < c.X + c.W)
            {
                double mid = c.X + c.W / 2;
                return imgPt.X < mid ? idx : idx + 1;
            }
        }
        return onLine[^1].idx + 1;
    }

    // --- Rendering ---------------------------------------------------------

    private void RenderSelection()
    {
        // Ensure pool is big enough — one band per distinct line in selection.
        int needed = 0;

        if (_anchor >= 0 && _cursor >= 0 && _cells.Count > 0)
        {
            int a = Math.Min(_anchor, _cursor);
            int b = Math.Max(_anchor, _cursor);
            if (a < b)
            {
                var (scale, ox, oy) = ImageTransform();
                var brush = (SolidColorBrush)FindResource("SelectionFill");

                int i = a;
                while (i < b)
                {
                    int line = _cells[i].LineIndex;
                    int j = i;
                    while (j < b && _cells[j].LineIndex == line) j++;
                    // Band from cells[i..j) on this line.
                    double x1 = _cells[i].X;
                    double x2 = _cells[j - 1].X + _cells[j - 1].W;
                    double y = _cells[i].Y;
                    double h = _cells[i].H;
                    // Use line max height in case of varying glyphs.
                    for (int k = i; k < j; k++) h = Math.Max(h, _cells[k].H);

                    var rect = EnsureBand(needed);
                    rect.Fill = brush;
                    rect.Visibility = Visibility.Visible;
                    Canvas.SetLeft(rect, x1 * scale + ox);
                    Canvas.SetTop(rect, y * scale + oy);
                    rect.Width = (x2 - x1) * scale;
                    rect.Height = h * scale;
                    needed++;
                    i = j;
                }
            }
        }

        for (int k = needed; k < _bandPool.Count; k++)
            _bandPool[k].Visibility = Visibility.Collapsed;
    }

    private Rectangle EnsureBand(int index)
    {
        while (_bandPool.Count <= index)
        {
            var r = new Rectangle
            {
                IsHitTestVisible = false,
                RadiusX = 2,
                RadiusY = 2,
                Visibility = Visibility.Collapsed
            };
            OverlayCanvas.Children.Add(r);
            _bandPool.Add(r);
        }
        return _bandPool[index];
    }

    // --- Mouse -------------------------------------------------------------

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = CanvasToImage(e.GetPosition(OverlayCanvas));
        int caret = CaretAt(p);

        if (e.ClickCount >= 3)
        {
            if (_cells.Count > 0) { _anchor = 0; _cursor = _cells.Count; }
            RenderSelection();
            _dragging = false;
            if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard(silent: true);
            return;
        }

        if (e.ClickCount == 2)
        {
            SelectWordAt(caret);
            RenderSelection();
            _dragging = false;
            if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard(silent: true);
            return;
        }

        _anchor = caret;
        _cursor = caret;
        _dragging = true;
        RenderSelection();
        OverlayCanvas.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = CanvasToImage(e.GetPosition(OverlayCanvas));
        _cursor = CaretAt(p);
        RenderSelection();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        OverlayCanvas.ReleaseMouseCapture();
        _dragging = false;
        var p = CanvasToImage(e.GetPosition(OverlayCanvas));
        _cursor = CaretAt(p);
        RenderSelection();
        if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard(silent: true);
    }

    private void Overlay_RightUp(object sender, MouseButtonEventArgs e)
    {
        CopySelectionToClipboard(silent: false);
    }

    private void SelectWordAt(int caret)
    {
        if (_cells.Count == 0) return;
        int idx = Math.Clamp(caret, 0, _cells.Count - 1);
        // If caret is at end (after last cell), use previous cell for word boundary.
        if (caret == _cells.Count) idx = _cells.Count - 1;

        int line = _cells[idx].LineIndex;
        int word = _cells[idx].WordIndex;

        int start = idx;
        while (start > 0 && _cells[start - 1].LineIndex == line && _cells[start - 1].WordIndex == word)
            start--;
        int end = idx;
        while (end < _cells.Count - 1 && _cells[end + 1].LineIndex == line && _cells[end + 1].WordIndex == word)
            end++;

        _anchor = start;
        _cursor = end + 1;
    }

    // --- Clipboard ---------------------------------------------------------

    private string BuildSelectedText()
    {
        if (_anchor < 0 || _cursor < 0) return string.Empty;
        int a = Math.Min(_anchor, _cursor);
        int b = Math.Max(_anchor, _cursor);
        if (a == b) return string.Empty;

        var sb = new System.Text.StringBuilder();
        int currentLine = _cells[a].LineIndex;
        int currentWord = _cells[a].WordIndex;
        for (int i = a; i < b; i++)
        {
            var c = _cells[i];
            if (c.LineIndex != currentLine)
            {
                sb.Append('\n');
                currentLine = c.LineIndex;
                currentWord = c.WordIndex;
            }
            else if (c.WordIndex != currentWord)
            {
                sb.Append(' ');
                currentWord = c.WordIndex;
            }
            sb.Append(c.Ch);
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
        Close();
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var src = CaptureService.ToBitmapSource(_bitmap);
            System.Windows.Clipboard.SetImage(src);
        }
        catch { }
        Close();
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
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && _cells.Count > 0)
        {
            _anchor = 0; _cursor = _cells.Count; RenderSelection();
        }
    }
}
