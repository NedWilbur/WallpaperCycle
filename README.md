# WallpaperCycle

A small Windows tray app for the 3440 x 1440 display. It gets free wallpapers from Wallhaven, changes them at startup and hourly, and fills the screen without stretching.

## Start

In PowerShell, run:

```powershell
.\start.ps1
```

To start automatically when you sign in, run:

```powershell
.\install.ps1
```

Right-click the tray icon for **Next**, **Favorite**, **Pause**, and **Black**, or to open settings and saved images. **Start with Windows** toggles automatic startup.

## Settings

The first run creates the commented `config.jsonc` file. It accepts `//` and `/* ... */` comments. Changes reload automatically. Existing `config.json` files are imported once. Defaults use SFW wallpapers and need no API key. NSFW-only (`Purity: "001"`) requires a valid API key; without one, rotation warns and uses the local fallback. An invalid key gets a Wallhaven authorization error and the same fallback.

The config file documents every setting next to its value. Its fields are:

| Setting | Valid values | Purpose |
|---|---|---|
| `IntervalMinutes` | 1-10080; default `60` | Time between automatic changes. |
| `Width`, `Height` | Each 1-16384; defaults `3440`, `1440` | Minimum image dimensions. |
| `AspectRatioTolerance` | 0-0.25; default `0.10` | How far image proportions may differ from the display. |
| `Query` | Wallhaven search text; default `""` | Narrow results by subject or tags. |
| `Categories` | `100`, `010`, `001`, `110`, `101`, `011`, `111` | Three flags in General, Anime, People order. |
| `Purity` | `100`, `010`, `001`, `110`, `101`, `011`, `111` | Three flags in SFW, Sketchy, NSFW order. `001` means NSFW only and requires a valid `ApiKey`. |
| `ApiKey` | String; default `""` | Wallhaven key, required only when NSFW is enabled. |
| `RequestTimeoutSeconds` | 5-300; default `30` | Search and download timeout. |
| `BlackBackground` | `true` or `false`; default `false` | Keep the desktop black instead of applying rotated images. The tray toggle updates this value. |
| `Sorting` | `date_added`, `relevance`, `random`, `views`, `favorites`, `toplist`; default `random` | Wallhaven result order. |
| `Colors` | Empty or comma-separated names: `red`, `pink`, `purple`, `blue`, `cyan`, `green`, `yellow`, `orange`, `brown`, `black`, `gray`, `white` | Filter by dominant color, e.g. `"blue,white"`. Names are converted to Wallhaven color codes. |
| `BlackBackgroundScheduleEnabled` | `true` or `false`; default `true` | Automatically use a black background during the configured local-time window and resume rotation afterward, including after wake/sign-in. Choosing **Next wallpaper** during the window skips black until that window ends, then normal rotation continues. |
| `BlackBackgroundStartHour`, `BlackBackgroundEndHour` | Whole hours from `0` to `23`; defaults `0` and `7` | Local 24-hour clock; start is inclusive and end exclusive. Example: `22` to `7` covers overnight. |

Wallhaven also accepts these sorting and color filters in its [API search](https://wallhaven.cc/help/api#search).

Use **Favorite** in the tray menu to copy the active image into the Git-tracked `favorites/` folder. Searches advance through Wallhaven result pages and skip images already in `wallpapers/`, using one search request per change. On download failure, the app tries favorites first, then downloaded images, and uses a black background if neither folder has a usable image. Downloaded images in `wallpapers/` are excluded from Git.

Logs are in `data/app.log`.

## Build and test

Requires Windows and .NET Framework 4.8, already installed on this machine. No .NET SDK or extra packages are needed.

```powershell
.\build.ps1
.\test.ps1
```

The test suite briefly changes the desktop wallpaper and restores it. Run it from your normal Windows desktop session.
