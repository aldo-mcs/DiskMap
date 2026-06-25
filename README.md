# DiskMap

A fast, no-bloat disk usage analyzer for Windows — a modern alternative to WinDirStat and WizTree. Scan a drive, see where your space went, and clean it up, all from one utilitarian window.

![Version](https://img.shields.io/badge/version-1.0.1-blue) ![Platform](https://img.shields.io/badge/platform-Windows-0078D6) ![.NET](https://img.shields.io/badge/.NET-10-512BD4)

## Why DiskMap

- **Never requires admin.** Runs fully featured as a standard user; an optional one-click "Run Elevated" unlocks instant whole-drive scans via direct NTFS `$MFT` reading. No admin, no nag.
- **Fast.** Multi-core recursive scanning by default; on NTFS volumes when elevated, reads the volume's Master File Table directly instead of walking folders — whole-drive scans that take WinDirStat minutes finish in seconds.
- **Utilitarian, not "beautified."** Dense data tables, monospace numbers, a custom dark/light chrome — built for people who want answers, not a dashboard.
- **Local-first.** No telemetry, no account, no cloud. Settings and scan history live in `%LOCALAPPDATA%\DiskMap` as plain JSON/SQLite.

## Features

- **Two scan engines, picked automatically.** A parallel recursive walker (works everywhere, any filesystem) and a direct NTFS `$MFT` reader (NTFS + elevated only) that falls back transparently on any failure.
- **Treemap and Sunburst views** of the same scan, colored by file type, with breadcrumb drill-in and two-way selection sync with the file tree.
- **Duplicate file detection** — size grouping → prefix-hash cull → full hash confirmation, grouped for one-click cleanup.
- **Scan history** — every scan is saved locally; diff any two snapshots to see what grew or shrank.
- **Built-in cleanup** — a dedicated panel for known junk (temp files, browser caches, thumbnail caches, old error reports, Recycle Bin) with reveal/recycle actions. Nothing is ever deleted without you choosing it; deletions go to the Recycle Bin, not permanent.
- **Filters** — by file type and size range, live as you type.
- **Advanced Mode** — for people who want it: hard link counts, alternate data streams, MFT record index, reparse/junction detail, slack space, scan engine and timing diagnostics.
- **Reliability built for real drives** — long-path (`\\?\`) support past 260 characters, junction/symlink cycle detection, configurable reparse-point handling (ignore/show/follow), crash-safe scan resume via periodic checkpoints, an optional memory saver for 10M+ file drives, and a "scan impact" throttle (Fast/Balanced/Low) so a background scan doesn't fight you for the machine.
- **Light and dark themes**, switching live with no restart, defaulting to your Windows setting.
- **A real Settings window** for all of the above — scan speed, reparse behavior, memory saver, crash-safe resume, minimum duplicate size, elevation preference.

## Screenshots

| Main Window | Cleanup Tab |
|-------------|-------------|
| <img src="https://github.com/user-attachments/assets/8382c600-e880-428b-abb7-68b2ac7965dc" width="450"> | <img src="https://github.com/user-attachments/assets/51910675-c4de-42c7-85dd-f0bb58b10d31" width="450"> |

### History Tab

<p align="center">
  <img src="https://github.com/user-attachments/assets/8a2bb02c-2738-431b-9e85-d0d57fe4500b" width="700">
</p>


## Installing

Download the latest installer from the [Releases](https://github.com/aldo-mcs/DiskMap/releases) page and run it. DiskMap installs to Program Files, adds a Start Menu shortcut, and includes a clean uninstaller — no admin rights needed to install or run.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows (WPF).

```
dotnet build
dotnet run --project src/DiskMap.App
dotnet test
```

## Solution layout

```
src/
  DiskMap.Core/        # class library, no UI dependencies, unit-testable
    Scanning/             # recursive scanner, options, checkpoint/resume, long-path + cycle detection
      Mft/                  # direct NTFS $MFT reader: volume data runs, FILE record parsing, tree building
    FileTypes/            # extension -> category mapping, color hue assignment, rollup stats
    Duplicates/           # size-group -> hash-cull -> full-hash duplicate finder
    Snapshots/            # SQLite-backed scan history + snapshot diffing
    Cleanup/              # known-junk category scanners (temp/cache/recycle bin) + safe cleaner
    Settings/             # persisted user preferences
    Actions/              # reveal / open / recycle (real Recycle Bin, not permanent delete) / zip
  DiskMap.App/          # WPF UI (net10.0-windows)
    Controls/              # custom-rendered treemap and sunburst (no charting library)
    Treemap/                  # squarify + radial layout algorithms and hit-testing
    ViewModels/             # MainViewModel + row / legend / duplicate-group / cleanup view models
    Views/                  # Settings, History, About, Welcome, Confirm dialog
    Infrastructure/        # theme manager (live switching), file-type colors, formatting, converters
    Themes/                 # Light/Dark palettes + shared structural styles
  DiskMap.Tests/        # xUnit: scanner, MFT parsing, cleanup, duplicates
installer/              # Inno Setup script for the Windows installer
```

## Support

DiskMap is free and independently built by [aldo-mcs](https://github.com/aldo-mcs). If it's useful to you, a donation goes a long way:

- [GitHub Sponsors](https://github.com/sponsors/aldo-mcs)
- [Donate via Stripe](https://buy.stripe.com/fZu28sd6m7qneKHbmpejK01)

## License

Copyright © Acsoft. All rights reserved. This is proprietary software — see [LICENSE](LICENSE) for terms. Source is published here for transparency, not for reuse, modification, or redistribution.
