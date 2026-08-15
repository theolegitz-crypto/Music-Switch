using System.Drawing;
using System.Windows.Forms;
using MediaController.Core;
using MediaController.Services;

namespace MediaController.UI;

/// <summary>
/// System tray presence. Uses System.Windows.Forms.NotifyIcon - WPF has no tray API
/// and a third party tray package would buy nothing here.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    // NotifyIcon.Text is capped at 63 characters by Windows.
    private const int TooltipLimit = 63;

    private readonly MediaSessionService _sessions;
    private readonly SettingsService _settings;
    private readonly StartupService _startup;

    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _icon;

    private bool _disposed;

    public TrayIconService(MediaSessionService sessions, SettingsService settings, StartupService startup)
    {
        _sessions = sessions;
        _settings = settings;
        _startup = startup;

        _menu = new ContextMenuStrip();

        // Rebuilt every time it opens, so a player appearing or disappearing can never
        // leave a stale or broken menu behind.
        _menu.Opening += (_, _) => RebuildMenu();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Media Controller",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();

        _sessions.Changed += OnSessionsChanged;
        UpdateTooltip();
    }

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    /// <summary>Null means "Auto".</summary>
    public event Action<string?>? PreferredPlayerChanged;

    public event Action<bool>? StartWithWindowsChanged;

    public void ShowWarning(string message)
    {
        try
        {
            _icon.ShowBalloonTip(6000, "Media Controller", message, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show a balloon tip: " + ex.Message);
        }
    }

    public void ShowInfo(string message)
    {
        try
        {
            _icon.ShowBalloonTip(6000, "Media Controller", message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show an info balloon: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Changed -= OnSessionsChanged;

        try
        {
            // Hide before dispose, otherwise a ghost icon can linger in the tray.
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn("Tray icon cleanup failed: " + ex.Message);
        }
    }

    private void OnSessionsChanged()
    {
        // GSMTC events arrive on thread pool threads; NotifyIcon belongs to the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            UpdateTooltip();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(UpdateTooltip));
        }
    }

    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _icon.Text = Truncate(_sessions.NowPlaying, TooltipLimit);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not update the tray tooltip: " + ex.Message);
        }
    }

    private void RebuildMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in old)
        {
            item.Dispose();
        }

        _menu.Items.Add(new ToolStripMenuItem("Media Controller") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var track = _sessions.CurrentTrack;
        if (track is null)
        {
            _menu.Items.Add(new ToolStripMenuItem("No active media") { Enabled = false });
        }
        else
        {
            var headline = track.Combined.Length > 0 ? "♫  " + track.Combined : "♫  Unknown track";
            _menu.Items.Add(new ToolStripMenuItem(Truncate(headline, 90)) { Enabled = false });
            _menu.Items.Add(new ToolStripMenuItem(Truncate("     " + track.Player + "  •  " + track.Status, 90)) { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(BuildPlayerMenu());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        _menu.Items.Add(settingsItem);

        var startupItem = new ToolStripMenuItem("Start with Windows") { Checked = _startup.IsEnabled() };
        startupItem.Click += (_, _) => StartWithWindowsChanged?.Invoke(!startupItem.Checked);
        _menu.Items.Add(startupItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exitItem);
    }

    private ToolStripMenuItem BuildPlayerMenu()
    {
        var root = new ToolStripMenuItem("Player");
        var preferred = _settings.Current.PreferredPlayer;

        var auto = new ToolStripMenuItem("Auto") { Checked = preferred is null };
        auto.Click += (_, _) => PreferredPlayerChanged?.Invoke(null);
        root.DropDownItems.Add(auto);
        root.DropDownItems.Add(new ToolStripSeparator());

        var sessions = _sessions.GetSessions();

        if (sessions.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem("No active media") { Enabled = false });
            return root;
        }

        foreach (var session in sessions)
        {
            var label = session.DisplayName + (session.IsPlaying ? "  (playing)" : string.Empty);
            var item = new ToolStripMenuItem(label)
            {
                Checked = string.Equals(preferred, session.Id, StringComparison.OrdinalIgnoreCase)
            };

            var id = session.Id;
            item.Click += (_, _) => PreferredPlayerChanged?.Invoke(id);
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 3)] + "...";

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not load the application icon: " + ex.Message);
        }

        return SystemIcons.Application;
    }
}
