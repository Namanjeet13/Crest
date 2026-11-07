# Crest

A free, open-source Dynamic Island for Windows. A small black notch sits at the top of your screen, shows what's happening, and springs open when you hover over it.

## What works today (v0.1)

- **Notch or floating island.** Spring-physics animations. It never steals focus and stays out of Alt+Tab.
- **Now playing** from any app (Spotify, browsers, media players): artwork, an accent colour taken from the album, seek, play/pause, next and previous, and a live visualizer.
- **Peeks**: brief pop-ups for volume changes, track changes, mic mute, clipboard copies, picked colours, and when an app starts using your mic.
- **One mute for every app** (Ctrl+Alt+M), plus a call pill with a timer while any app uses your mic.
- **Privacy dots** for mic and camera use.
- **Clipboard history**: encrypted on disk with DPAPI, with pinning, and skips copies from password managers (Ctrl+Alt+V).
- **Quick controls**: volume, brightness on laptops, Wi-Fi, Bluetooth, Snip, Night light, and a colour picker (Ctrl+Alt+C).
- **System**: CPU, memory, battery and uptime.
- **Settings**: shape, hide until hover, hide in fullscreen, start with Windows, visualizer, and peek toggles.
- Ctrl+Alt+N opens and closes the island.

## Build

Requires the .NET 8 SDK and Windows 10 2004 or later.

```
dotnet build
dotnet run --project src/Crest
```

Settings and clipboard history live in `%AppData%\Crest`.

See [ROADMAP.md](ROADMAP.md) for what's next.
