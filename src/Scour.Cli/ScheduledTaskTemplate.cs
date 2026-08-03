using System.Xml;
using Scour.Core;

namespace Scour.Cli;

public static class ScheduledTaskTemplate
{
    public static void Write(
        string outputPath,
        string rootPath,
        ScanPreset preset,
        string reportDirectory,
        string? cliPath = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var fullRootPath = Path.GetFullPath(rootPath);
        var fullReportDirectory = Path.GetFullPath(reportDirectory);
        var executable = Path.GetFullPath(cliPath ?? Environment.ProcessPath ?? "scour.exe");
        var reportPath = Path.Combine(fullReportDirectory, "scour-weekly.json");

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false };
        using var writer = XmlWriter.Create(fullOutputPath, settings);
        writer.WriteStartElement("Task", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        writer.WriteAttributeString("version", "1.4");

        WriteElement(writer, "RegistrationInfo", () =>
        {
            WriteElement(writer, "Description", "Scour weekly disk findings report");
        });
        WriteElement(writer, "Triggers", () =>
        {
            writer.WriteStartElement("CalendarTrigger");
            WriteElement(writer, "StartBoundary", NextSundayAtThreeAm());
            WriteElement(writer, "Enabled", "true");
            WriteElement(writer, "ScheduleByWeek", () =>
            {
                WriteElement(writer, "WeeksInterval", "1");
                WriteElement(writer, "DaysOfWeek", () => writer.WriteStartElement("Sunday"));
            });
            writer.WriteEndElement();
        });
        WriteElement(writer, "Principals", () =>
        {
            writer.WriteStartElement("Principal");
            writer.WriteAttributeString("id", "Author");
            WriteElement(writer, "LogonType", "InteractiveToken");
            WriteElement(writer, "RunLevel", "LeastPrivilege");
            writer.WriteEndElement();
        });
        WriteElement(writer, "Settings", () =>
        {
            WriteElement(writer, "MultipleInstancesPolicy", "IgnoreNew");
            WriteElement(writer, "DisallowStartIfOnBatteries", "false");
            WriteElement(writer, "StopIfGoingOnBatteries", "false");
            WriteElement(writer, "AllowHardTerminate", "true");
            WriteElement(writer, "StartWhenAvailable", "true");
            WriteElement(writer, "ExecutionTimeLimit", "PT2H");
            WriteElement(writer, "Enabled", "true");
        });
        writer.WriteStartElement("Actions");
        writer.WriteAttributeString("Context", "Author");
        writer.WriteStartElement("Exec");
        WriteElement(writer, "Command", executable);
        WriteElement(writer, "Arguments", $"--path \"{fullRootPath}\" --preset {preset} --json --export-csv \"{reportPath}\"");
        WriteElement(writer, "WorkingDirectory", Path.GetDirectoryName(executable)!);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string NextSundayAtThreeAm()
    {
        var date = DateTime.Today.AddDays(((int)DayOfWeek.Sunday - (int)DateTime.Today.DayOfWeek + 7) % 7);
        if (date <= DateTime.Today)
            date = date.AddDays(7);
        return date.AddHours(3).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteElement(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteElement(XmlWriter writer, string name, Action content)
    {
        writer.WriteStartElement(name);
        content();
        writer.WriteEndElement();
    }
}
