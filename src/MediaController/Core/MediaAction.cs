namespace MediaController.Core;

public enum MediaAction
{
    Next,
    Previous,
    PlayPause
}

/// <summary>
/// Outcome of one media command. <paramref name="Before"/> is a snapshot from the exact
/// GSMTC session targeted by the command. <paramref name="TargetSessionId"/> keeps popup
/// metadata pinned to that player even if Windows changes its global current session while
/// the player is switching tracks.
/// </summary>
public sealed record MediaActionResult(
    MediaAction Action,
    bool Success,
    bool UsedFallback,
    TrackInfo? Before,
    string? TargetSessionId);
