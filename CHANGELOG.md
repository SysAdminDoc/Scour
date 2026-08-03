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

## [v0.3.0] - %Y->- (HEAD -> main, origin/main, origin/HEAD)

- Added: Add CI/CD build workflow
- ci: add build and release workflow
- Added: Add real-time result streaming and folder grouping
- Fixed: Fix 6 bugs found during code audit
- Added: Add 5 new scanners, MFT engine, and context menu integration (v0.3.0)
- Fixed: Fix icon resource not found at runtime
- Added: Add app icon and assembly metadata
- Initial release v0.2.0 - Multi-function file cleanup tool
