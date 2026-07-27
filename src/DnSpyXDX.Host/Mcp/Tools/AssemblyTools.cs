using System.ComponentModel;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Tools;

[McpServerToolType]
public sealed class AssemblyTools(IDecompilerBackend backend, WorkspaceState workspace, McpServerSettings settings, McpActivityLog activity)
{
    [McpServerTool(Name = "list_assemblies", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists managed assemblies currently open in DnSpyXDX without exposing local paths.")]
    public IReadOnlyList<McpAssemblyDescriptor> ListAssemblies()
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin();
        var assemblies = backend.Assemblies.Select(ToDescriptor).ToArray();
        activity.Add(new(started, "list_assemblies", null, "succeeded", DateTimeOffset.UtcNow - started));
        return assemblies;
    }

    [McpServerTool(Name = "close_assembly", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Closes an assembly by module MVID and releases its file handles and cached documents.")]
    public async Task<CloseAssemblyResult> CloseAssemblyAsync(Guid moduleMvid, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin();
        var assembly = backend.Assemblies.FirstOrDefault(candidate => candidate.ModuleMvid == moduleMvid);
        if (assembly is null)
        {
            activity.Add(new(started, "close_assembly", moduleMvid.ToString("D"), "succeeded", DateTimeOffset.UtcNow - started));
            return new(moduleMvid, false);
        }
        try
        {
            await backend.CloseAsync(assembly.SessionId);
            workspace.CloseAssembly(moduleMvid);
            workspace.SetBusy(false, $"Unloaded {assembly.Name} through MCP");
            activity.Add(new(started, "close_assembly", moduleMvid.ToString("D"), "succeeded", DateTimeOffset.UtcNow - started));
            return new(moduleMvid, true);
        }
        catch (Exception exception)
        {
            var state = exception is OperationCanceledException ? "cancelled" : "failed";
            activity.Add(new(started, "close_assembly", moduleMvid.ToString("D"), state, DateTimeOffset.UtcNow - started, SafeError(exception)));
            throw;
        }
    }

    [McpServerTool(Name = "open_assembly", ReadOnly = true, UseStructuredContent = true)]
    [Description("Opens a managed DLL or EXE from an explicitly allowed local root using metadata-only inspection.")]
    public async Task<McpAssemblyDescriptor> OpenAssemblyAsync(
        [Description("Absolute path below an allowed root.")] string path,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var safeTarget = Path.GetFileName(path);
        activity.Begin();
        try
        {
            var canonicalPath = ValidatePath(path);
            var result = ToDescriptor(await backend.OpenAsync(canonicalPath, cancellationToken));
            workspace.SetBusy(false, $"Opened {result.Name} through MCP");
            activity.Add(new(started, "open_assembly", safeTarget, "succeeded", DateTimeOffset.UtcNow - started));
            return result;
        }
        catch (Exception exception)
        {
            var state = exception is OperationCanceledException ? "cancelled" : "failed";
            activity.Add(new(started, "open_assembly", safeTarget, state, DateTimeOffset.UtcNow - started, SafeError(exception)));
            throw;
        }
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "Request cancelled.",
        UnauthorizedAccessException => "Access denied.",
        ArgumentException => "Invalid path.",
        _ => "Assembly inspection failed."
    };

    private string ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.", nameof(path));
        var canonicalPath = Path.GetFullPath(path);
        var allowed = settings.AllowedRoots.Any(root => IsWithin(canonicalPath, Path.GetFullPath(root)));
        if (!allowed) throw new UnauthorizedAccessException("The path is outside the configured MCP roots.");
        return canonicalPath;
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison) && !Path.IsPathRooted(relative);
    }

    private static McpAssemblyDescriptor ToDescriptor(AssemblyDescriptor assembly) => new(
        assembly.ModuleMvid, assembly.Name, assembly.TargetFramework, assembly.Architecture,
        $"dnspyxdx://assembly/{assembly.ModuleMvid:D}");
}

public sealed record McpAssemblyDescriptor(Guid ModuleMvid, string Name, string TargetFramework, string Architecture, string ResourceUri);
public sealed record CloseAssemblyResult(Guid ModuleMvid, bool Closed);
