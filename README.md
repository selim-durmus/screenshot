# Screenshot OCR

A minimal Windows 11 screenshot tool inspired by the macOS Live Text / region-capture experience:

1. Press a global hotkey (default **Ctrl + Shift + X**) → screen freezes
2. Drag a rectangle over the part you want
3. A floating window appears with the captured image; any text in it is selectable
4. Click-drag to select words, `Ctrl+C` (or release mouse — auto-copy is on by default) to copy

Runs from the system tray. No installer, no admin rights.

## Tech

- .NET 8 WPF (`net8.0-windows10.0.19041.0`)
- `Windows.Media.Ocr` — native Windows OCR, no external models
- `System.Windows.Forms.NotifyIcon` — tray icon
- `RegisterHotKey` Win32 API — global shortcut
- Registry `HKCU\...\Run` — launch-on-startup toggle
- Self-contained single-file publish — one portable `.exe`

## Project structure

```
ScreenshotOCR.csproj
App.xaml(.cs)                     tray icon, hotkey wiring, capture flow
app.manifest                      per-monitor DPI awareness

Models/
  AppSettings.cs                  hotkey binding, startup, language
  OcrResult.cs                    OCR word + bounding box

Services/
  SettingsService.cs              JSON persistence in %APPDATA%\ScreenshotOCR
  HotkeyService.cs                RegisterHotKey / WndProc
  CaptureService.cs               full-virtual-screen bitmap + crop
  OcrService.cs                   Windows.Media.Ocr wrapper
  StartupService.cs               HKCU Run key toggle

Windows/
  RegionSelectorWindow            frozen-screen fullscreen drag-to-select
  ResultWindow                    image + word hit-boxes + selection overlay
  SettingsWindow                  hotkey rebind, startup, language, auto-copy
```

## Building

You cannot run this on macOS — `Windows.Media.Ocr` only works on Windows. But you can build from any OS with the .NET 8 SDK.

Debug build (on Windows):

```powershell
dotnet build
dotnet run
```

Portable release `.exe`:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ScreenshotOCR.exe` (~20–30 MB, single file). Copy it anywhere and double-click.

## Shortcuts (at runtime)

| Action | Default |
|---|---|
| Start capture | **Ctrl + Shift + X** (configurable) |
| Cancel capture | Esc |
| Copy selection | Ctrl + C (or auto on mouse-up) |
| Select all text | Ctrl + A |
| Close result window | Esc |

## Settings

Settings live in `%APPDATA%\ScreenshotOCR\settings.json`. Edit via the tray icon → Settings, or delete the file to reset.

## Known limitations

- OCR accuracy depends on the Windows language pack. Install additional languages via Settings → Time & language → Language & region on Windows.
- `RegisterHotKey` is exclusive — if another app already owns your chosen combination, registration fails and you'll get a tray notification.
- Per-monitor DPI is declared in the manifest, but very aggressive DPI mixing on multi-monitor setups may need testing.
