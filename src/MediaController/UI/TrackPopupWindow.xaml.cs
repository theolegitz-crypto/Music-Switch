using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaController.Core;
using MediaController.Native;

namespace MediaController.UI;

/// <summary>
/// One passive liquid-glass OSD window shared by track and music-volume notifications.
/// It never activates, never accepts input, and is reused rather than stacked.
/// </summary>
public partial class TrackPopupWindow : Window
{
    private enum PopupMode
    {
        Track,
        Volume
    }

    private static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ContentSwapDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan TopmostPulseInterval = TimeSpan.FromMilliseconds(180);

    private static readonly CubicEase EaseOut = CreateEase(EasingMode.EaseOut);
    private static readonly CubicEase EaseIn = CreateEase(EasingMode.EaseIn);

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _topmostPulseTimer;
    private readonly DispatcherTimer _trackTimelineTimer;

    private IntPtr _handle;
    private bool _fadingOut;
    private bool _gameOverlayMode;
    private string _shownKey = string.Empty;
    private PopupMode _mode = PopupMode.Track;

    private TimeSpan _trackPosition;
    private TimeSpan _trackDuration;
    private DateTimeOffset _trackPositionCapturedAt;
    private bool _trackIsPlaying;

    public TrackPopupWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hideTimer.Tick += OnHideTick;

