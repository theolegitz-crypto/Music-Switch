using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaController.Models;

namespace MediaController.UI.Controls;

/// <summary>
/// Records a hotkey from a real key press. Plain WPF keyboard events are enough here -
/// the Settings window has focus, so no keyboard hook of any kind is involved.
/// </summary>
public partial class HotkeyCaptureControl : UserControl
{
    private static readonly Brush NormalBorder = Frozen(0xFF, 0xFF, 0xFF, 0x1A);
    private static readonly Brush RecordingBorder = Frozen(0xA9, 0x70, 0xFF, 0xFF);
    private static readonly Brush ErrorBorder = Frozen(0xFF, 0x7A, 0x8A, 0xCC);
    private static readonly Brush PrimaryText = Frozen(0xF7, 0xF3, 0xFF, 0xFF);
    private static readonly Brush SecondaryText = Frozen(0xB9, 0xAE, 0xC5, 0xFF);

    private HotkeySettings _hotkey = new();
    private HotkeySettings _beforeCapture = new();
    private bool _capturing;

    public HotkeyCaptureControl()
    {
        InitializeComponent();
    }

    /// <summary>Raised when recording starts, so the host can release the global hotkeys.</summary>
    public event Action? CaptureStarted;

    /// <summary>
    /// Raised when recording ends, whichever way. The argument is the newly recorded
    /// combination, or null if the user cancelled with Esc or cleared the hotkey.
    /// </summary>
    public event Action<HotkeySettings?>? CaptureFinished;

    public HotkeySettings Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value.Clone();
            Display.Text = _hotkey.ToString();
        }
    }

    public bool IsCapturing => _capturing;

    public void SetError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            ErrorLine.Visibility = Visibility.Collapsed;
            ErrorLine.Text = string.Empty;
            ApplyVisual();
            return;
        }

        ErrorLine.Text = "⚠  " + message;
        ErrorLine.Visibility = Visibility.Visible;
        ApplyVisual();
    }

    /// <summary>Recording glows purple, an occupied combination gets a muted red edge.</summary>
    private void ApplyVisual()
    {
        var hasError = ErrorLine.Visibility == Visibility.Visible;

        Box.BorderBrush = _capturing ? RecordingBorder : hasError ? ErrorBorder : NormalBorder;
        BoxGlow.Opacity = _capturing ? 0.5 : 0.0;
        Display.Foreground = _capturing ? SecondaryText : PrimaryText;
    }

    /// <summary>Ends recording without committing. Safe to call when not recording.</summary>
    public void CancelCapture()
    {
        if (!_capturing)
        {
            return;
        }

        Hotkey = _beforeCapture;
        Stop();
        CaptureFinished?.Invoke(null);
    }

    private void OnChangeClick(object sender, RoutedEventArgs e)
    {
        // Clicking Change while already recording first triggers OnBoxLostFocus, which
        // cancels the previous attempt - so this always starts a clean recording.
        _beforeCapture = _hotkey.Clone();
        _capturing = true;

        SetError(null);
        Display.Text = "Press a new key combination...";
        ApplyVisual();

        // Focus must leave the button, otherwise Space and Enter click it instead of being recorded.
        Keyboard.Focus(Box);

        CaptureStarted?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // Handling it here also stops Esc from reaching the window's Cancel button.
        e.Handled = true;

        // With Alt held, WPF delivers the real key as SystemKey and Key as Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        if (key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            Hotkey = new HotkeySettings();
            Stop();
            CaptureFinished?.Invoke(null);
            return;
        }

        // A modifier on its own is not a hotkey - keep waiting for the real key.
        if (IsModifier(key))
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Keyboard.Modifiers does not report the Windows key, so ask the device directly.
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!ctrl && !alt && !shift && !win)
        {
            SetError("Add at least one modifier: Ctrl, Alt, Shift or Win.");
            return;
        }

        Hotkey = HotkeySettings.Create(ctrl, alt, shift, win, key);
        Stop();
        CaptureFinished?.Invoke(_hotkey.Clone());
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CancelCapture();

    private void Stop()
    {
        _capturing = false;
        ApplyVisual();
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.None;

    private static Brush Frozen(byte r, byte g, byte b, byte a)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
