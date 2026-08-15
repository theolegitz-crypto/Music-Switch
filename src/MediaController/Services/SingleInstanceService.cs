using System.IO;
using System.IO.Pipes;
using System.Text;
using MediaController.Core;

namespace MediaController.Services;

/// <summary>
/// Keeps one Media Controller process per Windows session and lets later manual launches
/// ask the already-running instance to open Settings. Named pipes are used rather than a
/// named event so a request can be acknowledged and is not lost during startup races.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\MediaController.SingleInstance";
    private const string PipeName = "MediaController.OpenSettings.v2";
    private const string OpenSettingsCommand = "open-settings";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
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
        return createdNew;
    }

    public void StartOpenSettingsListener(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!_ownsMutex || _disposed || _listenerTask is not null)
        {
            return;
        }

        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(callback, _listenerCts.Token));
    }

    /// <summary>
    /// Called by a short-lived second process. A few retries cover the case where the first
    /// process owns the mutex but is still constructing WPF/services and has not opened the pipe.
    /// </summary>
    public static bool RequestOpenSettings()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                client.Connect(timeout: 250);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };
                using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                writer.WriteLine(OpenSettingsCommand);
                var response = reader.ReadLine();
                return string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(75);
            }
            catch (IOException)
            {
                Thread.Sleep(75);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not signal the running instance: " + ex.Message);
                return false;
            }
        }

        Logger.Warn("The running instance did not expose its command pipe in time.");
        return false;
    }

    private static async Task ListenLoopAsync(Action callback, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, OpenSettingsCommand, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        callback();
                        await writer.WriteLineAsync("ok").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Single-instance OpenSettings callback failed: " + ex.Message);
                        await writer.WriteLineAsync("error").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn("Single-instance pipe listener failed: " + ex.Message);

                try
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
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
            _listenerCts?.Cancel();
        }
        catch
        {
            // Process is shutting down.
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;

        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released / abandoned during shutdown.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
