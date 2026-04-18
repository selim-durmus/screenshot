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
    private record Cell(
        char Ch,
        int LineIndex,
        int WordIndex,
        double X, double Y, double W, double H,
        bool IsLastInWord);

    // Vertical inset on each side for the outline so it hugs the ink rather than
    // OCR's loose typographic bounds.
    private const double BandTopInset = 0.06;
    private const double BandBottomInset = 0.02;
    private const double OutlineThickness = 1.5;
    private const double OutlineCornerRadius = 4;

    private readonly Bitmap _bitmap;
    private readonly OcrResult _ocr;
    private readonly List<Cell> _cells = new();
    private readonly Dictionary<int, (double Y, double H)> _lineMetrics = new();
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

    private int CaretAt(WpfPoint imgPt)
    {
        if (_cells.Count == 0) return 0;

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

        int line = _cells[bestIdx].LineIndex;
        var onLine = new List<(int idx, Cell c)>();
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].LineIndex == line) onLine.Add((i, _cells[i]));

        if (onLine.Count == 0) return 0;

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

    // --- Rendering: outlined boxes (macOS Live Text style) -----------------

    private void RenderSelection()
    {
        int needed = 0;

        if (_anchor >= 0 && _cursor >= 0 && _cells.Count > 0)
        {
            int a = Math.Min(_anchor, _cursor);
            int b = Math.Max(_anchor, _cursor);
            if (a < b)
            {
                var (scale, ox, oy) = ImageTransform();
                var stroke = (SolidColorBrush)FindResource("SelectionStroke");
                var fill = (SolidColorBrush)FindResource("SelectionFill");

                int i = a;
                while (i < b)
                {
                    int line = _cells[i].LineIndex;
                    int j = i;
                    while (j < b && _cells[j].LineIndex == line) j++;

                    double x1 = _cells[i].X;
                    double x2 = _cells[j - 1].X + _cells[j - 1].W;

                    var (lineY, lineH) = _lineMetrics[line];
                    double bandY = lineY + lineH * BandTopInset;
                    double bandH = lineH * (1 - BandTopInset - BandBottomInset);

                    // Pad horizontally slightly so the outline doesn't clip the glyph edges.
                    double padX = Math.Max(1.5, bandH * 0.15);
                    double boxX = x1 * scale + ox - padX;
                    double boxY = bandY * scale + oy;
                    double boxW = (x2 - x1) * scale + padX * 2;
                    double boxH = bandH * scale;

                    var rect = EnsureBand(needed);
                    rect.Stroke = stroke;
                    rect.Fill = fill;
                    rect.StrokeThickness = OutlineThickness;
                    rect.RadiusX = OutlineCornerRadius;
                    rect.RadiusY = OutlineCornerRadius;
                    rect.Visibility = Visibility.Visible;
                    Canvas.SetLeft(rect, boxX);
                    Canvas.SetTop(rect, boxY);
                    rect.Width = boxW;
                    rect.Height = boxH;
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
        int caret = CaretAt(p);

        if (e.ClickCount >= 4)
        {
            if (_cells.Count > 0) { _anchor = 0; _cursor = _cells.Count; }
            _dragging = false;
            RenderSelection();
            TryAutoCopyAndClose();
            return;
        }

        if (e.ClickCount == 3)
        {
            SelectLineAt(caret);
            _dragging = false;
            RenderSelection();
            TryAutoCopyAndClose();
            return;
        }

        if (e.ClickCount == 2)
        {
            SelectSubwordAt(caret);
            _dragging = false;
            RenderSelection();
            TryAutoCopyAndClose();
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
        TryAutoCopyAndClose();
    }

    private void Overlay_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (CopySelectionToClipboard()) Close();
    }

    private void TryAutoCopyAndClose()
    {
        if (!App.Settings.CopyToClipboardOnSelect) return;
        if (CopySelectionToClipboard()) Close();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int ClampCaretToCell(int caret)
    {
        if (caret >= _cells.Count) return _cells.Count - 1;
        if (caret < 0) return 0;
        return caret;
    }

    private void SelectSubwordAt(int caret)
    {
        if (_cells.Count == 0) return;
        int idx = ClampCaretToCell(caret);

        if (!IsWordChar(_cells[idx].Ch))
        {
            _anchor = idx;
            _cursor = idx + 1;
            return;
        }

        int line = _cells[idx].LineIndex;
        int start = idx;
        while (start > 0
               && _cells[start - 1].LineIndex == line
               && IsWordChar(_cells[start - 1].Ch))
            start--;

        int end = idx;
        while (end < _cells.Count - 1
               && _cells[end + 1].LineIndex == line
               && IsWordChar(_cells[end + 1].Ch))
            end++;

        _anchor = start;
        _cursor = end + 1;
    }

    private void SelectLineAt(int caret)
    {
        if (_cells.Count == 0) return;
        int idx = ClampCaretToCell(caret);
        int line = _cells[idx].LineIndex;

        int start = idx;
        while (start > 0 && _cells[start - 1].LineIndex == line) start--;
        int end = idx;
        while (end < _cells.Count - 1 && _cells[end + 1].LineIndex == line) end++;

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
            _anchor = 0; _cursor = _cells.Count; RenderSelection();
        }
    }

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
