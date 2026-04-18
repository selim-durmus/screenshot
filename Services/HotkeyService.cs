using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenshotOCR.Models;

namespace ScreenshotOCR.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    public bool Register(IntPtr windowHandle, HotkeyBinding binding)
    {
        Unregister();

        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);

        var vk = VirtualKeyFromString(binding.Key);
        if (vk == 0) return false;

        _registered = RegisterHotKey(windowHandle, HotkeyId, (uint)binding.Modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static uint VirtualKeyFromString(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim().ToUpperInvariant();

        if (key.Length == 1)
        {
            char c = key[0];
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        return key switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "SPACE" => 0x20,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            _ => 0
        };
    }

    public void Dispose() => Unregister();
}
