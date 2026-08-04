using Scour.Core;

namespace Scour.Cli;

public static class TuiRunner
{
    public static async Task<int> RunAsync(
        CliOptions initial,
        TextReader input,
        TextWriter output,
        TextWriter? progressWriter = null,
        CancellationToken ct = default)
    {
        var rootPath = initial.RootPath;
        var preset = initial.Preset;
        var scannerNames = initial.ScannerNames.ToList();
        CliExecutionResult? lastResult = null;

        await WriteLineAsync(output, "Scour TUI - type help for commands, scan to run, quit to exit", ct);
        await WriteLineAsync(output, $"Path: {rootPath}", ct);
        await WriteLineAsync(output, $"Preset: {preset}", ct);

        while (true)
        {
            await output.WriteAsync($"scour [{preset}]> ");
            await output.FlushAsync(ct);
            var line = await input.ReadLineAsync(ct);
            if (line == null)
                return lastResult?.ExitCode ?? 0;

            var commandLine = line.Trim();
            if (commandLine.Length == 0)
                continue;

            var (command, argument) = SplitCommand(commandLine);
            switch (command.ToLowerInvariant())
            {
                case "help":
                    await WriteHelpAsync(output, ct);
                    break;
                case "path":
                    if (string.IsNullOrWhiteSpace(argument))
                    {
                        await WriteLineAsync(output, $"Path: {rootPath}", ct);
                    }
                    else
                    {
                        var candidate = Unquote(argument);
                        if (Directory.Exists(candidate))
                        {
                            rootPath = candidate;
                            await WriteLineAsync(output, $"Path set to: {rootPath}", ct);
                        }
                        else
                        {
                            await WriteLineAsync(output, $"Path does not exist: {candidate}", ct);
                        }
                    }
                    break;
                case "preset":
                    if (Enum.TryParse<ScanPreset>(argument, true, out var selectedPreset))
                    {
                        preset = selectedPreset;
                        await WriteLineAsync(output, $"Preset set to: {preset}", ct);
                    }
                    else
                    {
                        await WriteLineAsync(output, "Preset must be Quick, Deep, or Forensic.", ct);
                    }
                    break;
                case "scanner":
                    var updatedScanners = await SetScannersAsync(argument, scannerNames, output, ct);
                    if (updatedScanners != null)
                        scannerNames = updatedScanners.ToList();
                    break;
                case "scan":
                    lastResult = await ScanAsync(initial, rootPath, preset, scannerNames, progressWriter, ct);
                    CliRunner.WriteHumanSummary(lastResult, output);
                    WriteRows(lastResult, output);
                    break;
                case "json":
                    if (lastResult == null)
                        await WriteLineAsync(output, "Run scan before requesting JSON.", ct);
                    else
                        await WriteLineAsync(output, CliRunner.ToJson(lastResult), ct);
                    break;
                case "clear":
                    lastResult = null;
                    await WriteLineAsync(output, "Last result cleared.", ct);
                    break;
                case "quit":
                case "exit":
                    return lastResult?.ExitCode ?? 0;
                default:
                    await WriteLineAsync(output, "Unknown command. Type help for commands.", ct);
                    break;
            }
        }
    }

    private static async Task<CliExecutionResult> ScanAsync(
        CliOptions initial,
        string rootPath,
        ScanPreset preset,
        IReadOnlyList<string> scannerNames,
        TextWriter? progressWriter,
        CancellationToken ct)
    {
        var options = initial with
        {
            RootPath = rootPath,
            Preset = preset,
            ScannerNames = scannerNames.ToArray(),
            Json = false,
            ExportCsvPath = null,
            QuarantinePath = null,
            ScheduledTaskPath = null,
            ReportDirectory = null,
            ShowHelp = false,
            ShowVersion = false,
            ShowTui = false,
        };
        return await CliRunner.ExecuteAsync(options, ct, progressWriter);
    }

    private static async Task<IReadOnlyList<string>?> SetScannersAsync(
        string argument,
        IReadOnlyList<string> current,
        TextWriter output,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await WriteLineAsync(output, current.Count == 0 ? "Scanners: preset selection" : $"Scanners: {string.Join(", ", current)}", ct);
            return null;
        }

        if (argument.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLineAsync(output, "Scanners reset to preset selection.", ct);
            return [];
        }

        var names = ParseScannerNames(argument);
        var known = CliRunner.CreateScanners().Select(scanner => scanner.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(name => !known.Contains(name)).ToArray();
        if (unknown.Length > 0)
        {
            await WriteLineAsync(output, $"Unknown scanner(s): {string.Join(", ", unknown)}", ct);
            return null;
        }

        await WriteLineAsync(output, $"Scanners set to: {string.Join(", ", names)}", ct);
        return names;
    }

    private static List<string> ParseScannerNames(string argument)
        => argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void WriteRows(CliExecutionResult result, TextWriter output)
    {
        foreach (var item in result.Items.Take(12))
            output.WriteLine($"  {(item.Selected ? "[x]" : "[ ]")} {item.Name}  {item.SizeBytes:N0} B  {item.FullPath}");

        if (result.Items.Count > 12)
            output.WriteLine($"  ... {result.Items.Count - 12:N0} more result(s); use json for the complete report.");
    }

    private static async Task WriteHelpAsync(TextWriter output, CancellationToken ct)
    {
        await WriteLineAsync(output, "Commands:", ct);
        await WriteLineAsync(output, "  scan                         Run the selected scanners", ct);
        await WriteLineAsync(output, "  path <directory>             Change the scan path", ct);
        await WriteLineAsync(output, "  preset <Quick|Deep|Forensic> Change the scanner preset", ct);
        await WriteLineAsync(output, "  scanner <name[,name...]>     Select explicit scanners; scanner all resets to preset", ct);
        await WriteLineAsync(output, "  json                         Print the complete last report", ct);
        await WriteLineAsync(output, "  clear                        Clear the last report", ct);
        await WriteLineAsync(output, "  quit                         Exit the TUI", ct);
    }

    private static (string Command, string Argument) SplitCommand(string line)
    {
        var separator = line.IndexOf(' ');
        return separator < 0
            ? (line, "")
            : (line[..separator], line[(separator + 1)..].Trim());
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static Task WriteLineAsync(TextWriter output, string value, CancellationToken ct)
        => output.WriteLineAsync(value.AsMemory(), ct);
}
