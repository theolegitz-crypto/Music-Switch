# Media Controller 0.4.2

- Fixed manual launch when Media Controller is already running: opening the shortcut now reliably opens/restores Settings.
- Replaced the single-instance activation event with a reliable acknowledged named-pipe command.
- Repairs old Desktop and Start Menu shortcuts after updating from 0.4.0/0.4.1, clearing stale `--background` arguments.
- Forces the new purple application icon onto existing shortcuts and asks Explorer to refresh its icon cache.
- Tray icon now loads directly from the embedded application resource instead of the Windows associated-icon cache.
- A single left-click on the tray icon now opens Settings; right-click still opens the tray menu.
