using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using ScreenshotOCR.Models;
using ScreenshotOCR.Services;
using ScreenshotOCR.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenshotOCR;

public partial class App : System.Windows.Application
{
    public static AppSettings Settings => _settingsService.Current;
    private static SettingsService _settingsService = new();

    private HotkeyService? _hotkey;
    private WinForms.NotifyIcon? _tray;
    private HiddenWindow? _hidden;
    private bool _captureInProgress;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _settingsService.Load();

        _hidden = new HiddenWindow();
        _hidden.Show();
        _hidden.Hide();

        InitTray();
        RegisterHotkey();
    }

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Screenshot OCR"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Capture…", null, (_, _) => StartCapture());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => StartCapture();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        // Minimal generated icon: a white "T" on transparent square.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var font = new Font("Segoe UI", 18, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(System.Drawing.Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("T", font, brush, new RectangleF(0, 0, 32, 32), sf);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void RegisterHotkey()
    {
        _hotkey?.Unregister();
        _hotkey = new HotkeyService();
        _hotkey.HotkeyPressed += StartCapture;
        var handle = new WindowInteropHelper(_hidden!).Handle;
        bool ok = _hotkey.Register(handle, _settingsService.Current.CaptureHotkey);
        if (!ok)
        {
            _tray?.ShowBalloonTip(
                3000,
                "Screenshot OCR",
                $"Couldn't register {_settingsService.Current.CaptureHotkey.Display()}. Another app may be using it.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    private void StartCapture()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            var vs = CaptureService.GetVirtualScreen();
            var frozen = CaptureService.CaptureVirtualScreen();

            var selector = new RegionSelectorWindow(frozen, vs);
            selector.ShowDialog();

            if (!selector.WasConfirmed || selector.SelectedRegion is not { } region)
            {
                frozen.Dispose();
                return;
            }

            var cropped = CaptureService.CropBitmap(frozen, region);
            frozen.Dispose();

            _ = RunOcrAndShowAsync(cropped);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private async Task RunOcrAndShowAsync(Bitmap cropped)
    {
        try
        {
            var result = await OcrService.RecognizeAsync(cropped, _settingsService.Current.OcrLanguage);
            var window = new ResultWindow(cropped, result);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(3000, "OCR failed", ex.Message, WinForms.ToolTipIcon.Error);
        }
    }

    private void ShowSettings()
    {
        _hotkey?.Unregister();
        var win = new SettingsWindow(_settingsService);
        win.ShowDialog();
        RegisterHotkey();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}

// Hidden top-level window required so RegisterHotKey has a message loop target.
internal class HiddenWindow : System.Windows.Window
{
    public HiddenWindow()
    {
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Width = 1; Height = 1;
        Left = -32000; Top = -32000;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
    }
}
