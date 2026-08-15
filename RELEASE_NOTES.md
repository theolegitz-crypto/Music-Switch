# Media Controller 0.5.1

## Volume OSD + track timeline

- Fixed the broken volume-OSD XAML/code-behind mismatch that caused missing-name compile errors.
- Music volume feedback is now vertical: a purple liquid-glass level bar fills from bottom to top.
- Volume Up / Volume Down / Mute still affect only the targeted music application's Windows audio session.
- Added a live track-duration bar to the normal track popup.
- Track position and duration come from GSMTC timeline properties; no Spotify/Yandex API is used.
- While the popup is visible and playback is active, its progress bar advances locally every 200 ms for smooth feedback.
- Players that do not expose a valid duration simply hide the timeline row instead of showing incorrect data.
- Track and volume notifications continue to share one popup window and never stack.

Rapid-skip queuing, artwork, updater, icon, settings activation, and the optional Game Bar companion remain unchanged.
