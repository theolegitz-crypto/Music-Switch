namespace MediaController.Core;

/// <summary>Snapshot of the audio-session volume for the currently targeted music player.</summary>
public sealed record VolumeState(int Percent, bool IsMuted, string Player, bool IsAvailable)
{
    public static readonly VolumeState Unavailable = new(0, false, "No matching music audio session", false);
}
