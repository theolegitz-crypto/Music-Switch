using MediaController.Core;

namespace MediaController.Services;

/// <summary>
/// Keeps one Media Controller process per Windows session and gives a later manual launch
/// a tiny IPC path to ask the already-running instance to open Settings.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\MediaController.SingleInstance";
    private const string OpenSettingsEventName = @"Local\MediaController.OpenSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _openSettingsEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;
    private bool _disposed;

    public bool TryAcquire()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SingleInstanceService));
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            return false;
        }

        // AutoReset remembers one signal until a listener consumes it. That makes startup
        // robust even if the second process is launched while the first is still initializing.
        _openSettingsEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            OpenSettingsEventName,
            out _);

        return true;
    }

    public void StartOpenSettingsListener(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!_ownsMutex || _openSettingsEvent is null || _disposed)
        {
            return;
        }

        _registeredWait?.Unregister(null);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _openSettingsEvent,
            (_, _) =>
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Logger.Warn("Single-instance OpenSettings callback failed: " + ex.Message);
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Called by the short-lived second process. A few small retries cover the tiny window
    /// between the first process acquiring its mutex and creating the named event.
    /// </summary>
    public static bool RequestOpenSettings()
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(OpenSettingsEventName);
                signal.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not signal the running instance: " + ex.Message);
                return false;
            }
        }

        Logger.Warn("The running instance did not expose its OpenSettings signal in time.");
        return false;
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
            _registeredWait?.Unregister(null);
        }
        catch
        {
            // Process is shutting down; nothing useful to recover here.
        }

        _registeredWait = null;
        _openSettingsEvent?.Dispose();
        _openSettingsEvent = null;

        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was already released or ownership changed during shutdown.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
