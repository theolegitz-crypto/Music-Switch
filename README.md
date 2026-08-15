# Media Controller v0.5

A lightweight Windows media controller for Yandex Music, Spotify, browsers, Media Player and other GSMTC-compatible players.

## Normal use

Install the desktop app with the Velopack-generated `MediaController.Desktop-Setup.exe`. No console is required for normal use. Settings and hotkeys are kept in `%AppData%\MediaController` and survive application updates.

The installed app checks GitHub Releases in the background (when enabled in Settings). When a new release exists, Settings offers **Download update** and then **Restart & update**.

## v0.5 fullscreen overlay

The normal WPF track popup remains the default for the desktop, ordinary windows and borderless games.

v0.5 adds an **optional Xbox Game Bar companion widget** for games where a normal Windows topmost popup cannot appear over true fullscreen. The desktop app sends only the current popup metadata (track, artist, player, playback state and a small artwork image) to the local Game Bar widget through a named pipe.

The Game Bar companion is a separately packaged UWP app extension, so it needs a one-time local installation:

1. double-click `Install Game Bar Overlay.cmd`;
2. if the script says UWP build tools are missing, open Visual Studio Installer and add **Universal Windows Platform development** plus a Windows 10/11 SDK, then run it again;
3. keep Media Controller running;
4. press **Win + G**;
5. open the **Widget menu** and choose **Music Switch**;
6. **pin** the widget;
7. optionally enable Game Bar **click-through** for the pinned widget.

After the widget connects, Media Controller automatically routes notifications from monitor-filling fullscreen windows through Game Bar. Normal desktop/windowed notifications continue using the existing WPF popup.

To remove only the companion widget, run `Uninstall Game Bar Overlay.cmd`.

### Fullscreen limitation

Xbox Game Bar itself has a documented limitation with some Vulkan/OpenGL games in fullscreen. If Game Bar cannot appear over a particular fullscreen configuration, use the game's DirectX renderer (for Dota, typically DX11) or borderless/windowed mode.

## Build an installer locally

Double-click:

`Build Installer.cmd`

The script asks for:

1. release version;
2. optional public GitHub repository URL such as `https://github.com/OWNER/REPO`.

It publishes a self-contained Windows x64 build, installs/updates the matching Velopack CLI, creates the installer and opens the `artifacts\Releases` folder.

The Game Bar companion is intentionally not bundled into the Velopack package because it is a separately signed AppX/UWP extension. Use `Install Game Bar Overlay.cmd` once for that component.

## Recommended release workflow

The included `.github/workflows/release.yml` publishes the main desktop application:

1. open **Actions**;
2. select **Build installer and publish release**;
3. click **Run workflow**;
4. enter a version such as `0.5.0`.

GitHub Actions publishes the self-contained desktop app, writes the current repository URL into `update-source.txt`, creates Velopack full/delta packages and Setup.exe, and uploads the release assets to GitHub Releases.

## Development

Regular IDE / `dotnet run` builds intentionally have updates disabled because they are not Velopack-installed applications. This is expected. Use the generated Setup.exe to test the real desktop update path.

`MediaController.GameBar.csproj` is intentionally **not** part of `MediaController.sln`, so ordinary `dotnet build` continues to work without UWP tooling. The companion is built separately by `Install Game Bar Overlay.cmd` using Visual Studio MSBuild.

## Technology

- C# / .NET 8 / WPF
- GSMTC (`Windows.Media.Control`)
- Win32 `RegisterHotKey`
- `SendInput` media-key fallback
- Windows Core Audio sessions for **music-player-only volume**
- Xbox Game Bar UWP widget for optional fullscreen overlay
- local named-pipe IPC between the WPF process and Game Bar widget
- Velopack 1.2.0 for desktop installer and updates
