# Media Controller 0.4.3

- Reworked rapid media control into a lossless FIFO command queue.
- Every hotkey press is captured immediately even while the player is still switching tracks.
- Rapid Next/Previous bursts stay pinned to the same GSMTC player and no longer drift to Telegram.
- Removed the per-press pre-wait that could make fast skipping appear to stop responding.
- Keeps queued presses while Yandex/Spotify briefly recreates its media session, then drains them in order.
- Added a short retry for players that temporarily refuse a skip during track transition.
- Popup/metadata/artwork work remains fully outside the command path and cannot block skipping.
