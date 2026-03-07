# Scour

![Version](https://img.shields.io/badge/version-0.2.0-CBA6F7?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows-blue?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)

A high-performance disk cleanup utility for Windows. Scour uses Win32 native filesystem APIs to scan millions of files fast, then helps you identify and remove wasted space across 7 different scanner types.

Built with C# .NET 9 + WPF. Catppuccin Mocha dark theme. Zero dependencies to install.

![Scour Screenshot](https://img.shields.io/badge/screenshot-coming_soon-333?style=flat-square)

## Features

### 7 Built-in Scanners
| Scanner | Description |
|---------|-------------|
| **Empty Folders** | Bottom-up detection of truly empty directories (handles nested empties) |
| **Duplicate Files** | 3-phase detection: size grouping -> partial SHA256 (4KB) -> full hash |
| **Big Files** | Top 100 largest files via min-heap (O(n log N)) |
| **Temp Files** | Pattern-based detection (.tmp, .bak, .log, Office lock files, macOS metadata, etc.) |
| **Zero-Length Files** | Find 0-byte empty files cluttering your filesystem |
| **Old Files** | Files not modified in 365+ days |
| **Broken Links** | Broken symbolic links and junctions |

### Performance
- **Win32 P/Invoke** (`FindFirstFileW`/`FindNextFileW`) bypasses .NET System.IO overhead
- **Parallel directory walking** at shallow depths with `Parallel.ForEachAsync`
- **Partial hash optimization** - only full-hashes files that collide on 4KB prefix hash
- **DataGrid virtualization** for smooth scrolling through large result sets
- **Long path support** via `\\?\` prefix (handles paths > 260 chars)

### UI/UX
- **Catppuccin Mocha** dark theme with custom borderless window chrome
- **Keyboard shortcuts**: F5 (scan), Escape (cancel), Ctrl+A/D/I (select all/none/invert), Ctrl+E (export), Delete
- **Filter bar** - real-time search results by name, path, or detail
- **Right-click context menu** - open file location, copy path, remove from list
- **Drag-and-drop** - drop a folder onto the window to set scan path
- **Export** to CSV or JSON
- **Sortable columns** with proper numeric/date sorting
- **Duplicate group coloring** - subtle tinted row backgrounds per duplicate group
- **Scan All** - run all 7 scanners in parallel with one click
- **Selected size summary** - status bar shows count and total size of selected items
- **Scan duration** and error count in status bar
- **Settings persistence** - all options, window position, and excluded directories saved to `%LOCALAPPDATA%\Scour\settings.json`

### Delete Modes
- **Recycle Bin** (default) - uses `SHFileOperation` with `FOF_ALLOWUNDO`
- **Permanent** - bypasses recycle bin
- **Simulate** - dry run, no files touched

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
```

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.

## Project Structure

```
Scour/
  src/
    Scour.Core/           # Core library - models, interfaces, services
      Interfaces/         # IScannerModule contract
      Models/             # ScanConfig, ScanResultItem, DeleteMode
      Native/             # Win32 P/Invoke (FindFirstFileW, SHFileOperation)
      Services/           # FileSystemWalker, FileHasher, DeletionService, AppSettings
    Scour.Scanners/       # Scanner implementations
      ScannerBase.cs      # Shared base class (deletion, reset)
      EmptyDirectoryScanner.cs
      DuplicateFileScanner.cs
      BigFileScanner.cs
      TempFileScanner.cs
      ZeroLengthFileScanner.cs
      OldFileScanner.cs
      BrokenSymlinkScanner.cs
    Scour.App/            # WPF desktop application
      Views/              # MainWindow XAML + code-behind
      ViewModels/         # MainViewModel, ScannerViewModel, ViewModelBase
      Converters/         # Value converters (bool, enum, group color)
      Theme/              # Catppuccin Mocha XAML ResourceDictionary
```

## Architecture

- **MVVM** with `ViewModelBase`, `RelayCommand`, `AsyncRelayCommand`
- **Plugin pattern** via `IScannerModule` interface - add scanners by implementing one interface
- **`ScannerBase`** abstract class handles shared deletion logic
- **`Channel<T>`** for streaming file entries from background walker to consumers
- **`IProgress<ScanProgress>`** for real-time UI updates during scans
- **`CancellationToken`** throughout for responsive cancellation
- **`ListCollectionView`** for real-time filtering without re-scanning

## Excluded Directories (Default)

System Volume Information, RECYCLER, $RECYCLE.BIN, winsxs, System32, GAC_MSIL, GAC_32, node_modules, .git, .svn, .hg

Configurable via the UI sidebar.

## License

MIT
