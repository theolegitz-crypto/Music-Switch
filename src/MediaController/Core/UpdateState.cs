namespace MediaController.Core;

public enum UpdatePhase
{
    Disabled,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Error
}

public sealed record UpdateState(
    UpdatePhase Phase,
    string Message,
    string CurrentVersion,
    string? LatestVersion = null,
    int Progress = 0,
    string? ReleaseNotes = null)
{
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading;

    public bool CanCheck => Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Available or UpdatePhase.Error;

    public bool CanDownload => Phase == UpdatePhase.Available;

    public bool CanRestart => Phase == UpdatePhase.ReadyToRestart;
}
