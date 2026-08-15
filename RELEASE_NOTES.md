# Media Controller 0.4.6

## What's new

- Hardened the track popup for fullscreen and borderless games such as Dota 2.
- Reasserts the popup as a topmost, non-activating Win32 window while it is visible.
- Detects monitor-filling foreground windows and switches to a stronger game-popup z-order mode.
- Uses the full monitor bounds in fullscreen mode and the work area for normal desktop windows.
- Adds a short topmost heartbeat so a game that refreshes its z-order cannot immediately bury the popup.
- Popup remains click-through/non-activating and does not steal keyboard focus from the game.
- No DLL injection, graphics hooks, process injection or game-memory access.
- Keeps rapid skip, music-only volume control, updater and final application icon from 0.4.5.

> Note: true exclusive fullscreen can bypass normal desktop composition. 0.4.6 is a best-effort external overlay fix without injecting into the game; borderless/fullscreen-optimized modes are the intended target.
