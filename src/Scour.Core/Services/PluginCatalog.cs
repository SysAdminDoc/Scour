using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scour.Core.Interfaces;

namespace Scour.Core.Services;

public sealed class PluginManifest
{
    public int ManifestVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    [JsonPropertyName("assembly")]
    public string AssemblyFile { get; init; } = "";

    [JsonPropertyName("type")]
    public string? TypeName { get; init; }
}

public sealed record PluginDiscoveryResult(
    IReadOnlyList<IScannerModule> Modules,
    IReadOnlyList<string> Errors);

public static class PluginCatalog
{
    public const int CurrentManifestVersion = 1;
    public const string ManifestFileName = "scour-plugin.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string GetDefaultDirectory()
        => Path.Combine(AppRuntime.DataDirectory, "Plugins");

    public static PluginDiscoveryResult Discover(string? pluginDirectory = null)
    {
        var modules = new List<IScannerModule>();
        var errors = new List<string>();
        var directory = pluginDirectory ?? GetDefaultDirectory();

        string[] manifests;
        try
        {
            if (!Directory.Exists(directory))
                return new(modules, errors);

            manifests = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(IsManifestFile)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            return new(modules, [$"Plugin directory '{directory}': {ex.Message}"]);
        }

        foreach (var manifestPath in manifests)
            LoadManifest(manifestPath, directory, modules, errors);

        return new(modules, errors);
    }

    private static void LoadManifest(
        string manifestPath,
        string pluginDirectory,
        List<IScannerModule> modules,
        List<string> errors)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("manifest is empty");
            ValidateManifest(manifest, pluginDirectory);

            var assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifest.AssemblyFile));
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var candidateTypes = GetCandidateTypes(assembly, manifest.TypeName);
            var loaded = 0;

            foreach (var type in candidateTypes)
            {
                if (!typeof(IScannerModule).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                if (Activator.CreateInstance(type) is not IScannerModule module)
                    continue;

                modules.Add(module);
                loaded++;
            }

            if (loaded == 0)
                throw new InvalidDataException("assembly did not expose a constructible IScannerModule");
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(manifestPath)}: {ex.Message}");
        }
    }

    private static IEnumerable<Type> GetCandidateTypes(Assembly assembly, string? typeName)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            return type == null ? [] : [type];
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    private static void ValidateManifest(PluginManifest manifest, string pluginDirectory)
    {
        if (manifest.ManifestVersion != CurrentManifestVersion)
            throw new InvalidDataException($"unsupported manifestVersion {manifest.ManifestVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("id and name are required");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("version is required");
        if (string.IsNullOrWhiteSpace(manifest.AssemblyFile) ||
            Path.IsPathRooted(manifest.AssemblyFile) ||
            !manifest.AssemblyFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("assembly must be a relative .dll path");
        }

        var directory = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("assembly path must remain inside the plugin directory");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("assembly was not found", assemblyPath);
    }

    private static bool IsManifestFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".plugin.json", StringComparison.OrdinalIgnoreCase);
    }
}
