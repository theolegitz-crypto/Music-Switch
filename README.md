# Media Controller v0.4

A lightweight Windows media controller for Yandex Music, Spotify, browsers, Media Player and other GSMTC-compatible players.

## Normal use

Starting with v0.4, users should install the app with the Velopack-generated `MediaController.Desktop-Setup.exe`. No console is required for normal use. Settings and hotkeys are kept in `%AppData%\MediaController` and survive application updates.

The installed app checks GitHub Releases in the background (when enabled in Settings). When a new release exists, Settings offers **Download update** and then **Restart & update**.

## Build an installer locally without remembering commands

Double-click:

`Build Installer.cmd`

The script asks for:

1. release version;
2. optional public GitHub repository URL such as `https://github.com/OWNER/REPO`.

It then publishes a self-contained Windows x64 build, installs/updates the matching Velopack CLI, creates the installer and opens the `artifacts\Releases` folder.

If you leave the repository URL blank, Setup.exe is still created, but that build has online updates disabled.

## Recommended release workflow

Upload this project to a GitHub repository. The included `.github/workflows/release.yml` can publish releases entirely from the GitHub web UI:

1. open **Actions**;
2. select **Build installer and publish release**;
3. click **Run workflow**;
4. enter a version such as `0.4.6`.

GitHub Actions publishes a self-contained app, writes the current repository URL into `update-source.txt`, creates Velopack full/delta packages and Setup.exe, and uploads the release assets to GitHub Releases.

The GitHub token used by Actions is never embedded into the application. Installed clients use unauthenticated access to a public GitHub Releases feed.

## Development

Regular IDE / `dotnet run` builds intentionally have updates disabled because they are not Velopack-installed applications. This is expected. Use the generated Setup.exe to test the real update path.

## Technology

- C# / .NET 8
- WPF
- GSMTC (`Windows.Media.Control`)
- Win32 `RegisterHotKey`
- `SendInput` media-key fallback
- Windows Core Audio (`IAudioEndpointVolume`) for master-volume control
- Velopack 1.2.0 for installer and updates
