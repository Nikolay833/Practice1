# Dropshelf

Dropshelf is a small Windows utility that turns the top of your screen into an Apple-style **island**: an invisible
(or black, if you prefer) notch that shows live activities like the iPhone's Dynamic Island and opens into a real
terminal. It also has a floating "basket" you can summon mid-drag and a searchable clipboard history. It lives in the
system tray.

It is an independent Windows app inspired by Droppy for macOS and by Apple's design. It is not affiliated with, endorsed
by, or derived from Droppy, its authors or Apple, and it uses no Droppy or Apple code or assets.

For a plain-language tour of everything it does, see [FEATURES.md](FEATURES.md).

## Features

- **The island** - fully transparent by default (*Transparent island*): nothing shows at the top-centre of the primary
  screen until something happens, and its text is light grey (turn the setting off for a black MacBook-style notch).
  While collapsed it is click-through. Rest the cursor on it (or click it, with *Open on hover* off, or press
  `Ctrl+Shift+N`) and it opens into a **real terminal**: PowerShell or Command Prompt in a Windows pseudo console
  (ConPTY), drawn by Dropshelf's own VT screen, so interactive programs such as `claude` run in it. A click anywhere else
  closes it; the shell keeps running while it is closed.
- **Live activities** - while collapsed, the island shows a leading view at its left end and a trailing view at its right
  end, like the Dynamic Island, and springs wider to fit them. Short-lived HUDs (volume, brightness, battery, Bluetooth,
  tool results, screenshots, notifications) take it over for about 1.8 seconds and then hand it back. See
  [Island activities](#island-activities).
  - **Now playing** - album art and a five-bar waveform tinted with a colour from the artwork (white without one) while
    media plays (also for ten seconds after a pause). The waveform follows the real output level (Core Audio peak meter)
    and falls back to a pseudo-random animation. The *Now Playing* tab follows Droppy's player: art, bold title, artist and
    a live waveform; a thick seekable progress bar with elapsed and remaining time; big previous / play-pause / next
    between shuffle and repeat buttons; and a right column with *Playing From* and a volume slider. Works with any app
    that reports to Windows' media controls.
  - **Volume and brightness** - a speaker glyph, a white fill bar and the percentage. Scrolling the mouse wheel over the
    collapsed island changes the volume. The native Windows volume popup can be hidden while the HUD shows (best effort,
    see [Notes](#notes-on-the-windows-hooks)).
  - **Battery and Bluetooth** - charger plugged in or removed (green bolt), low battery at 20 % and 10 % (red pulse), fully
    charged; Bluetooth devices connecting or disconnecting with their name, an icon and their battery level when Windows
    reports it. Nothing is shown on a desktop without a battery.
  - **Timer / Pomodoro** - laid out like the iOS StandBy timer: an orange minute ruler (drag, scroll or click; 1-120 min),
    a "Start Timer" capsule and large orange digits; Pause / Cancel while running; a Pomodoro capsule. While it runs the island
    shows an orange ring and countdown (green on breaks), like Apple's timer Live Activity. Monotonic clock, sleep-aware,
    survives a restart; a soft chime when a phase ends.
  - **Today** - two StandBy-style widgets: weather (now, plus an hourly forecast with chance of rain; Open-Meteo, off by
    default because it sends your approximate location) and a week calendar with today circled and the next event from
    ICS links and, in the MSIX version, Windows calendars. Meeting alerts on
    the island within ten minutes of an event.
  - **Notifications** (MSIX version only, off by default) - Windows toast notifications on the island with a red count
    badge; clicking opens the app. Privacy mode, per-app mute and do-not-disturb in fullscreen.
- **Floating basket** - shake the mouse left and right while dragging something, or press `Ctrl+Shift+B`. A glass card
  like Droppy's appears at the cursor and accepts files, folders, text and links (files are kept by reference; text and
  links are saved as `.txt` / `.url` files). Compact, it shows a fanned stack of previews (drag it to drag everything out)
  and an "N Files >" button; expanded, a back button, the title and total size, icon / list views and every item with a
  hover remove badge. Drag items back out (this removes them from the basket, and it hides itself when empty),
  double-click to open, right-click for Droppy's icon menu: Copy, Open, Show in Explorer, Convert to..., Resize, Extract
  Text, Compress, Create ZIP, Extract Here, Remove.
- **Convert & compress** - basket context menu: *Convert to... PNG / JPEG*, *Resize 50 % / 25 % / Max Width*,
  *Compress JPEG (80 / 60)*, *Create ZIP* and *Extract Here* (protected against path tricks and zip bombs).
  Results are new, uniquely named files next to the source (never overwriting anything) and appear in the basket.
- **Screenshot + OCR** - `Ctrl+Shift+S` dims every monitor; drag a region, `Enter` / release to capture, `F` for the whole
  monitor, `Esc` to cancel. The PNG is saved to `Pictures\Screenshots`, copied to the clipboard (the clipboard history
  ignores it) and the island shows a thumbnail with a **Copy text** button that runs Windows OCR.
- **Clipboard history** - like Droppy's: `Ctrl+Shift+V` slides a black strip of cards up from the bottom of the screen
  (kind, age, preview, size; white outline on the selected card) with a search field and filter capsules (Clipboard,
  Pinned, Text, Images, Files). Type to search, `Left` / `Right` to choose, `Enter` to paste into the window you came from. Remembers text, images and file lists (default 50 entries, pinned entries are kept). Content flagged as
  private by password managers is never recorded; the history is encrypted with Windows DPAPI. The list is virtualized.
- **Apple design** - Apple's dark-mode system colours and label greys, SF Pro when installed (Segoe UI Variable
  otherwise), iOS/macOS switches, sliders, segmented controls, plain accent buttons and macOS menus with an icon on every
  row. Settings looks like macOS System Settings (traffic-light window buttons, grouped sections) and dialogs are
  macOS-style alerts. Accent colours are Apple's: Blue (default), Purple, Pink, Red, Orange, Yellow, Green,
  Teal, Indigo, Graphite, plus *Windows accent*.
- **Hide in fullscreen** - the island slides up out of view while a game, video or presentation is fullscreen on its
  monitor, everything on it stops, and HUDs stay quiet (optionally except the volume HUD).
- **Always running** - installs itself on first run, starts with Windows, relaunches after a crash and comes back after
  update reboots (see [Always running](#always-running)). The MSIX version is installed and updated by Windows and starts
  through a startup task instead.
- **Tray icon** - Open Dropshelf, Open basket, Clipboard history, Take screenshot, Terminal, Settings, Uninstall, Quit.
  Only one instance runs at a time.

## Performance

The app is built so that nothing is drawn or computed while nothing changes:

- **The island window is not resized per frame.** It is a layered (per-pixel alpha) window, and resizing one reallocates
  its surfaces. The window is grown once at the start of an animation (with headroom for the overshoot), the island is
  sized inside it, and the window is fitted to the island when the springs settle. Unchanged positions are never sent to
  Win32 again. Fully transparent pixels let clicks through, so the headroom never blocks anything.
- **Capped frame rate for long animations.** `FrameLoop` follows `CompositionTarget.Rendering` for short transitions, but
  the waveform, spinners and the battery pulse tick from a 30 fps dispatcher timer, so playing music no longer keeps the
  render loop at the monitor's refresh rate.
- **Hidden means stopped.** Layers that cannot be seen are `Hidden`, not just transparent: the collapsed island's waveform
  stops while it is open, the open panel's views (scrubber timer) stop while it is closed, and everything stops while a
  fullscreen app hides the island.
- **No 3D, no per-item effects.** The 3D mascot (a `Viewport3D` with a drop-shadow effect, re-rendered as the cursor moved)
  and the per-tile hover shadow and tilt are gone.
- **Quiet mouse hook.** The low-level hook forwards mouse moves to the UI thread only while the left button is held and at
  most every 8 ms; ordinary movement never wakes the app.
- **No effects in popups.** Settings and the alerts are ordinary windows (DWM draws their shadow and Windows 11 rounded
  corners) instead of layered windows rendering a `DropShadowEffect` on every scroll frame. The basket and the clipboard
  strip are layered (for Droppy's large corner radii) but have no effects and are only resized when their layout changes.
  The clipboard cards are virtualized.

## Animations

Everything is spring-driven. Short transitions run at the display's frame rate, long-running ones at 30 fps, and frames
are only produced while something moves.

- **Island** - opens with a soft, slightly bouncy spring (like the Dynamic Island) animating width, height and corner
  radius together while the panel content zooms in from 92 % and fades up; closes faster with no overshoot. Live
  activities grow it with a small spring and crossfade their content.
- **Tab capsule** - the highlight circle glides between the icon tabs; the capsule drops in under the island as it opens.
- **Basket** - the content springs in (scale 0.86 to 1 with a slight overshoot) and collapses back, and pops when it
  switches between compact and expanded; dropped items fly in from the drop point.
- **Clipboard strip** - slides up from the bottom edge with a spring and fades in; the first cards fade in one after
  another.

Settings > Appearance > Animations has three levels:

| Level | Effect |
| --- | --- |
| Full (default) | Everything above. |
| Reduced | Short, non-overshooting springs and simple fades (like Apple's Reduce Motion). |
| Off | No animation; everything jumps to its final state. |

If you never choose a level, Windows' *Animation effects* switch (Settings > Accessibility > Visual effects) is honoured:
with it off, the level is Reduced. Picking Full yourself overrides that.

## Island activities

`Activities/` holds the base for everything that lives on the island, so features plug in without touching the island
window.

- `IPillActivity` - an id, a priority, `IsActive`, a kind (`Live`: stays while active; `Hud`: raises `HudRequested` and is shown
  for about 1.8 s), a compact content factory (`CompactContent`: a leading and a trailing element, with their widths), an
  optional expanded view (a tab title and a content factory) and the events `StateChanged` / `HudRequested`.
  `PillActivityBase` implements the boilerplate.
- `ActivityHost` - decides what the island shows (a requested HUD, otherwise the active live activity with the highest
  priority), crossfades between activities with springs (`CrossfadeHost`) and tells the island window which tabs exist
  (`ActivityTabBar`, an Apple segmented control). Register an activity once in `App.StartActivities`.
- `IslandWindow` springs the island to the size the current content needs. Nothing runs while nothing changes: WinRT / COM
  events are marshalled to the UI thread, and frames are only produced while a spring moves.
- A `CompactContent` can be *interactive* (the island then takes mouse input, does not open on hover and a click on its
  button does not open it: the screenshot HUD's *Copy text*), a HUD can set its own duration and ask to be kept until the
  island closes (results of tools), and a tab can pick its position (`TabOrder`).

Every activity is created inside a `try`; on a machine where its API is missing (no Core Audio, no WinRT media controls, no
WMI brightness class...) the feature is simply off and the rest keeps working.

## Notes on the windows hooks

- **Volume popup** - the native popup is looked up right after a volume or brightness change and hidden: on Windows 10 / early
  Windows 11 the small topmost `NativeHWNDHost` window (with a `DirectUIHWND` child) of explorer.exe is minimized; on Windows 11
  22H2+ a small, topmost, bottom-centred `XamlExplorerHostIslandWindow` of explorer.exe is hidden. If the window cannot be
  found, both popups show. Turning *Replace Windows volume popup* off just stops the hiding. It is only hidden when the HUD
  really shows (not while the island is open or a fullscreen app hides it).
- **Scroll for volume** - uses the same low-level mouse hook as shake-to-open basket (installed only while one of the two
  is on). The wheel is only captured over the collapsed island; anywhere else it passes through untouched. Mouse moves are only
  forwarded to the UI thread while the left button is held (for the shake), at most every 8 ms.
- **Fullscreen** - checked once a second while *Hide in fullscreen apps* is on: the foreground window must cover the whole
  island monitor and either `SHQueryUserNotificationState` reports busy / D3D fullscreen / presentation mode, or the window has no
  title bar. The desktop and shell windows never count.

## Always running

Dropshelf is meant to be started once and then forgotten.

- **Self-install** - the first time you run the downloaded `Dropshelf.exe` it copies itself to
  `%LOCALAPPDATA%\Programs\Dropshelf\Dropshelf.exe`, starts that copy and exits. It also creates a Start Menu shortcut
  (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dropshelf.lnk`). No admin rights are needed. This is skipped under
  a debugger, with `--no-install`, and for anything that is not the single-file `Dropshelf.exe` (for example `dotnet run`
  or a loose debug build). If the copy fails, the app simply keeps running from where it was started.
- **Updating** - download the new `Dropshelf.exe` and double-click it. It asks the running instance to quit, waits up to
  10 seconds for it to exit, replaces the installed exe atomically (an older exe never replaces a newer one) and starts it again. Your clipboard history, notes and settings are kept.
- **Starts with Windows** - "Launch at startup" is on by default. The `HKCU\...\Run` value points at the installed exe
  (quoted, with `--autostart`) and is rewritten at every start if the path changed. Turn it off in Settings.
- **Crash recovery** - after a fatal error the app logs it and starts a fresh copy of itself (`--restarted`). To avoid a
  crash loop it relaunches at most 3 times within 30 minutes (`restarts.json`, reset after 10 healthy minutes); after that it tells you and stays down.
  Ordinary exceptions inside a single UI handler are logged and the app keeps running, as before.
- **Windows restarts** - the app registers with `RegisterApplicationRestart`, so Windows Update reboots and "restart apps
  after signing in" bring it back.
- **Uninstall** - tray icon > *Uninstall...*. It removes the autostart entry and the Start Menu shortcut, quits, and then
  deletes the installed exe. Your data is kept unless you turn on *Also delete my data*.

- **MSIX version** - Windows installs, updates and removes it, so none of the above self-install steps run. It starts with
  Windows through a **startup task** (listed in Task Manager > Startup apps, where you can turn it off; Settings then says so,
  because an app cannot turn it back on after that), is relaunched after a crash through its AppUserModelID, and the tray
  *Uninstall* item opens Settings > Apps. The first time it runs on a PC that also has the portable version set to start with
  Windows, it offers once to remove that autostart entry (only one copy ever runs, but this keeps sign-in tidy; if Windows keeps
  the removal private to the package, delete the entry in Task Manager > Startup apps).

Command-line switches: `--no-install` (run from where it is, never self-install), `--autostart` (added by the Run key),
`--restarted` (added after a crash relaunch or a Windows restart; waits a few seconds for the old process to exit).

## Hotkeys

| Keys | Action |
| --- | --- |
| `Ctrl+Shift+B` | Open the basket at the cursor |
| `Ctrl+Shift+V` | Open clipboard history |
| `Ctrl+Shift+S` | Screenshot: drag a region (`Enter` / release = capture, `F` = full screen, `Esc` = cancel) |
| `Ctrl+Shift+N` | Open the island with the keyboard in the terminal |
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

### MSIX version (notifications and Windows calendar)

The portable exes are the default and the easiest. The **MSIX version** is a second download that gives the app *package
identity*, which Windows requires for two features:

- **notifications on the island** (reading Windows toast notifications), and
- **Windows calendar** events in the Today tab and the meeting alert.

Everything else is identical. It is one file, `Dropshelf.msixbundle`, that holds both the x64 and Arm64 packages; Windows picks the
right one. It is signed with a self-signed certificate, so Windows has to trust that certificate once. From PowerShell:

```powershell
irm https://github.com/Nikolay833/Practice1/releases/latest/download/Install-Dropshelf-MSIX.ps1 | iex
```

`Install-Dropshelf-MSIX.ps1` downloads the bundle and `Dropshelf.cer` from the latest release, adds the certificate to *Local
Machine > Trusted People* (it asks for administrator permission once, with a Windows prompt) and then installs the bundle for
your own account. Needs Windows 10 version 2004 or later. To do it by hand: trust `Dropshelf.cer` (double-click > *Install
Certificate* > *Local Machine* > *Trusted People*), then double-click `Dropshelf.msixbundle`. Remove it in Settings > Apps.

Switching between the portable and the MSIX version is fine; only one of them runs at a time. Their settings and data are
both kept under `%LOCALAPPDATA%\Dropshelf`. The MSIX package turns off Windows' write virtualization (`desktop6` registry and file
system virtualization plus the `unvirtualizedResources` capability), so the packaged app writes to the real `%LOCALAPPDATA%` and
`HKCU` instead of a private copy inside the package. That is what lets the two versions share settings and data, and lets the MSIX
version find and remove the portable version's autostart entry. Data an MSIX copy wrote before this change may still be in the
package's private store and is not migrated.

#### MSIX signing certificate

A signed package needs a certificate whose subject is exactly `CN=Dropshelf`. If the repository has no certificate secret, every
CI run creates a new self-signed one, so the certificate (and with it the trust step above) changes with every release. To keep one
certificate for all releases, create it once on a Windows PC and store it as repository secrets:

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Dropshelf" -CertStoreLocation Cert:\CurrentUser\My `
  -NotAfter (Get-Date).AddYears(10) -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
$password = Read-Host "Choose a password for the .pfx"
Export-PfxCertificate -Cert $cert -FilePath dropshelf.pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force)
[Convert]::ToBase64String([IO.File]::ReadAllBytes("dropshelf.pfx")) | Set-Clipboard   # paste as secret MSIX_PFX_BASE64
```

Then add two secrets under **Settings > Secrets and variables > Actions**: `MSIX_PFX_BASE64` (the clipboard text) and
`MSIX_PFX_PASSWORD` (the password). Keep `dropshelf.pfx` somewhere safe and do not commit it (`*.pfx` is ignored). The workflow
then signs with it and publishes the matching `Dropshelf.cer`, so people who trusted it once can update without repeating that step.

### Arm PCs

If you run the x64 exe on an Arm PC it works (Windows emulates it), but the Arm version runs faster and uses less battery.
Dropshelf notices this: once, a notice on the island, and permanently a line in Settings > General, both with a **Get Arm version**
button. The button downloads `Dropshelf-arm64.exe` from the latest release (progress shows on the island) and runs it, which replaces
the installed copy. Nothing is downloaded until you click, and *Don't show again* hides the notice. The MSIX version is never affected:
its bundle already installs the Arm package on Arm PCs.

Releases are published on pushes to `main`, on `v*` tags, and from **Actions → Build → Run workflow**. Every other
push still builds both exes as the **Dropshelf-win-x64** and **Dropshelf-win-arm64** artifacts of its Actions run, plus the signed
bundle and its certificate as the **Dropshelf-msix** artifact. If the MSIX job fails, the release is still published without it.

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

The app icon is generated by `tools/make_icon.py` and the MSIX tile images in `packaging/Assets` by `tools/make_msix_assets.py`
(both need Pillow).

The MSIX bundle is built without Visual Studio, on Windows with the Windows 10/11 SDK installed (CI's `windows-latest` has it):

```powershell
./packaging/build-msix.ps1 -Version 1.0.1.0 -OutDir msix-out        # publishes x64 + arm64, packs both, bundles them
./packaging/sign-msix.ps1 -Bundle msix-out/Dropshelf.msixbundle -OutDir msix-out   # signs, writes msix-out/Dropshelf.cer
```

## Data

Everything lives in `%LOCALAPPDATA%\Dropshelf`: `settings.json`, `clipboard.json` (encrypted), `notes.json` (encrypted),
`calendar.json` (the ICS links, encrypted), `weather.json` (last forecast and location, encrypted), `timer.json` (a running timer),
the `Items` folder
(text/link items in the basket), the `Clipboard` folder (copied images), `restarts.json` (crash-loop guard) and `dropshelf.log`.
The installed program is in `%LOCALAPPDATA%\Programs\Dropshelf` (portable version; the MSIX version lives in `C:\Program Files\WindowsApps`).
Notification app names and the mute list are part of `settings.json`; notification text is never stored.

## Project layout

```
src/Dropshelf/
  Island/     the island window: notch shape, spring layout without per-frame resizes, hover, tabs
  Animation/  spring integrator, frame loop (vsync or capped rate), animation setting, tile animations
  Activities/ island activity base (IPillActivity, ActivityHost, CrossfadeHost, segmented tab bar)
    Media/    now playing (Windows.Media.Control session, artwork tint, waveform, Now Playing tab)
    Hud/      volume + brightness HUD, HUD building blocks, native popup suppression, toast HUD, progress ring + progress HUD
    Power/    battery HUD
    Devices/  Bluetooth HUD
    Timer/    timer + Pomodoro (engine, island view, tab, chime)
    Notes/    quick notes (store, tab)
    Calendar/ ICS parser, calendar service (ICS + Windows appointment store), meeting HUD, Today tab
    Notifications/ Windows toast listener (event or polling), notification HUD with queue and click
    Weather/  Open-Meteo service, WMO codes, vector weather icons, idle-island temperature
  Items/      basket item model and store, drag/drop, icon loading, the tile list
  Basket/     floating basket window
  Tools/      convert / resize / compress images, zip / extract, output naming, background runner
  Screenshot/ region-capture overlay (one window per monitor), capture + save, Windows OCR
  Clipboard/  history service, the card-strip window
  Services/   hotkeys, mouse hook, shake detection, settings, installer, startup registration, crash guard, tray,
              Core Audio endpoint (volume + meter), fullscreen watcher
  Interop/    P/Invoke, Core Audio COM declarations, app activation, window helpers
  Settings/   settings window, alerts
  Themes/     the Apple theme (colours, fonts, control templates), accent service
packaging/    MSIX: manifest template, tile images, build and sign scripts (makeappx / signtool)
tools/        icon + MSIX asset generators, installer scripts (portable and MSIX)
```
