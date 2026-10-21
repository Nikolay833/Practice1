# Handoff: Dropshelf

Read this first when you pick the project up in a new session. `README.md` (developer view) and `FEATURES.md` (every
feature in plain words) are the full references; this file is the short version plus what is in flight.

## What it is

**Dropshelf** is a Windows 10/11 desktop app (C# 12, WPF, .NET 8) that puts an Apple-style Dynamic Island / Droppy-style
notch at the top centre of the screen. The owner installs each CI build on an **arm64 Lenovo laptop** (Windows 11
24H2, 1920x1200 at 150 %, auto-hiding taskbar) and reports back what to change.

Current state (build **1.0.30**):

- **Transparent island** (setting *Transparent island*, on by default): no black notch at all. Everything is clear
  (alpha 1/255 so it still takes the mouse) and the text is light grey (`IslandWindow.LightGrey`, `#D0D0D4`).
  Turning it off brings back the black notch with white text and glass tab buttons.
- **Two tabs** in the open island (720 x 400): **terminal** and **notes**. On the transparent island the tab switcher is
  plain words (`terminal`, `notes`, `settings`) in monospace; the open tab is bright.
- **Terminal**: a real PowerShell or Command Prompt (Settings → Terminal) in a Windows pseudo console (ConPTY) with
  an xterm-style screen emulator, so interactive tools like `claude` run in it. The shell keeps running while the
  island is closed. PATH is rebuilt from the registry each time the shell starts, so newly installed tools are found.
- **Notes**: drawn like the terminal (light grey monospace, no boxes). Titles on the left, editor on the right, saved
  encrypted to `notes.json`.
- **Closed island live activities / HUDs**: now playing, volume and brightness, battery and charging, Bluetooth
  devices, Caps Lock / Do Not Disturb / Silent indicators, mic/camera in-use dot, download progress, notifications,
  meeting alerts, weather temperature, and a split island (a second activity in a bubble joined by a liquid neck).
- **Native Windows popups hidden** so only the island shows them: volume (the volume keys are taken over with
  `RegisterHotKey`), brightness, and Lenovo's Caps Lock popup (`Activities/Hud/OsdSuppressor.cs`).
- Also: a floating **basket** (Ctrl+Shift+B), **clipboard history** (Ctrl+Shift+V), **screenshots** (Ctrl+Shift+S),
  tray menu, settings window, self-updating portable install and an MSIX package.

Removed on purpose (the owner asked): the Shelf, Clock (timer/stopwatch/alarms), Today and Now Playing tabs. Their old
data files are left untouched in `%LOCALAPPDATA%\Dropshelf`.

## Working rules

- **Branch**: develop and push only on `claude/festive-mayer-m3hm15`. Do not open a pull request unless asked.
- **No model names** in commits, PRs or code.
- **Do not copy Droppy's code or assets.** `Droppy-16.0.1-arm64.dmg` in the repo root is only a reference the owner
  added; leave it alone.
- The owner writes casually and briefly; answer in plain words, short, and **always finish with the direct download
  link** of the newest build (see below). They cannot build it themselves.
- Code style: match the surrounding code (XML doc comments on types and non-obvious members, explicit types, braces
  everywhere, plain-English comments that say why).

## Build and check

The app targets `net8.0-windows10.0.19041.0` but builds on Linux thanks to `EnableWindowsTargeting`:

```sh
timeout 600 $HOME/.dotnet/dotnet build src/Dropshelf/Dropshelf.csproj -c Release
```

If `$HOME/.dotnet` is missing, install the .NET 8 SDK with `dotnet-install.sh --channel 8.0`. It cannot be run on
Linux, so every Windows-only behaviour is verified by the owner on the real laptop.

XAML resource keys: check every `{StaticResource X}` / `FindResource("X")` exists in `Themes/Theme.xaml` or the file
itself before pushing (a missing key only fails at runtime). The previous session used a small script for this; grep
works too.

## CI and download links

`.github/workflows/build.yml` runs on every push and builds `x64`, `arm64` and an MSIX. The version is
`1.0.<run_number>`. Artifacts: `Dropshelf-win-arm64`, `Dropshelf-win-x64`, `Dropshelf-msix`.

Use the GitHub MCP tools (there is no `gh` CLI): `actions_list` → `list_workflow_runs` filtered to the branch, wait for
`conclusion: success`, then `list_workflow_run_artifacts`. Give the owner the **arm64** link in this form (one click
downloads the zip):

```
https://github.com/Nikolay833/Practice1/actions/runs/<run id>/artifacts/<artifact id>
```

Latest: run 30 (1.0.30) → https://github.com/Nikolay833/Practice1/actions/runs/39407613695/artifacts/12256854245

## Where things are

```
src/Dropshelf/
  App.xaml.cs                 startup: services, activity registration, hotkeys, tray, settings changes
  Island/IslandWindow.*       the island: shape, springs, hover/expand/collapse, tabs, keyboard, ApplyLook (transparency)
  Activities/
    IPillActivity.cs          activity contract (+ IWantsKeyboard, IFocusableTab)
    ActivityHost.cs           picks what the closed island shows, HUD timing, split bubble
    ActivityTabBar.cs         tab switcher (glass icons, or Plain words on the transparent island)
    Terminal/                 PseudoConsole (ConPTY), VtScreen (VT parser + grid), TerminalScreen (drawing, keys),
                              TerminalSession (shell lifetime, PATH refresh), TerminalActivity
    Notes/                    NoteStore (notes.json, DPAPI), NotesView (code-only view), NotesActivity
    Hud/                      volume/brightness HUD, IndicatorActivity (Caps/DND/Silent), OsdSuppressor (hides
                              Windows' own popups), toast/progress HUDs
    Media/ Power/ Devices/ Downloads/ Notifications/ Calendar/ Weather/
  Services/                   settings, hotkeys, tray, audio endpoint, message window (brightness events), paths, log
  Settings/SettingsWindow.*   settings UI
```

User data: `%LOCALAPPDATA%\Dropshelf` (`settings.json`, `clipboard.json`, `notes.json`, `dropshelf.log`). When the
owner reports a bug, ask for the last lines of `dropshelf.log`.

## Hard-won details

- **Island window** is layered and no-activate. Fully transparent pixels let clicks through, so anything clickable on
  the transparent island needs a `#01000000` background. Typing requires `AllowActivation()` (removes
  `WS_EX_NOACTIVATE`). Elements implementing `IWantsKeyboard`, or text boxes, trigger it on click. The island stays open
  while one of them has focus.
- **Windows OSD suppression** (`OsdSuppressor`): a WinEvent hook runs on its own thread. Windows 11 24H2 shows the
  volume/brightness flyout in a full-screen untitled `XamlExplorerHostIslandWindow`, which gets moved off-screen during
  key bursts. The Lenovo Caps Lock popup (process names containing `hotkey`/`capslk`/`numlk`/`osd`) is made layered with
  alpha 0, parked off-screen and kept there. Volume keys are taken over entirely with `RegisterHotKey` (VK 0xAD–0xAF),
  and brightness changes come from `RegisterPowerSettingNotification`. `RestoreShell` puts everything back on exit.
- **Terminal**: the VT emulator handles CSI cursor/erase/insert/delete, scroll regions, SGR 16/256/truecolor, wide
  characters, alternate screen, bracketed paste and device queries. `COLORTERM=truecolor` and `TERM_PROGRAM=Dropshelf`
  are set for child programs.

## Open items / waiting on the owner

- **1.0.30 is untested on the laptop**: the Notes tab, the plain-word tab switcher and focus moving when a tab is
  clicked.
- **Claude Code in the terminal**: the owner installed it (`C:\Users\nikir\.local\bin\claude.exe`) but `claude` was "not
  recognized" in the island because Dropshelf's PATH was stale. 1.0.29 fixed that (PATH refresh). It is unconfirmed
  whether `claude` now runs well in the island; watch for rendering glitches in the VT emulator with its UI.
- **Lenovo Caps Lock popup**: the last report was that only Caps Lock still flashed sometimes. The "neutralize" fix
  (1.0.27) has not been confirmed either way. Volume is confirmed fixed, and brightness was fine after 1.0.25.
