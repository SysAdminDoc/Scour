# Scour

![Version](https://img.shields.io/badge/version-0.3.0-CBA6F7?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows-blue?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)

A high-performance disk cleanup utility for Windows. Scour uses Win32 native filesystem APIs and NTFS MFT direct reading to scan millions of files fast, then helps you identify and remove wasted space across 19 different scanner types.

Built with C# .NET 9 + WPF. Catppuccin Mocha dark theme. Zero dependencies to install.

![Scour Screenshot](https://img.shields.io/badge/screenshot-coming_soon-333?style=flat-square)

## Features

### 19 Built-in Scanners
| Scanner | Description |
|---------|-------------|
| **Empty Folders** | Bottom-up detection of truly empty directories (handles nested empties) |
| **Duplicate Files** | 3-phase detection: size grouping -> partial SHA256 (4KB) -> configurable SHA256 or BLAKE3 full hash |
| **Media Duplicates** | Near-duplicate photos and videos using dimensions plus bounded dHash/perceptual buckets |
| **WinSxS Analysis** | Audit stale servicing workspaces and DISM reclaimable data without deleting component-store files |
| **Browser Cache** | Find disposable Chrome, Edge, Brave, and Firefox cache data with profile-level breakdowns |
| **System Space** | Surface hibernation, pagefile, swapfile, and crash-dump storage with protected system actions |
| **Game Orphans** | Compare Steam, Epic, and GOG install records with on-disk game directories |
| **VHDX Bloat** | Inspect Docker Desktop and WSL virtual disks before an explicit compact action |
| **Recycle Bin** | Inspect deleted items by volume, original path, deletion time, and user SID |
| **Big Files** | Top 100 largest files via min-heap (O(n log N)) plus proportional folder sizes in the Treemap tab |
| **Temp Files** | Pattern-based detection (.tmp, .bak, .log, Office lock files, macOS metadata, etc.) |
| **Zero-Length Files** | Find 0-byte empty files cluttering your filesystem |
| **Old Files** | Files not modified in 365+ days |
| **Broken Links** | Broken symbolic links and junctions |
| **Broken Shortcuts** | Windows .lnk shortcuts pointing to deleted targets (COM IShellLink) |
| **Long Paths** | Files/folders with paths exceeding 260 chars (MAX_PATH) |
| **Locked Files** | Files that can't be opened (locked by processes, permission denied) |
| **Duplicate Archives** | .zip/.7z/.rar files sitting next to their already-extracted contents |
| **Orphaned App Data** | Leftover AppData/ProgramData/Program Files from uninstalled programs |

### Performance
- **Win32 P/Invoke** (`FindFirstFileW`/`FindNextFileW`) bypasses .NET System.IO overhead
- **NTFS MFT reader** - direct Master File Table enumeration via USN journal for instant volume-wide indexing (admin required)
- **Persistent MFT cache** - `%LOCALAPPDATA%\Scour\mft-cache.bin` stores journal checkpoints and applies USN deltas atomically between full rebuilds
- **Parallel directory walking** at shallow depths with `Parallel.ForEachAsync`
- **Partial hash optimization** - only full-hashes files that collide on 4KB prefix hash
- **Optional BLAKE3 backend** - portable managed implementation with no package dependency; SHA256 remains the default compatibility option
- **DataGrid virtualization** for smooth scrolling through large result sets
- **Folder-size treemap** - Big Files builds a proportional folder tree and renders it in an alternate tab with bounded layout depth
- **Long path support** via `\\?\` prefix (handles paths > 260 chars)

### UI/UX
- **Catppuccin Mocha** dark theme with custom borderless window chrome
- **Theme toggle** - persisted Catppuccin Mocha, Latte, and OLED palettes
- **Keyboard shortcuts**: F5 (scan), Escape (cancel), Ctrl+A/D/I (select all/none/invert), Ctrl+E (export), Delete
- **Filter bar** - real-time search results by name, path, or detail
- **Right-click context menu** - open file location, copy path, remove from list
- **Drag-and-drop** - drop a folder onto the window to set scan path
- **Export** to CSV or JSON
- **Sortable columns** with proper numeric/date sorting
- **Duplicate group coloring** - subtle tinted row backgrounds per duplicate group
- **Scan All** - run all 19 scanners in parallel with one click
- **Scan presets** - choose Quick, Deep, or Forensic bundles before using Scan All; the default Forensic preset preserves the full scan behavior
- **Selected size summary** - status bar shows count and total size of selected items
- **Scan duration** and error count in status bar
- **Per-scanner telemetry** - the progress pane reports files/s and MB/s while scanning
- **Result pinning** - pin important findings from the context menu and keep them marked across rescans
- **Finding explanations** - select a result to see the scanner rule, reason, safety state, suggested action, and path
- **Settings persistence** - all options, window position, and excluded directories saved to `%LOCALAPPDATA%\Scour\settings.json`
- **Windows Explorer context menu** - right-click any folder and select "Scan with Scour"

### Delete Modes
- **Recycle Bin** (default) - uses `SHFileOperation` with `FOF_ALLOWUNDO`
- **Permanent** - bypasses recycle bin
- **Simulate** - dry run, no files touched

### CLI Automation

The self-contained `scour.exe` publish supports JSON/CSV reporting and safe unattended quarantine:

```powershell
scour.exe --path C:\Data --preset Deep --json --export-csv C:\Reports\scour.csv
scour.exe --path C:\Data --scanner "Temp Files" --dry-run --quarantine-to D:\Scour-Quarantine
scour.exe --scheduled-task C:\Reports\ScourWeekly.xml --path C:\Data --report-dir C:\Reports
```

Exit codes are `0` for no selected findings, `1` when findings are returned, and `2` for argument, scan, export, or quarantine errors. `--quarantine-to` moves selected findings outside the scan tree; add `--dry-run` to preview those moves. `--scheduled-task` writes a weekly Task Scheduler XML template that drops a JSON report in the chosen report directory.

## Download

Grab the latest release from the [Releases](https://github.com/SysAdminDoc/Scour/releases) page.

**Self-contained** - no .NET runtime installation required. Single `Scour.App.exe` file (~130 MB).

## Build from Source

```bash
# Clone
git clone https://github.com/SysAdminDoc/Scour.git
cd Scour

# Build
dotnet build -c Release

# Run
dotnet run --project src/Scour.App

# Publish self-contained single-file exe
dotnet publish src/Scour.App/Scour.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --source https://api.nuget.org/v3/index.json -o ./publish

# Publish the CLI
dotnet publish src/Scour.Cli/Scour.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/cli
```

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.

## Project Structure

```
Scour/
  src/
    Scour.Core/           # Core library - models, interfaces, services
      Interfaces/         # IScannerModule contract
      Models/             # ScanConfig, ScanResultItem, FolderSizeNode, ScanPreset, DeleteMode
      Native/             # Win32 P/Invoke, NTFS MFT reader
      Services/           # FileSystemWalker, FileHasher, Blake3Hasher, TreemapLayout, DeletionService, AppSettings, ContextMenuService
    Scour.Scanners/       # Scanner implementations (19 modules)
      ScannerBase.cs      # Shared base class (deletion, reset)
      EmptyDirectoryScanner.cs
      DuplicateFileScanner.cs
      BigFileScanner.cs
      TempFileScanner.cs
      ZeroLengthFileScanner.cs
      OldFileScanner.cs
      BrokenSymlinkScanner.cs
      BrokenShortcutScanner.cs
      LongPathScanner.cs
      LockedFileScanner.cs
      DuplicateArchiveScanner.cs
      OrphanedAppDataScanner.cs
    Scour.App/            # WPF desktop application
      Views/              # MainWindow XAML + code-behind
      ViewModels/         # MainViewModel, ScannerViewModel, ViewModelBase
      Converters/         # Value converters (bool, enum, group color)
      Theme/              # Catppuccin Mocha XAML ResourceDictionary
    Scour.Cli/            # Dependency-free JSON/CSV automation executable
```

## Architecture

- **MVVM** with `ViewModelBase`, `RelayCommand`, `AsyncRelayCommand`
- **Plugin pattern** via `IScannerModule` interface - add scanners by implementing one interface
- **`ScannerBase`** abstract class handles shared deletion logic
- **`Channel<T>`** for streaming file entries from background walker to consumers
- **`IProgress<ScanProgress>`** for real-time UI updates during scans
- **`CancellationToken`** throughout for responsive cancellation
- **`ListCollectionView`** for real-time filtering without re-scanning
- **NTFS MFT** direct reading via `FSCTL_ENUM_USN_DATA` for volume-wide file indexing
- **COM IShellLink** interop for parsing Windows shortcut (.lnk) targets
- **Registry-based** orphaned app detection cross-referencing installed programs

## Excluded Directories (Default)

System Volume Information, RECYCLER, $RECYCLE.BIN, winsxs, System32, GAC_MSIL, GAC_32, node_modules, .git, .svn, .hg

Configurable via the UI sidebar.

## License

MIT
