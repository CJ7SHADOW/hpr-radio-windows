# HPR Radio for Windows

An unofficial Windows 10/11 player for **Heartland Public Radio**: Classic Country and Bluegrass
Gospel, commercial-free. It's the Windows companion to the Android and Debian apps, and it reads the
same `channels.json`.

- **Live channels** with the song on the air, album art, and the last 15 songs played
- **Keeps itself connected.** It reconnects when the stream or your network drops (it keeps trying for
  as long as the app is playing), and fails over to the next stream address if one won't start
- **Good 'N Country on demand**: recent episodes and the archive by year, seek bar, 15 s back / 30 s
  forward, resume where you stopped, played/unplayed, song playlist PDFs
- **Media keys and Windows' media pop-up**: Play/Pause, Stop, Next/Previous work from the keyboard,
  the pop-up that appears with the volume keys, Bluetooth headsets and the lock screen. For live radio,
  Next/Previous switch channels; in an episode they skip forward 30 s / back 15 s
- **Its own volume control** (the speaker at the top), separate from the Windows volume. Scroll over it,
  or use Ctrl+↑/↓ and Ctrl+M to mute; it remembers your level
- Keeps the computer from sleeping while something's playing
- Light and dark mode follow Windows; remembers the window's size and place

## Install

Download **HPR-Radio-Setup-x.y.z.exe** from the repo's **Releases** page and run it. It installs just for
you (no administrator password) and adds HPR Radio to the Start menu.

Windows may show **"Windows protected your PC"** the first time, because the app isn't signed with a paid
certificate. Click **More info**, then **Run anyway**. It only asks once.

To uninstall: Settings → Apps → Installed apps → HPR Radio. Your settings stay in `%APPDATA%\HPR Radio`
in case you reinstall; delete that folder to remove them too.

There's also a **portable** zip: unzip it anywhere and run `HprRadio.exe`, no installing.

## Keyboard

| | |
|---|---|
| Space | play / stop (pause in an episode) |
| Esc, Alt+←, mouse back button | back |
| Ctrl+← / Ctrl+→ | previous / next channel (skip in an episode) |
| Ctrl+↑ / Ctrl+↓ | volume |
| Ctrl+M | mute |
| F5 | reload the channel list |

## The channel list

The app downloads `channels.json` every time it starts, from `https://hpr.org/apps/android/channels.json`:
the same file the Android app uses. If that can't be reached, it uses the last copy it downloaded, and
failing that, the copy built in. The bottom of the home screen says which one is in use.

To point it somewhere else for testing, start it with `HprRadio.exe --config-url https://…/channels.json`,
or put `{ "config_url": "https://…/channels.json" }` in `%APPDATA%\HPR Radio\settings.json`.

## Where it keeps things

| | |
|---|---|
| `%APPDATA%\HPR Radio\settings.json` | volume, mute, last channel, working stream per channel, window position |
| `%APPDATA%\HPR Radio\progress.json` | where you stopped in each episode |
| `%LOCALAPPDATA%\HPR Radio\Cache` | saved channel list, album art (safe to delete) |

## Troubleshooting

Set the environment variable `HPR_DEBUG=1` and start the app; it writes a log to `%TEMP%\hpr-radio.log`.

Windows "N" editions need the Media Feature Pack for the media keys and pop-up; everything else works
without it.

## How it's built

C# / .NET 10 / WPF. VLC's engine (LibVLC) does the playing, trimmed to its audio parts by `tools/trim-vlc.ps1` (keeps the installer around 50 MB). A silent Windows media player hooks into the
media keys and pop-up. Song titles and album art come from the station's Centova Cast song history,
with album art looked up on iTunes, then Deezer.

Every push builds it on GitHub (**Actions** tab). The build also runs a self-test: the app plays the real
HPR streams (silently), loads the song histories, art and Good 'N Country pages, seeks/pauses/resumes an
episode, and takes screenshots. The screenshots and log go to the `ci-results` branch. A commit with
`[release]` in its message publishes the installer as a release for the version in `HprRadio.csproj`.

```
src/HprRadio/
  Program.cs          startup, single instance, command line
  SelfTest.cs         the automated run used by the build
  Core/               Catalog (channels.json), Recent (song history), Artwork, Episodes + progress,
                      Player (VLC, reconnect/failover), MediaControls (media keys), Hub (shared state)
  UI/                 MainWindow (header, volume, now-playing bar), Home/Channel/Show/Episode pages,
                      Theme (light/dark palette), Styles.xaml
installer/            Inno Setup script
```

Unofficial. Programming, streams and the HPR logo belong to Heartland Public Radio, Inc. Playback uses
LibVLC (LGPL 2.1).
