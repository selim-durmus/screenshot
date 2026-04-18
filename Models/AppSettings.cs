namespace ScreenshotOCR.Models;

public class AppSettings
{
    public HotkeyBinding CaptureHotkey { get; set; } = new()
    {
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
        Key = "X"
    };

    public HotkeyBinding CloseHotkey { get; set; } = new()
    {
        Modifiers = HotkeyModifiers.Control,
        Key = "W"
    };

    public bool LaunchOnStartup { get; set; } = false;

    public bool CopyToClipboardOnSelect { get; set; } = true;

    public string OcrLanguage { get; set; } = "en-US";
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8
}

public class HotkeyBinding
{
    public HotkeyModifiers Modifiers { get; set; }
    public string Key { get; set; } = "X";

    public string Display()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join(" + ", parts);
    }
}
