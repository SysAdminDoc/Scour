using System.Reflection;

namespace Scour.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliParser.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(CliHelp.Text);
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.3.0");
            return 0;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ScheduledTaskPath))
            {
                ScheduledTaskTemplate.Write(
                    options.ScheduledTaskPath,
                    options.RootPath,
                    options.Preset,
                    options.ReportDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "reports"));
                Console.WriteLine($"Wrote scheduled task template: {Path.GetFullPath(options.ScheduledTaskPath)}");
                return 0;
            }

            var progressWriter = options.Json ? null : Console.Error;
            var result = await CliRunner.ExecuteAsync(options, progressWriter: progressWriter);
            if (options.Json)
                Console.WriteLine(CliRunner.ToJson(result));
            else
                CliRunner.WriteHumanSummary(result, Console.Out);
            return result.ExitCode;
        }
        catch (CliArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Scan cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }
}

internal static class CliHelp
{
    public const string Text = """
        Scour CLI - read-only disk findings with optional quarantine

        Usage:
          scour.exe [--path PATH] [--preset QUICK|DEEP|FORENSIC]
                    [--scanner NAME[,NAME...]] [--json]
                    [--export-csv PATH] [--quarantine-to PATH] [--dry-run]
          scour.exe --scheduled-task PATH [--path PATH] [--report-dir PATH]

        Options:
          --path PATH             Root directory (default: current directory)
          --preset NAME           Scanner bundle (default: Forensic)
          --scanner NAME          Select one or more scanner names; overrides preset
          --json                  Emit one machine-readable JSON report on stdout
          --export-csv PATH       Write all findings to a CSV report
          --quarantine-to PATH    Move selected findings outside the scan tree
          --dry-run               Report quarantine moves without changing files
          --scheduled-task PATH   Write a weekly Task Scheduler XML template
          --report-dir PATH       Report directory used by --scheduled-task
          --help                  Show this help
          --version               Show the CLI version

        Exit codes:
          0  No selected findings
          1  Findings were returned
          2  Argument, scan, export, or quarantine error
        """;
}
