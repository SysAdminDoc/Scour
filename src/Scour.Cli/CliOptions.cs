using Scour.Core;

namespace Scour.Cli;

public sealed record CliOptions(
    string RootPath,
    ScanPreset Preset,
    IReadOnlyList<string> ScannerNames,
    bool Json,
    bool DryRun,
    bool SinceLastRun,
    string? ExportCsvPath,
    string? QuarantinePath,
    string? ScheduledTaskPath,
    string? ReportDirectory,
    bool ShowHelp,
    bool ShowVersion,
    bool ShowTui);

public sealed class CliArgumentException(string message) : Exception(message);
