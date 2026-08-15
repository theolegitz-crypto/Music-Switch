using System.Threading.Channels;
using MediaController.Core;
using Windows.Media.Control;

namespace MediaController.Services;

/// <summary>
/// Turns global-hotkey presses into media commands.
///
/// The important bit here is that ExecuteAsync never waits for a previous GSMTC request.
/// Every key press is appended to an in-memory FIFO immediately, then one lightweight worker
/// sends those commands in order. This prevents rapid Next/Next/Next bursts from being lost
/// while still avoiding parallel GSMTC calls racing onto different Windows media sessions.
/// </summary>
public sealed class MediaControlService : IDisposable
{
    /// <summary>
    /// Presses that arrive close together are one burst and stay pinned to the same player.
    /// The timestamp is captured when the user presses the key, not when the queued command is
    /// eventually processed, so a temporary player stall cannot accidentally expire the pin.
    /// </summary>
    private const long BurstJoinWindowMs = 1400;

    /// <summary>
    /// Tiny pacing between queued skip commands. This is short enough to feel immediate but
    /// gives players that briefly toggle their transport state a chance to accept every press.
    /// Metadata/artwork are never awaited here.
    /// </summary>
    private const int SkipPacingMs = 55;

    /// <summary>
    /// If a player recreates its GSMTC session during a track switch, keep one queued command
    /// pending for a bounded time instead of consuming/lossing it or rerouting it to Telegram.
    /// </summary>
    private const int SessionRecoveryTimeoutMs = 1200;

    private readonly MediaSessionService _sessions;
    private readonly MediaKeyFallbackService _fallback;
    private readonly Channel<QueuedCommand> _commands;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _enqueueGate = new();

    private long _lastEnqueueTicks;
    private long _burstSequence;
    private int _pendingCount;
    private bool _disposed;

    // These fields are touched only by the single queue worker.
    private long _activeBurstId = -1;
    private string? _activeTargetSessionId;
    private GlobalSystemMediaTransportControlsSession? _activeTargetSession;

