using System.Diagnostics;
using System.Text;
using MediaController.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MediaController.Services;

/// <summary>
/// Controls the Windows audio-session volume of the same application that Media Controller
/// currently targets through GSMTC. It never changes endpoint/master Windows volume.
///
/// Matching is generic: SourceAppUserModelId from GSMTC is compared with the process/session
/// identity published by Core Audio. No Spotify/Yandex-specific API or hard-coded integration
/// is used. When a player owns several Core Audio sessions under the same process name, all of
/// those matching sessions are moved together, which mirrors the per-application mixer model.
/// </summary>
public sealed class VolumeService
{
    private readonly MediaSessionService _mediaSessions;

    public event Action<VolumeState>? StateChanged;

    public VolumeService(MediaSessionService mediaSessions)
    {
        _mediaSessions = mediaSessions;
    }

    public VolumeState GetState()
    {
        try
        {
            using var target = OpenTarget();
            return target is null ? VolumeState.Unavailable : ReadState(target);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read music volume: " + ex.Message);
            return VolumeState.Unavailable;
        }
    }

    public VolumeState Adjust(int deltaPercent)
    {
        try
        {
            using var target = OpenTarget();
            if (target is null)
            {
                Logger.Info("Music volume command ignored: no matching audio session for the selected media player.");
                StateChanged?.Invoke(VolumeState.Unavailable);
                return VolumeState.Unavailable;
            }

            var representative = target.Sessions[0].SimpleAudioVolume;
            var level = representative.Volume;
            var desired = Math.Clamp(level + deltaPercent / 100f, 0f, 1f);

            foreach (var session in target.Sessions)
            {
                var volume = session.SimpleAudioVolume;
                if (volume is null)
                {
                    continue;
                }

                volume.Volume = desired;
                if (desired > 0f && volume.Mute)
                {
                    volume.Mute = false;
                }
            }

            var state = ReadState(target);
            StateChanged?.Invoke(state);
            return state;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not change music volume: " + ex.Message);
            var state = GetState();
            StateChanged?.Invoke(state);
            return state;
        }
    }

    public VolumeState ToggleMute()
    {
        try
        {
            using var target = OpenTarget();
            if (target is null)
            {
                Logger.Info("Music mute command ignored: no matching audio session for the selected media player.");
                StateChanged?.Invoke(VolumeState.Unavailable);
                return VolumeState.Unavailable;
            }

            var shouldMute = !target.Sessions[0].SimpleAudioVolume.Mute;
            foreach (var session in target.Sessions)
            {
                if (session.SimpleAudioVolume is not null)
                {
                    session.SimpleAudioVolume.Mute = shouldMute;
                }
            }

            var state = ReadState(target);
            StateChanged?.Invoke(state);
            return state;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not toggle music mute: " + ex.Message);
            var state = GetState();
            StateChanged?.Invoke(state);
            return state;
        }
    }

    private VolumeTarget? OpenTarget()
    {
        var gsmSession = _mediaSessions.GetPreferredOrCurrentSession();
        var sourceId = _mediaSessions.GetSessionId(gsmSession);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var playerName = ResolvePlayerDisplayName(sourceId);
        var matcher = new TargetMatcher(sourceId, playerName);

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        var candidates = new List<Candidate>();

        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? session = null;
                try
                {
                    session = sessions[i];
                    if (session.SimpleAudioVolume is null || session.IsSystemSoundsSession)
                    {
                        session.Dispose();
                        continue;
                    }

                    var pid = session.GetProcessID;
                    var processName = ProcessNameOf(pid);
                    var sessionIdentifier = SafeSessionIdentifier(session);
                    var score = matcher.Score(processName, sessionIdentifier);

                    if (score < TargetMatcher.MinimumScore)
                    {
                        session.Dispose();
                        continue;
                    }

                    candidates.Add(new Candidate(session, processName, score, session.State == AudioSessionState.AudioSessionStateActive));
                    session = null; // ownership moved to candidates
                }
                catch
                {
                    session?.Dispose();
                }
            }

            if (candidates.Count == 0)
            {
                device.Dispose();
                enumerator.Dispose();
                return null;
            }

