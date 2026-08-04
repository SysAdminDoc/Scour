# Changelog

All notable changes to Scour will be documented in this file.

## Unreleased

- Added: Media Duplicates scanner with WIC image dHashing, bounded video perceptual buckets, container dimensions, and safe duplicate selection.
- Added: Dependency-free executable scanner test harness with end-to-end media grouping coverage.
- Added: Audit-only WinSxS analysis for stale servicing workspaces and DISM-reported reclaimable component data.
- Added: Browser cache scanner with Chrome, Edge, Brave, and Firefox profile-level cache breakdowns.
- Added: Protected System Space scanner for hibernation, pagefile, swapfile, and crash-dump storage actions.
- Added: Game Orphans scanner comparing Steam, Epic, and GOG install records with game library directories.
- Added: VHDX Bloat scanner for Docker Desktop and WSL virtual disks with protected Optimize-VHD compaction.
- Added: Read-only Recycle Bin scanner with per-volume metadata, original paths, deletion times, and SID resolution.
- Added: Persistent MFT cache with atomic snapshots and USN-journal delta refresh with full-enumeration fallback.
- Added: Scan-since-last-run mode in the WPF app and CLI, using changed USN paths to narrow traversal and safely falling back to a full scan.
- Added: Portable mode detection via `portable.flag` or removable-drive execution, with local state storage and registry-free Explorer integration.
- Added: Dependency-free Windows Terminal TUI with scan, path, preset, scanner, JSON, and report commands, plus a profile fragment.
- Added: Versioned plugin manifest discovery from the user or portable Plugins directory with path containment and scanner-interface validation.
- Added: Optional dependency-free BLAKE3 full-hash backend for duplicate detection, selectable alongside the SHA256 default.
- Added: Quick, Deep, and Forensic scan presets with persisted bundle selection for Scan All.
- Added: Big Files Treemap tab with proportional folder-size layout and direct-file buckets.
- Added: Dependency-free `scour.exe` CLI with JSON/CSV reports, Quick/Deep/Forensic selection, dry-run quarantine previews, weekly Task Scheduler XML generation, and explicit exit codes.
- Added: Persisted Catppuccin Mocha, Latte, and OLED theme selector in the WPF sidebar.
- Added: Per-scanner progress telemetry with files-per-second and megabytes-per-second rates.
- Added: Persistent result pinning with context-menu toggles and row indicators across rescans.
- Added: Per-result finding explanations with scanner rules, reasons, safety guidance, and suggested actions in the WPF panel and JSON export.
- Added: Context-menu result exclusions with exact-path matching while retaining the built-in directory-name exclusions.

## [v0.3.0] - %Y->- (HEAD -> main, origin/main, origin/HEAD)

- Added: Add CI/CD build workflow
- ci: add build and release workflow
- Added: Add real-time result streaming and folder grouping
- Fixed: Fix 6 bugs found during code audit
- Added: Add 5 new scanners, MFT engine, and context menu integration (v0.3.0)
- Fixed: Fix icon resource not found at runtime
- Added: Add app icon and assembly metadata
- Initial release v0.2.0 - Multi-function file cleanup tool
