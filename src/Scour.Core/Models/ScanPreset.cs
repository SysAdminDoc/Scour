namespace Scour.Core;

public enum ScanPreset
{
    Quick,
    Deep,
    Forensic,
}

public sealed record ScanPresetDefinition(
    ScanPreset Preset,
    string Description,
    IReadOnlySet<string> ScannerNames);

public static class ScanPresetCatalog
{
    private static readonly IReadOnlySet<string> QuickScanners =
        CreateSet(
            "Empty Folders",
            "Big Files",
            "Temp Files",
            "Zero-Length Files",
            "Old Files");

    private static readonly IReadOnlySet<string> DeepScanners =
        CreateSet(
            "Empty Folders",
            "Duplicate Files",
            "Media Duplicates",
            "Browser Cache",
            "Big Files",
            "Temp Files",
            "Zero-Length Files",
            "Old Files",
            "Broken Symlinks",
            "Broken Shortcuts",
            "Long Paths",
            "Locked Files",
            "Duplicate Archives",
            "Orphaned App Data");

    private static readonly IReadOnlySet<string> ForensicScanners =
        CreateSet(
            "Empty Folders",
            "Duplicate Files",
            "Media Duplicates",
            "WinSxS Analysis",
            "Browser Cache",
            "System Space",
            "Game Orphans",
            "VHDX Bloat",
            "Recycle Bin",
            "Big Files",
            "Temp Files",
            "Zero-Length Files",
            "Old Files",
            "Broken Symlinks",
            "Broken Shortcuts",
            "Long Paths",
            "Locked Files",
            "Duplicate Archives",
            "Orphaned App Data");

    private static readonly IReadOnlyDictionary<ScanPreset, ScanPresetDefinition> Definitions =
        new Dictionary<ScanPreset, ScanPresetDefinition>
        {
            [ScanPreset.Quick] = new(
                ScanPreset.Quick,
                "Five fast, low-noise cleanup scanners",
                QuickScanners),
            [ScanPreset.Deep] = new(
                ScanPreset.Deep,
                "Cleanup and duplicate scanners, excluding system-specific probes",
                DeepScanners),
            [ScanPreset.Forensic] = new(
                ScanPreset.Forensic,
                "Every scanner, including protected system and application audits",
                ForensicScanners),
        };

    public static IReadOnlyList<ScanPreset> Presets { get; } = Enum.GetValues<ScanPreset>();

    public static ScanPresetDefinition GetDefinition(ScanPreset preset)
        => Definitions[preset];

    public static bool Includes(ScanPreset preset, string scannerName)
        => preset == ScanPreset.Forensic || GetDefinition(preset).ScannerNames.Contains(scannerName);

    private static IReadOnlySet<string> CreateSet(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
}
