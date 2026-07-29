using System.Reflection.PortableExecutable;

namespace DnSpyXDX.Application;

public sealed record DebugLaunchTargetInspection(
    bool IsLaunchable,
    string? Error = null,
    string? SuggestedPath = null);

public static class DebugLaunchTargetInspector
{
    public static DebugLaunchTargetInspection Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(false, "Select a managed executable or entry-point DLL.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return new(false, $"Debug target path is invalid: {exception.Message}");
        }

        if (!File.Exists(fullPath))
            return new(false, $"Debug target was not found: {fullPath}");

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(true);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return new(false, "CoreCLR launch requires an .exe or .dll target.");

        if (HasManagedEntryPoint(fullPath, out var inspectionError))
            return new(true);

        var suggestion = FindSiblingApplication(fullPath);
        var name = Path.GetFileName(fullPath);
        var message = inspectionError ??
            $"{name} is a class library and cannot be launched directly.";
        if (suggestion is not null)
            message += $" Select the application entry point instead: {Path.GetFileName(suggestion)}.";
        else
            message += " Select the application's .exe or entry-point .dll.";
        return new(false, message, suggestion);
    }

    public static string? FindSiblingApplication(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
        if (directory is null || !Directory.Exists(directory)) return null;

        try
        {
            var candidates = Directory
                .EnumerateFiles(directory, "*.runtimeconfig.json")
                .Select(runtimeConfig =>
                    runtimeConfig[..^".runtimeconfig.json".Length])
                .Select(stem =>
                    File.Exists(stem + ".exe")
                        ? stem + ".exe"
                        : File.Exists(stem + ".dll") &&
                          HasManagedEntryPoint(stem + ".dll", out _)
                            ? stem + ".dll"
                            : null)
                .Where(candidate => candidate is not null)
                .Cast<string>()
                .Distinct(PathComparer())
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasManagedEntryPoint(
        string path,
        out string? error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null)
            {
                error = $"{Path.GetFileName(path)} is not a managed .NET assembly.";
                return false;
            }
            if (reader.PEHeaders.CorHeader
                    .EntryPointTokenOrRelativeVirtualAddress == 0)
            {
                error = null;
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or IOException or
                UnauthorizedAccessException)
        {
            error = $"Could not inspect {Path.GetFileName(path)}: {exception.Message}";
            return false;
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
