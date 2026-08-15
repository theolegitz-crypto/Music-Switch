using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaController.Core;

namespace MediaController.Services;

/// <summary>
/// Optional bridge to the Xbox Game Bar companion widget. The desktop process owns the
/// named-pipe server and the UWP widget is a read-only client. If the widget is not installed
/// or not pinned/running, this service stays dormant and the normal WPF popup is used.
/// </summary>
public sealed class GameBarBridgeService : IDisposable
{
    public const string PipeName = @"LOCAL\MediaController.GameBar";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();

    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _serverLoop;
    private string? _lastPayload;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _pipe?.IsConnected == true && _writer is not null;
            }
        }
    }

    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        if (_disposed || _serverLoop is not null)
        {
            return;
        }

        _serverLoop = Task.Run(ServerLoopAsync);
    }

    public void Publish(TrackInfo track, ImageSource? artwork, TimeSpan duration)
    {
        if (_disposed || !track.HasTrack)
        {
            return;
        }

        try
        {
            var message = new OverlayMessage(
                track.Title,
                track.Artist,
                track.Player,
                track.Status,
                track.IsPlaying,
                Math.Clamp((int)Math.Round(duration.TotalMilliseconds), 500, 10000),
                EncodeArtwork(artwork));

            var payload = JsonSerializer.Serialize(message, JsonOptions);
            lock (_gate)
            {
                _lastPayload = payload;
            }

            _ = SendAsync(payload);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not prepare Game Bar overlay payload: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _pipe = null;
        }

        _sendGate.Dispose();
        _cts.Dispose();
    }

    private async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            StreamWriter? writer = null;

            try
            {
                pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                string? initial;
                lock (_gate)
                {
                    _pipe = pipe;
                    _writer = writer;
                    initial = _lastPayload;
                }

                Logger.Info("Xbox Game Bar overlay connected.");
                ConnectionChanged?.Invoke(true);

                // If the widget was opened immediately after a skip, let it catch the most
                // recent payload instead of waiting for the next key press.
                if (!string.IsNullOrWhiteSpace(initial))
                {
                    await writer.WriteLineAsync(initial).ConfigureAwait(false);
                }

                while (!_cts.IsCancellationRequested && pipe.IsConnected)
                {
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    Logger.Warn("Xbox Game Bar overlay bridge disconnected: " + ex.Message);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_writer, writer))
                    {
                        _writer = null;
                    }

                    if (ReferenceEquals(_pipe, pipe))
                    {
                        _pipe = null;
                    }
                }

                try { writer?.Dispose(); } catch { }
                try { pipe?.Dispose(); } catch { }
                ConnectionChanged?.Invoke(false);
            }

            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(350, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SendAsync(string payload)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _sendGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            StreamWriter? writer;
            lock (_gate)
            {
                writer = _writer;
            }

            if (writer is null)
            {
                return;
            }

            try
            {
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not send Game Bar overlay payload: " + ex.Message);
                ResetConnection();
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void ResetConnection()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _pipe = null;
        }
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        // AppContainer clients need an ACL that allows app packages. The pipe carries only
        // transient media metadata, so allowing all local AppContainers is intentionally used
        // here instead of requiring a hard-coded package SID for a sideloaded companion.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier("S-1-15-2-1"), // ALL APPLICATION PACKAGES
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            256 * 1024,
            security);
    }

    private static string? EncodeArtwork(ImageSource? source)
    {
        if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        BitmapSource output = bitmap;
        var max = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (max > 192)
        {
            var scale = 192.0 / max;
            var resized = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            resized.Freeze();
            output = resized;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(output));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private sealed record OverlayMessage(
        string Title,
        string Artist,
        string Player,
        string Status,
        bool IsPlaying,
        int DurationMs,
        string? ArtworkBase64);
}
