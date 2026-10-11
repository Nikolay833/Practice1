# Dropshelf

Dropshelf is a small Windows utility that gives you a drag-and-drop shelf at the top of your screen, a
floating "basket" you can summon mid-drag, and a searchable clipboard history. It lives in the system tray.

It is an independent Windows app inspired by Droppy for macOS. It is not affiliated with, endorsed by, or
derived from Droppy or its authors, and it uses no Droppy code or assets.

## Features

- **Notch shelf** - a black pill flush with the top edge of the primary screen. Hover it (or drag files over it) and
  it animates open into a shelf. While collapsed the pill is click-through, so it does not block clicks on tab strips
  or title bars underneath; it becomes solid when you drag something near it (or, with "Expand on hover" off, when
  the cursor is over it so you can click it open).
  - Drop files, folders, text or links onto it. Files are kept by reference (nothing is copied); text and links are
    saved as `.txt` / `.url` files so they can be dragged out again.
  - Drag items out to Explorer or any other app. Ctrl/Shift-click to multi-select and drag several at once.
    Files and folders are always copied out (the originals are never moved); only text and link items can be
    moved, which removes them from the shelf.
  - Right-click: Open, Show in Explorer, Copy, Remove. Double-click opens an item.
  - Items whose files no longer exist are removed when the shelf opens. Items on a drive or network share that is
    currently unavailable (unplugged USB drive, offline share) are kept.
  - The shelf survives restarts (optional).
- **Floating basket** - shake the mouse left and right while dragging something, or press `Ctrl+Shift+B`.
  A small acrylic window appears at the cursor and accepts drops. Drag items back out (this removes them from the
  basket, including their saved text/link files, and it hides itself when empty) or press **Move all to shelf**.
