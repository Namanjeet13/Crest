# Roadmap

Goal: feature parity with the commercial notch apps, then go beyond them, while staying free, open source, smooth and light.

## Next up
- [ ] **Replace the Windows volume and brightness flyouts.** Hide the system OSD and let Crest's peeks take over.
- [ ] **Synced lyrics** from LRCLIB, shown on the island while music plays.
- [ ] **Notification mirror**: show Windows toasts on the island (UserNotificationListener).
- [ ] **Multi-monitor support**: choose a display, or follow the cursor.
- [ ] **Per-app rules and themes**: hide for certain apps, accent colours, sizes.

## Bigger features
- [ ] **AI quick-ask**: type a question in the island and get the answer inline, using your own API key or a local model.
- [ ] **Timers and focus sessions** with a live countdown in the compact pill.
- [ ] **Widgets**: weather, next calendar event, a focus timer.
- [ ] **Game strip**: FPS, CPU, GPU and RAM over fullscreen games (PresentMon / ETW).
- [ ] **QR file drop**: send a file to your phone over local Wi-Fi.
- [ ] **PC Deck**: use your phone as a macro pad and remote control.
- [ ] **Stream tools**: OBS recording pill, Twitch/YouTube chat drawer, a render-export progress pill.
- [ ] **Local API**: scripts can push text lines, progress bars and tiles onto the island.
- [ ] **Screen recording** started from the island.
- [ ] **Taskbar replacement** (experimental).
- [ ] **Localization.**

## Performance
- CPU is about 0% while idle, including with music and the visualizer running.
- Memory: a Debug build of WPF uses about 85 MB. The target is under 40 MB, using ReadyToRun/trimmed Release builds, lazy creation of the expanded view, and freeing the album art while collapsed.

## Known issues
- Hotkeys that another app already owns fail silently. They should be rebindable, and Crest should warn when one doesn't register.
