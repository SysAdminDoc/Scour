using Scour.Core;

namespace Scour.Cli;

public static class CliParser
{
    public static CliOptions Parse(string[] args)
    {
        var rootPath = Directory.GetCurrentDirectory();
        var preset = ScanPreset.Forensic;
        var scannerNames = new List<string>();
        var json = false;
        var dryRun = false;
        var sinceLastRun = false;
        string? exportCsvPath = null;
        string? quarantinePath = null;
        string? scheduledTaskPath = null;
        string? reportDirectory = null;
        var showHelp = false;
        var showVersion = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--since-last-run":
                    sinceLastRun = true;
                    break;
                case "--path":
                case "-p":
                    rootPath = ReadValue(args, ref index, argument);
                    break;
                case "--preset":
                    preset = ParsePreset(ReadValue(args, ref index, argument));
                    break;
                case "--scanner":
                case "-s":
                    AddScannerNames(scannerNames, ReadValue(args, ref index, argument));
                    break;
                case "--export-csv":
                    exportCsvPath = ReadValue(args, ref index, argument);
                    break;
                case "--quarantine-to":
                    quarantinePath = ReadValue(args, ref index, argument);
                    break;
                case "--scheduled-task":
                    scheduledTaskPath = ReadValue(args, ref index, argument);
                    break;
                case "--report-dir":
                    reportDirectory = ReadValue(args, ref index, argument);
                    break;
                default:
                    if (argument.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
                        rootPath = argument[7..];
                    else if (argument.StartsWith("--preset=", StringComparison.OrdinalIgnoreCase))
                        preset = ParsePreset(argument[9..]);
                    else if (argument.StartsWith("--scanner=", StringComparison.OrdinalIgnoreCase))
                        AddScannerNames(scannerNames, argument[10..]);
                    else if (argument.StartsWith("--export-csv=", StringComparison.OrdinalIgnoreCase))
                        exportCsvPath = argument[13..];
                    else if (argument.StartsWith("--quarantine-to=", StringComparison.OrdinalIgnoreCase))
                        quarantinePath = argument[16..];
                    else if (argument.StartsWith("--scheduled-task=", StringComparison.OrdinalIgnoreCase))
                        scheduledTaskPath = argument[17..];
                    else if (argument.StartsWith("--report-dir=", StringComparison.OrdinalIgnoreCase))
                        reportDirectory = argument[13..];
                    else
                        throw new CliArgumentException($"Unknown argument: {argument}");
                    break;
            }
        }

        return new CliOptions(
            rootPath,
            preset,
            scannerNames,
            json,
            dryRun,
            sinceLastRun,
            exportCsvPath,
            quarantinePath,
            scheduledTaskPath,
            reportDirectory,
            showHelp,
            showVersion);
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new CliArgumentException($"Missing value for {argument}");
        return args[index];
    }

    private static ScanPreset ParsePreset(string value)
        => Enum.TryParse<ScanPreset>(value, ignoreCase: true, out var preset)
            ? preset
            : throw new CliArgumentException($"Unknown preset '{value}'. Use Quick, Deep, or Forensic.");

    private static void AddScannerNames(List<string> names, string value)
    {
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
    }
}
