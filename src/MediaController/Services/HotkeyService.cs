using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MediaController.Core;
using MediaController.Models;
using MediaController.Native;

namespace MediaController.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message window.
/// No low-level keyboard hook: nothing is intercepted, nothing touches another process.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // Application hotkey ids must be in the range 0x0000..0xBFFF.
    private const int BaseId = 0xB000;

    /// <summary>Scratch id used only to ask Windows whether a combination is free.</summary>
    private const int ProbeId = 0xBFFF;

    private readonly Dictionary<int, MediaAction> _registered = new();

    private HwndSource? _source;
    private AppSettings? _applied;
    private bool _suspended;
    private bool _disposed;

    public event Action<MediaAction>? HotkeyPressed;

    /// <summary>Creates the hidden window that receives WM_HOTKEY. Must run on the UI thread.</summary>
    public void Start()
    {
        if (_source is not null || _disposed)
        {
            return;
        }

        var parameters = new HwndSourceParameters("MediaControllerHotkeyWindow", 1, 1)
        {
            WindowStyle = 0, // no WS_VISIBLE - never shown, never focusable, never in Alt+Tab
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            PositionX = 0,
            PositionY = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        Logger.Info("Hidden hotkey window created.");
    }

    /// <summary>
    /// Re-registers all configured hotkeys. Returns the actions whose combination could not be taken,
    /// so the caller can restore the previous, working configuration.
    /// </summary>
    public IReadOnlyList<MediaAction> Apply(AppSettings settings)
    {
        Start();
        UnregisterAll();

        _applied = settings.Clone();
        _suspended = false;

        var failed = new List<MediaAction>();

        foreach (var pair in Pairs(settings))
        {
            if (!TryRegister(pair.Action, pair.Hotkey))
            {
                failed.Add(pair.Action);
            }
        }

        return failed;
    }

    /// <summary>
    /// Releases the global hotkeys while the user records a new one. Without this,
    /// RegisterHotKey would swallow the very keys the capture control is waiting for -
    /// pressing the current Next combination would skip a track instead of being recorded.
    /// </summary>
    public void Suspend()
    {
        if (_suspended || _disposed)
        {
            return;
        }

        _suspended = true;
        UnregisterAll();
        Logger.Info("Hotkeys suspended for capture.");
    }

    /// <summary>Restores whatever was registered before the matching Suspend().</summary>
    public void Resume()
    {
        if (!_suspended || _disposed)
        {
            return;
        }

        _suspended = false;

        if (_applied is null)
        {
            return;
        }

        foreach (var pair in Pairs(_applied))
        {
            TryRegister(pair.Action, pair.Hotkey);
        }

        Logger.Info("Hotkeys restored after capture.");
    }

    /// <summary>
    /// Asks Windows whether a combination is free, by actually taking it on a scratch id
    /// and immediately giving it back. Call this while suspended, otherwise the app's own
    /// registrations make its current hotkeys look occupied.
    /// </summary>
    public bool IsAvailable(HotkeySettings hotkey)
    {
        if (_source is null || !hotkey.IsValid)
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (vk == 0)
        {
            return false;
        }

        if (!NativeMethods.RegisterHotKey(_source.Handle, ProbeId, ModifiersOf(hotkey), vk))
        {
            return false;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, ProbeId);
        return true;
    }

    public void UnregisterAll()
    {
        if (_source is null)
        {
            return;
        }

        foreach (var id in _registered.Keys.ToList())
        {
            if (!NativeMethods.UnregisterHotKey(_source.Handle, id))
            {
                Logger.Warn("UnregisterHotKey failed for id " + id + " (Win32 error " + Marshal.GetLastWin32Error() + ").");
            }
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            UnregisterAll();

            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }

            Logger.Info("Hotkeys released.");
        }
        catch (Exception ex)
        {
            Logger.Error("Hotkey cleanup failed.", ex);
        }
    }

    private static uint ModifiersOf(HotkeySettings hotkey, bool noRepeat = true)
    {
        var modifiers = noRepeat ? NativeMethods.MOD_NOREPEAT : 0u;
        if (hotkey.Ctrl) modifiers |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt) modifiers |= NativeMethods.MOD_ALT;
        if (hotkey.Shift) modifiers |= NativeMethods.MOD_SHIFT;
        if (hotkey.Win) modifiers |= NativeMethods.MOD_WIN;
        return modifiers;
    }

    private static IEnumerable<(MediaAction Action, HotkeySettings Hotkey)> Pairs(AppSettings settings)
    {
        yield return (MediaAction.Next, settings.NextHotkey);
        yield return (MediaAction.Previous, settings.PreviousHotkey);
        yield return (MediaAction.PlayPause, settings.PlayPauseHotkey);
        yield return (MediaAction.VolumeUp, settings.VolumeUpHotkey);
        yield return (MediaAction.VolumeDown, settings.VolumeDownHotkey);
        yield return (MediaAction.Mute, settings.MuteHotkey);
    }

    private bool TryRegister(MediaAction action, HotkeySettings hotkey)
    {
        if (_source is null)
        {
            return false;
        }

        if (!hotkey.IsValid)
        {
            Logger.Warn("Hotkey for " + action + " is incomplete (" + hotkey + "); not registered.");
            return false;
        }

        // Volume up/down intentionally allow key-repeat while held. Media transport and Mute
        // keep MOD_NOREPEAT so one physical press always means one action.
        var allowRepeat = action is MediaAction.VolumeUp or MediaAction.VolumeDown;
        var modifiers = ModifiersOf(hotkey, noRepeat: !allowRepeat);
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (vk == 0)
        {
            Logger.Warn("Hotkey for " + action + " maps to no virtual key (" + hotkey + ").");
            return false;
        }

        var id = BaseId + (int)action;

        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, vk))
        {
            var error = Marshal.GetLastWin32Error();
            var reason = error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? "already in use by another program"
                : "Win32 error " + error;

            Logger.Warn("RegisterHotKey failed for " + action + " (" + hotkey + "): " + reason + ".");
            return false;
        }

        _registered[id] = action;
        Logger.Info("Hotkey registered: " + hotkey + " -> " + action + ".");
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY || !_registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            return IntPtr.Zero;
        }

        handled = true;

        try
        {
            HotkeyPressed?.Invoke(action);
        }
        catch (Exception ex)
        {
            Logger.Error("Hotkey handler for " + action + " threw.", ex);
        }

        return IntPtr.Zero;
    }
}
