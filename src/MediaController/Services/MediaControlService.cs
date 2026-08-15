using MediaController.Core;
using Windows.Media.Control;

namespace MediaController.Services;

/// <summary>
/// The one place that decides how a hotkey turns into a media command:
/// GSMTC first, system media key only when GSMTC cannot do it.
/// Rapid command bursts are pinned to one player. Commands themselves are kept tiny:
/// the serialized section only sends the GSMTC command and never waits for metadata, so
/// every quick key press is preserved instead of building a slow metadata backlog.
/// </summary>
public sealed class MediaControlService
{
    /// <summary>
    /// A track skip can briefly make a player report Paused/Changing or even recreate its
    /// GSMTC session. During a burst of key presses we deliberately keep targeting the same
    /// application instead of asking Windows to rank all sessions again after every press.
    /// </summary>
    private const long BurstTargetWindowMs = 4000;

    private readonly MediaSessionService _sessions;
    private readonly MediaKeyFallbackService _fallback;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private string? _burstTargetSessionId;
    private long _burstTargetExpiresAt;

    public MediaControlService(MediaSessionService sessions, MediaKeyFallbackService fallback)
    {
        _sessions = sessions;
        _fallback = fallback;
    }

    /// <summary>
    /// Raised after every command, whatever its outcome. Hotkeys and the Settings test
    /// buttons both go through ExecuteAsync, so both raise this and both get a popup.
    /// </summary>
    public event Action<MediaActionResult>? ActionCompleted;

    public Task<MediaActionResult> NextAsync() => ExecuteAsync(MediaAction.Next);

    public Task<MediaActionResult> PreviousAsync() => ExecuteAsync(MediaAction.Previous);

    public Task<MediaActionResult> PlayPauseAsync() => ExecuteAsync(MediaAction.PlayPause);

    public async Task<MediaActionResult> ExecuteAsync(MediaAction action)
    {
        // WM_HOTKEY can arrive again while the previous async GSMTC call is still in flight.
        // Without serialization two calls can observe different Windows "current" sessions.
        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(action).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<MediaActionResult> ExecuteCoreAsync(MediaAction action)
    {
        var succeeded = false;
        var usedFallback = false;
        string? targetSessionId = null;
        TrackInfo? before = null;

        try
        {
            // During an active burst never reselect another Windows "current" player just
            // because the pinned app temporarily recreated its GSMTC session. Wait briefly for
            // that same application to reappear; otherwise a paused Telegram voice message can
            // become the accidental target between two Yandex skips.
            var pinnedId = GetPinnedTargetId();
            var session = pinnedId is null
                ? _sessions.GetPreferredOrCurrentSession()
                : await WaitForPinnedSessionAsync(pinnedId).ConfigureAwait(false);

            if (session is not null)
            {
                targetSessionId = _sessions.GetSessionId(session);
                PinBurstTarget(targetSessionId);

                // Never await metadata in the command path. TryGetMediaPropertiesAsync can be
                // surprisingly slow while a player is changing tracks, and doing that once per
                // press made rapid Next/Next/Next bursts feel as if they stopped. A cached snapshot
                // is enough for the popup to recognise a later metadata change.
                before = _sessions.GetLastUsefulTrack(targetSessionId);

                succeeded = action switch
                {
                    MediaAction.Next => await WinRt.TryBoolAsync(() => session.TrySkipNextAsync(), "TrySkipNextAsync").ConfigureAwait(false),
                    MediaAction.Previous => await WinRt.TryBoolAsync(() => session.TrySkipPreviousAsync(), "TrySkipPreviousAsync").ConfigureAwait(false),
                    _ => await WinRt.TryBoolAsync(() => session.TryTogglePlayPauseAsync(), "TryTogglePlayPauseAsync").ConfigureAwait(false)
                };

                if (!succeeded)
                {
                    Logger.Warn("GSMTC refused " + action + "; falling back to the system media key.");
                }
            }
            else
            {
                if (pinnedId is not null)
                {
                    // A burst already belongs to a concrete player. Do not send a generic media
                    // key while that player is absent: Windows could route it to Telegram/Chrome.
                    // The queued command waited for the pinned app first; if it still did not
                    // return, fail this one safely instead of controlling the wrong application.
                    targetSessionId = pinnedId;
                    before = _sessions.GetLastUsefulTrack(pinnedId);
                    Logger.Warn("Pinned media session " + pinnedId + " did not reappear in time; " + action + " was not rerouted to another app.");
                    return Complete(action, false, false, before, targetSessionId);
                }

                before = _sessions.CurrentTrack;
                Logger.Info("No media session for " + action + "; using the system media key.");
            }
        }
        catch (Exception ex)
        {
            // A player closing/recreating its session mid-command lands here. It must never
            // reach the message loop. Keep the pinned id so the popup can wait for that app's
            // session to reappear instead of following an unrelated Windows current session.
            Logger.Error("GSMTC command " + action + " failed.", ex);
            succeeded = false;
        }

        if (!succeeded)
        {
            _fallback.Send(action);
            usedFallback = true;
        }

        return Complete(
            action,
            succeeded || usedFallback,
            usedFallback,
            before,
            targetSessionId ?? GetPinnedTargetId());
    }

    private MediaActionResult Complete(
        MediaAction action,
        bool succeeded,
        bool usedFallback,
        TrackInfo? before,
        string? targetSessionId)
    {
        var result = new MediaActionResult(action, succeeded, usedFallback, before, targetSessionId);

        try
        {
            ActionCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            // A popup problem must never turn a working media command into a failure.
            Logger.Warn("A media action listener threw: " + ex.Message);
        }

        return result;
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> WaitForPinnedSessionAsync(string id)
    {
        var session = _sessions.FindSessionById(id);
        if (session is not null)
        {
            return session;
        }

        // Track switches that recreate a GSMTC session are normally much quicker than this.
        // The bounded wait protects burst routing without turning ordinary key presses sluggish.
        const int timeoutMs = 650;
        const int probeMs = 25;
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(probeMs).ConfigureAwait(false);
            session = _sessions.FindSessionById(id);
            if (session is not null)
            {
                return session;
            }
        }

        return null;
    }

    private string? GetPinnedTargetId()
    {
        var id = _burstTargetSessionId;
        if (id is null)
        {
            return null;
        }

        if (Environment.TickCount64 > Volatile.Read(ref _burstTargetExpiresAt))
        {
            _burstTargetSessionId = null;
            return null;
        }

        return id;
    }

    private void PinBurstTarget(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _burstTargetSessionId = sessionId;
        Volatile.Write(ref _burstTargetExpiresAt, Environment.TickCount64 + BurstTargetWindowMs);
    }
}
