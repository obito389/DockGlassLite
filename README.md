# DockGlass Lite

DockGlass Lite is a small Windows desktop utility for:

- A lightweight bottom Dock that accepts dragged `.lnk` shortcuts and folders in edit mode.
- Pausing Dock hover/click behavior when another foreground window covers the Dock.

This project intentionally excludes wallpapers, widgets, theme stores, accounts, cloud sync, video playback, and WebView.

## Current Status

This is the first source prototype for Phase 0/Phase 1.

Implemented source modules:

- WPF app shell.
- Settings window.
- Dock floating window.
- Edit-mode `.lnk` and folder drag-and-drop handling.
- Shortcut launch.
- JSON config storage.
- Simple text-based Dock icons for the first runnable build.
- Foreground-window overlap detection for inactive Dock state.
- HKCU startup registration.

## Requirements

Install these on Windows:

- .NET 10 SDK or newer.
- Visual Studio 2022 with:
  - .NET desktop development workload.
  - Windows 10/11 SDK.

## Build

From this folder:

```powershell
cd .\src\DockGlassLite
dotnet restore
dotnet build -c Debug -p:Platform=x64
dotnet run -c Debug -p:Platform=x64
```

If `dotnet` is not in PATH yet, use:

```powershell
"C:\Program Files\dotnet\dotnet.exe" build -c Debug -p:Platform=x64
```

## First Manual Test

1. Start Wallpaper Engine if you use it.
2. Run DockGlass Lite.
3. Enter edit mode, then drag a `.lnk` shortcut or folder into the Dock.
4. Click the Dock item to launch the shortcut or open the folder.
5. Put a normal window over the Dock and confirm hover/click behavior pauses.
6. Move the window away and confirm Dock interaction resumes.

## Notes

This runnable prototype uses WPF instead of WinUI 3 to avoid the large Windows App SDK NuGet dependency chain during early testing. The service boundaries remain small, so the UI can be migrated later if needed.
