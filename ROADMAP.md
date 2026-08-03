# Scour Roadmap

Future direction for Scour, a Win32/MFT-powered disk cleanup utility. Focus: more scanner coverage, smarter dedup, and enterprise/CLI friendliness.

## Planned Features

### New scanners
- Hibernation/pagefile/crash-dump surface view with one-click disable + shrink
- Steam/Epic/GOG orphaned game prefix detection (install records vs disk folders)
- Docker/WSL vhdx bloat analyzer with compact-vhd integration
- Recycle Bin introspection (per-volume `$Recycle.Bin` enumeration with original path + user SID resolution)

### Engine & performance
- Persistent MFT cache (`%LOCALAPPDATA%\Scour\mft-cache.bin`) with USN delta refresh instead of full re-enum
- BLAKE3 as optional hash backend behind partial SHA256 (faster full-hash pass on big files)
- Scan presets (Quick / Deep / Forensic) that enable scanner bundles with one click
- Folder size treemap view (WizTree-style) as an alternate tab on Big Files

### CLI, automation, scripting
- `scour.exe` CLI with JSON output mode for PowerShell/SCCM/Intune pipelines
- `--dry-run` / `--export-csv` / `--quarantine-to` flags for unattended runs
- Exit codes aligned with SystemUpdatePro conventions (0 clean, 1 items found, >1 error)
- Scheduled task template generator for weekly automatic scans with report drop

### UI/UX
- Dark + Light + OLED theme toggle (Catppuccin Latte for light)
- Per-scanner progress pane with throughput MB/s and files/s
- Result pinning (keep flagged items across rescans)
- "Why is this flagged?" explain-panel per row for debloat-script authoring

## Competitive Research

- **WizTree** uses MFT scan + treemap; one-pane visual drilldown is the killer feature - add treemap as a dedicated view on top of the existing Big Files scanner.
- **BleachBit / CCleaner** ship shipped-cleaner lists (per-app junk locations) and community-maintained profile trees; Scour can add a YAML-driven "app junk" scanner so community PRs don't require C# changes.
- **TreeSize Free** exposes NTFS compression ratio + allocated-vs-logical size columns; valuable signal for sparse files and compressed archives.
- **PatchMyPC / Dell DCU** style enterprise runs: JSON exit reports + event-log writes are table-stakes for RMM adoption - align with SystemUpdatePro's `update_history.json` format.

## Nice-to-Haves

- Right-click "Add exclusion" straight from result rows (auto-populate exclusion list)
- "Scan since last run" mode via USN journal delta (only files changed since last scan)
- Portable mode detection (no registry writes if launched from removable drive)
- Windows Terminal `scour` profile with a built-in TUI alternative to the WPF GUI
- Signed MSIX + winget manifest for `winget install SysAdminDoc.Scour`
- Plugin manifest so third parties can ship scanners as separate DLLs dropped into `%LOCALAPPDATA%\Scour\Plugins\`

## Open-Source Research (Round 2)

### Related OSS Projects
- https://github.com/Eul45/omni-search — Tauri + Rust + C++, USN/MFT direct scanning with live USN-journal incremental updates, duplicate finder, Recycle-Bin delete flow
- https://github.com/windirstat/windirstat — GPLv2 classic treemap, extension statistics view
- https://sourceforge.net/projects/doublefile/ — Double File, project-based duplicate DB with stored checksums
- https://github.com/shundhammer/qdirstat — Qt KDirStat port, cleanup action framework (run external commands on selected paths)
- https://github.com/qarmin/czkawka — Rust multi-platform duplicate/empty/broken finder, very fast, good UX reference
- https://github.com/arsenetar/dupeguru — Python fuzzy dup (filename, audio tags, image similarity) — borrows for content-class matches
- https://github.com/topics/disk-cleanup — Topic hub

### Features to Borrow
- USN-journal live incremental index so reopening the app refreshes in milliseconds (omni-search — you already plan this, make it the default)
- Extension statistics / treemap cross-view — WinDirStat's killer feature that Scour currently lacks
- Cleanup-action framework (qdirstat) — right-click a folder, run a user-defined shell command with `%p` substitution (e.g., "run CCleaner on this branch")
- Fuzzy/perceptual image dup detection via pHash for similar-not-identical photos (dupeguru)
- Audio-tag dup detection (artist+title+length match rather than byte-match) (dupeguru)
- Project-based checksum DB so offline drives can still be de-duped against online ones (Double File)
- Czkawka-style "cache similar results" so re-running a scan with a filter tweak doesn't rehash (czkawka)

### Patterns & Architectures Worth Studying
- DeviceIoControl + FSCTL_ENUM_USN_DATA enumeration over filesystem walk (omni-search / WizTree) — 20-100x faster, need Admin + `\.\C:` access, handle unopenable-volume error by falling back to walk
- 3-phase dup detection (size -> partial 4KB hash -> full hash) is industry standard; czkawka adds a 4th phase (blake3 streaming) for cancelable long ops
- Treemap renderer as a separate widget fed by size tree (WinDirStat abstraction) — reusable across Scour's scanner types
- Per-scanner plugin ABI (interface + .dll discovery in `%LOCALAPPDATA%\Scour\Plugins\`) — czkawka's Rust trait system is a good mental model for a C# IScanner interface
