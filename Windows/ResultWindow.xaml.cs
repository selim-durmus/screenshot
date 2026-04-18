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
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
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
    private record Cell(
        char Ch,
        int LineIndex,
        int WordIndex,
        double X, double Y, double W, double H,
        bool IsLastInWord);

    // Word bounds are refined to actual ink rows upstream (see
    // OcrService.RefineVerticalBoundsToInk), so the band can sit directly
    // on the measured bounds with no heuristic inset.
    private const double BandTopInset = 0.0;
    private const double BandBottomInset = 0.0;
    private const double BandCornerRadius = 3;

    private readonly Bitmap _bitmap;
    private readonly OcrResult _ocr;
    private readonly List<Cell> _cells = new();
    private readonly Dictionary<int, (double Y, double H)> _lineMetrics = new();
    private readonly List<Rectangle> _bandPool = new();
    private readonly List<Rectangle> _debugRects = new();

    // Rectangular selection model (image-pixel space). Null = no selection.
    private WpfRect? _selectionRect;
    // Anchor point for an in-progress drag (image-pixel space).
    private WpfPoint? _dragStart;
    private bool _dragging;
    private bool _didInitialSize;
    private bool _debugOn;

    // Dashed marquee rectangle shown during drag, hidden otherwise.
    private Rectangle? _marquee;

    public ResultWindow(Bitmap bitmap, OcrResult ocr)
    {
        InitializeComponent();
        _bitmap = bitmap;
        _ocr = ocr;
        CapturedImage.Source = CaptureService.ToBitmapSource(bitmap);

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => { RenderSelection(); RenderDebug(); };
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
        _lineMetrics.Clear();

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

        foreach (var group in _cells.GroupBy(c => c.LineIndex))
        {
            double yTop = group.Min(c => c.Y);
            double yBot = group.Max(c => c.Y + c.H);
            _lineMetrics[group.Key] = (yTop, yBot - yTop);
        }
    }

    // --- Coordinate math ---------------------------------------------------

    private (double scale, double offsetX, double offsetY) ImageTransform()
    {
        // Read dimensions from OverlayCanvas, NOT CapturedImage. WPF's Image
        // control with Stretch=Uniform measures itself to its content size
        // after scaling — so CapturedImage.ActualHeight shrinks to the
        // uniformly-scaled image height, missing the vertical letterbox in
        // the Grid cell. OverlayCanvas fills the cell and reports its true
        // dimensions. Using Image's ActualHeight made offsetY collapse to 0
        // and offset every overlay upward by the letterbox amount.
        double ctrlW = OverlayCanvas.ActualWidth;
        double ctrlH = OverlayCanvas.ActualHeight;
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

    // --- Rendering: rectangular selection ----------------------------------
    //
    // The highlight shown to the user is NOT the raw selection rectangle.
    // For each line that has any cell intersecting `_selectionRect`, we draw
    // one band spanning the horizontal range of the intersected cells on
    // that line (clamped to the line's Y/H). The marquee rectangle itself
    // is only shown during a live drag.

    private IEnumerable<Cell> IntersectedCells()
    {
        if (_selectionRect is not WpfRect r || r.Width <= 0 || r.Height <= 0) yield break;
        foreach (var c in _cells)
        {
            var cellRect = new WpfRect(c.X, c.Y, c.W, c.H);
            if (r.IntersectsWith(cellRect)) yield return c;
        }
    }

    private void RenderSelection()
    {
        int needed = 0;

        if (_cells.Count > 0 && _selectionRect is WpfRect)
        {
            var (scale, ox, oy) = ImageTransform();
            var fill = (SolidColorBrush)FindResource("SelectionFill");

            // Group intersected cells by line, draw one band per line spanning
            // only the horizontal range of the selected cells on that line.
            foreach (var lineGroup in IntersectedCells().GroupBy(c => c.LineIndex))
            {
                double x1 = lineGroup.Min(c => c.X);
                double x2 = lineGroup.Max(c => c.X + c.W);
                if (!_lineMetrics.TryGetValue(lineGroup.Key, out var m)) continue;
                double bandY = m.Y + m.H * BandTopInset;
                double bandH = m.H * (1 - BandTopInset - BandBottomInset);

                var rect = EnsureBand(needed);
                rect.Stroke = null;
                rect.StrokeThickness = 0;
                rect.Fill = fill;
                rect.RadiusX = BandCornerRadius;
                rect.RadiusY = BandCornerRadius;
                rect.Visibility = Visibility.Visible;
                Canvas.SetLeft(rect, x1 * scale + ox);
                Canvas.SetTop(rect, bandY * scale + oy);
                rect.Width = (x2 - x1) * scale;
                rect.Height = bandH * scale;
                needed++;
            }
        }

        for (int k = needed; k < _bandPool.Count; k++)
            _bandPool[k].Visibility = Visibility.Collapsed;

        RenderMarquee();
    }

    private void RenderMarquee()
    {
        if (_marquee is null)
        {
            _marquee = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                StrokeThickness = 1,
                StrokeDashArray = { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            OverlayCanvas.Children.Add(_marquee);
        }

        if (!_dragging || _selectionRect is not WpfRect r || r.Width <= 0 || r.Height <= 0)
        {
            _marquee.Visibility = Visibility.Collapsed;
            return;
        }

        var (scale, ox, oy) = ImageTransform();
        Canvas.SetLeft(_marquee, r.X * scale + ox);
        Canvas.SetTop(_marquee, r.Y * scale + oy);
        _marquee.Width = r.Width * scale;
        _marquee.Height = r.Height * scale;
        _marquee.Visibility = Visibility.Visible;
    }

    private Rectangle EnsureBand(int index)
    {
        while (_bandPool.Count <= index)
        {
            var r = new Rectangle
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
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

        if (e.ClickCount >= 4)
        {
            _selectionRect = AllCellsRect();
            _dragging = false;
            RenderSelection();
            if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard();
            return;
        }

        if (e.ClickCount == 3)
        {
            _selectionRect = LineRectAt(p);
            _dragging = false;
            RenderSelection();
            if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard();
            return;
        }

        if (e.ClickCount == 2)
        {
            _selectionRect = SubwordRectAt(p);
            _dragging = false;
            RenderSelection();
            if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard();
            return;
        }

        _dragStart = p;
        _selectionRect = new WpfRect(p, p);
        _dragging = true;
        RenderSelection();
        OverlayCanvas.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragStart is not WpfPoint a) return;
        var b = CanvasToImage(e.GetPosition(OverlayCanvas));
        _selectionRect = new WpfRect(a, b);
        RenderSelection();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        OverlayCanvas.ReleaseMouseCapture();
        _dragging = false;

        if (_dragStart is WpfPoint a)
        {
            var b = CanvasToImage(e.GetPosition(OverlayCanvas));
            var r = new WpfRect(a, b);
            // Treat a mouseup-at-mousedown (no drag) as "clear selection".
            _selectionRect = (r.Width < 1 && r.Height < 1) ? (WpfRect?)null : r;
        }
        _dragStart = null;
        RenderSelection();

        // Silently copy on mouse-up if the setting is on. Doesn't close the
        // window — only explicit copies (Ctrl+C, Copy All, Copy Image) do.
        if (App.Settings.CopyToClipboardOnSelect) CopySelectionToClipboard();
    }

    private void Overlay_RightUp(object sender, MouseButtonEventArgs e)
    {
        // Right-click copies but keeps the window open. Only Ctrl+C closes.
        CopySelectionToClipboard();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Find the cell closest to an image-pixel point (reused for word/line click expansion).
    private int NearestCellIndex(WpfPoint imgPt)
    {
        int bestIdx = 0;
        double bestDistSq = double.MaxValue;
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            double dx = imgPt.X < c.X ? c.X - imgPt.X
                      : imgPt.X > c.X + c.W ? imgPt.X - (c.X + c.W)
                      : 0;
            double dy = imgPt.Y < c.Y ? c.Y - imgPt.Y
                      : imgPt.Y > c.Y + c.H ? imgPt.Y - (c.Y + c.H)
                      : 0;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestDistSq) { bestDistSq = d2; bestIdx = i; }
        }
        return bestIdx;
    }

    private WpfRect? SubwordRectAt(WpfPoint imgPt)
    {
        if (_cells.Count == 0) return null;
        int idx = NearestCellIndex(imgPt);
        var hit = _cells[idx];

        int start = idx, end = idx;
        if (IsWordChar(hit.Ch))
        {
            while (start > 0 && _cells[start - 1].LineIndex == hit.LineIndex
                   && IsWordChar(_cells[start - 1].Ch)) start--;
            while (end < _cells.Count - 1 && _cells[end + 1].LineIndex == hit.LineIndex
                   && IsWordChar(_cells[end + 1].Ch)) end++;
        }
        // Non-word-char (punctuation): select just that char.

        return UnionCellRect(start, end);
    }

    private WpfRect? LineRectAt(WpfPoint imgPt)
    {
        if (_cells.Count == 0) return null;
        int idx = NearestCellIndex(imgPt);
        int line = _cells[idx].LineIndex;

        int start = idx, end = idx;
        while (start > 0 && _cells[start - 1].LineIndex == line) start--;
        while (end < _cells.Count - 1 && _cells[end + 1].LineIndex == line) end++;

        return UnionCellRect(start, end);
    }

    private WpfRect? AllCellsRect()
    {
        if (_cells.Count == 0) return null;
        return UnionCellRect(0, _cells.Count - 1);
    }

    private WpfRect UnionCellRect(int fromIdx, int toIdx)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue;
        double x2 = double.MinValue, y2 = double.MinValue;
        for (int i = fromIdx; i <= toIdx; i++)
        {
            var c = _cells[i];
            x1 = Math.Min(x1, c.X);
            y1 = Math.Min(y1, c.Y);
            x2 = Math.Max(x2, c.X + c.W);
            y2 = Math.Max(y2, c.Y + c.H);
        }
        return new WpfRect(x1, y1, x2 - x1, y2 - y1);
    }

    // --- Clipboard ---------------------------------------------------------

    private string BuildSelectedText()
    {
        var selected = IntersectedCells()
            .OrderBy(c => c.LineIndex)
            .ThenBy(c => c.WordIndex)
            .ToList();
        if (selected.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        int currentLine = selected[0].LineIndex;
        int currentWord = selected[0].WordIndex;
        for (int i = 0; i < selected.Count; i++)
        {
            var c = selected[i];
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

    // Returns true if something was actually written to the clipboard.
    private bool CopySelectionToClipboard()
    {
        var text = BuildSelectedText();
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch { return false; }
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
        // Configurable close hotkey (default Ctrl+W) — plus Esc always works as a
        // universal cancel key.
        if (e.Key == Key.Escape || MatchesBinding(e, App.Settings.CloseHotkey))
        {
            Close();
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (CopySelectionToClipboard())
            {
                Close();
            }
            else if (!string.IsNullOrWhiteSpace(_ocr.FullText))
            {
                // Nothing selected — copy all as a fallback. CopyAll_Click closes on its own.
                CopyAll_Click(this, new RoutedEventArgs());
            }
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && _cells.Count > 0)
        {
            _selectionRect = AllCellsRect();
            RenderSelection();
        }

        // F9 — toggle diagnostic overlay. Shows a thin red outline at every
        // word's position as returned by our OCR pipeline. If these rectangles
        // visually hug the text, rendering is correct and the bug is in
        // selection math. If they don't, the issue is upstream (OCR data or
        // the image->canvas coordinate transform).
        if (e.Key == Key.F9)
        {
            _debugOn = !_debugOn;
            RenderDebug();
        }
    }

    private void RenderDebug()
    {
        foreach (var r in _debugRects) OverlayCanvas.Children.Remove(r);
        _debugRects.Clear();
        if (_debugLabel is not null) { OverlayCanvas.Children.Remove(_debugLabel); _debugLabel = null; }

        if (!_debugOn || _ocr.Words.Count == 0) return;

        var (scale, ox, oy) = ImageTransform();
        var wordStroke = new SolidColorBrush(Color.FromArgb(200, 255, 60, 60));   // red — OCR word bbox
        var lineStroke = new SolidColorBrush(Color.FromArgb(160, 80, 200, 255));  // cyan — OCR line bbox
        var canvasStroke = new SolidColorBrush(Color.FromArgb(220, 255, 230, 0)); // yellow — canvas bounds
        var imageStroke = new SolidColorBrush(Color.FromArgb(220, 0, 230, 80));   // green — computed image display rect
        wordStroke.Freeze(); lineStroke.Freeze(); canvasStroke.Freeze(); imageStroke.Freeze();

        // YELLOW: the full bounds of OverlayCanvas (where we think the Canvas
        // lives). If this matches the container edges visually, the Canvas
        // is layered correctly on top of the Image.
        var canvasBox = new Rectangle
        {
            Stroke = canvasStroke,
            StrokeThickness = 2,
            Fill = null,
            IsHitTestVisible = false,
            StrokeDashArray = { 1, 2 }
        };
        Canvas.SetLeft(canvasBox, 0);
        Canvas.SetTop(canvasBox, 0);
        canvasBox.Width = OverlayCanvas.ActualWidth;
        canvasBox.Height = OverlayCanvas.ActualHeight;
        OverlayCanvas.Children.Add(canvasBox);
        _debugRects.Add(canvasBox);

        // GREEN: where OUR transform thinks the rendered image lives inside
        // the canvas (offsetX, offsetY, bitmapWidth * scale, bitmapHeight * scale).
        // If this green rectangle doesn't visually match the edges of the
        // captured image, our transform has a bug — and everything else in the
        // overlay is going to be misaligned by the same amount.
        var imageBox = new Rectangle
        {
            Stroke = imageStroke,
            StrokeThickness = 2,
            Fill = null,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(imageBox, ox);
        Canvas.SetTop(imageBox, oy);
        imageBox.Width = _bitmap.Width * scale;
        imageBox.Height = _bitmap.Height * scale;
        OverlayCanvas.Children.Add(imageBox);
        _debugRects.Add(imageBox);

        // RED: OCR word bboxes.
        foreach (var w in _ocr.Words)
        {
            var r = new Rectangle
            {
                Stroke = wordStroke,
                StrokeThickness = 1,
                Fill = null,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(r, w.X * scale + ox);
            Canvas.SetTop(r, w.Y * scale + oy);
            r.Width = w.Width * scale;
            r.Height = w.Height * scale;
            OverlayCanvas.Children.Add(r);
            _debugRects.Add(r);
        }

        // CYAN: line bboxes (union of words on each line).
        foreach (var group in _ocr.Words.GroupBy(w => w.LineIndex))
        {
            double x1 = group.Min(w => w.X);
            double x2 = group.Max(w => w.X + w.Width);
            double y1 = group.Min(w => w.Y);
            double y2 = group.Max(w => w.Y + w.Height);

            var r = new Rectangle
            {
                Stroke = lineStroke,
                StrokeThickness = 1,
                Fill = null,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                StrokeDashArray = { 2, 2 }
            };
            Canvas.SetLeft(r, x1 * scale + ox);
            Canvas.SetTop(r, y1 * scale + oy);
            r.Width = (x2 - x1) * scale;
            r.Height = (y2 - y1) * scale;
            OverlayCanvas.Children.Add(r);
            _debugRects.Add(r);
        }

        // Telemetry label: the raw transform numbers as a sanity check.
        var label = new TextBlock
        {
            Text = $"bmp: {_bitmap.Width}×{_bitmap.Height}  "
                 + $"canvas: {OverlayCanvas.ActualWidth:0}×{OverlayCanvas.ActualHeight:0}  "
                 + $"scale: {scale:0.000}  offset: ({ox:0.0}, {oy:0.0})  "
                 + $"bmpSrc DPI: {((BitmapSource)CapturedImage.Source).DpiX:0}×{((BitmapSource)CapturedImage.Source).DpiY:0}",
            Foreground = canvasStroke,
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(6, 3, 6, 3),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, 4);
        OverlayCanvas.Children.Add(label);
        _debugLabel = label;
    }

    private TextBlock? _debugLabel;

    private static bool MatchesBinding(KeyEventArgs e, HotkeyBinding b)
    {
        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;

        HotkeyModifiers mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        if (mods != b.Modifiers) return false;

        var target = ParseKey(b.Key);
        return target.HasValue && effectiveKey == target.Value;
    }

    private static Key? ParseKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToUpperInvariant();
        if (s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z') return Key.A + (s[0] - 'A');
        if (s.Length == 1 && s[0] >= '0' && s[0] <= '9') return Key.D0 + (s[0] - '0');
        return s switch
        {
            "F1" => Key.F1, "F2" => Key.F2, "F3" => Key.F3, "F4" => Key.F4,
            "F5" => Key.F5, "F6" => Key.F6, "F7" => Key.F7, "F8" => Key.F8,
            "F9" => Key.F9, "F10" => Key.F10, "F11" => Key.F11, "F12" => Key.F12,
            "SPACE" => Key.Space,
            "PRINTSCREEN" or "PRTSC" => Key.PrintScreen,
            _ => null
        };
    }
}
