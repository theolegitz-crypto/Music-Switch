# Xbox Game Bar fullscreen overlay — one-time setup

The desktop Media Controller is still installed and updated through Velopack. The fullscreen Game Bar overlay is a separate Windows UWP app extension and must be sideloaded once.

## 1. Build and install the companion

Double-click:

`Install Game Bar Overlay.cmd`

The script automatically:

- finds Visual Studio MSBuild and an installed Windows SDK;
- builds the x64 UWP Game Bar companion;
- creates a local development signing certificate under `.local`;
- trusts only the public part of that certificate for the current Windows user;
- signs the AppX package;
- installs/updates it for the current user.

`.local` is ignored by Git so the private signing key/password are not committed.

If the script reports missing UWP/XAML targets, use **Visual Studio Installer → Modify** and add:

- **Universal Windows Platform development**;
- a Windows 10 or Windows 11 SDK.

Then run the installer script again.

## 2. Pin the widget

1. Keep the normal Media Controller app running.
2. Press `Win + G`.
3. Open the **Widget menu**.
4. Choose **Music Switch**.
5. Click **Pin** so the widget remains available over games.
6. Optionally enable Game Bar **click-through** for the pinned widget.

The card itself stays transparent when there is no notification. When you skip/pause a track in a fullscreen game, Media Controller sends the current title/artist/artwork to the pinned widget.

## 3. Remove it

Double-click:

`Uninstall Game Bar Overlay.cmd`

This removes only the Game Bar companion, not the desktop Media Controller.

## Dota / Vulkan note

Xbox Game Bar has a platform limitation where it may itself be unable to display over some Vulkan/OpenGL games in fullscreen. If `Win + G` is not visible over Dota in the same fullscreen mode, use Dota's DirectX 11 renderer or borderless mode. No desktop overlay implementation can make a Game Bar widget appear when Game Bar itself cannot be composited over that renderer.