- **Clipboard history** - remembers text, images and file lists (default 50 entries, pinned entries are kept).
  Press `Ctrl+Shift+V` to open the picker, type to search, `Enter` to paste into the window you came from.
  Clipboard content flagged as private by password managers (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory`) is never recorded. Text over 1,000,000 characters is not recorded. The history file
  is encrypted at rest with Windows DPAPI (current user); copied images are stored as PNG files.
- **Pill activities** - the collapsed pill can show live content like a Dynamic Island: a priority-ordered set of
  activities, each with a compact view left and right of the mascot (the pill springs wider to fit it) and optionally a
  tab in the expanded shelf. Short-lived HUDs (volume, brightness, battery, Bluetooth) take the pill over for about
  1.8 seconds and then hand it back. The expanded shelf has a tab bar: **Shelf** (default, and selected again whenever you
  drag files in) plus one tab per activity that has an expanded view. See [Pill activities](#pill-activities).
  - **Now playing** - album art left of the mascot and an equalizer right of it while media plays (also for ten seconds
    after a pause). The *Now Playing* tab has big art over a soft glow of itself, title, artist, source app, a seekable
    scrubber and previous / play-pause / next. Works with any app that reports to Windows' media controls (Spotify, browsers,
    Media Player...). The equalizer follows the real output level (Core Audio peak meter) and falls back to a pseudo-random
    animation if no meter is available.
  - **Volume and brightness HUD** - a speaker glyph (muted / low / medium / high), an animated fill bar and the percentage
    when the volume changes; the same for the brightness of a laptop panel. Scrolling the mouse wheel over the collapsed
    pill changes the volume. The native Windows volume popup can be hidden while the HUD shows (best effort, see
    [Notes](#notes-on-the-windows-hooks)).
  - **Battery and Bluetooth** - plugging in or unplugging the charger (charging bolt, percentage), low battery warnings at
    20 % and 10 % (red pulse) and a fully-charged notice; Bluetooth devices connecting or disconnecting with their name,
    an icon (headphones, keyboard, mouse...) and their battery level when Windows reports it. Nothing is shown on a
    desktop without a battery.
- **Convert & compress** - right-click a shelf or basket item (several can be selected) > **Tools**. Images (PNG, JPG, BMP,
  GIF, TIFF, and WebP / HEIC / AVIF when Windows has the codec): *Convert to PNG / JPG*, *Resize 50 % / 25 % / to a max width*,
  *Compress JPG (quality 80 / 60)*. Any files or folders: *Compress to ZIP* (named after the item, or `Archive.zip`), and
  *Extract here* for `.zip` files (into a new folder named after the archive). The EXIF orientation is applied to the pixels.
  Results are new files next to the source (or in `Downloads` when that folder is read-only), named like
  `photo (converted).png`, made unique with ` (2)`, ` (3)`..., never overwriting anything; they appear on the shelf with a
  fly-in. The work runs off the UI thread; a job that takes longer than a moment shows a progress ring on the pill, and the
  result is a HUD such as *Converted 3 files*. (Windows ships no WebP *encoder*, so WebP can be read but not written.)
- **Screenshot to shelf + OCR** - `Ctrl+Shift+S` dims every monitor; drag a rectangle (live size label, handles that spring in),
  release to capture, `Enter` to capture while dragging, `F` or the *Full screen* button for the whole monitor under the cursor,
  `Esc` / right-click to cancel. The PNG is saved as `Pictures\Screenshots\Dropshelf yyyy-MM-dd HH.mm.ss.png`, copied to the
  clipboard (the clipboard history ignores it), added to the shelf, and the pill shows a thumbnail with a **Copy text** button
  that runs Windows OCR on it and copies the recognized lines. Image items have *Copy text (OCR)* in their context menu too. OCR
  needs a Windows language pack (Settings > Time & language); without one you get a message instead.
- **Timer / Pomodoro** - the *Timer* tab has presets (5, 10, 25 min), a custom `mm:ss` box, start / pause / reset and a Pomodoro
  mode (work, short break, ... a long break after the last round; lengths in Settings). While it runs the pill shows a progress
  ring around a stopwatch and the remaining time; the ring goes from the accent (green on breaks) to orange in the last minute.
  When a phase ends the pill grows with a spring *ding*, the mascot bounces and a soft chime plays (mute it in Settings). Time is
  kept as an end timestamp, so it is exact, survives sleep, and a running timer survives a restart.
- **Quick notes** - the *Notes* tab has a note list and a plain-text editor, auto-saved 500 ms after you stop typing to
  `notes.json`, encrypted with Windows DPAPI like the clipboard history. The first line is the title. Drop text onto the tab to
  append it. `Ctrl+Shift+N` opens the shelf on the tab with the caret in the editor. The shelf is normally a no-activate window;
  clicking into a text box (or the hotkey) lets it take the keyboard, it stays open while the box has focus, and focus goes back to
  your previous window when you press `Esc`, click elsewhere or it collapses.
- **Calendar & weather** - the *Today* tab: a weather card (temperature, condition, high / low, five-day strip) and today's and
  tomorrow's agenda with a "now" marker. Events come from calendar links (**ICS** URLs, parsed by Dropshelf itself: time zones,
  all-day events and common recurrence rules; the URLs are stored encrypted) and, in the installed (MSIX) build, from the Windows
  calendars. Within ten minutes of an event the pill shows *Meeting in 5 min - Title* with a countdown and a HUD when it
  starts. Weather comes from [Open-Meteo](https://open-meteo.com) (no key): the device location if Windows allows it, otherwise
  the city from Settings; refreshed every 30 minutes and after sleep, cached, shown as *offline* when it cannot be reached.
  Weather is **off by default** because turning it on sends your approximate location (or the city) to open-meteo.com; Settings >
  Calendar & Weather also has the unit (C / F), an optional temperature on the idle pill and a *Test* button for calendar links.
- **Appearance** - eight accent colours (Blue default, Indigo, Purple, Pink, Red, Orange, Green, Teal) plus *Windows accent*,
  which follows the accent colour you set in Windows. The accent applies live everywhere, including the mascot (optional).
  Pill style *Black* (default) or *Glass* (dark, translucent). The mascot can be hidden.
- **Hide in fullscreen** - the pill slides up out of view while a game, video or presentation is fullscreen on the pill's
  monitor, and HUDs stay quiet (optionally except the volume HUD).
- **Always running** - installs itself on first run, starts with Windows, relaunches after a crash and comes back after
  update reboots (see [Always running](#always-running)).
- **Tray icon** - Show shelf, Open basket, Clipboard history, Screenshot to shelf, Quick note, Settings, Uninstall, Quit. Only one instance runs at a time.
- **Settings** - grouped into General (launch at startup, hide in fullscreen apps, install location), Shelf (remember items,
  expand on hover, shake to open basket), Media & HUD (now playing, volume / brightness / battery / Bluetooth HUDs, replace the
  Windows volume popup, scroll for volume, volume HUD in fullscreen), Appearance (accent, pill style, mascot), Animations,
  Clipboard (on/off and size), Screenshot, Timer (Pomodoro lengths, chime) and Calendar & Weather. Changes apply immediately.
- **Animations** - everything is spring-driven and runs at the display's frame rate; frames are only produced while
  something is moving (see [Animations](#animations)).

## Animations

- **"Drop" mascot** - a glossy translucent blue teardrop in the left of the pill: a real 3D mesh (`Viewport3D`, generated
  procedurally and lit with a key light, ambient light, specular highlight and a faint glow) with two white eyes. Its head
  follows the cursor (damped springs, limited to +-25 degrees), it blinks every 3-6 seconds, occasionally looks around
  left and right and breathes. When you drag something toward the pill it gets excited (squash-and-stretch bounce, wider
  eyes); when something lands on the shelf it gulps (squash). The render loop only runs while something moves; between
  idle blinks nothing is drawn.
- **Spring pill** - the pill opens with a damped spring (about 4 % overshoot) animating width, height and corner radius
  together; it closes faster with no overshoot. A soft blue bloom and lit rim fade in while it is open or a drag hovers.
  (The glow is drawn inside the pill because the pill's window is exactly pill-sized.)
- **Liquid drag magnet** - while you drag toward the collapsed pill it stretches a few pixels toward the cursor and glows
  brighter the closer you get.
- **Tiles** - tiles flip in (staggered 30 ms apart) when the shelf opens, lift with a growing glow and tilt toward the
  cursor on hover, shrink and fade when removed, and fly in from the drop point when you drop something.
- **Basket** - springs open from the cursor (scale 0.6 to 1 with a slight overshoot) and collapses back toward it.
- **Clipboard popup** - opens with a spring scale (0.94 to 1), fade and 8 px slide-up; rows fade in one after another and
  the selection highlight glides between rows.

Settings > Animations has three levels:

| Level | Effect |
| --- | --- |
| Full (default) | Everything above. |
| Subtle | Short, non-overshooting springs and simple fades/slides. No mascot head tracking or look-around, no tile flips or tilts, no drag-magnet stretch. |
| Off | No animation; everything jumps to its final state. |

If you never choose a level, Windows' *Animation effects* switch (Settings > Accessibility > Visual effects) is honoured:
with it off, the level is Subtle. Picking Full yourself overrides that.

## Pill activities

`Activities/` holds the base for everything that lives on the pill, so later features (timer, notes, calendar...) plug in
without touching the shelf window.

- `IPillActivity` - an id, a priority, `IsActive`, a kind (`Live`: stays while active; `Hud`: raises `HudRequested` and is shown
  for about 1.8 s), a compact content factory (`CompactContent`: one element left and one right of the mascot, with their
  widths), an optional expanded view (a tab title and a content factory) and the events `StateChanged` / `HudRequested`.
  `PillActivityBase` implements the boilerplate.
- `ActivityHost` - decides what the pill shows (a requested HUD, otherwise the active live activity with the highest
  priority), crossfades between activities with springs (`CrossfadeHost`) and tells the shelf window which tabs exist
  (`ActivityTabBar`). Register an activity once in `App.StartActivities`.
- The shelf window springs the pill to the size the current content needs; the mascot slides to the centre between the two
  views while an activity is shown. Nothing runs while nothing changes: WinRT / COM events are marshalled to the UI thread,
  and frames are only produced while a spring moves.

- Round 2 additions to the base: a `CompactContent` can be *interactive* (the pill then takes mouse input, does not open on
  hover and a click on its button does not open the shelf: the screenshot HUD's *Copy text*), a HUD can set its own duration and
  ask to be kept until the shelf closes (results of tools), and a tab can pick its position (`TabOrder`). A tab may also take
  drops itself (`IShelfDropTarget`, used by Notes).

Every activity is created inside a `try`; on a machine where its API is missing (no Core Audio, no WinRT media controls, no
WMI brightness class...) the feature is simply off and the rest keeps working.

## Notes on the windows hooks

- **Volume popup** - the native popup is looked up right after a volume or brightness change and hidden: on Windows 10 / early
  Windows 11 the small topmost `NativeHWNDHost` window (with a `DirectUIHWND` child) of explorer.exe is minimized; on Windows 11
  22H2+ a small, topmost, bottom-centred `XamlExplorerHostIslandWindow` of explorer.exe is hidden. If the window cannot be
  found, both popups show. Turning *Replace Windows volume popup* off just stops the hiding. It is only hidden when the HUD
  really shows (not while the shelf is open or a fullscreen app hides the pill).
- **Scroll for volume** - uses the same low-level mouse hook as shake-to-open basket (installed only while one of the two
  is on). The wheel is only captured over the collapsed pill; anywhere else it passes through untouched.
- **Fullscreen** - checked once a second while *Hide in fullscreen apps* is on: the foreground window must cover the whole
  pill monitor and either `SHQueryUserNotificationState` reports busy / D3D fullscreen / presentation mode, or the window has no
  title bar. The desktop and shell windows never count.

## Always running

Dropshelf is meant to be started once and then forgotten.

- **Self-install** - the first time you run the downloaded `Dropshelf.exe` it copies itself to
  `%LOCALAPPDATA%\Programs\Dropshelf\Dropshelf.exe`, starts that copy and exits. It also creates a Start Menu shortcut
  (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dropshelf.lnk`). No admin rights are needed. This is skipped under
  a debugger, with `--no-install`, and for anything that is not the single-file `Dropshelf.exe` (for example `dotnet run`
  or a loose debug build). If the copy fails, the app simply keeps running from where it was started.
