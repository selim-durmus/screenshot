using Microsoft.Win32;

namespace ScreenshotOCR.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenshotOCR";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        if (key is null) return false;
        var value = key.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)!;
        if (enabled)
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
