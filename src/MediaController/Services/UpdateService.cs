using System.IO;
using System.Reflection;
using System.Windows;
using MediaController.Core;
using Velopack;
using Velopack.Sources;

namespace MediaController.Services;

/// <summary>
/// Velopack integration. The app only talks to GitHub Releases; packaging and installer creation
/// are handled by vpk. In a normal dotnet run build update-source.txt is empty, so the updater
/// simply stays disabled instead of throwing NotInstalledException.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly UpdateManager? _manager;
    private UpdateInfo? _availableUpdate;
    private bool _disposed;

    public UpdateService()
    {
        CurrentVersion = ResolveCurrentVersion();
        RepositoryUrl = ReadRepositoryUrl();

        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            State = new UpdateState(
                UpdatePhase.Disabled,
                "Automatic updates are enabled in installer / GitHub release builds.",
                CurrentVersion);
            return;
        }

        try
        {
            var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: false);
            _manager = new UpdateManager(source);

            if (!_manager.IsInstalled)
            {
                State = new UpdateState(
                    UpdatePhase.Disabled,
                    "Install Media Controller with Setup.exe to enable automatic updates.",
                    CurrentVersion);
                return;
            }

            if (_manager.CurrentVersion is not null)
            {
                CurrentVersion = _manager.CurrentVersion.ToString();
            }

            if (_manager.UpdatePendingRestart is { } pending)
            {
                State = new UpdateState(
                    UpdatePhase.ReadyToRestart,
                    $"Version {pending.Version} is downloaded and ready.",
                    CurrentVersion,
                    pending.Version.ToString(),
                    100);
            }
            else
            {
                State = new UpdateState(
                    UpdatePhase.Idle,
                    "Updates are checked automatically in the background.",
                    CurrentVersion);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Update service initialization failed: " + ex.Message);
            State = new UpdateState(UpdatePhase.Error, "Updater could not be initialized.", CurrentVersion);
        }
    }

    public event Action<UpdateState>? StateChanged;

    public string CurrentVersion { get; private set; }

    public string RepositoryUrl { get; }

    public UpdateState State { get; private set; }

    public async Task<UpdateState> CheckForUpdatesAsync()
    {
        if (_disposed || _manager is null || !_manager.IsInstalled)
        {
            return State;
        }

        if (!await _operation.WaitAsync(0).ConfigureAwait(false))
        {
            return State;
        }

        try
        {
            if (_manager.UpdatePendingRestart is { } pending)
            {
                return SetState(new UpdateState(
                    UpdatePhase.ReadyToRestart,
                    $"Version {pending.Version} is downloaded and ready.",
                    CurrentVersion,
                    pending.Version.ToString(),
                    100));
            }

            SetState(new UpdateState(UpdatePhase.Checking, "Checking for updates…", CurrentVersion));

            _availableUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_availableUpdate is null)
            {
                return SetState(new UpdateState(
                    UpdatePhase.UpToDate,
                    "You're up to date.",
                    CurrentVersion));
            }

            var target = _availableUpdate.TargetFullRelease;
            return SetState(new UpdateState(
                UpdatePhase.Available,
                $"Media Controller {target.Version} is available.",
                CurrentVersion,
                target.Version.ToString(),
                0,
                NormalizeNotes(target.NotesMarkdown)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Update check failed: " + ex.Message);
            return SetState(new UpdateState(
                UpdatePhase.Error,
                "Could not check for updates. Check your internet connection and try again.",
                CurrentVersion));
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<UpdateState> DownloadUpdateAsync()
    {
        if (_disposed || _manager is null || !_manager.IsInstalled)
        {
            return State;
        }

        if (!await _operation.WaitAsync(0).ConfigureAwait(false))
        {
            return State;
        }

        try
        {
            if (_availableUpdate is null)
            {
                SetState(new UpdateState(UpdatePhase.Checking, "Checking for updates…", CurrentVersion));
                _availableUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            }

            if (_availableUpdate is null)
            {
                return SetState(new UpdateState(UpdatePhase.UpToDate, "You're up to date.", CurrentVersion));
            }

            var target = _availableUpdate.TargetFullRelease;
            SetState(new UpdateState(
                UpdatePhase.Downloading,
                $"Downloading {target.Version}…",
                CurrentVersion,
                target.Version.ToString(),
                0,
                NormalizeNotes(target.NotesMarkdown)));

            var progress = new Action<int>(value =>
            {
                value = Math.Clamp(value, 0, 100);
                SetState(new UpdateState(
                    UpdatePhase.Downloading,
                    $"Downloading {target.Version}… {value}%",
                    CurrentVersion,
                    target.Version.ToString(),
                    value,
                    NormalizeNotes(target.NotesMarkdown)));
            });

            await _manager.DownloadUpdatesAsync(_availableUpdate, progress).ConfigureAwait(false);

            return SetState(new UpdateState(
                UpdatePhase.ReadyToRestart,
                $"Version {target.Version} is ready. Restart to update.",
                CurrentVersion,
                target.Version.ToString(),
                100,
                NormalizeNotes(target.NotesMarkdown)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Update download failed: " + ex.Message);
            return SetState(new UpdateState(
                UpdatePhase.Error,
                "The update could not be downloaded. Try again later.",
                CurrentVersion));
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Starts Velopack's updater in wait-for-exit mode, then gracefully shuts WPF down. This lets
    /// App.OnExit release hotkeys, tray resources and the single-instance mutex before files change.
    /// </summary>
    public bool RestartAndApply()
    {
        if (_disposed || _manager is null || !_manager.IsInstalled)
        {
            return false;
        }

        try
        {
            var target = _manager.UpdatePendingRestart ?? _availableUpdate?.TargetFullRelease;
            if (target is null)
            {
                return false;
            }

            _manager.WaitExitThenApplyUpdates(target, silent: false, restart: true);
            Application.Current.Dispatcher.BeginInvoke(new Action(Application.Current.Shutdown));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not start the updater.", ex);
            SetState(new UpdateState(UpdatePhase.Error, "Could not start the updater.", CurrentVersion));
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Do not dispose the semaphore here: a background check/download may still be unwinding
        // while WPF is shutting down and will release it in its finally block.
    }

    private UpdateState SetState(UpdateState state)
    {
        State = state;

        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Logger.Warn("Update state listener failed: " + ex.Message);
        }

        return state;
    }

    private static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string ReadRepositoryUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "update-source.txt");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var value = File.ReadAllText(path).Trim().TrimEnd('/');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   uri.Scheme == Uri.UriSchemeHttps &&
                   uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? value
                : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read update-source.txt: " + ex.Message);
            return string.Empty;
        }
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var clean = notes.Replace("\r\n", "\n").Trim();
        return clean.Length <= 600 ? clean : clean[..597] + "…";
    }
}