- **Updating** - download the new `Dropshelf.exe` and double-click it. It asks the running instance to quit, waits up to
  5 seconds for it, overwrites the installed exe and starts it again. Your shelf, clipboard history and settings are kept.
- **Starts with Windows** - "Launch at startup" is on by default. The `HKCU\...\Run` value points at the installed exe
  (quoted, with `--autostart`) and is rewritten at every start if the path changed. Turn it off in Settings.
- **Crash recovery** - after a fatal error the app logs it and starts a fresh copy of itself (`--restarted`). To avoid a
  crash loop it relaunches at most 3 times within 2 minutes (`restarts.json`); after that it tells you and stays down.
  Ordinary exceptions inside a single UI handler are logged and the app keeps running, as before.
- **Windows restarts** - the app registers with `RegisterApplicationRestart`, so Windows Update reboots and "restart apps
  after signing in" bring it back.
- **Uninstall** - tray icon > *Uninstall...*. It removes the autostart entry and the Start Menu shortcut, quits, and then
  deletes the installed exe. Your data is kept unless you tick *Also delete my shelf & clipboard data*.

Command-line switches: `--no-install` (run from where it is, never self-install), `--autostart` (added by the Run key),
`--restarted` (added after a crash relaunch or a Windows restart; waits a few seconds for the old process to exit).