            // The strongest identity match wins. When the same application publishes several
            // Core Audio sessions (common with browsers/multi-process players), keep all sessions
            // with the same process identity and near-identical score so one hotkey moves the
            // application's mixer slider consistently.
            var best = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.IsActive)
                .First();

            var bestProcess = Normalize(best.ProcessName);
            var chosen = candidates
                .Where(candidate =>
                    candidate.Score >= best.Score - 5 &&
                    Normalize(candidate.ProcessName) == bestProcess)
                .Select(candidate => candidate.Session)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (!chosen.Contains(candidate.Session))
                {
                    candidate.Session.Dispose();
                }
            }

            Logger.Info($"Music volume target: {playerName} -> {best.ProcessName} ({chosen.Count} audio session(s), score {best.Score}).");
            return new VolumeTarget(enumerator, device, chosen, playerName);
        }
        catch
        {
            foreach (var candidate in candidates)
            {
                candidate.Session.Dispose();
            }

            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    private static VolumeState ReadState(VolumeTarget target)
    {
        if (target.Sessions.Count == 0 || target.Sessions[0].SimpleAudioVolume is null)
        {
            return VolumeState.Unavailable;
        }

        var volume = target.Sessions[0].SimpleAudioVolume;
        var percent = (int)Math.Round(Math.Clamp(volume.Volume, 0f, 1f) * 100f);
        return new VolumeState(percent, volume.Mute, target.PlayerName, true);
    }

    private string ResolvePlayerDisplayName(string sourceId)
    {
        var known = _mediaSessions.GetSessions().FirstOrDefault(session =>
            string.Equals(session.Id, sourceId, StringComparison.OrdinalIgnoreCase));

        return known?.DisplayName ?? FriendlyNameFromSourceId(sourceId);
    }

    private static string ProcessNameOf(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeSessionIdentifier(AudioSessionControl session)
    {
        try
        {
            return session.GetSessionIdentifier ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FriendlyNameFromSourceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Music player";
        }

        var name = value.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < name.Length)
        {
            name = name[(bang + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? "Music player" : name;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private sealed class TargetMatcher
    {
        public const int MinimumScore = 55;

        private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "org", "net", "ru", "app", "application", "desktop", "exe", "windows",
            "microsoft", "client", "launcher"
        };

        private readonly string _source;
        private readonly string _display;
        private readonly HashSet<string> _tokens;

        public TargetMatcher(string sourceId, string displayName)
        {
            _source = Normalize(sourceId);
            _display = Normalize(displayName);
            _tokens = TokensOf(sourceId)
                .Concat(TokensOf(displayName))
                .Where(token => token.Length >= 4 && !NoiseTokens.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public int Score(string processName, string sessionIdentifier)
        {
            var process = Normalize(processName);
            var identifier = Normalize(sessionIdentifier);
            if (process.Length == 0)
            {
                return 0;
            }

            var score = 0;

            if (_source == process || _display == process)
            {
                score = Math.Max(score, 100);
            }

            if (_source.Contains(process, StringComparison.Ordinal) ||
                process.Contains(_source, StringComparison.Ordinal))
            {
                score = Math.Max(score, 92);
            }

            if (_display.Length >= 4 &&
                (_display.Contains(process, StringComparison.Ordinal) ||
                 process.Contains(_display, StringComparison.Ordinal)))
            {
                score = Math.Max(score, 88);
            }

            if (identifier.Contains(process, StringComparison.Ordinal) &&
                (_source.Contains(process, StringComparison.Ordinal) || _display.Contains(process, StringComparison.Ordinal)))
            {
                score = Math.Max(score, 95);
            }

            foreach (var token in _tokens)
            {
                if (process.Contains(token, StringComparison.Ordinal))
                {
                    score = Math.Max(score, token.Length >= 7 ? 82 : 72);
                }

                if (identifier.Contains(token, StringComparison.Ordinal))
                {
                    score = Math.Max(score, token.Length >= 7 ? 76 : 64);
                }
            }

            return score;
        }

        private static IEnumerable<string> TokensOf(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var token = new StringBuilder();
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    token.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
            }
        }
    }

    private sealed record Candidate(AudioSessionControl Session, string ProcessName, int Score, bool IsActive);

    private sealed class VolumeTarget : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private readonly MMDevice _device;
        private bool _disposed;

        public VolumeTarget(
            MMDeviceEnumerator enumerator,
            MMDevice device,
            List<AudioSessionControl> sessions,
            string playerName)
        {
            _enumerator = enumerator;
            _device = device;
            Sessions = sessions;
            PlayerName = playerName;
        }

        public List<AudioSessionControl> Sessions { get; }

        public string PlayerName { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var session in Sessions)
            {
                session.Dispose();
            }

            _device.Dispose();
            _enumerator.Dispose();
        }
    }
}
