# Dropshelf: the complete guide

> **Status:** Dropshelf compiles and the automatic builds on GitHub pass for every version (Intel/AMD, Arm and the MSIX
> package), but nobody has run it on a real Windows PC yet. Treat this as a test version. Everything below describes how
> it is designed to work.

---

## Contents

1. [What is this?](#1-what-is-this)
2. [The idea in one picture](#2-the-idea-in-one-picture)
3. [Words used in this guide](#3-words-used-in-this-guide)
4. [Getting it and installing it](#4-getting-it-and-installing-it)
5. [The island (the notch at the top of the screen)](#5-the-island-the-notch-at-the-top-of-the-screen)
6. [How it stays fast](#6-how-it-stays-fast)
7. [The basket](#7-the-basket)
8. [Clipboard history](#8-clipboard-history)
9. [Live activities on the island](#9-live-activities-on-the-island)
10. [Now playing (music and video)](#10-now-playing-music-and-video)
11. [Volume and brightness](#11-volume-and-brightness)
12. [Battery, devices and status indicators](#12-battery-devices-and-status-indicators)
13. [Notifications on the island](#13-notifications-on-the-island)
14. [File tools: convert, resize, compress, zip](#14-file-tools-convert-resize-compress-zip)
15. [Screenshots and copying text out of images](#15-screenshots-and-copying-text-out-of-images)
16. [The terminal](#16-the-terminal)
17. [Notes, and removed tabs](#17-notes-and-removed-tabs)
18. [Calendar and weather](#18-calendar-and-weather)
19. [Looks: the Apple design, colours, animations](#19-looks-the-apple-design-colours-animations)
20. [Hiding during games, videos and presentations](#20-hiding-during-games-videos-and-presentations)
21. [Always running: startup, crashes, updates](#21-always-running-startup-crashes-updates)
22. [The tray icon](#22-the-tray-icon)
23. [Every setting](#23-every-setting)
24. [Every keyboard shortcut](#24-every-keyboard-shortcut)
25. [Privacy and security](#25-privacy-and-security)
26. [Where your data is stored](#26-where-your-data-is-stored)
27. [Uninstalling](#27-uninstalling)
28. [Intel/AMD vs Arm PCs](#28-intelamd-vs-arm-pcs)
29. [Known limits](#29-known-limits)
30. [For developers: how it is built](#30-for-developers-how-it-is-built)

---

## 1. What is this?

**Dropshelf is a free helper app for Windows 10 and 11.** It puts an island at the top-centre of your screen, designed to
move like Apple's MacBook notch, the iPhone's **Dynamic Island** and the Mac notch app **Droppy**. By default it is
**fully transparent**: there is no black notch, nothing shows until something happens. While closed it shows live
information in light grey: the song that is playing, your volume, your battery, a download, your next meeting. Rest the
mouse at the top-centre of the screen and it opens into a **real terminal** (PowerShell or Command Prompt), so you can
run anything there, including `claude`.

Next to the island there is a **basket** (a floating drop zone you can summon while dragging files) and a searchable
**clipboard history**.

**Where the idea comes from.** On Apple MacBooks there is a black cut-out at the top of the screen for the camera, called
the *notch*. Mac apps such as **Droppy** turn that notch into a control centre. Windows laptops have no notch, so
Dropshelf draws its own. Dropshelf was written from scratch: it is *not* made by Droppy's authors or by Apple, uses none
of their code, pictures or names, and is not affiliated with them in any way. (The original Droppy file,
`Droppy-16.0.1-arm64.dmg`, is in this repository only as the reference it was inspired by. It is a Mac app and does not
run on Windows.)

**What it costs.** Nothing. There is no account, no sign-in, no ads and no tracking.

---

## 2. The idea in one picture

```
 ┌───────────────────────────────── your screen ──────────────────────────────────┐
 │                                                                                │
 │                       ♪              ▂▅▃▆     ← the island (closed, playing):    │
 │                                                  just the information, no notch │
 │                                                                                │
 │   rest the mouse at the top-centre and it opens, still transparent:             │
 │                                                                                │
 │        PS C:\Users\you> claude                                                   │
 │        ╭────────────────────────────────────────────╮                           │
 │        │ ✻ Welcome to Claude Code!                  │   light grey text,          │
 │        ╰────────────────────────────────────────────╯   straight on whatever     │
 │        > _                                              is behind                │
 │                                  ( ⚙ )                                         │
 └──────────────────────────────────────────────────────────────────────────────┘
```

- **Closed:** nothing at all at the top of the screen (with *Transparent island* off: a 184 × 32 black notch).
- **Live activity:** a picture or icon at the left and live information at the right, like the Dynamic Island.
- **Open:** a 720 × 400 terminal (PowerShell or Command Prompt) with a small round settings button under it.
---

## 3. Words used in this guide

| Word | What it means here |
|---|---|
| **Island** | The black notch at the top-centre of the screen. |
| **Basket** | A floating drop area that appears next to your mouse when you need it. |
| **Item** | Anything you dropped into the basket: a file, a folder, some text or a web link. |
| **Clipboard** | Where Windows keeps what you last copied (Ctrl+C). Normally it only remembers one thing. |
| **Tray** | The small icons in the bottom-right corner of the taskbar, next to the clock. Hidden ones are behind the `^` arrow. |
| **HUD** | "Heads-up display": a short pop-up on the island, like the volume level, that disappears by itself. |
| **Live activity** | Live information shown on the closed island, such as the current song or a running timer. |
| **Hotkey** | A keyboard shortcut that works from any app, for example Ctrl+Shift+V. |
| **Portable exe** | The normal download: one `.exe` file that installs itself. |
| **MSIX** | An optional second way to install, using Windows' own app-package format. Needed for two features. |
| **x64 / Arm64** | The two kinds of processors Windows PCs use. See [section 28](#28-intelamd-vs-arm-pcs). |
| **OCR** | "Optical character recognition": reading text out of a picture. |
| **ICS link** | A private web address for a calendar, offered by Google Calendar, Outlook, iCloud and others. |

---

## 4. Getting it and installing it

### What you need

- Windows 10 (version 1809 or newer) or Windows 11.
- A 64-bit PC: either Intel/AMD (almost all PCs) or Arm (Snapdragon laptops, Surface Pro X / Pro 11, Copilot+ PCs).
- Nothing else. .NET and everything else it needs are packed inside the file. No administrator rights are needed for
  the normal version.
- Optional: Apple's **SF Pro** fonts. If they are installed, Dropshelf uses them, exactly like a Mac; otherwise it uses
  Windows' Segoe UI Variable, which is the closest built-in font.

### Option A: the one-click installer (easiest)

1. Open the [latest release](https://github.com/Nikolay833/Practice1/releases/latest) page on GitHub.
2. Download **`Install-Dropshelf.cmd`**.
3. Double-click it. A black window opens, it checks which kind of processor your PC has, downloads the matching
   version, starts it, and says *Done*. Press any key to close the window.

> A *release* only exists once the project has been published (merged into `main` or tagged `v...`). Until then use
> option C.

### Option B: pick the file yourself

On the same release page, download **one** of these:

| Your PC | File |
|---|---|
| Intel or AMD processor (most PCs) | `Dropshelf-x64.exe` |
| Arm processor (Snapdragon, Surface Pro X / Pro 11, Copilot+ PCs) | `Dropshelf-arm64.exe` |

Not sure which one you have? Open **Settings → System → About** and look at **System type**:
"x64-based processor" means x64, "ARM-based processor" means Arm64.

Double-click the file you downloaded.

### Option C: straight from the automatic builds (before a release exists)

1. Go to the **Actions** tab of the GitHub repository and open the newest green **Build** run. You must be signed in
   to GitHub to download from there.
2. Scroll down to **Artifacts** and download **Dropshelf-win-x64** or **Dropshelf-win-arm64**.
3. It downloads as a zip: right-click it → **Extract All**, then double-click the exe inside.

### The blue "Windows protected your PC" warning

The exe is not signed with a paid code-signing certificate, so Windows SmartScreen will probably warn you the first
time. Click **More info** and then **Run anyway**. This only happens once per downloaded file.

### What happens the first time it runs

1. Dropshelf copies itself to `%LOCALAPPDATA%\Programs\Dropshelf\Dropshelf.exe` (a folder in your own user profile,
   which is why no administrator permission is needed).
2. It adds a **Start Menu** shortcut called *Dropshelf*.
3. It sets itself to **start automatically every time you sign in** to Windows.
4. It starts the installed copy and closes the one you downloaded. You can delete the downloaded file now.
5. The black island appears at the top of your screen and a drop icon appears in the tray.

### Option D: the MSIX version (optional, for two extra features)

The MSIX version is identical except that Windows treats it as a "proper" installed app with an identity. Windows only
lets such apps do two things:

- **show your notifications on the island**, and
- **read your Windows calendar.**

To install it, open PowerShell and run:

```powershell
irm https://github.com/Nikolay833/Practice1/releases/latest/download/Install-Dropshelf-MSIX.ps1 | iex
```

The script downloads `Dropshelf.msixbundle` and its certificate `Dropshelf.cer`, asks once for administrator permission
(to tell Windows to trust the certificate), then installs the app for your account. One bundle holds both the Intel/AMD
and the Arm version; Windows picks the right one. You can also do it by hand: double-click `Dropshelf.cer` → *Install
Certificate* → *Local Machine* → *Trusted People*, then double-click `Dropshelf.msixbundle`.

The portable and MSIX versions can be swapped freely. Only one ever runs at a time, and both use the same settings and
data.

---

## 5. The island (the notch at the top of the screen)

### What it looks like and where it is

- By default the island is **fully transparent** (*Transparent island* in Settings): no black notch, no outline, nothing
  at all at the top of the screen until something shows. Live information and the open terminal appear in light grey
  straight on top of whatever is behind. Turn the setting off for Apple's black notch (184 × 32 pixels, with the
  outward-curved "shoulders" of a MacBook notch and rounded bottom corners) and white text.
- It sits at the top edge of your **main** monitor, centred horizontally.
- It always stays on top of other windows and never appears in the taskbar or in Alt+Tab.
- It is sharp on high-resolution screens and when you change Windows' display scaling (it is fully DPI-aware).

### It does not get in your way

While closed, the island is **click-through**: clicks go straight to whatever is underneath it (a browser tab, a title
bar...). It only becomes "solid" when it needs to be: when *Open on hover* is turned off and your mouse is on it (so you
can click it open), or while a pop-up with a button (the screenshot's *Copy text*) is showing.

To save power it checks where your mouse is only 4 times a second when the mouse is far away (more than ~120 pixels),
and about 16 times a second when you are close.

### Opening and closing

- **Hover:** *rest* the mouse at the top-centre of the screen (on the island) for about a third of a second and it opens
  (if *Open on hover* is on, which is the default). Passing over it, holding a mouse button, or clicking on it never
  opens it, so the browser tabs, address bar or title bar underneath stay usable: after a click there it stays closed
  until the mouse has left it.
- **Click:** with *Open on hover* off, click it to open. **Ctrl+Shift+N** or the tray's *Terminal* opens it with the
  keyboard already in the terminal; the tray's *Notes* does the same for the notes.
- It **closes by itself** a quarter of a second after the mouse leaves, and **at once when you click anywhere else**,
  but never while you are typing in the terminal or the notes.
- Opening uses a soft, slightly bouncy spring like the Dynamic Island.

### What is inside

The open island (720 × 400) has two tabs: the **terminal** (see [section 16](#16-the-terminal)) and **notes** (see
[section 17](#17-notes-and-removed-tabs)). Under it are the tab names, `terminal` and `notes`, in the same light grey
monospaced text (the open one bright), and `settings`. With *Transparent island* off they are a small glass capsule of
icons and a round glass settings button instead. Clicking a tab puts the keyboard straight into it.

---

## 6. How it stays fast

Earlier versions lagged. This version was reworked around one rule: **never draw anything that does not change.**

- **The island window is no longer resized 60+ times a second.** The island is a see-through ("layered") window, and
  resizing such a window throws away and recreates its drawing surfaces. Now the window is sized once at the start of an
  animation (with room for the bounce), the island grows and shrinks *inside* it, and the window is fitted exactly once
  the animation settles. Moving and resizing only happens when something actually changed.
- **The 3D mascot is gone.** It was a real 3D model with a glow effect, redrawn whenever your mouse moved near the top of
  the screen.
- **Long-running animations are capped at 30 frames a second.** The music waveform, spinners and the low-battery pulse
  used to redraw at your monitor's full refresh rate (60–240 times a second) for as long as music played. Short
  transitions (opening, closing, sliding) still use the full refresh rate so they stay smooth.
- **Hidden things stop.** When the island is closed, the open panel is really hidden (not just transparent), so the Now
  Playing progress bar stops updating; when it is open, the closed island's waveform stops; when a fullscreen app hides the
  island, everything on it stops.
- **The mouse hook is quiet.** The global mouse listener (needed for "shake to open basket" and "scroll for volume") used
  to wake the app for every single mouse movement, up to 1000 times a second with a gaming mouse. It now only passes on
  movements while the left button is held (that is, while dragging), and at most 125 times a second.
- **No more drop-shadow effects.** Settings, the clipboard history, the basket and the dialogs used to draw a blurred
  shadow themselves, which made scrolling slow. Settings and the dialogs are now ordinary windows (Windows draws their
  shadow and rounded corners); the basket and the clipboard strip have no effects at all, and they change size only
  when their layout changes, never while animating.
- **The clipboard cards are virtualized.** Only the cards you can see exist, however long your history is (up to 500).
- **No hover effects on basket items** (they used a blurred shadow and a 3D tilt per item).

---

## 7. The basket

The basket is a floating drop zone that comes to *you* while you are dragging something. It is laid out like Droppy's
basket.

**How to open it:**

- **Shake the mouse** left and right while dragging something. Precisely: hold the left button, move at least 8 pixels
  to start a drag, then change direction 4 times, each swing at least 40 pixels long, within 0.6 seconds.
- Or press **Ctrl+Shift+B** at any time.
- Or use the tray menu → *Open basket*.

**What it is:** a dark glass card with large rounded corners and a grab handle on top, springing open at your mouse
pointer. It stays on top and accepts drops of files, folders, text and web links.

| You drop | What the basket keeps |
|---|---|
| A file or folder | A **link** to the original. Nothing is copied, so it takes no space. |
| Selected text (from a browser, Word...) | The text, saved as a small `.txt` file so you can drag it out as a file later. |
| A web link (from a browser's address bar) | A `.url` shortcut. Only `http://` and `https://` links are accepted, for safety. Anything else is kept as text. |

**Two layouts:**

- **Compact** (what you see first): a **✕** button, a **⌄** menu button (*Show All*, *Copy All*, *Clear Basket*), the newest
  items as a **fanned stack of previews**, and a capsule button like **"3 Images ›"**, **"1 Video ›"** or **"5 Files ›"**.
  **Drag the stack** to drag *everything* out at once. Double-click the stack or press the capsule to see all items.
- **Expanded:** a round **‹ back** button, the title ("5 Files") with the **total size** under it ("258.1 MB"), and round
  **icon / list** buttons. Items are big icons or picture thumbnails with their names (no boxes around them); pointing at
  one shows a small grey **✕** to remove it. List view shows a small icon, the name and the folder.

**Using the items:**

- **Drag** an item out to a folder, the desktop, an email, a chat, anywhere that accepts files. Each item you drag out
  leaves the basket, and the basket closes by itself when it is empty.
- **Several at once:** Ctrl+click to pick individual items, Shift+click for a range, then drag any of the selected ones.
- **Files and folders are always copied out**, never moved, so your original file stays exactly where it was.
- **Double-click** an item to open it.
- **Right-click** an item for Droppy's menu (each row with an icon): *Copy*, *Open*, *Show in Explorer*, then for pictures
  *Convert to…*, *Resize*, *Extract Text* and *Compress*, then *Create ZIP*, *Extract Here* for zips, and *Remove* (see
  [section 14](#14-file-tools-convert-resize-compress-zip)).

Drag the grab handle (or the header) to move the basket. It never steals keyboard focus from the app you are working in.
Its contents are not kept after a restart.

---

## 8. Clipboard history

Normally Windows only remembers the last thing you copied. Dropshelf remembers the **last 50** (you can choose 10 to
500).

**Open it:** press **Ctrl+Shift+V** anywhere. Like Droppy's clipboard, a black strip with rounded corners slides up from
the **bottom of the screen**. It shows your history as a row of **cards**: each card has a header with the kind (*Text*,
*Image*, *File*) and its age ("1 hour ago") with a small icon (a blue pin when pinned), the text or picture, and a footer
such as "57 characters" or "1628 × 1588". The selected card has a **white outline**. Above the cards are a **search
field** (already selected, so you can just type) and filter capsules with coloured dots: **Clipboard** (everything),
**Pinned**, **Text**, **Images**, **Files**.

**What is recorded:**

- **Text** (up to 1,000,000 characters per entry; bigger copies are skipped).
- **Images** (saved as PNG files).
- **Copied files** (the list of file names).
- The same thing copied twice in a row is stored only once.

**Using it:**

| Key | Action |
|---|---|
| type | Filters the list instantly. |
| ← / → (or ↑ / ↓) | Moves the selection from card to card. The mouse wheel scrolls the row. |
| Enter | Puts that entry back on the clipboard, closes the window, returns you to the app you were in and pastes it there. |
| Ctrl+P | Pins or unpins the entry. Pinned entries are never pushed out by new ones. |
| Delete | Removes the entry (only when the search box is empty, so you can still delete letters you typed). |
| Esc | Closes the window. Clicking elsewhere closes it too. |

Right-click a card for *Paste*, *Pin / Unpin* and *Delete*. Double-click pastes it.

**Safety details:**

- Password managers mark the passwords they copy as private (`ExcludeClipboardContentFromMonitorProcessing` /
  `CanIncludeInClipboardHistory`). Dropshelf **never records** those.
- The history file is **encrypted** with Windows' own per-user encryption (DPAPI). Another user on the same PC, or
  someone who copies the file to another PC, cannot read it.
- Things Dropshelf copies itself (for example a screenshot) are not added to the history.
- Pasting only happens if your original window really is back in front; otherwise it just leaves the entry on the
  clipboard. It also waits until you have let go of Shift, Alt and the Windows key so the paste is not mixed up with
  them.
- Opened from the tray menu instead of the hotkey, Enter only copies (it does not paste).
- Turning clipboard history off in Settings also frees the Ctrl+Shift+V shortcut.

---

## 9. Live activities on the island

Even while closed, the island can show live information, like the iPhone's Dynamic Island. There are two kinds:

- **Live activities** stay as long as they are relevant: the song that is playing, a running timer, an upcoming
  meeting, a download, the temperature. The island smoothly widens and the information appears at its two ends (a
  picture or icon on the left, details on the right).
- **Split island:** when two are live at once (for example music and a download), the more important one keeps the
  island and the other **splits off into a small bubble** to its right, like the iPhone: the album art or a download
  ring. The two stay **joined by a liquid neck** (black from the top edge down
  to a soft waist; transparent too when the island is), so they read as one shape pinching apart rather than two separate things. The bubble springs out
  from under the island, stretching the neck, and slides back when it is no longer needed. Rest the cursor on the bubble to open the island.
- **HUDs** are short pop-ups that take over the island for about 1.8 seconds and then hand it back: volume, brightness,
  battery, Bluetooth, notifications, "Converted 3 files", screenshot taken, a finished download.

**Camera and microphone dot:** while any app uses the camera, a small **green dot** sits at the right end of the island;
while one uses the microphone (and not the camera), an **orange dot**, as on the iPhone. Windows records which apps use
them (the same information as its own privacy icon in the taskbar), so the dot is reliable; it is checked every 1.5
seconds.

**Downloads:** while a browser downloads into your **Downloads** folder (Chrome, Edge, Brave, Opera, Firefox...), the
island shows a blue spinning ring with a download arrow and how much has arrived ("12.4 MB", or "2 · 40 MB" for two
downloads). Browsers do not say how big the file will be, so the ring spins instead of filling. When it finishes, the
island shows the file's icon and name with "Downloaded": **drag the icon out** of the island into a folder, an email or a
chat, or click it to open the file.

Changes between them cross-fade with spring animations. Each feature has its own on/off switch, and if a feature cannot
work on a particular PC (for example no battery, or no brightness control on a desktop monitor) it simply stays off
without affecting anything else.

---

## 10. Now playing (music and video)

Works with **any app that shows up in Windows' own media controls**: Spotify, YouTube and other sites in Chrome, Edge
or Firefox, Windows Media Player, Apple Music, VLC and more.

**On the closed island:** the album art (small, rounded) at the left end and a **waveform** at the right end, like the
Dynamic Island. The waveform's five bars grow from their middle and take a **colour from the album art** (white when the
art has no strong colour). They follow the **real sound level** coming out of your speakers; if that cannot be measured,
they fall back to a gentle random animation. The island keeps showing the song for 10 seconds after you pause.

There is no Now Playing tab any more (the island only holds the terminal); the song shows on the closed island.

Setting: *Now Playing* (on by default).

---

## 11. Volume and brightness

- **Volume:** whenever the volume changes (keyboard keys, speaker icon, any app), the island shows a speaker icon
  (muted / low / medium / high), a white bar and the percentage.
- **Brightness:** the same for a laptop screen's brightness (desktop monitors usually do not report brightness to
  Windows, so then nothing is shown).
- **Takes over the volume keys:** Windows has no setting to turn its volume pop-up off, so while the island is on screen
  Dropshelf registers the volume up / down / mute keys itself (an ordinary global shortcut, no keyboard hook). Windows then
  never receives them and never shows its pop-up; Dropshelf changes the volume in the same 2 % steps and the island shows
  it. In a fullscreen app (island hidden) or with *Hide the Windows popups* off, the keys go back to Windows. If another
  app already owns a volume key, Windows keeps that one and the pop-up hiding below covers it.
- **Hides Windows' own pop-ups:** by default Dropshelf hides Windows' built-in volume / brightness / media box, also while
  the island is open. Windows tells Dropshelf the moment any window appears (an accessibility "window shown" event, nothing
  is injected into other programs), and the pop-up is hidden in that same instant, before it is really drawn. On Windows
  11 the box is then gone for good while Dropshelf runs; it comes back when the island goes away (fullscreen app, setting
  off, quitting). Many laptops add their own pop-ups for volume, brightness and **Caps Lock**, for example Lenovo's
  *FnHotkey* helper; those are hidden the same way when their program's name says it is an on-screen display ("OSD") or
  a hotkey helper, and only right after a change the island shows (so a keyboard-light or Fn-lock pop-up stays). If a
  pop-up still gets through, the first windows that appeared after a change are written to `dropshelf.log`, which shows
  what it is. This is a workaround; a future Windows update could break it, in which case you would simply see both.
  Turn it off with *Hide the Windows popups*.
- **Scroll to change volume:** roll the mouse wheel over the closed island to turn the volume up or down. The wheel works
  normally everywhere else.
- Changing your default speakers is handled silently (no pop-up).

Settings: *Volume*, *Brightness*, *Hide the Windows popups*, *Scroll over the island to change volume*,
*Show volume in fullscreen* (all on, except the last one).

---

## 12. Battery, devices and status indicators

**Battery** (laptops and tablets only; nothing is shown on desktops), drawn like the iPhone's:

- **Plugging in the charger:** "Charging" on the left; on the right the percentage next to a small battery whose **green
  fill glides up from empty** while the number counts up with it, a **lightning bolt pops in** and a soft highlight
  **sweeps across** the battery once.
- **Unplugging:** "On Battery" with a white battery; the bolt shrinks away.
- **Low battery** at **20 %** and again at **10 %**: "Low Battery" in red with a red battery that breathes.
- **Charged** when it reaches 100 % on the charger.

**Bluetooth devices**, like AirPods on the iPhone:

- **Connecting:** the device's icon (headphones, keyboard, mouse, game controller, phone or a generic one) **pops in on a
  blue disc** and two rings **ripple out** of it; next to it the name and "Connected" in blue.
- If the device reports its **battery level** to Windows (many headphones do), a **green ring fills up** to it with the
  number inside (red at 20 % or less).
- **Disconnecting:** the disc turns grey and sinks back a little, "Disconnected".

**Status indicators** (small pills that say what just changed):

- **Silent Mode:** muting the sound (the mute key, the tray, any app) shows **Silent Mode On** with a red bell that
  **rings** and a slash through it; unmuting shows **Silent Mode Off**. Changing the volume still shows the volume bar.
- **Caps Lock On / Off**, with an arrow that rises into place (filled while on).
- **Do Not Disturb On / Off** with a purple moon that swings in, when Do Not Disturb or a Focus session turns on or off
  (Windows 11).

Settings: *Battery*, *Bluetooth devices*, *Silent Mode*, *Caps Lock*, *Do Not Disturb* (all on by default).

---

## 13. Notifications on the island

**Only in the MSIX version** (Windows only allows packaged apps to read notifications). **Off by default.**

When it is on, notifications from your apps (WhatsApp, Discord, Teams, Mail, Outlook...) also appear on the island:
the app's logo at the left end, *App · Title* and the first line of the message on the right, for about 4 seconds.

- Several notifications at once are shown one after another with a red **+2**-style badge.
- **Clicking** the island opens the app that sent the notification and clears the queue.
- **Hide notification content:** shows only the app name and "New notification", useful when sharing your screen.
- **Mute per app:** Settings lists every app that has sent a notification so far, each with a switch.
- **Do not disturb in fullscreen** (on by default): no notification pop-ups during games or videos.
- The first time you turn it on, **Windows asks for permission** to let Dropshelf read notifications.
- Windows' own notification banner still appears too; apps are not allowed to switch that off.
- Notification text is **never saved** anywhere.

In the portable version the setting shows "Requires the MSIX version".

---

## 14. File tools: convert, resize, compress, zip

Right-click any item in the basket. You can select several items first; the tool runs on all of them. Picture tools
(*Convert to…*, *Resize*, *Extract Text*, *Compress*) only appear when pictures are selected, *Extract Here* only for zips.

**For pictures** (PNG, JPG, BMP, GIF, TIFF, and WebP / HEIC / AVIF if Windows has the codec installed):

| Tool | Result |
|---|---|
| Convert to… → PNG | Lossless PNG copy. |
| Convert to… → JPEG | JPG copy (transparent areas become white). |
| Resize → 50 % / 25 % | Half or quarter size. |
| Resize → Max Width… | Asks for a width in pixels; height follows automatically. |
| Compress → JPEG, High Quality (80) / Smaller (60) | Smaller file, slightly lower quality. |

Photos taken sideways on a phone are rotated correctly (EXIF orientation). Windows has no WebP *writer*, so WebP
pictures can be opened and converted to PNG/JPG, but not saved as WebP. Pictures over 100 megapixels are refused to
protect your memory.

**For any files or folders:**

| Tool | Result |
|---|---|
| Create ZIP | One `.zip` named after the item, or `Archive.zip` for several. |
| Extract Here | For `.zip` files: unpacks into a **new folder** named after the zip. |

**Rules that apply to every tool:**

- **Your originals are never changed or overwritten.** Results are new files next to the original, named like
  `photo (converted).png`, `photo (converted) (2).png` and so on. If that folder is read-only, they go to your
  `Downloads` folder.
- The new files **appear in the basket** with a fly-in animation.
- Long jobs show a **progress ring** on the island; when done the island says something like *Converted 3 files*.
- The work happens in the background, so nothing freezes.

**Safety of zip extraction:**

- Files inside a zip that try to escape into other folders (`..\..\Windows\...`), use hidden Windows "streams"
  (names containing `:`) or reserved device names (`CON`, `NUL`, `COM1`...) are rejected.
- **"Zip bombs"** (tiny zips that unpack to enormous sizes) are stopped: more than 100,000 files, more than 20 GB (or
  more than your free space minus 1 GB), or a compression ratio above 1000:1 aborts the extraction and deletes the
  partial folder.
- If the zip came from the internet, the extracted files **keep Windows' "downloaded from the internet" mark**, so
  Windows still warns before running them, exactly as Explorer does.
- When creating a zip, a file that cannot be read completely is left out instead of being stored broken.

---

## 15. Screenshots and copying text out of images

**Take a screenshot: Ctrl+Shift+S**

1. All your monitors dim.
2. Drag a rectangle over the area you want. A live label shows its size; handles spring in at the corners.
3. Let go (or press **Enter**) to capture. Press **F** or the *Full screen* button to capture the whole monitor under
   the mouse. **Esc** or right-click cancels.

What happens to it:

- Saved as a PNG in `Pictures\Screenshots`, named like `Dropshelf 2026-10-11 18.32.05.png`.
- **Copied to the clipboard**, ready to paste (but not added to clipboard history).
- The island shows a small thumbnail with a **Copy text** button.

**Copy text (OCR):** press *Copy text* on that pop-up, or right-click any picture in the basket → *Extract Text*.
Windows' built-in text recognition reads the picture and copies the text, line by line, to the clipboard. It needs a
Windows language pack for the language in the picture (Settings → Time & language → Language); without one you get a
message explaining that.

Note: Ctrl+Shift+S is "Save As" in some apps. While Dropshelf has it, those apps cannot use it. If you need it, turn the
shortcut off in Settings → Screenshot (the tray menu still offers *Take screenshot*).

---

## 16. The terminal

The open island is a **real terminal**: PowerShell (default) or Command Prompt, chosen in Settings → *Terminal* → *Shell*.
It runs in a Windows *pseudo console*, the same mechanism Windows Terminal uses, so the program believes it has a real
console window: colours, prompts, arrow keys, full-screen tools and interactive programs all work. Type `claude` and
Claude Code runs right there in the island (if it is installed on your PC, the same as in any other terminal).

- **No extras:** no title, no buttons, no hint text, no input box; just the shell, in a monospaced font, light grey on the
  transparent island (programs that use colours keep them).
- **Click into it to type.** Every key goes to the shell the way a terminal sends it: Enter, Backspace, Tab, **Esc**
  (claude uses it to interrupt), arrows, Home / End, Page Up / Down, F-keys, **Ctrl+C**, Ctrl+D, Alt+letters.
- **Copy and paste:** drag over text to select it (it is copied at once); Ctrl+C with a selection copies it; **Ctrl+V**,
  Shift+Insert or a right-click pastes (as one block for programs that ask for "bracketed paste").
- **Scroll back:** the mouse wheel or Shift+Page Up / Down goes through the last 2000 lines; typing jumps back down.
- It **starts the first time the island opens**, in your user folder, and **keeps running while the island is closed**:
  a long command, or claude working on something, carries on and is still there when you open it again.
- If the shell ends (`exit`), press **Enter** to start a new one. Changing the shell in Settings starts the new one.
- The island stays open while the terminal has the keyboard; click anywhere else to close it.
- Needs Windows 10 version 1809 or newer.

---

## 17. Notes, and removed tabs

The **notes** tab sits next to the terminal and looks like it: no boxes or lines, just light grey monospaced text on
the transparent island.

- On the left: `+ new note` and the title of every note (its first line), newest first. The open note is bright, the
  others dim; click one to open it.
- On the right: when the note was last edited, `delete` (a note with text asks for a second click within a couple of
  seconds), and the note itself. Just type; there is nothing to save.
- **Ctrl+N** starts a new note, **Esc** lets go of the keyboard so the island can close.
- Notes are saved half a second after you stop typing, encrypted for your Windows account, in `notes.json`. Notes
  written in earlier versions come back as they were.

Earlier versions also had a **Shelf** for files dragged onto the notch, a **Clock** (alarms, stopwatch, timer and
Pomodoro), **Today** (week calendar and weather widget) and a **Now Playing** tab. They have been removed. What they
saved is left untouched in the data folder (`shelf.json`, `alarms.json`, `timer.json`, `stopwatch.json`), so nothing is
lost; Dropshelf simply no longer reads those files. The song that is playing still shows on the closed island, and the **basket**
([section 7](#7-the-basket)) still parks files for you.

---

## 18. Calendar and weather

There is no Today tab any more; the calendar is used for **meeting alerts** and the weather for the optional
**temperature on the closed island**.

### Weather

- Current temperature with a drawn icon, and an hourly forecast for the next five hours.
- **Off by default**, because it needs to send your rough location to the weather service. Press **Turn on** in the
  tab or enable it in Settings.
- Location: from Windows' location service if you allow it, otherwise the **city** you type in Settings.
- Data from [Open-Meteo](https://open-meteo.com), a free weather service that needs no account or key.
- Updates every 30 minutes and after the PC wakes up. Shows the last known weather marked *offline* if there is no
  internet.
- Celsius or Fahrenheit (chosen automatically from your region, changeable).
- Optional: show the current temperature and icon on the **closed island** when nothing else is happening.

### Calendar

Events can come from two places, used together:

1. **Calendar links (ICS).** Google Calendar, Outlook.com, iCloud and most others can give you a private address
   ("secret iCal address" / "ICS link") for your calendar. Paste one or more into Settings → Calendar & Weather and
   press **Test** to see how many events it found. Rules:
   - Only secure `https://` (or `webcal://`) links are accepted, because the link itself is the password to your
     calendar. Plain `http://` is refused.
   - The links are stored **encrypted**.
   - Updated every 15 minutes. Repeating events (daily, weekly on certain days, monthly, yearly, with end dates or a
     number of repeats, and skipped dates), all-day events and time zones are understood.
   - A broken or unusual event is skipped instead of breaking the whole calendar; one bad link does not affect the
     others.
2. **Your Windows calendars** (MSIX version only). Read-only. Windows asks for calendar permission once; if you said
   no, Settings has a button to the right Windows privacy page.

### Meeting alerts

When an event starts **within 10 minutes**, the closed island shows *Meeting in 5 min · Title* with a countdown, and a
pop-up when it starts. Setting: *Meeting alerts* (on by default).

---

## 19. Looks: the Apple design, colours, animations

Everything is designed to look like Apple's own dark appearance and like Droppy, the Mac notch app this project is
modelled on:

- **The island:** fully transparent by default, with light grey text and Dynamic Island spring motion; with *Transparent
  island* off, the MacBook notch shape, black at the top and dark glass at the bottom.
- **Droppy layouts:** the two-state basket, the clipboard card strip and the icon menus follow Droppy's designs.
- **Colours:** Apple's system colours (dark mode): blue `#0A84FF`, green `#30D158`, orange `#FF9F0A`, red `#FF453A`...
  Text uses Apple's label and secondary-label greys.
- **Font:** SF Pro when installed, otherwise Segoe UI Variable.
- **Controls:** iOS/macOS-style switches, thin sliders with a white knob, segmented controls, accent-tinted plain
  buttons, rounded filled text fields with an accent focus ring, and macOS-style menus (accent highlight, white text).
- **Settings** looks like macOS System Settings: red/yellow/green window buttons, a centred title, and grouped rounded
  sections.
- **Dialogs** are macOS-style alerts with the app icon, a bold title and two equal buttons.
- The glass on the island and basket is drawn by Dropshelf (translucent dark with a light rim), because Windows' blur
  cannot be used on windows with free-form shapes; it does not blur what is behind it.
- Icons use Windows' Segoe Fluent Icons (Apple's SF Symbols may not be used outside Apple platforms).

### Accent colour

Used for highlights, selections, switches, buttons and menus. Changes apply instantly everywhere.

Choices, in macOS order: **Blue** (default), Purple, Pink, Red, Orange, Yellow, Green, Teal, Indigo, Graphite, or
**Windows accent** (follows the colour you picked in Windows' own personalisation settings). Light accents such as
yellow automatically get dark text on buttons.

### Animations

Every movement uses spring physics, so things settle naturally. Short transitions run at your monitor's full refresh
rate; long-running ones are capped at 30 frames a second, and nothing is drawn while nothing moves (see
[section 6](#6-how-it-stays-fast)).

- **Island:** opens with a soft spring that animates width, height and corner roundness together while the contents zoom
  in; closes a bit faster with no bounce. Live activities grow it with a small spring.
- **Tabs:** the highlight circle glides between the icon tabs; the capsule drops in under the island as it opens.
- **Basket:** its contents spring in and collapse back when it closes, and pop when switching between compact and
  expanded; dropped items fly in from where you dropped them.
- **Clipboard strip:** slides up from the bottom edge and fades in; the first cards appear one after another.
- **Switches:** the knob slides with a small bounce.

Three levels in Settings → Appearance → Animations:

| Level | What you get |
|---|---|
| **Full** (default) | Everything above. |
| **Reduced** | Short springs without bounce and simple fades, like Apple's *Reduce Motion*. |
| **Off** | No animation at all; everything jumps straight to its final state. |

If you never pick a level and you have turned off Windows' *Animation effects* (Settings → Accessibility → Visual
effects), Dropshelf uses Reduced automatically.

---

## 20. Hiding during games, videos and presentations

With *Hide in fullscreen apps* on (the default), Dropshelf checks once a second whether a fullscreen game, video or
presentation covers the island's monitor. If so, the island slides up out of sight, stops all its animations and pop-ups
stay quiet (optionally except the volume pop-up, setting *Show volume in fullscreen*). When you leave fullscreen, it
slides back.

---

## 21. Always running: startup, crashes, updates

Dropshelf is meant to be installed once and then forgotten.

- **Starts at every sign-in.** *Launch at startup* is on by default. (Technically: a `Run` entry in your user registry
  pointing to the installed exe. In the MSIX version: a Windows "startup task", visible in Task Manager → Startup apps.)
  The island appears a moment after you log in, after every restart and every shutdown.
- **Comes back after Windows Update restarts** (it registers with Windows' "restart apps after signing in").
- **Restarts itself after a crash.** If something goes seriously wrong it saves what it can and starts a fresh copy.
  To avoid an endless crash loop it does this **at most 3 times in 30 minutes**; after that it shows a message and
  stays closed. The counter resets after it has run fine for 10 minutes.
- **Only one copy ever runs.** Starting it a second time does nothing.
- **Updating:** download a newer exe and double-click it. It asks the running copy to close, waits for it (up to ~10
  seconds), safely replaces the installed file and starts again. Your clipboard history, notes and settings are kept.
  An **older** exe will not overwrite a newer installed one. (The MSIX version is updated by installing a newer bundle.)
- **Quitting on purpose** (tray → Quit) is respected: Windows will not bring it back until your next sign-in.
- If an update cannot replace the file (for example it is locked), it tells you and keeps running from where it was
  started.

Settings → General shows where it is installed and whether it starts automatically (or why not, for example
"turned off in Task Manager").

---

## 22. The tray icon

The drop icon near the clock (click `^` if it is hidden). **Left-click** it to open the island; **right-click** it for
the menu (styled like a macOS dark menu):

| Menu item | What it does |
|---|---|
| Open Dropshelf | Opens the island. |
| Open basket | Opens the basket at the mouse. |
| Clipboard history | Opens the history window. |
| Take screenshot | Starts a screenshot. |
| Terminal | Opens the island with the keyboard in the terminal. |
| Settings… | Opens the settings window. |
| Uninstall… | Removes Dropshelf (see [section 27](#27-uninstalling)). In the MSIX version this opens Windows' Apps settings instead. |
| Quit | Closes Dropshelf until the next sign-in. |

---

## 23. Every setting

Open with the tray icon → *Settings…* or the round settings button on the open island. Every change applies
immediately; there is no Save button. Settings are grouped like this:

| Section | Setting | Default |
|---|---|---|
| **General** | Launch at startup | On |
| | Hide in fullscreen apps | On |
| | Install location and startup status | (information) |
| | Arm PC notice with *Get Arm version* (only on Arm PCs running the x64 exe) | (information) |
| **Island** | Transparent island | On |
| | Open on hover | On |
| | Scroll over the island to change volume | On |
| | Shake to open basket | On |
| **Live Activities** | Now Playing | On |
| | Split island | On |
| | Downloads | On |
| | Camera and microphone dot | On |
| | Volume | On |
| | Hide the Windows popups | On |
| | Show volume in fullscreen | Off |
| | Brightness | On |
| | Battery | On |
| | Bluetooth devices | On |
| | Silent Mode / Caps Lock / Do Not Disturb | On / On / On |
| **Notifications** (MSIX) | Show notifications on the island | Off |
| | Hide notification content | Off |
| | Muted apps | none |
| | Do not disturb in fullscreen | On |
| **Appearance** | Accent colour | Blue |
| | Animations: Full / Reduced / Off | Full |
| **Clipboard** | Clipboard history | On |
| | History size (10–500) | 50 |
| **Screenshot** | Ctrl+Shift+S shortcut | On |
| **Terminal** | Shell: PowerShell / Command Prompt | PowerShell |
| **Calendar & Weather** | Calendar | On |
| | Meeting alerts | On |
| | Windows calendars (MSIX) | On |
| | Calendar links (ICS) + Test button | none |
| | Weather | Off |
| | Use my device location / City | location |
| | Units °C / °F | from your region |
| | Show temperature on the island | Off |
| **Keyboard Shortcuts** | List of all shortcuts | (information) |

---

## 24. Every keyboard shortcut

**Anywhere in Windows:**

| Keys | Action |
|---|---|
| `Ctrl+Shift+B` | Open the basket at the mouse |
| `Ctrl+Shift+V` | Open clipboard history |
| `Ctrl+Shift+S` | Take a screenshot |
| `Ctrl+Shift+N` | Open the terminal, ready to type |

**Inside the screenshot overlay:** `Enter` or release = capture, `F` = full screen, `Esc` / right-click = cancel.

**Inside clipboard history:** `↑` `↓` move, `Enter` paste, `Ctrl+P` pin, `Delete` remove, `Esc` close.

**Inside the terminal:** every key goes to the shell (Esc and Ctrl+C included); `Ctrl+V` / `Shift+Insert` / right-click
paste, select to copy, `Shift+Page Up / Down` or the wheel scroll back.

**Mouse:** hover the top-centre of the screen to open, wheel over the closed island for volume, shake while dragging
for the basket.

If another app already uses one of the global shortcuts, Dropshelf shows a message instead of stealing it.

---

## 25. Privacy and security

- **No account, no ads, no analytics, no tracking.** Dropshelf does not send anything about you anywhere.
- It only goes on the internet for:
  - **weather** (only when you turn it on; sends your rough coordinates or city to open-meteo.com),
  - **your own calendar links** (only the links you paste),
  - **downloading the Arm version** (only when you click *Get Arm version*).
- **Encrypted with your Windows account (DPAPI):** clipboard history, calendar links, cached weather/location.
- **Never stored:** notification text, passwords copied by password managers.
- Basket items are only *links* to your files; Dropshelf never uploads or copies them anywhere.
- Web links dropped into the basket are only accepted as `http`/`https`, so a malicious link cannot launch a program.
- Files dragged out of the basket are copied, never moved, so originals cannot get lost.
- Zip extraction is protected against path tricks and zip bombs and keeps Windows' internet-download warning.
- The uninstaller only deletes Dropshelf's own folders and double-checks the path before deleting anything.

---

## 26. Where your data is stored

Everything is in **`%LOCALAPPDATA%\Dropshelf`** (paste that into the Explorer address bar to open it):

| File / folder | Contains |
|---|---|
| `settings.json` | All settings (including the muted-notification app list) |
| `clipboard.json` | Clipboard history (encrypted) |
| `notes.json` | Your notes (encrypted) |
| `Clipboard\` | Images from the clipboard history (PNG) |
| `Items\` | Text and links you dropped into the basket (`.txt`, `.url`) |
| `calendar.json` | Calendar links (encrypted) |
| `weather.json` | Last forecast and location (encrypted) |
| `restarts.json` | The crash-loop counter |
| `dropshelf.log` | A log of errors, useful for reporting problems |

The program itself is in `%LOCALAPPDATA%\Programs\Dropshelf` (portable version) or managed by Windows in
`C:\Program Files\WindowsApps` (MSIX version). Screenshots go to `Pictures\Screenshots`.

Files from removed features (`shelf.json`, `timer.json`, `stopwatch.json`, `alarms.json`) may still be
there from earlier versions; Dropshelf leaves them alone.

If you report a problem, `dropshelf.log` is the file to send.

---

## 27. Uninstalling

**Portable version:** tray icon → **Uninstall…**. A macOS-style alert asks for confirmation and offers a switch
*Also delete my data* (off by default). It removes the startup entry and the Start Menu shortcut, closes, waits until it
has really exited, then deletes the installed program. Your data folder is only deleted if you turned the switch on.

**MSIX version:** Windows **Settings → Apps → Installed apps → Dropshelf → Uninstall**. The tray item opens that page
for you.

---

## 28. Intel/AMD vs Arm PCs

PCs run on one of two processor families:

- **x64** (Intel and AMD): nearly every desktop and most laptops.
- **Arm64**: newer, battery-efficient laptops with Qualcomm Snapdragon chips, such as the Surface Pro X / Pro 11 and
  "Copilot+ PCs". (Arm is a British company that designs processors; Apple's M1–M4 Macs are also Arm, which is why the
  Droppy file is called `arm64.dmg`.)

Dropshelf comes in both, built from exactly the same code:

- `Dropshelf-x64.exe` for Intel/AMD.
- `Dropshelf-arm64.exe` for Arm.
- The MSIX bundle contains both and Windows chooses.

The x64 version also runs on Arm PCs (Windows translates it on the fly with a feature called Prism), but slower and
using more battery. If Dropshelf notices it is the x64 version running on an Arm PC, it shows a one-time notice on the
island and a permanent line in Settings → General with a **Get Arm version** button. Clicking it downloads the Arm exe
(progress on the island) and swaps it in. Nothing is downloaded unless you click, and *Don't show again* hides the
notice.

To check which one is running: Task Manager → **Details** tab → right-click a column header → *Select columns* →
tick **Architecture**. `Dropshelf.exe` shows `ARM64` or `x64`.

---

## 29. Known limits

- **Not yet tested on a real PC.** It compiles and builds cleanly, but expect some rough edges. Please send
  `dropshelf.log` with any problem.
- The island appears on the **main monitor only**.
- **SF Pro** is Apple's font and is not installed on Windows by default; without it the text uses Segoe UI Variable, so
  it looks very close to, but not exactly like, a Mac. Icons are Windows' Segoe Fluent Icons, not SF Symbols.
- **Hiding Windows' volume pop-up** relies on a workaround that a Windows update could break (then both show).
- **Notifications and Windows calendar** need the MSIX version, and its certificate must be trusted once. Unless the
  project owner sets up a permanent certificate (see the README), each new MSIX release has a new certificate that must
  be trusted again.
- Windows' new Outlook does not always share its events with Windows' calendar store; use an ICS link if your agenda
  stays empty.
- **Shake detection** cannot tell a file drag from any other drag, so shaking while selecting text also opens the
  basket.
- **Ctrl+Shift+S** blocks "Save As" in apps that use it, while enabled.
- Things dragged from apps that do not offer real files or text (for example some email attachments) are ignored.
- The **portable exe is large** (~70 MB) because it contains its own copy of .NET, so nothing else needs installing.
- The island is always on top; in windowed (not fullscreen) games it stays visible.
- No WebP saving (Windows has no WebP encoder).
- Only 64-bit Windows is supported.

---

## 30. For developers: how it is built

- **Language and framework:** C# 12 with WPF on .NET 8, targeting `net8.0-windows10.0.19041.0` so it can use modern
  Windows (WinRT) features: media controls, OCR, geolocation, Bluetooth device watching, calendar and notifications.
- **No third-party libraries** except Microsoft's own `System.Management` (for laptop brightness events). Low-level
  Windows features (audio, hooks, hotkeys, clipboard listening, window tricks) are called directly through P/Invoke and
  hand-written COM interfaces.
- **Code layout**, organised by feature:

```
src/Dropshelf/
  Island/         the island window (notch shape, spring layout, hover, tabs)
  Animation/      spring physics, frame loop (vsync or capped rate), animation levels, tile animations
  Activities/     the live-activity system on the island (IPillActivity, ActivityHost, segmented tabs, cross-fades)
    Terminal/     the terminal: pseudo console (ConPTY), VT screen emulator, the drawn terminal view
    Media/        now playing (Windows media sessions, artwork tint, waveform)
    Hud/          volume + brightness pop-ups, hiding the Windows pop-up, toasts, progress ring
    Power/        battery pop-ups
    Devices/      Bluetooth pop-ups
    Downloads/    browser download progress and the drag-out "Downloaded" pop-up
    Calendar/     ICS parser, calendar service, meeting alerts
    Notifications/ notification listener and island view
    Weather/      Open-Meteo client, weather codes and icons
  Items/          basket items: model, store, drag and drop, icon loading, the tile list
  Basket/         the floating basket
  Tools/          image convert/resize/compress, zip/extract
  Screenshot/     capture overlay (one per monitor), saving, OCR
  Clipboard/      clipboard history service and window
  Services/       hotkeys, mouse hook, shake detection, settings, installer, autostart, crash guard, tray, audio, fullscreen
  Interop/        Windows API declarations, window helpers
  Settings/       settings window, alerts
  Themes/         the Apple theme (colours, fonts, controls), accent colours
packaging/        MSIX manifest, tile images, build and sign scripts
tools/            icon generators, installer scripts
```

- **Automatic builds (GitHub Actions):** every push builds `Dropshelf-x64.exe`, `Dropshelf-arm64.exe` and a signed
  `Dropshelf.msixbundle` on Windows build machines. Pushes to `main`, `v*` tags and manual runs also publish a GitHub
  release with all files, the one-click installers and a "which file do I need" table. Each build stamps the version
  `1.0.<build number>`.
- **Build it yourself** (needs the .NET 8 SDK; `dotnet build` also works on Linux and macOS for a compile check,
  thanks to `EnableWindowsTargeting`):

```powershell
dotnet build Dropshelf.sln -c Release
dotnet publish src/Dropshelf/Dropshelf.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
# use -r win-arm64 for the Arm build
```

- **Command-line switches:** `--no-install` (run from where it is, never self-install), `--autostart` (added by the
  startup entry), `--restarted` (added after a crash or Windows restart).

See [README.md](README.md) for the technical reference.
