using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaController.Core;
using MediaController.Native;

namespace MediaController.UI;

/// <summary>
/// The single popup instance. It is created once and hidden between uses - never closed,
/// never re-created, so a burst of Next presses can only ever update one window.
/// </summary>
public partial class TrackPopupWindow : Window
{
    private static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ContentSwapDuration = TimeSpan.FromMilliseconds(130);

    private static readonly CubicEase EaseOut = CreateEase(EasingMode.EaseOut);
    private static readonly CubicEase EaseIn = CreateEase(EasingMode.EaseIn);

    private readonly DispatcherTimer _hideTimer;

    private IntPtr _handle;
    private bool _fadingOut;
    private string _shownKey = string.Empty;

    public TrackPopupWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hideTimer.Tick += OnHideTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;

        // WS_EX_NOACTIVATE is what actually keeps the game in the foreground: even a click
        // cannot make this window active. WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        // Do not ask DWM to draw a second rounded top-level frame here. The popup already
        // has per-pixel rounded corners via WPF; mixing both can produce a pale rectangular
        // outline around the transparent window on some Windows 11 configurations.
    }

    /// <summary>Updates the content and (re)starts the show plus auto-hide cycle. UI thread only.</summary>
    public void ShowTrack(TrackInfo? track, ImageSource? artwork, TimeSpan duration, bool onActiveMonitor)
    {
        var wasVisible = IsVisible && !_fadingOut;

        Render(track, artwork);
        _fadingOut = false;

        if (!IsVisible)
        {
            Show();
        }

        // Layout must settle before the height is known, otherwise the window is positioned
        // from a stale size and hangs off the edge of the work area.
        UpdateLayout();
        Position(onActiveMonitor);

        if (wasVisible)
        {
            // Already on screen: swap the content in place instead of replaying the entrance.
            PlayContentSwap();
        }
        else
        {
            PlayEntrance();
        }

        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
    }

    /// <summary>Content-only refresh: the auto-hide countdown keeps running.</summary>
    public void UpdateTrack(TrackInfo? track, ImageSource? artwork, bool onActiveMonitor)
    {
        if (!IsVisible || _fadingOut)
        {
            return;
        }

        var changed = KeyOf(track) != _shownKey;

        Render(track, artwork);
        UpdateLayout();
        Position(onActiveMonitor);

        if (changed)
        {
            PlayContentSwap();
        }
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
    }

    private void Render(TrackInfo? track, ImageSource? artwork)
    {
        // Title falls back to the artist, then to a generic label, so the popup is never
        // blank for a player that publishes only half its metadata.
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
    }

    private static string KeyOf(TrackInfo? track) =>
        track is null ? string.Empty : track.ArtworkKey + "" + track.Status;

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

    /// <summary>A new track while the popup is already up: nudge the art, don't re-enter.</summary>
    private void PlayContentSwap()
    {
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, Animate(0.94, 1, ContentSwapDuration, EaseOut));
        ArtworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(0.94, 1, ContentSwapDuration, EaseOut));
        TextHost.BeginAnimation(OpacityProperty, Animate(0.45, 1, ContentSwapDuration, EaseOut));
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        _fadingOut = true;

        RootOffset.BeginAnimation(TranslateTransform.YProperty, Animate(0, 4, FadeOutDuration, EaseIn));

        var fade = Animate(Opacity, 0.0, FadeOutDuration, EaseIn);
        fade.Completed += (_, _) =>
        {
            // A new action may have restarted the popup while this fade was running.
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

    private static DoubleAnimation Animate(double from, double to, TimeSpan duration, IEasingFunction ease) =>
        new(from, to, new Duration(duration)) { EasingFunction = ease };

    private static CubicEase CreateEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// Places the popup bottom-right of the work area, in device pixels via SetWindowPos.
    /// Going through Win32 avoids all DIP conversion, so the popup lands correctly on a
    /// 150% scaled second monitor without any DPI arithmetic of our own.
    /// </summary>
    private void Position(bool onActiveMonitor)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var monitor = IntPtr.Zero;

            if (onActiveMonitor)
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground != IntPtr.Zero)
                {
                    monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
                }
            }

            if (monitor == IntPtr.Zero)
            {
                monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            }

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info) ||
                !NativeMethods.GetWindowRect(_handle, out var bounds))
            {
                return;
            }

            var dpi = NativeMethods.GetDpiForWindow(_handle);
            var margin = (int)Math.Round(16.0 * (dpi == 0 ? 96 : dpi) / 96.0);

            // The card is inset inside the window by its own margin, so trim that back off
            // to keep the visible gap at 16 DIP rather than 16 plus the shadow padding.
            var inset = (int)Math.Round(12.0 * (dpi == 0 ? 96 : dpi) / 96.0);

            var x = info.rcWork.Right - bounds.Width - margin + inset;
            var y = info.rcWork.Bottom - bounds.Height - margin + inset;

            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not position the track popup: " + ex.Message);
        }
    }
}
