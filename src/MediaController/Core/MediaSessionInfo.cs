using Windows.Storage.Streams;

namespace MediaController.Core;

/// <summary>A flattened, UI friendly snapshot of one GSMTC media session.</summary>
public sealed class MediaSessionInfo
{
    /// <summary>GSMTC SourceAppUserModelId. Used as the stable identity of a player.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public bool IsCurrent { get; init; }

    public bool IsPlaying { get; init; }
}

/// <summary>
/// What is playing right now, read straight from GSMTC media properties.
/// Everything is a plain string so a player that reports nothing still produces a usable snapshot.
/// </summary>
public sealed record TrackInfo(
    string Player,
    string Title,
    string Artist,
    string Album,
    string Status,
    bool IsPlaying,
    IRandomAccessStreamReference? Thumbnail,
    TimeSpan Position,
    TimeSpan Duration)
{
    public bool HasTrack => Title.Length > 0 || Artist.Length > 0;

    /// <summary>Identity of the artwork, used as the cache key. Deliberately excludes status.</summary>
    public string ArtworkKey => Player + "" + Title + "" + Artist + "" + Album;

    /// <summary>"Artist - Title", or whichever half exists.</summary>
    public string Combined =>
        string.Join(" - ", new[] { Artist, Title }.Where(part => part.Length > 0));

    /// <summary>Title and artist only: what "the track changed" means for the popup.</summary>
    public bool SameTrackAs(TrackInfo? other) =>
        other is not null &&
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        string.Equals(Artist, other.Artist, StringComparison.Ordinal);

    public string ToNowPlayingLine()
    {
        var status = Status.ToLowerInvariant();
        return Combined.Length > 0
            ? Player + ": " + Combined + " (" + status + ")"
            : Player + " (" + status + ")";
    }
}
