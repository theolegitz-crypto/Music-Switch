using System.Windows.Media;
using System.Windows.Threading;
using MediaController.Core;
using MediaController.Native;
using MediaController.UI;

namespace MediaController.Services;

/// <summary>
/// Owns the single track popup. Media commands never wait for this service: every press can
/// be sent immediately, while metadata resolution is independently superseded by the newest
/// action. The popup is always pinned to the exact GSMTC application targeted by the command.
/// </summary>
public sealed class TrackPopupService : IDisposable
{
    /// <summary>
    /// A player can publish several intermediate metadata states while a rapid skip burst is
    /// still settling. Keep observing the final command for a short bounded window and update
    /// the same popup whenever the target player publishes a newer useful track.
    /// </summary>
    private static readonly int[] ProbeTimesMs = { 80, 170, 300, 500, 800, 1200, 1700, 2400 };

    private readonly MediaSessionService _sessions;
    private readonly MediaControlService _control;
    private readonly MediaArtworkService _artwork;
    private readonly SettingsService _settings;
    private readonly GameBarBridgeService _gameBar;
    private readonly Dispatcher _dispatcher;

    private TrackPopupWindow? _window;
    private int _generation;
    private bool _disposed;

    public TrackPopupService(
        MediaSessionService sessions,
        MediaControlService control,
        MediaArtworkService artwork,
        SettingsService settings,
        GameBarBridgeService gameBar,
        Dispatcher dispatcher)
    {
        _sessions = sessions;
        _control = control;
        _artwork = artwork;
        _settings = settings;
        _gameBar = gameBar;
        _dispatcher = dispatcher;

        _control.ActionCompleted += OnActionCompleted;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _control.ActionCompleted -= OnActionCompleted;

        try
        {
            Post(() =>
            {
                _window?.HideNow();
                _window?.Close();
                _window = null;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not close the track popup: " + ex.Message);
        }
    }

    private void OnActionCompleted(MediaActionResult result)
    {
        if (_disposed || !_settings.Current.ShowTrackPopup || !result.Success)
        {
            return;
        }

        // Every new action invalidates metadata/artwork work from an older press. This only
        // cancels cosmetic resolution; it never cancels or delays the media commands themselves.
        var generation = Interlocked.Increment(ref _generation);

        if (string.IsNullOrWhiteSpace(result.TargetSessionId))
        {
            Logger.Info("Popup skipped because the command had no stable GSMTC target.");
            return;
        }

        // Guarantee visible feedback for every successful press. Use only a snapshot belonging
        // to the same player, never Windows' global current session, so a paused Telegram voice
        // message cannot leak into a Yandex/Spotify popup. Showing the previous song for a few
        // milliseconds is preferable to the popup disappearing while the player changes tracks.
        var immediate = _sessions.GetLastUsefulTrack(result.TargetSessionId);
        if (immediate is null && result.Before?.HasTrack == true)
        {
            immediate = result.Before;
        }

        if (immediate is not null)
        {
            ShowImmediately(immediate, generation);
        }

        _ = ResolveAsync(result, generation, immediate);
    }

    private async Task ResolveAsync(MediaActionResult result, int generation, TrackInfo? initial)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(result.TargetSessionId))
            {
                return;
            }

            var lastShownKey = DisplayKey(initial);
            var elapsed = 0;

            foreach (var targetMs in ProbeTimesMs)
            {
                await Task.Delay(targetMs - elapsed).ConfigureAwait(false);
                elapsed = targetMs;

                if (!IsCurrent(generation))
                {
                    return;
                }

                var track = await _sessions.ReadTrackAsync(result.TargetSessionId).ConfigureAwait(false);
                if (!IsCurrent(generation))
                {
                    return;
                }

                // Session recreation and empty metadata are normal during a skip. Never render
                // those transport states as "Unknown track / No active media".
                if (track is null || !track.HasTrack)
                {
                    continue;
                }

                var key = DisplayKey(track);
                if (key == lastShownKey)
                {
                    continue;
                }

                // For the first couple of probes after Next/Previous, unchanged pre-command
                // metadata is not useful. Once it really changes, render it and keep probing:
                // a five-skip burst may legitimately publish several intermediate tracks before
                // the final one arrives.
                if (result.Action != MediaAction.PlayPause &&
                    result.Before?.HasTrack == true &&
                    track.SameTrackAs(result.Before) &&
                    targetMs < 500)
                {
                    continue;
                }

                lastShownKey = key;
                await RenderResolvedAsync(track, generation).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Popup failures must never affect a working media command.
            Logger.Warn("Track popup could not resolve metadata: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens/restarts the popup synchronously from cached metadata. Artwork decode never delays
    /// the feedback; cached artwork is used immediately and missing artwork falls back locally.
    /// </summary>
    private void ShowImmediately(TrackInfo track, int generation)
    {
        var cached = _artwork.TryGetCached(track);

        Post(() =>
        {
            if (IsCurrent(generation))
            {
                Show(track, cached);
            }
        });

        if (cached is null && track.Thumbnail is not null)
        {
            _ = FillArtworkAsync(track, generation);
        }
    }

    /// <summary>
    /// A real metadata change restarts the popup timer. This is deliberate: if the final track
    /// in a rapid burst arrives late, the user still gets the full configured display duration.
    /// </summary>
    private Task RenderResolvedAsync(TrackInfo track, int generation)
    {
        var cached = _artwork.TryGetCached(track);

        Post(() =>
        {
            if (IsCurrent(generation))
            {
                Show(track, cached);
            }
        });

        if (cached is null && track.Thumbnail is not null)
        {
            // Do not make metadata observation wait for image decoding. During a burst the
            // title/artist may change again before an older cover has finished loading; the
            // generation guard below prevents stale artwork from being painted.
            _ = FillArtworkAsync(track, generation);
        }

        return Task.CompletedTask;
    }

    private async Task FillArtworkAsync(TrackInfo track, int generation)
    {
        try
        {
            var artwork = await _artwork.GetAsync(track).ConfigureAwait(false);
            if (artwork is null || !IsCurrent(generation))
            {
                return;
            }

            Post(() =>
            {
                if (IsCurrent(generation))
                {
                    Update(track, artwork);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Track artwork could not be loaded: " + ex.Message);
        }
    }

    private static string DisplayKey(TrackInfo? track) =>
        track is null ? string.Empty : track.ArtworkKey + "\u001f" + track.Status;

    private bool IsCurrent(int generation) =>
        !_disposed && Volatile.Read(ref _generation) == generation;

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private void Show(TrackInfo track, ImageSource? artwork)
    {
        try
        {
            var settings = _settings.Current;
            var duration = TimeSpan.FromSeconds(Math.Clamp(settings.TrackPopupDurationSeconds, 0.5, 10.0));

            // A connected/pinned Game Bar widget is preferred only for a fullscreen foreground
            // window. Normal desktop/windowed use keeps the lightweight WPF popup. Borderless
            // fullscreen also routes through Game Bar once the widget is pinned, which avoids
            // duplicate overlays and keeps one rendering path for all game fullscreen modes.
            if (_gameBar.IsConnected && IsForegroundFullscreen())
            {
                _window?.HideNow();
                _gameBar.Publish(track, artwork, duration);
                return;
            }

            var window = _window ??= new TrackPopupWindow();
            window.ShowTrack(track, artwork, duration, settings.ShowPopupOnActiveMonitor);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show the track popup: " + ex.Message);
        }
    }

    private void Update(TrackInfo track, ImageSource? artwork)
    {
        try
        {
            var settings = _settings.Current;
            if (_gameBar.IsConnected && IsForegroundFullscreen())
            {
                _window?.HideNow();
                _gameBar.Publish(
                    track,
                    artwork,
                    TimeSpan.FromSeconds(Math.Clamp(settings.TrackPopupDurationSeconds, 0.5, 10.0)));
                return;
            }

            _window?.UpdateTrack(track, artwork, settings.ShowPopupOnActiveMonitor);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not update the track popup: " + ex.Message);
        }
    }

    private static bool IsForegroundFullscreen()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || !NativeMethods.GetWindowRect(foreground, out var window))
            {
                return false;
            }

            var monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            const int tolerance = 8;
            var screen = info.rcMonitor;
            return Math.Abs(window.Left - screen.Left) <= tolerance &&
                   Math.Abs(window.Top - screen.Top) <= tolerance &&
                   Math.Abs(window.Right - screen.Right) <= tolerance &&
                   Math.Abs(window.Bottom - screen.Bottom) <= tolerance;
        }
        catch
        {
            return false;
        }
    }
}
