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

    public HotkeySettings VolumeUpHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.Up);

    public HotkeySettings VolumeDownHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.Down);

    public HotkeySettings MuteHotkey { get; set; } = HotkeySettings.Create(true, false, true, false, Key.M);

    /// <summary>Amount changed by one Volume Up / Down hotkey press.</summary>
    public int VolumeStepPercent { get; set; } = 5;

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
        VolumeUpHotkey = VolumeUpHotkey.Clone(),
        VolumeDownHotkey = VolumeDownHotkey.Clone(),
        MuteHotkey = MuteHotkey.Clone(),
        VolumeStepPercent = VolumeStepPercent,
        PreferredPlayer = PreferredPlayer,
        StartWithWindows = StartWithWindows,
        ShowTrackPopup = ShowTrackPopup,
        TrackPopupDurationSeconds = TrackPopupDurationSeconds,
        ShowPopupOnActiveMonitor = ShowPopupOnActiveMonitor,
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically
    };
}
