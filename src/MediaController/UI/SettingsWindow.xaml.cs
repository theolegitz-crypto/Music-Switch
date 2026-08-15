using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MediaController.Core;
using MediaController.Models;
using MediaController.Native;
using MediaController.Services;
using MediaController.UI.Controls;

namespace MediaController.UI;

public partial class SettingsWindow : Window
{
    private static readonly double[] DurationOptions = { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };

    private readonly SettingsService _settings;
    private readonly MediaSessionService _sessions;
    private readonly MediaControlService _control;
    private readonly HotkeyService _hotkeys;
    private readonly StartupService _startup;
    private readonly UpdateService _updates;

    public SettingsWindow(
        SettingsService settings,
        MediaSessionService sessions,
        MediaControlService control,
        HotkeyService hotkeys,
        StartupService startup,
        UpdateService updates)
    {
        InitializeComponent();

        _settings = settings;
        _sessions = sessions;
        _control = control;
        _hotkeys = hotkeys;
        _startup = startup;
        _updates = updates;

        foreach (var box in HotkeyBoxes())
        {
            box.CaptureStarted += OnCaptureStarted;
            box.CaptureFinished += gesture => OnCaptureFinished(box, gesture);
        }

        DurationBox.ItemsSource = DurationOptions;

        Load(settings.Current);

        _sessions.Changed += OnSessionsChanged;
        _updates.StateChanged += OnUpdateStateChanged;
        RefreshUpdateUi(_updates.State);
        Closed += OnClosed;
    }

    private IEnumerable<HotkeyCaptureControl> HotkeyBoxes()
    {
        yield return NextHotkeyBox;
        yield return PreviousHotkeyBox;
        yield return PlayPauseHotkeyBox;
    }

    private void Load(AppSettings settings)
    {
        NextHotkeyBox.Hotkey = settings.NextHotkey;
        PreviousHotkeyBox.Hotkey = settings.PreviousHotkey;
        PlayPauseHotkeyBox.Hotkey = settings.PlayPauseHotkey;

        var options = new List<PlayerOption> { new(null, "Auto") };
        foreach (var session in _sessions.GetSessions())
        {
            options.Add(new PlayerOption(session.Id, session.DisplayName));
        }

        var preferred = settings.PreferredPlayer;
        if (preferred is not null &&
            !options.Any(option => string.Equals(option.Id, preferred, StringComparison.OrdinalIgnoreCase)))
        {
            // Keep a preferred player that is not running right now instead of silently resetting it.
            options.Add(new PlayerOption(preferred, preferred + " (not running)"));
        }

        PlayerBox.ItemsSource = options;
        PlayerBox.SelectedItem = options.First(option =>
            string.Equals(option.Id, preferred, StringComparison.OrdinalIgnoreCase));

        ShowPopupBox.IsChecked = settings.ShowTrackPopup;
        ActiveMonitorBox.IsChecked = settings.ShowPopupOnActiveMonitor;
        DurationBox.SelectedItem = DurationOptions
            .OrderBy(option => Math.Abs(option - settings.TrackPopupDurationSeconds))
            .First();

        StartupBox.IsChecked = _startup.IsEnabled();
        AutoUpdateBox.IsChecked = settings.CheckForUpdatesAutomatically;

        RefreshNowPlaying();
    }

    // --- live now playing block ---