    public MediaControlService(MediaSessionService sessions, MediaKeyFallbackService fallback)
    {
        _sessions = sessions;
        _fallback = fallback;

        _commands = Channel.CreateUnbounded<QueuedCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _worker = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Raised after each queued command has actually been sent (or definitively failed).
    /// Popup work is a listener only and can never block the command queue.
    /// </summary>
    public event Action<MediaActionResult>? ActionCompleted;

    public Task<MediaActionResult> NextAsync() => ExecuteAsync(MediaAction.Next);

    public Task<MediaActionResult> PreviousAsync() => ExecuteAsync(MediaAction.Previous);

    public Task<MediaActionResult> PlayPauseAsync() => ExecuteAsync(MediaAction.PlayPause);

    /// <summary>
    /// Enqueues in O(1) and returns immediately with a Task representing this particular press.
    /// WM_HOTKEY therefore remains responsive even if the media player is still changing tracks.
    /// </summary>
    public Task<MediaActionResult> ExecuteAsync(MediaAction action)
    {
        if (_disposed)
        {
            return Task.FromResult(new MediaActionResult(action, false, false, null, null));
        }

        var completion = new TaskCompletionSource<MediaActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var command = new QueuedCommand(action, GetEnqueueBurstId(), completion);

        // Increment before publishing to the channel: the single reader is intentionally very
        // fast and can otherwise dequeue between TryWrite and Increment, making the counter
        // briefly negative and incorrectly skipping burst pacing.
        var pending = Interlocked.Increment(ref _pendingCount);
        if (!_commands.Writer.TryWrite(command))
        {
            Interlocked.Decrement(ref _pendingCount);
            completion.TrySetResult(new MediaActionResult(action, false, false, null, null));
            return completion.Task;
        }

        if (pending == 4 || (pending > 4 && pending % 10 == 0))
        {
            Logger.Info($"Rapid media-command burst queued ({pending} pending presses).");
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.Writer.TryComplete();
        _shutdown.Cancel();

        // Never block WPF shutdown waiting for an external player. Pending callers are completed
        // by DrainPending after cancellation so no Task is left hanging in tests/UI code. The
        // CTS is disposed only after the worker has observed cancellation; disposing it here could
        // race a Task.Delay that is about to read _shutdown.Token.
        DrainPending();
        _ = _worker.ContinueWith(
            _ => _shutdown.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendingCount);

                MediaActionResult result;
                try
                {
                    result = await ExecuteQueuedAsync(command, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    command.Completion.TrySetResult(
                        new MediaActionResult(command.Action, false, false, null, _activeTargetSessionId));
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("Queued media command failed unexpectedly.", ex);
                    result = Complete(
                        command.Action,
                        succeeded: false,
                        usedFallback: false,
                        before: null,
                        targetSessionId: _activeTargetSessionId);
                }

                command.Completion.TrySetResult(result);

                // Pace only when there is already another queued press. A single ordinary press
                // pays no artificial delay at all.
                if (IsSkip(command.Action) && Volatile.Read(ref _pendingCount) > 0)
                {
                    await Task.Delay(SkipPacingMs, _shutdown.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            Logger.Error("Media command queue stopped unexpectedly.", ex);
        }
        finally
        {
            DrainPending();
        }
    }

    private async Task<MediaActionResult> ExecuteQueuedAsync(QueuedCommand command, CancellationToken cancellationToken)
    {
        PrepareBurstTarget(command.BurstId);

        var action = command.Action;
        var targetId = _activeTargetSessionId;
        var session = RefreshBurstSessionReference();
        var before = _sessions.GetLastUsefulTrack(targetId);

        // A new burst may have started when no session was available at all. In that special case
        // retain the old generic media-key fallback behaviour, because there is no player identity
        // that could be protected from Telegram/Chrome rerouting.
        if (session is null && string.IsNullOrWhiteSpace(targetId))
        {
            before ??= _sessions.CurrentTrack;
            Logger.Info("No GSMTC media session for " + action + "; using the system media key.");
            _fallback.Send(action);
            return Complete(action, true, true, before, null);
        }

        // During a real skip Yandex/Spotify can briefly remove the session from GetSessions().
        // First try the burst's existing object (it often remains valid), then recover a fresh
        // object under the same SourceAppUserModelId without ever selecting another application.
        var succeeded = session is not null && await TrySendAsync(session, action).ConfigureAwait(false);

        if (!succeeded && !string.IsNullOrWhiteSpace(targetId))
        {
            var recovered = await RecoverPinnedSessionAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                _activeTargetSession = recovered;

                // A short retry is materially more reliable during very fast skipping than
                // immediately consuming the press as a failure while the player is transitioning.
                succeeded = await TrySendAsync(recovered, action).ConfigureAwait(false);
                if (!succeeded)
                {
                    await Task.Delay(65, cancellationToken).ConfigureAwait(false);

                    var latest = _sessions.FindSessionById(targetId) ?? recovered;
                    _activeTargetSession = latest;
                    succeeded = await TrySendAsync(latest, action).ConfigureAwait(false);
                }
            }
        }

        if (succeeded)
        {
            return Complete(action, true, false, before, targetId);
        }

        // Generic media keys are safe only when Windows itself still calls this exact player the
        // current session. Otherwise a paused Telegram voice message could receive the fallback.
        if (!string.IsNullOrWhiteSpace(targetId) && _sessions.IsWindowsCurrentSession(targetId))
        {
            Logger.Warn("GSMTC refused " + action + "; current session is still the pinned player, using media-key fallback.");
            _fallback.Send(action);
            return Complete(action, true, true, before, targetId);
        }

        Logger.Warn(
            "GSMTC could not deliver " + action + " to pinned session " +
            (targetId ?? "<none>") + "; the press was not rerouted to another player.");

        return Complete(action, false, false, before, targetId);
    }

    private void PrepareBurstTarget(long burstId)
    {
        if (_activeBurstId == burstId)
        {
            return;
        }

        _activeBurstId = burstId;
        _activeTargetSession = _sessions.GetPreferredOrCurrentSession();
        _activeTargetSessionId = _sessions.GetSessionId(_activeTargetSession);
    }

    /// <summary>
    /// Prefer a freshly published session object when available, but keep the previous object if
    /// the manager is temporarily between remove/add notifications. This avoids a needless wait
    /// before every rapid press.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? RefreshBurstSessionReference()
    {
        if (string.IsNullOrWhiteSpace(_activeTargetSessionId))
        {
            return _activeTargetSession;
        }

        var fresh = _sessions.FindSessionById(_activeTargetSessionId);
        if (fresh is not null)
        {
            _activeTargetSession = fresh;
        }

        return _activeTargetSession;
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> RecoverPinnedSessionAsync(
        string sourceAppUserModelId,
        CancellationToken cancellationToken)
    {
        var immediate = _sessions.FindSessionById(sourceAppUserModelId);
        if (immediate is not null)
        {
            return immediate;
        }

        var deadline = Environment.TickCount64 + SessionRecoveryTimeoutMs;

        // The wait belongs to the one press currently at the front of the FIFO. Any extra hotkey
        // presses continue to enqueue instantly behind it, and all of them are drained as soon as
        // the same player reappears. Nothing is silently discarded.
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

            var session = _sessions.FindSessionById(sourceAppUserModelId);
            if (session is not null)
            {
                return session;
            }
        }

        return null;
    }

    private static Task<bool> TrySendAsync(
        GlobalSystemMediaTransportControlsSession session,
        MediaAction action)
    {
        return action switch
        {
            MediaAction.Next => WinRt.TryBoolAsync(() => session.TrySkipNextAsync(), "TrySkipNextAsync"),
            MediaAction.Previous => WinRt.TryBoolAsync(() => session.TrySkipPreviousAsync(), "TrySkipPreviousAsync"),
            _ => WinRt.TryBoolAsync(() => session.TryTogglePlayPauseAsync(), "TryTogglePlayPauseAsync")
        };
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
            // Popup/update listeners are cosmetic and must never poison the queue worker.
            Logger.Warn("A media action listener threw: " + ex.Message);
        }

        return result;
    }

    private long GetEnqueueBurstId()
    {
        lock (_enqueueGate)
        {
            var now = Environment.TickCount64;
            if (_burstSequence == 0 || now - _lastEnqueueTicks > BurstJoinWindowMs)
            {
                _burstSequence++;
            }

            _lastEnqueueTicks = now;
            return _burstSequence;
        }
    }

    private void DrainPending()
    {
        while (_commands.Reader.TryRead(out var command))
        {
            Interlocked.Decrement(ref _pendingCount);
            command.Completion.TrySetResult(
                new MediaActionResult(command.Action, false, false, null, _activeTargetSessionId));
        }
    }

    private static bool IsSkip(MediaAction action) =>
        action is MediaAction.Next or MediaAction.Previous;

    private sealed record QueuedCommand(
        MediaAction Action,
        long BurstId,
        TaskCompletionSource<MediaActionResult> Completion);
}
