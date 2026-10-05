# MacShell — a macOS desktop for Windows

MacShell replaces the Windows desktop with a macOS Sonoma/Sequoia-style environment. It hides the
taskbar and the Explorer desktop and puts a menu bar, Dock, Finder, Launchpad, Spotlight, Mission Control,
Control Center and System Settings in their place. Your normal Windows apps keep running *inside* it:
they appear in the Dock with running dots, drive the menu bar, show up in Mission Control and the
⌘Tab switcher, and maximise into the space between the menu bar and the Dock.

## Run it

```
publish\MacShell.exe
```

* **Start at every login:** System Settings → General → *Open MacShell at login*.
* **Quit / return to Windows:**  → *Return to Windows…*, or press **Ctrl + Alt + Shift + Q**.
* **Emergency restore** (if MacShell was killed and the taskbar is still hidden): run
  `scripts\restore-windows.cmd` (or `MacShell.exe --restore`). Signing out and back in also restores everything.

Requires Windows 10/11 and the .NET 8 Desktop Runtime.

## Keyboard

On a PC keyboard, **Alt sits where ⌘ is** on a Mac, and the Windows key is used for system shortcuts.
Inside MacShell's own apps (Finder, System Settings) the menus show ⌘, which means **Ctrl**.

| Action | Shortcut |
|---|---|
| Launchpad | Dock icon (tapping **Win** does nothing by default — change it in System Settings → MacShell) |
| Spotlight | **Win + Space** or **Alt + Space** |
| App Switcher (⌘Tab) | **Alt + Tab** or **Win + Tab** (hold, tap Tab; Q quits, H hides) |
| Mission Control | **Win + ↑** |
| Show Desktop | **Win + D** |
| New Finder window | **Win + E** |
| System Settings | **Win + ,** |
| Screenshot (full / selection) | **Win + Shift + 3** / **Win + Shift + 4** (saved to the Desktop) |
| Force Quit Applications | **Ctrl + Alt + Esc** |
| Return to Windows | **Ctrl + Alt + Shift + Q** |

Finder: Return renames, Space is Quick Look, Ctrl+O / Ctrl+↓ opens, Ctrl+↑ goes to the enclosing folder,
Delete or Ctrl+Backspace moves to Trash, Ctrl+I is Get Info, Ctrl+1–4 switches views, Ctrl+T opens a tab,
Ctrl+Shift+N makes a new folder, Ctrl+Shift+G is Go to Folder, Ctrl+Shift+. shows hidden files.

## What's in it

* **Menu bar**: Apple menu (About This Mac, System Settings, Recent Items, Force Quit, Sleep/Restart/Shut Down,
  Lock Screen, Log Out), the bold active-app name, per-app menus, and status items (battery, Wi-Fi, Spotlight,
  Control Center, clock). Classic Win32 apps that have a real menu bar (Notepad++, 7-Zip, regedit …) get
  it mirrored into the global menu bar. The Window menu adds Fill, Center and Move & Resize (halves and quarters)
  for any app.
* **Dock**: fisheye magnification, running indicators, red notification badges (from apps' own badge counts and
  "(3) App" style window titles), launch bounce, name labels, right-click menus (window list, Keep in Dock,
  Show in Finder, Hide, Quit; hold Alt for Force Quit), drag to reorder, hold an icon above the Dock until
  "Remove" appears to remove it, drag apps / PWAs / shortcuts in from Launchpad, Finder or the desktop to pin them,
  drop files on an app to open them, on Trash to delete, a Downloads stack, and auto-hide.
* **Finder**: sidebar (Favorites, iCloud/OneDrive, Locations, Tags), Icon / List / Column / Gallery views,
  tabs, back/forward, path bar, status bar with free space, "This Mac" (Windows Search index) or folder search,
  Quick Look, Get Info, rename in place, marquee selection, drag and drop, copy/cut/paste (Explorer-compatible),
  Duplicate, Compress/Expand, Make Alias, colour tags, Trash with Put Back and Empty, Recents, Applications.
* **Desktop**: wallpaper and free-form icons — drag them anywhere (no snapping, positions remembered), drag them
  onto folders, apps or the Dock; *Clean Up* / *Clean Up By* and *Use Stacks* in the desktop's right-click menu.
* **Windows** never slide under the menu bar (like macOS), and maximised windows fit between the menu bar and Dock.
* **Sound** menu-bar item: volume, output device switching, and Windows' own Volume Mixer / Sound Settings.
* **Launchpad** (paged grid, search, Other folder) • **Spotlight** (apps, files, calculator, settings, web) •
  **Mission Control** (live window thumbnails) • **App Switcher** • **Control Center** (Wi-Fi, Bluetooth,
  Focus, Dark Mode, Windows taskbar toggle, brightness, volume, media keys) • **Notification Center** widgets •
  **About This Mac** • **Force Quit** • **System Settings** (Appearance with light/dark/auto and accent colours,
  Wallpaper, Desktop & Dock, Control Center, Spotlight, Sound, General, Keyboard, Finder, MacShell).
* Fullscreen apps (games, videos) automatically hide the menu bar and Dock.

## Honest limitations

* **Other apps' title bars.** Windows apps keep their own title bars and buttons. MacShell can't restyle another
  process's window frame reliably.
* **Fonts.** Apple's SF Pro isn't bundled because its licence doesn't allow it. MacShell uses SF Pro automatically
  if it's installed, then *Inter*, then Segoe UI Variable.
* **Wallpapers.** Apple's wallpapers are copyrighted, so MacShell renders its own Sonoma/Sequoia-style ones.
  *Add Photo…* in Wallpaper settings takes any image.
* **System tray icons** live in the hidden Windows taskbar. Use Control Center → *Taskbar* to show it
  temporarily.
* While MacShell runs, Windows' "automatically hide the taskbar" option is switched on (so the hidden taskbar stops
  reserving space); your original setting is restored when MacShell quits, and by `scripts\restore-windows.cmd`.
* The menu bar and Dock appear on the primary display. Other displays get the wallpaper.

## Command line

```
MacShell.exe                  start (or open a Finder window if already running)
MacShell.exe --windowed       run without taking over (keeps the taskbar; for trying it out)
MacShell.exe --quit           quit the running instance and restore Windows
MacShell.exe --restore        emergency restore of the taskbar/desktop
MacShell.exe --open <what>    finder[:path] | launchpad | spotlight[:query] | missioncontrol | settings[:pane]
                              | controlcenter | notifications | about | forcequit | screenshot | area
```

Settings are stored in `%APPDATA%\MacShell\settings.json`, and errors are logged to `%APPDATA%\MacShell\macshell.log`.

## Build

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
