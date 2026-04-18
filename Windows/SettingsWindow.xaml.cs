using System.Windows;
using System.Windows.Controls;
using ScreenshotOCR.Models;
using ScreenshotOCR.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButton = System.Windows.Input.MouseButton;

namespace ScreenshotOCR.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private HotkeyBinding _pendingHotkey;
    private bool _capturingHotkey;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _pendingHotkey = new HotkeyBinding
        {
            Modifiers = settings.Current.CaptureHotkey.Modifiers,
            Key = settings.Current.CaptureHotkey.Key
        };

        HotkeyDisplay.Text = _pendingHotkey.Display();
        StartupCheck.IsChecked = StartupService.IsEnabled();
        AutoCopyCheck.IsChecked = settings.Current.CopyToClipboardOnSelect;

        PopulateLanguages();
        KeyDown += OnKeyDown;
    }

    private void PopulateLanguages()
    {
        var langs = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages;
        foreach (var l in langs)
            LanguageCombo.Items.Add(l.LanguageTag);

        if (LanguageCombo.Items.Count == 0)
            LanguageCombo.Items.Add(_settings.Current.OcrLanguage);

        LanguageCombo.SelectedItem = _settings.Current.OcrLanguage;
        if (LanguageCombo.SelectedIndex < 0 && LanguageCombo.Items.Count > 0)
            LanguageCombo.SelectedIndex = 0;
    }

    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        RebindButton.Content = "Press keys…";
        HotkeyDisplay.Text = "…";
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey)
        {
            if (e.Key == Key.Escape) Close_Click(this, new RoutedEventArgs());
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturingHotkey = false;
            RebindButton.Content = "Change";
            HotkeyDisplay.Text = _pendingHotkey.Display();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        HotkeyModifiers mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        string keyStr = key.ToString();
        if (key is >= Key.A and <= Key.Z) keyStr = key.ToString();
        else if (key is >= Key.D0 and <= Key.D9) keyStr = key.ToString().Substring(1);
        else if (key is >= Key.F1 and <= Key.F12) keyStr = key.ToString();
        else if (key == Key.PrintScreen) keyStr = "PrintScreen";
        else if (key == Key.Space) keyStr = "Space";

        _pendingHotkey = new HotkeyBinding { Modifiers = mods, Key = keyStr };
        HotkeyDisplay.Text = _pendingHotkey.Display();
        _capturingHotkey = false;
        RebindButton.Content = "Change";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Current.CaptureHotkey = _pendingHotkey;
        _settings.Current.CopyToClipboardOnSelect = AutoCopyCheck.IsChecked == true;
        if (LanguageCombo.SelectedItem is string lang)
            _settings.Current.OcrLanguage = lang;

        _settings.Save();
        StartupService.SetEnabled(StartupCheck.IsChecked == true);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