## Hotkeys

| Keys | Action |
| --- | --- |
| `Ctrl+Shift+B` | Open the basket at the cursor |
| `Ctrl+Shift+V` | Open clipboard history |
| `Ctrl+Shift+S` | Screenshot: drag a region (`Enter` / release = capture, `F` = full screen, `Esc` = cancel) |
| `Ctrl+Shift+N` | Quick note: open the shelf on the Notes tab with the editor focused |
| `Ctrl+N`, `Esc` | In the notes editor: new note, release the keyboard |
| `Up` / `Down`, `Enter` | In clipboard history: move, copy back and paste |
| `Ctrl+P`, `Delete`, `Esc` | In clipboard history: pin/unpin, remove entry, close |

## Download

There are two builds:

| Your PC | File |
|---|---|
| Intel or AMD processor (most PCs) | `Dropshelf-x64.exe` |
| Arm processor (Snapdragon, Surface Pro X / Pro 11, Copilot+ PCs) | `Dropshelf-arm64.exe` |

Not sure? **Settings → System → About → System type** says "x64-based processor" or "ARM-based processor".

**Easiest:** open the latest [release](https://github.com/Nikolay833/Practice1/releases/latest), download
`Install-Dropshelf.cmd` and double-click it. It detects your processor and installs the matching build. Or from
PowerShell:

```powershell
irm https://raw.githubusercontent.com/Nikolay833/Practice1/main/tools/install.ps1 | iex
```

Or download the exe yourself from the release and run it. It is a single self-contained file, so no .NET install is
needed. On first run it installs itself and starts with Windows (see [Always running](#always-running)); to update
later, or to switch between x64 and arm64, just run the other exe.

Releases are published on pushes to `main`, on `v*` tags, and from **Actions → Build → Run workflow**. Every other
push still builds both exes as the **Dropshelf-win-x64** and **Dropshelf-win-arm64** artifacts of its Actions run.

## Build locally

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The project targets
`net8.0-windows10.0.19041.0` (the Windows 10 2004 SDK projection, for `Windows.Media.Control`, `Windows.Devices.*` and
`UISettings`) and supports Windows 10 1809 and later; NuGet fetches the SDK projection on restore.

```powershell
dotnet build Dropshelf.sln -c Release

# single-file executable, same as CI (use -r win-arm64 for the Arm build)
dotnet publish src/Dropshelf/Dropshelf.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
```

The project sets `EnableWindowsTargeting`, so it also compiles on Linux/macOS, but it only runs on Windows 10/11.

The app icon is generated by `tools/make_icon.py` (needs Pillow).

## Data

Everything lives in `%LOCALAPPDATA%\Dropshelf`: `settings.json`, `shelf.json`, `clipboard.json` (encrypted), `notes.json` (encrypted),
`calendar.json` (the ICS links, encrypted), `weather.json` (last forecast and location, encrypted), `timer.json` (a running timer),
the `Items` folder
(text/link items), the `Clipboard` folder (copied images), `restarts.json` (crash-loop guard) and `dropshelf.log`.
The installed program is in `%LOCALAPPDATA%\Programs\Dropshelf`.

## Project layout

```
src/Dropshelf/
  Animation/  spring integrator, animation setting, 3D mascot (MascotView, TeardropMesh), tile animations
  Activities/ pill activity base (IPillActivity, ActivityHost, CrossfadeHost, tab bar)
    Media/    now playing (Windows.Media.Control session, equalizer, Now Playing tab)
    Hud/      volume + brightness HUD, HUD building blocks, native popup suppression, toast HUD, progress ring + progress pill
    Power/    battery HUD
    Devices/  Bluetooth HUD
    Timer/    timer + Pomodoro (engine, pill, tab, chime)
    Notes/    quick notes (store, tab)
    Calendar/ ICS parser, calendar service, meeting pill, Today tab
    Weather/  Open-Meteo service, WMO codes, vector weather icons, idle-pill temperature
  Tools/      convert / resize / compress images, zip / extract, output naming, background runner
  Screenshot/ region-capture overlay (one window per monitor), capture + save, Windows OCR
  Shelf/      notch window, item model, drag/drop, shared tile list
  Basket/     floating basket window
  Clipboard/  history service, popup window
  Services/   hotkeys, mouse hook, shake detection, settings, installer, startup registration, crash guard, tray,
              Core Audio endpoint (volume + meter), fullscreen watcher
  Interop/    P/Invoke, Core Audio COM declarations and window helpers
  Settings/   settings window, uninstall dialog
  Themes/     shared WPF styles, accent + pill style service
```
