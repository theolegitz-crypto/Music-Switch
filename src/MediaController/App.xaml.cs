using System.Windows;
using MediaController.Core;
using MediaController.Services;
using MediaController.UI;
using Velopack;

namespace MediaController;

/// <summary>
/// Starts straight into the tray: no main window, no startup flash, no stolen focus.
/// ShutdownMode is OnExplicitShutdown (App.xaml), so closing Settings never exits the app.
/// </summary>
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must run before the WPF application and before our single-instance mutex.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private const string MutexName = @"Local\MediaController.SingleInstance";

    private System.Threading.Mutex? _instanceMutex;

    private SettingsService _settingsService = null!;
    private StartupService _startupService = null!;
    private MediaSessionService _sessionService = null!;
    private MediaKeyFallbackService _fallbackService = null!;
    private MediaControlService _controlService = null!;
    private HotkeyService _hotkeyService = null!;
    private TrayIconService _trayService = null!;
    private MediaArtworkService _artworkService = null!;
    private TrackPopupService _popupService = null!;
    private UpdateService _updateService = null!;

    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance is already running; this one exits immediately.");
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled dispatcher exception.", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Unhandled exception.", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        Logger.Info("Media Controller starting.");

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();

        _startupService = new StartupService();
        _fallbackService = new MediaKeyFallbackService();

        _sessionService = new MediaSessionService();
        _sessionService.SetPreferredSession(settings.PreferredPlayer);

        _controlService = new MediaControlService(_sessionService, _fallbackService);
        _artworkService = new MediaArtworkService();
        _updateService = new UpdateService();

        // Subscribes to MediaControlService.ActionCompleted, so hotkeys and the Settings
        // test buttons both raise the popup without either of them knowing about it.
        _popupService = new TrackPopupService(_sessionService, _controlService, _artworkService, _settingsService, Dispatcher);

        _trayService = new TrayIconService(_sessionService, _settingsService, _startupService);
        _trayService.SettingsRequested += ShowSettings;
        _trayService.ExitRequested += Shutdown;
        _trayService.PreferredPlayerChanged += OnPreferredPlayerChanged;
        _trayService.StartWithWindowsChanged += OnStartWithWindowsChanged;

        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;

        var failed = _hotkeyService.Apply(settings);
        if (failed.Count > 0)
        {
            var actions = string.Join(", ", failed);
            Logger.Warn("Hotkeys not registered: " + actions);

            // A balloon rather than a modal box: the app may be starting with Windows.
            _trayService.ShowWarning(
                "These hotkeys are in use by another program and were not registered: " + actions +
                ". Open Settings from the tray icon to pick different combinations.");
        }

        if (settings.StartWithWindows != _startupService.IsEnabled())
        {
            _startupService.Apply(settings.StartWithWindows);
        }

        _ = _sessionService.InitializeAsync();

        if (settings.CheckForUpdatesAutomatically)
        {
            _ = CheckForUpdatesAfterStartupAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _hotkeyService?.Dispose();
            _trayService?.Dispose();
            _popupService?.Dispose();
            _updateService?.Dispose();
            _sessionService?.Dispose();
            Logger.Info("Media Controller stopped.");
        }
        catch (Exception ex)
        {
            Logger.Error("Shutdown failed.", ex);
        }
        finally
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Not owned - nothing to release.
            }

            _instanceMutex?.Dispose();
            _instanceMutex = null;
            base.OnExit(e);
        }
    }

    private void OnHotkeyPressed(MediaAction action) => _ = _controlService.ExecuteAsync(action);

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settingsService, _sessionService, _controlService, _hotkeyService, _startupService, _updateService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            // Do not compete with startup, game launchers or the first GSMTC discovery.
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            var state = await _updateService.CheckForUpdatesAsync().ConfigureAwait(false);

            if (state.Phase == UpdatePhase.Available)
            {
                await Dispatcher.InvokeAsync(() =>
                    _trayService.ShowInfo($"Media Controller {state.LatestVersion} is available. Open Settings to update."));
            }
            else if (state.Phase == UpdatePhase.ReadyToRestart)
            {
                await Dispatcher.InvokeAsync(() =>
                    _trayService.ShowInfo($"Media Controller {state.LatestVersion} is ready. Restart from Settings to update."));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Background update check failed: " + ex.Message);
        }
    }

    private void OnPreferredPlayerChanged(string? id)
    {
        var settings = _settingsService.Current.Clone();
        settings.PreferredPlayer = id;
        _settingsService.Save(settings);
        _sessionService.SetPreferredSession(id);
        Logger.Info("Preferred player set to " + (id ?? "Auto") + ".");
    }

    private void OnStartWithWindowsChanged(bool enabled)
    {
        _startupService.Apply(enabled);

        var settings = _settingsService.Current.Clone();
        settings.StartWithWindows = _startupService.IsEnabled();
        _settingsService.Save(settings);
    }
}