        _topmostPulseTimer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher)
        {
            Interval = TopmostPulseInterval
        };
        _topmostPulseTimer.Tick += OnTopmostPulse;

        _trackTimelineTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _trackTimelineTimer.Tick += OnTrackTimelineTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE keeps the game focused. TOOLWINDOW keeps the OSD out of Alt+Tab.
        // TRANSPARENT makes the passive overlay click-through at HWND level as well as WPF level.
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE |
                 NativeMethods.WS_EX_TOOLWINDOW |
                 NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Shows/restarts the normal track notification.</summary>
    public void ShowTrack(TrackInfo? track, ImageSource? artwork, TimeSpan duration, bool onActiveMonitor)
    {
        var contentChanged = _mode != PopupMode.Track || KeyOf(track) != _shownKey;
        _mode = PopupMode.Track;

        TrackContent.Visibility = Visibility.Visible;
        VolumeContent.Visibility = Visibility.Collapsed;
        RenderTrack(track, artwork);

        ShowCore(duration, onActiveMonitor, contentChanged);
    }

    /// <summary>
    /// Shows a compact music-only volume OSD. Repeated key-repeat presses update this same
    /// window and restart the timer, so holding Volume Up feels like a native volume overlay.
    /// </summary>
    public void ShowVolume(VolumeState state, TimeSpan duration, bool onActiveMonitor)
    {
        if (!state.IsAvailable)
        {
            return;
        }

        var contentChanged = _mode != PopupMode.Volume;
        _mode = PopupMode.Volume;

        TrackContent.Visibility = Visibility.Collapsed;
        VolumeContent.Visibility = Visibility.Visible;
        _trackTimelineTimer.Stop();
        RenderVolume(state);

        ShowCore(duration, onActiveMonitor, contentChanged);
    }

    /// <summary>Content-only track refresh; the auto-hide countdown keeps running.</summary>
    public void UpdateTrack(TrackInfo? track, ImageSource? artwork, bool onActiveMonitor)
    {
        if (!IsVisible || _fadingOut || _mode != PopupMode.Track)
        {
            return;
        }

        var changed = KeyOf(track) != _shownKey;

        RenderTrack(track, artwork);
        UpdateLayout();
        UpdateTrackTimelineVisual();
        Position(onActiveMonitor, forceZOrderCycle: false);

        if (changed)
        {
            PlayTrackContentSwap();
        }
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        _topmostPulseTimer.Stop();
        _trackTimelineTimer.Stop();
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
    }

    private void ShowCore(TimeSpan duration, bool onActiveMonitor, bool contentChanged)
    {
        var wasVisible = IsVisible && !_fadingOut;
        _fadingOut = false;

        if (!IsVisible)
        {
            Show();
        }

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_handle, NativeMethods.SW_SHOWNOACTIVATE);
        }

        UpdateLayout();
        if (_mode == PopupMode.Track)
        {
            UpdateTrackTimelineVisual();
        }
        Position(onActiveMonitor, forceZOrderCycle: true);

        _topmostPulseTimer.Stop();
        _topmostPulseTimer.Start();

        if (!wasVisible)
        {
            PlayEntrance();
        }
        else if (contentChanged)
        {
            PlayCurrentContentSwap();
        }
        else if (_mode == PopupMode.Volume)
        {
            // A held volume hotkey should still provide a small visual acknowledgement even
            // when the card stays in volume mode for the entire key-repeat burst.
            VolumeHost.BeginAnimation(OpacityProperty, Animate(0.72, 1, TimeSpan.FromMilliseconds(90), EaseOut));
        }

        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
    }

    private void RenderTrack(TrackInfo? track, ImageSource? artwork)
    {
        var title = track?.Title ?? string.Empty;
        var artist = track?.Artist ?? string.Empty;

        if (title.Length == 0)
        {
            title = artist.Length > 0 ? artist : "Unknown track";
            artist = string.Empty;
        }

        TitleText.Text = title;
        ArtistText.Text = artist;
        ArtistText.Visibility = artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        SourceText.Text = track is null
            ? "No active media"
            : StatusGlyph(track) + "  " + track.Status + "  ·  " + track.Player;

        if (artwork is null)
        {
            ArtworkBrush.ImageSource = null;
            ArtworkBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtworkBrush.ImageSource = artwork;
            ArtworkBorder.Visibility = Visibility.Visible;
        }

        _shownKey = KeyOf(track);
        ConfigureTrackTimeline(track);
    }

    private void RenderVolume(VolumeState state)
    {
        var percent = Math.Clamp(state.Percent, 0, 100);

        VolumeTitleText.Text = state.IsMuted ? "Music muted" : "Music volume";
        VolumePlayerText.Text = state.Player;
        VolumePercentText.Text = state.IsMuted ? "Muted" : percent + "%";
        VolumeGlyphText.Text = state.IsMuted
            ? "🔇"
            : percent == 0
                ? "🔈"
                : percent < 50
                    ? "🔉"
                    : "🔊";

        const double railHeight = 76.0;
        VolumeFill.Height = railHeight * percent / 100.0;
        VolumeFill.Opacity = state.IsMuted ? 0.32 : 1.0;
        _shownKey = "volume\u001f" + state.Player + "\u001f" + percent + "\u001f" + state.IsMuted;
    }

    private void ConfigureTrackTimeline(TrackInfo? track)
    {
        _trackTimelineTimer.Stop();

        if (track is null || track.Duration <= TimeSpan.Zero)
        {
            TrackTimelineHost.Visibility = Visibility.Collapsed;
            TrackProgressFill.Width = 0;
            TrackTimeText.Text = string.Empty;
            return;
        }

        _trackDuration = track.Duration;
        _trackPosition = ClampPosition(track.Position, track.Duration);
        _trackPositionCapturedAt = DateTimeOffset.UtcNow;
        _trackIsPlaying = track.IsPlaying;

        TrackTimelineHost.Visibility = Visibility.Visible;
        UpdateTrackTimelineVisual();

        if (_trackIsPlaying)
        {
            _trackTimelineTimer.Start();
        }
    }

    private void OnTrackTimelineTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _fadingOut || _mode != PopupMode.Track || _trackDuration <= TimeSpan.Zero)
        {
            _trackTimelineTimer.Stop();
            return;
        }

        UpdateTrackTimelineVisual();

        if (CurrentTrackPosition() >= _trackDuration)
        {
            _trackTimelineTimer.Stop();
        }
    }

    private void UpdateTrackTimelineVisual()
    {
        if (_trackDuration <= TimeSpan.Zero)
        {
            TrackTimelineHost.Visibility = Visibility.Collapsed;
            return;
        }

        var position = CurrentTrackPosition();
        var ratio = Math.Clamp(position.TotalMilliseconds / _trackDuration.TotalMilliseconds, 0.0, 1.0);
        var railWidth = TrackProgressRail.ActualWidth;
        TrackProgressFill.Width = railWidth > 0 ? railWidth * ratio : 0;
        TrackTimeText.Text = FormatTime(position) + " / " + FormatTime(_trackDuration);
    }

    private TimeSpan CurrentTrackPosition()
    {
        var position = _trackPosition;
        if (_trackIsPlaying)
        {
            position += DateTimeOffset.UtcNow - _trackPositionCapturedAt;
        }

        return ClampPosition(position, _trackDuration);
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            return duration;
        }

        return position;
    }

    private static string FormatTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private static string KeyOf(TrackInfo? track) =>
        track is null ? string.Empty : track.ArtworkKey + "\u001f" + track.Status;

    private static string StatusGlyph(TrackInfo track) => track.IsPlaying ? "▶" : "⏸";

    private void PlayEntrance()
    {
        var from = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = from;
        BeginAnimation(OpacityProperty, Animate(from, 1.0, RiseDuration, EaseOut));

        RootOffset.BeginAnimation(TranslateTransform.YProperty, Animate(8, 0, RiseDuration, EaseOut));
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Animate(0.98, 1, RiseDuration, EaseOut));
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(0.98, 1, RiseDuration, EaseOut));
    }

    private void PlayCurrentContentSwap()
    {
        if (_mode == PopupMode.Track)
        {
            PlayTrackContentSwap();
            return;
        }

        VolumeHost.BeginAnimation(OpacityProperty, Animate(0.35, 1, ContentSwapDuration, EaseOut));
        VolumeContent.BeginAnimation(OpacityProperty, Animate(0.6, 1, ContentSwapDuration, EaseOut));
    }

    private void PlayTrackContentSwap()
    {
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, Animate(0.94, 1, ContentSwapDuration, EaseOut));
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(0.94, 1, ContentSwapDuration, EaseOut));
        TextHost.BeginAnimation(OpacityProperty, Animate(0.45, 1, ContentSwapDuration, EaseOut));
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        _topmostPulseTimer.Stop();
        _trackTimelineTimer.Stop();
        _fadingOut = true;

        RootOffset.BeginAnimation(TranslateTransform.YProperty, Animate(0, 4, FadeOutDuration, EaseIn));

        var fade = Animate(Opacity, 0.0, FadeOutDuration, EaseIn);
        fade.Completed += (_, _) =>
        {
            if (!_fadingOut)
            {
                return;
            }

            _fadingOut = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            RootOffset.BeginAnimation(TranslateTransform.YProperty, null);
            RootOffset.Y = 0;
            Hide();
        };

        BeginAnimation(OpacityProperty, fade);
    }

    private void OnTopmostPulse(object? sender, EventArgs e)
    {
        if (!IsVisible || _fadingOut || _handle == IntPtr.Zero)
        {
            _topmostPulseTimer.Stop();
            return;
        }

        try
        {
            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            _topmostPulseTimer.Stop();
            Logger.Warn("Could not reassert popup topmost state: " + ex.Message);
        }
    }

    private static DoubleAnimation Animate(double from, double to, TimeSpan duration, IEasingFunction ease) =>
        new(from, to, new Duration(duration)) { EasingFunction = ease };

    private static CubicEase CreateEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// Places the OSD bottom-right of the foreground monitor. If the foreground window fills
    /// that monitor, use the complete monitor rectangle and a stronger one-time z-order cycle.
    /// </summary>
    private void Position(bool onActiveMonitor, bool forceZOrderCycle)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var foreground = onActiveMonitor ? NativeMethods.GetForegroundWindow() : IntPtr.Zero;
            var monitor = IntPtr.Zero;

            if (foreground != IntPtr.Zero)
            {
                monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            }

            if (monitor == IntPtr.Zero)
            {
                monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            }

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info) ||
                !NativeMethods.GetWindowRect(_handle, out var bounds))
            {
                return;
            }

            _gameOverlayMode = IsMonitorFillingForeground(foreground, info.rcMonitor);
            var target = _gameOverlayMode ? info.rcMonitor : info.rcWork;

            var dpi = NativeMethods.GetDpiForWindow(_handle);
            var scaleDpi = dpi == 0 ? 96 : dpi;
            var margin = (int)Math.Round(16.0 * scaleDpi / 96.0);
            var inset = (int)Math.Round(12.0 * scaleDpi / 96.0);

            var x = target.Right - bounds.Width - margin + inset;
            var y = target.Bottom - bounds.Height - margin + inset;

            if (_gameOverlayMode && forceZOrderCycle)
            {
                NativeMethods.SetWindowPos(
                    _handle,
                    NativeMethods.HWND_NOTOPMOST,
                    x,
                    y,
                    0,
                    0,
                    NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOOWNERZORDER);
            }

            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not position popup: " + ex.Message);
        }
    }

    private static bool IsMonitorFillingForeground(IntPtr foreground, NativeMethods.RECT monitor)
    {
        if (foreground == IntPtr.Zero || !NativeMethods.GetWindowRect(foreground, out var window))
        {
            return false;
        }

        const int tolerance = 3;
        return Math.Abs(window.Left - monitor.Left) <= tolerance &&
               Math.Abs(window.Top - monitor.Top) <= tolerance &&
               Math.Abs(window.Right - monitor.Right) <= tolerance &&
               Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;
    }
}