    private void OnSessionsChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshNowPlaying();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(RefreshNowPlaying));
        }
    }

    private void RefreshNowPlaying()
    {
        var track = _sessions.CurrentTrack;

        if (track is null)
        {
            NowPlayingTitle.Text = "No active media";
            NowPlayingArtist.Text = string.Empty;
            StatusText.Text = "Start playing something to control it";
            return;
        }

        NowPlayingTitle.Text = track.HasTrack
            ? (track.Title.Length > 0 ? track.Title : track.Artist)
            : "Unknown track";
        NowPlayingArtist.Text = track.Title.Length > 0 ? track.Artist : string.Empty;
        StatusText.Text = track.Player + " · " + track.Status;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        DwmHelper.TryEnableDarkMode(handle);
        DwmHelper.TrySetRoundedCorners(handle);

        // Only make the WPF surface transparent when Windows actually accepted
        // the backdrop. Otherwise the normal dark WPF background remains visible.
        if (DwmHelper.TryEnableMainWindowBackdrop(handle))
        {
            Background = Brushes.Transparent;
        }
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released between the event and DragMove.
        }
    }

    private void OnMinimise(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // --- hotkey capture ---

    private void OnCaptureStarted()
    {
        // Global hotkeys have to let go of the keyboard, otherwise RegisterHotKey swallows
        // the very combination the user is trying to record.
        _hotkeys.Suspend();
        HideError();
    }

    private void OnCaptureFinished(HotkeyCaptureControl box, HotkeySettings? gesture)
    {
        try
        {
            if (gesture is null)
            {
                box.SetError(null);
                return;
            }

            // Probed while suspended, so the app's own registrations cannot report a false clash.
            box.SetError(_hotkeys.IsAvailable(gesture)
                ? null
                : "This hotkey is already in use by another application.");
        }
        finally
        {
            _hotkeys.Resume();
        }
    }

    // --- test media controls: exactly the same path as a global hotkey ---

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _control.NextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _control.PreviousAsync();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _control.PlayPauseAsync();

    // --- updates ---

    private void OnUpdateStateChanged(UpdateState state)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshUpdateUi(state);
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshUpdateUi(state)));
        }
    }

    private void RefreshUpdateUi(UpdateState state)
    {
        VersionText.Text = "v" + state.CurrentVersion;
        UpdateStatusText.Text = state.Message;

        UpdateNotesText.Text = state.ReleaseNotes ?? string.Empty;
        UpdateNotesText.Visibility = string.IsNullOrWhiteSpace(state.ReleaseNotes)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CheckUpdateButton.IsEnabled = state.CanCheck;
        CheckUpdateButton.Content = state.Phase == UpdatePhase.Checking ? "Checking…" : "Check now";

        if (state.CanDownload)
        {
            UpdateActionButton.Content = "Download update";
            UpdateActionButton.IsEnabled = true;
            UpdateActionButton.Visibility = Visibility.Visible;
        }
        else if (state.Phase == UpdatePhase.Downloading)
        {
            UpdateActionButton.Content = state.Progress > 0 ? $"Downloading {state.Progress}%" : "Downloading…";
            UpdateActionButton.IsEnabled = false;
            UpdateActionButton.Visibility = Visibility.Visible;
        }
        else if (state.CanRestart)
        {
            UpdateActionButton.Content = "Restart & update";
            UpdateActionButton.IsEnabled = true;
            UpdateActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await _updates.CheckForUpdatesAsync();
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (_updates.State.CanRestart)
        {
            _updates.RestartAndApply();
            return;
        }

        if (_updates.State.CanDownload)
        {
            await _updates.DownloadUpdateAsync();
        }
    }

    // --- save ---

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var box in HotkeyBoxes())
        {
            box.CancelCapture();
        }

        var updated = _settings.Current.Clone();
        updated.NextHotkey = NextHotkeyBox.Hotkey.Clone();
        updated.PreviousHotkey = PreviousHotkeyBox.Hotkey.Clone();
        updated.PlayPauseHotkey = PlayPauseHotkeyBox.Hotkey.Clone();
        updated.PreferredPlayer = (PlayerBox.SelectedItem as PlayerOption)?.Id;
        updated.StartWithWindows = StartupBox.IsChecked == true;
        updated.ShowTrackPopup = ShowPopupBox.IsChecked == true;
        updated.ShowPopupOnActiveMonitor = ActiveMonitorBox.IsChecked == true;
        updated.TrackPopupDurationSeconds = DurationBox.SelectedItem is double seconds ? seconds : 2.0;
        updated.CheckForUpdatesAutomatically = AutoUpdateBox.IsChecked == true;

        if (!ValidateHotkey(NextHotkeyBox, updated.NextHotkey) ||
            !ValidateHotkey(PreviousHotkeyBox, updated.PreviousHotkey) ||
            !ValidateHotkey(PlayPauseHotkeyBox, updated.PlayPauseHotkey))
        {
            return;
        }

        if (!ValidateUnique(updated))
        {
            return;
        }

        var previous = _settings.Current.Clone();
        var failed = _hotkeys.Apply(updated);

        if (failed.Count > 0)
        {
            // Put the working configuration back: a rejected edit must never leave the app
            // without the hotkeys it already had.
            _hotkeys.Apply(previous);

            foreach (var action in failed)
            {
                BoxFor(action).SetError("This hotkey is already in use by another application.");
            }

            ShowError("Nothing was saved. Pick a different combination for: " +
                      string.Join(", ", failed.Select(Describe)) + ".");
            return;
        }

        if (!_startup.Apply(updated.StartWithWindows) || _startup.IsEnabled() != updated.StartWithWindows)
        {
            ShowError("Could not update the Windows startup entry. See the log for details.");
            return;
        }

        _settings.Save(updated);
        _sessions.SetPreferredSession(updated.PreferredPlayer);

        Logger.Info("Settings saved. Preferred player: " + (updated.PreferredPlayer ?? "Auto") + ".");
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _sessions.Changed -= OnSessionsChanged;
        _updates.StateChanged -= OnUpdateStateChanged;

        // Closing mid-recording must not leave the global hotkeys released.
        foreach (var box in HotkeyBoxes())
        {
            box.CancelCapture();
        }

        _hotkeys.Resume();
    }

    private bool ValidateHotkey(HotkeyCaptureControl box, HotkeySettings hotkey)
    {
        if (hotkey.IsValid)
        {
            return true;
        }

        box.SetError("Assign a key with at least one modifier: Ctrl, Alt, Shift or Win.");
        ShowError("Nothing was saved: one of the hotkeys is not assigned.");
        return false;
    }

    private bool ValidateUnique(AppSettings settings)
    {
        if (settings.NextHotkey.SameAs(settings.PreviousHotkey) ||
            settings.NextHotkey.SameAs(settings.PlayPauseHotkey) ||
            settings.PreviousHotkey.SameAs(settings.PlayPauseHotkey))
        {
            ShowError("The same combination is assigned to more than one action.");
            return false;
        }

        return true;
    }

    private HotkeyCaptureControl BoxFor(MediaAction action) => action switch
    {
        MediaAction.Next => NextHotkeyBox,
        MediaAction.Previous => PreviousHotkeyBox,
        _ => PlayPauseHotkeyBox
    };

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private static string Describe(MediaAction action) => action switch
    {
        MediaAction.Next => "Next track",
        MediaAction.Previous => "Previous track",
        _ => "Play / Pause"
    };

    private sealed class PlayerOption
    {
        public PlayerOption(string? id, string name)
        {
            Id = id;
            Name = name;
        }

        public string? Id { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }
}
