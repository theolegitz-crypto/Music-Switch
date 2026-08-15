using System.Windows;
using MediaController.Core;
using MediaController.Native;
using MediaController.Services;
using MediaController.UI;
using Velopack;

namespace MediaController;

/// <summary>
/// Manual launches open Settings; Windows startup uses --background and stays in the tray.
/// ShutdownMode is OnExplicitShutdown (App.xaml), so closing Settings never exits the app.
/// </summary>
public partial class App : Application
{
    private const string BackgroundArgument = "--background";
    private static string[] _launchArguments = Array.Empty<string>();

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must run before the WPF application and before our single-instance mutex.
        VelopackApp.Build().Run();

        _launchArguments = args;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private SingleInstanceService? _singleInstance;

    private SettingsService _settingsService = null!;
    private StartupService _startupService = null!;
    private MediaSessionService _sessionService = null!;
    private MediaKeyFallbackService _fallbackService = null!;
    private MediaControlService _controlService = null!;
    private HotkeyService _hotkeyService = null!;
    private TrayIconService _trayService = null!;
    private MediaArtworkService _artworkService = null!;
    private TrackPopupService _popupService = null!;
    private GameBarBridgeService _gameBarBridgeService = null!;
    private VolumeService _volumeService = null!;
    private UpdateService _updateService = null!;

    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var backgroundLaunch = _launchArguments.Any(arg =>
            string.Equals(arg, BackgroundArgument, StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            if (!backgroundLaunch)
            {
                Logger.Info("Another instance is running; asking it to open Settings.");
                SingleInstanceService.RequestOpenSettings();
            }
            else
            {
                Logger.Info("Background startup found an existing instance; exiting quietly.");
            }

            _singleInstance.Dispose();
            _singleInstance = null;
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
        _volumeService = new VolumeService(_sessionService);
        _artworkService = new MediaArtworkService();
        _updateService = new UpdateService();
        _gameBarBridgeService = new GameBarBridgeService();
        _gameBarBridgeService.Start();

        // Subscribes to MediaControlService.ActionCompleted, so hotkeys and the Settings
        // test buttons both raise the popup without either of them knowing about it.
        _popupService = new TrackPopupService(_sessionService, _controlService, _artworkService, _settingsService, _gameBarBridgeService, Dispatcher);

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

        // Re-apply the enabled startup entry so existing v0.4.0 installs are migrated from
        // a plain executable command to "MediaController.exe --background". Manual launches
        // open Settings; Windows startup must stay quiet in the tray.
        if (settings.StartWithWindows)
        {
            _startupService.Enable();
        }
        else if (_startupService.IsEnabled())
        {
            _startupService.Disable();
        }

        // Older installed shortcuts can survive an in-place Velopack update with stale
        // arguments/icon metadata. Repair them once on every installed startup.
        ShortcutRepairService.TryRepairInstalledShortcuts();

        _ = _sessionService.InitializeAsync();

        // Only start listening after all services required by ShowSettings exist. The named
        // Named-pipe retries keep an early second-launch request alive until this listener starts.
        _singleInstance!.StartOpenSettingsListener(() =>
            Dispatcher.BeginInvoke(new Action(ShowSettings)));

        if (!backgroundLaunch)
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings));
        }

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
            _gameBarBridgeService?.Dispose();
            _controlService?.Dispose();
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
            _singleInstance?.Dispose();
            _singleInstance = null;
            base.OnExit(e);
        }
    }

    private void OnHotkeyPressed(MediaAction action)
    {
        switch (action)
        {
            case MediaAction.VolumeUp:
                _volumeService.Adjust(_settingsService.Current.VolumeStepPercent);
                break;

            case MediaAction.VolumeDown:
                _volumeService.Adjust(-_settingsService.Current.VolumeStepPercent);
                break;

            case MediaAction.Mute:
                _volumeService.ToggleMute();
                break;

            default:
                _ = _controlService.ExecuteAsync(action);
                break;
        }
    }

    private void ShowSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings));
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                _settingsService, _sessionService, _controlService, _hotkeyService, _startupService, _volumeService, _updateService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        BringSettingsToFront(_settingsWindow);
    }

    private static void BringSettingsToFront(SettingsWindow window)
    {
        try
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(handle);
            }

            // Activate is the normal WPF path. The short Topmost pulse is a fallback for
            // Windows foreground restrictions and is immediately reverted. It is only used
            // after an explicit user request to open Settings, never for the track popup.
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not bring Settings to the foreground: " + ex.Message);
        }
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
