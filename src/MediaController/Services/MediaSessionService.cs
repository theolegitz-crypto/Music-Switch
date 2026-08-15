using MediaController.Core;
using Windows.Media.Control;

namespace MediaController.Services;

/// <summary>
/// Thin wrapper over GlobalSystemMediaTransportControlsSessionManager.
/// Knows nothing about Spotify / Yandex / browsers - only about whatever publishes a media session.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private const string Idle = "No active media";

    private readonly object _gate = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    // Replaced wholesale on refresh, never mutated, so readers can iterate a snapshot outside the lock.
    private List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private List<string> _knownIds = new();

    // Last non-empty metadata snapshot per player. The command path reads this synchronously
    // so rapid media-key bursts never wait on TryGetMediaPropertiesAsync.
    private readonly Dictionary<string, TrackInfo> _lastUsefulTracks =
        new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSession? _tracked;
    private string? _preferredId;
    private bool _disposed;

    /// <summary>Raised on any session or metadata change. May fire on a WinRT thread pool thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when the tracked session reports new metadata or playback state.
    /// The popup uses this so it can wait for the player to catch up after a skip.
    /// </summary>
    public event Action<TrackInfo?>? TrackChanged;

    public string NowPlaying { get; private set; } = Idle;

    /// <summary>Last known snapshot. Null when no session is available.</summary>
    public TrackInfo? CurrentTrack { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            Logger.Info("GSMTC session manager initialized.");
        }
        catch (Exception ex)
        {
            Logger.Error("GSMTC initialization failed; every command will use the media key fallback.", ex);
        }

        Refresh();
    }

    public void SetPreferredSession(string? sourceAppUserModelId)
    {
        lock (_gate)
        {
            _preferredId = string.IsNullOrWhiteSpace(sourceAppUserModelId) ? null : sourceAppUserModelId;
        }

        Refresh();
    }

    public IReadOnlyList<MediaSessionInfo> GetSessions()
    {
        List<GlobalSystemMediaTransportControlsSession> snapshot;
        lock (_gate)
        {
            snapshot = _sessions;
        }

        var currentId = IdOf(GetCurrentSession());
        var result = new List<MediaSessionInfo>();

        foreach (var session in snapshot)
        {
            var id = IdOf(session);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            // A browser can publish several sessions under one app id; the app id is our identity.
            if (result.Any(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new MediaSessionInfo
            {
                Id = id,
                DisplayName = FormatDisplayName(id),
                IsCurrent = string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase),
                IsPlaying = IsPlaying(session)
            });
        }

        return result;
    }

    public GlobalSystemMediaTransportControlsSession? GetCurrentSession()
    {
        try
        {
            return _manager?.GetCurrentSession();
        }
        catch (Exception ex)
        {
            Logger.Warn("GetCurrentSession failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Preferred player if explicitly selected. In Auto mode prefer an actually playing
    /// session over a paused/stale Windows current session (for example a Telegram voice
    /// message that merely remains published in GSMTC). Fall back to Windows current only
    /// when nothing is playing.
    /// </summary>
    public GlobalSystemMediaTransportControlsSession? GetPreferredOrCurrentSession()
    {
        string? preferred;
        List<GlobalSystemMediaTransportControlsSession> snapshot;
        lock (_gate)
        {
            preferred = _preferredId;
            snapshot = _sessions;
        }

        if (preferred is not null)
        {
            foreach (var session in snapshot)
            {
                if (string.Equals(IdOf(session), preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return session;
                }
            }
        }

        var current = GetCurrentSession();
        if (current is not null && IsPlaying(current))
        {
            return current;
        }

        // Windows sometimes reports a recently-used but paused communication session as
        // current. If another player is actively playing, that is the safer Auto target.
        var playing = snapshot.FirstOrDefault(IsPlaying);
        return playing ?? current;
    }

    /// <summary>Stable GSMTC identity used to keep a command and its popup on the same app.</summary>
    public string? GetSessionId(GlobalSystemMediaTransportControlsSession? session)
    {
        var id = IdOf(session);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>Finds a currently published session by SourceAppUserModelId.</summary>
    public GlobalSystemMediaTransportControlsSession? FindSessionById(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        List<GlobalSystemMediaTransportControlsSession> snapshot;
        lock (_gate)
        {
            snapshot = _sessions;
        }

        return snapshot.FirstOrDefault(session =>
            string.Equals(IdOf(session), sourceAppUserModelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Last non-empty metadata published by a specific player, if we have seen any.</summary>
    public TrackInfo? GetLastUsefulTrack(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        lock (_gate)
        {
            return _lastUsefulTracks.TryGetValue(sourceAppUserModelId, out var track)
                ? track
                : null;
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
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            Untrack();
        }
        catch (Exception ex)
        {
            Logger.Warn("GSMTC cleanup failed: " + ex.Message);
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Refresh();

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Refresh();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = UpdateNowPlayingAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = UpdateNowPlayingAsync();

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var sessions = new List<GlobalSystemMediaTransportControlsSession>();
        try
        {
            var raw = _manager?.GetSessions();
            if (raw is not null)
            {
                sessions = raw.ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("GetSessions failed: " + ex.Message);
        }

        var ids = sessions
            .Select(IdOf)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> previousIds;
        lock (_gate)
        {
            previousIds = _knownIds;
            _knownIds = ids;
            _sessions = sessions;
        }

        foreach (var added in ids.Except(previousIds, StringComparer.OrdinalIgnoreCase))
        {
            Logger.Info("Media session added: " + added);
        }

        foreach (var removed in previousIds.Except(ids, StringComparer.OrdinalIgnoreCase))
        {
            Logger.Info("Media session removed: " + removed);
        }

        TrackEffectiveSession();
        Changed?.Invoke();
        _ = UpdateNowPlayingAsync();
    }

    private void TrackEffectiveSession()
    {
        Untrack();

        var session = GetPreferredOrCurrentSession();
        _tracked = session;

        if (session is null)
        {
            return;
        }

        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not subscribe to session events: " + ex.Message);
        }
    }

    private void Untrack()
    {
        var previous = _tracked;
        _tracked = null;

        if (previous is null)
        {
            return;
        }

        try
        {
            previous.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            previous.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch (Exception ex)
        {
            // The player may already be gone - nothing left to clean up in that case.
            Logger.Warn("Could not unsubscribe from session events: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads a fresh snapshot straight from GSMTC rather than from the cache.
    /// The parameterless overload follows the effective Auto/preferred session.
    /// </summary>
    public Task<TrackInfo?> ReadTrackAsync() => ReadTrackAsync(null);

    /// <summary>
    /// Reads a specific application session when an id is supplied. This is intentionally
    /// used by the track popup so a command sent to Yandex/Spotify cannot suddenly display
    /// metadata from an unrelated Telegram/browser session while Windows changes its global
    /// current-session ranking. If that session disappeared, no unrelated metadata is used.
    /// </summary>
    public async Task<TrackInfo?> ReadTrackAsync(string? sourceAppUserModelId)
    {
        var session = string.IsNullOrWhiteSpace(sourceAppUserModelId)
            ? GetPreferredOrCurrentSession()
            : FindSessionById(sourceAppUserModelId);

        if (session is null)
        {
            return null;
        }

        var properties = await WinRt.TryAsync(
            () => session.TryGetMediaPropertiesAsync(),
            "TryGetMediaPropertiesAsync");

        var sessionId = IdOf(session);
        var track = new TrackInfo(
            FormatDisplayName(sessionId),
            properties?.Title?.Trim() ?? string.Empty,
            properties?.Artist?.Trim() ?? string.Empty,
            properties?.AlbumTitle?.Trim() ?? string.Empty,
            StatusOf(session),
            IsPlaying(session),
            properties?.Thumbnail);

        if (track.HasTrack && !string.IsNullOrWhiteSpace(sessionId))
        {
            lock (_gate)
            {
                _lastUsefulTracks[sessionId] = track;
            }
        }

        return track;
    }

    private async Task UpdateNowPlayingAsync()
    {
        if (_disposed)
        {
            return;
        }

        var track = await ReadTrackAsync();

        // The cache is refreshed unconditionally; only the notification is deduplicated.
        CurrentTrack = track;

        var text = track?.ToNowPlayingLine() ?? Idle;
        if (text == NowPlaying)
        {
            return;
        }

        NowPlaying = text;
        Changed?.Invoke();
        TrackChanged?.Invoke(track);
    }

    private static string StatusOf(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            return info is null ? "Unknown" : info.PlaybackStatus.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            return info is not null &&
                   info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static string IdOf(GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            return session?.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Cosmetic only: Spotify.exe becomes Spotify, a packaged app id becomes its last name part.
    /// Deliberately generic - there is no per-service integration table anywhere in this app.
    /// </summary>
    private static string FormatDisplayName(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Unknown player";
        }

        var name = id;

        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        var underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        // Application ids are rarely just a name: "ru.yandex.desktop.music" would collapse
        // to a useless "Music", and "Telegram.TelegramDesktop.679b14d5..." to a raw hash.
        // Drop the noise segments, then keep the last meaningful one.
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 1)
        {
            var meaningful = segments
                .Where(part => !Filler.Contains(part) && !LooksLikeIdentifier(part))
                .ToList();

            if (meaningful.Count == 0)
            {
                meaningful = segments.ToList();
            }

            var last = meaningful[^1];
            name = Generic.Contains(last) && meaningful.Count > 1
                ? meaningful[^2] + " " + last
                : last;
        }

        if (name.Length == 0)
        {
            return id;
        }

        if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase))
        {
            name = "Edge";
        }

        return string.Join(' ', name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(SplitCamelCase)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>A hash or numeric suffix, not something to show a person.</summary>
    private static bool LooksLikeIdentifier(string part) =>
        part.Length >= 8 && part.All(Uri.IsHexDigit) ||
        part.Length > 0 && part.All(char.IsDigit);

    /// <summary>"TelegramDesktop" -> "Telegram Desktop". Leaves single words alone.</summary>
    private static string SplitCamelCase(string word)
    {
        var text = new System.Text.StringBuilder(word.Length + 4);

        for (var i = 0; i < word.Length; i++)
        {
            if (i > 0 && char.IsUpper(word[i]) && !char.IsUpper(word[i - 1]))
            {
                text.Append(' ');
            }

            text.Append(word[i]);
        }

        return text.ToString();
    }

    /// <summary>Platform noise in application ids - never a product name on its own.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "io", "ru", "de", "fr", "co", "uk",
        "desktop", "win32", "windows", "app", "apps", "exe"
    };

    /// <summary>Words too generic to identify a player, so the segment before them is kept too.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "player", "media", "audio", "client", "main", "ui"
    };
}
