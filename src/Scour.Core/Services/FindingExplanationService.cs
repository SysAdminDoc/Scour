namespace Scour.Core.Services;

public sealed record FindingExplanation(
    string Title,
    string Rule,
    string Reason,
    string Safety,
    string SuggestedAction);

public static class FindingExplanationService
{
    public static FindingExplanation Explain(string scannerName, ScanResultItem item)
    {
        var rule = scannerName switch
        {
            "Empty Folders" => "Directory contains no non-ignored files or subdirectories.",
            "Duplicate Files" => "Same-size files matched through the partial and full content-hash phases.",
            "Media Duplicates" => "Media dimensions and perceptual fingerprint indicate a near-duplicate.",
            "WinSxS Analysis" => "Servicing workspace or component-store data was surfaced for audit.",
            "Browser Cache" => "The path belongs to a disposable browser cache or profile cache location.",
            "System Space" => "Protected operating-system storage was identified by its system role.",
            "Game Orphans" => "An install directory was not matched to the platform's recorded installations.",
            "VHDX Bloat" => "A Docker, WSL, or package virtual disk was found for review or compaction.",
            "Recycle Bin" => "The item is already deleted and is being inspected through Recycle Bin metadata.",
            "Big Files" => "The file is among the largest items discovered under the scan path.",
            "Temp Files" => "The filename, extension, or marker matches a temporary-file rule.",
            "Zero-Length Files" => "The file has a length of zero bytes.",
            "Old Files" => "The file has not been modified within the scanner's age threshold.",
            "Broken Symlinks" => "The symbolic link or junction target no longer resolves.",
            "Broken Shortcuts" => "The Windows shortcut target no longer exists.",
            "Long Paths" => "The path exceeds the traditional Windows MAX_PATH threshold.",
            "Locked Files" => "The file could not be opened with the requested sharing mode.",
            "Duplicate Archives" => "An archive appears alongside extracted content with matching names.",
            "Orphaned App Data" => "Application data remains without a matching installed application record.",
            _ => "The scanner rule identified this path for review.",
        };

        var detail = string.IsNullOrWhiteSpace(item.Detail) ? "No additional scanner detail." : item.Detail;
        var safety = item.IsSelected
            ? "Selected by default; review the path before taking a destructive action."
            : "Not selected by default because this finding may require manual review or a protected action.";
        var action = item.IsDirectory
            ? "Inspect the directory contents and confirm it is safe to remove or quarantine."
            : "Review the path and scanner detail, then use the selected delete mode or export it for policy review.";

        return new FindingExplanation(
            $"{scannerName}: {item.Name}",
            rule,
            $"{rule} Detail: {detail}",
            safety,
            action);
    }
}
