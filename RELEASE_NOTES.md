# Media Controller 0.5.0

## Fullscreen overlay

- Adds an optional **Xbox Game Bar companion widget** for track popups over supported true-fullscreen games.
- Keeps the existing lightweight WPF popup for desktop, windowed and borderless use.
- Automatically routes fullscreen popup content to Game Bar when the pinned widget is connected.
- Transfers title, artist, playback state and a small album-art image through a local named pipe.
- Preserves the purple liquid-glass visual style in the Game Bar widget.
- The widget auto-hides its content after the normal popup duration and can use Game Bar's pinned/click-through behavior.
- No DLL injection, DirectX hooks, game-memory access or process injection.

## Installation note

The Xbox Game Bar widget is a separate packaged UWP extension. After updating the main app to 0.5.0, run `Install Game Bar Overlay.cmd` once from the source folder, then press `Win + G`, select **Music Switch** from the Widget menu and pin it.

The main desktop app continues to update normally through Velopack/GitHub Releases.

## Known platform limitation

Xbox Game Bar itself may be unable to display over some Vulkan/OpenGL fullscreen games. In that case use a DirectX renderer or borderless/windowed mode.
