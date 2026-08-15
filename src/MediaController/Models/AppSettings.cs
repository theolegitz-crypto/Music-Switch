using System.Windows.Input;

namespace MediaController.Models;

public sealed class AppSettings
{
    // v0.2 defaults. Ctrl+Alt+Space turned out to clash with other software, and the
    // Ctrl+Shift+Page family is rarely claimed. Existing settings.json files are never
    // rewritten with these - a property present in the file always wins.
    public HotkeySettings NextHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.PageDown);

    public HotkeySettings PreviousHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.PageUp);

    public HotkeySettings PlayPauseHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.Space);

    /// <summary>GSMTC SourceAppUserModelId of the preferred player, or null for "Auto".</summary>
    public string? PreferredPlayer { get; set; }

    public bool StartWithWindows { get; set; }

    public bool ShowTrackPopup { get; set; } = true;

    public double TrackPopupDurationSeconds { get; set; } = 2.0;

    public bool ShowPopupOnActiveMonitor { get; set; } = true;

    public bool CheckForUpdatesAutomatically { get; set; } = true;

    public AppSettings Clone() => new()
    {
        NextHotkey = NextHotkey.Clone(),
        PreviousHotkey = PreviousHotkey.Clone(),
        PlayPauseHotkey = PlayPauseHotkey.Clone(),
        PreferredPlayer = PreferredPlayer,
        StartWithWindows = StartWithWindows,
        ShowTrackPopup = ShowTrackPopup,
        TrackPopupDurationSeconds = TrackPopupDurationSeconds,
        ShowPopupOnActiveMonitor = ShowPopupOnActiveMonitor,
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically
    };
}
