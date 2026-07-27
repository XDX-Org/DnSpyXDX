using System.ComponentModel;
using DnSpyXDX.Application;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Tools;

[McpServerToolType]
public sealed class AssemblyTools
{
    private readonly IDecompilerBackend backend;
    private readonly WorkspaceState workspace;
    private readonly McpServerSettings settings;
    private readonly McpActivityLog activity;
    private readonly SemaphoreSlim openConcurrency;

    public AssemblyTools(IDecompilerBackend backend, WorkspaceState workspace, McpServerSettings settings, McpActivityLog activity)
    {
        this.backend = backend;
        this.workspace = workspace;
        this.settings = settings;
        this.activity = activity;
        openConcurrency = new(Math.Max(1, settings.MaximumConcurrentRequests));
    }

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
            if (exception is OperationCanceledException) throw;
            throw McpErrors.Assembly(exception);
        }
    }

    [McpServerTool(Name = "open_assembly", ReadOnly = true, UseStructuredContent = true)]
    [Description("Opens a managed DLL or EXE from an explicitly allowed local root using metadata-only inspection.")]
    public async Task<McpAssemblyDescriptor> OpenAssemblyAsync(
        [Description("Absolute path below an allowed root.")] string path,
        McpServer server,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var safeTarget = Path.GetFileName(path);
        activity.Begin();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.RequestTimeout);
            var clientRoots = await GetClientRootsAsync(server, timeout.Token);
            await openConcurrency.WaitAsync(timeout.Token);
            McpAssemblyDescriptor result;
            try
            {
                if (backend.Assemblies.Count >= settings.MaximumOpenAssemblies)
                    throw new InvalidOperationException("The open assembly limit has been reached.");
                var roots = settings.AllowedRoots.ToArray();
                var canonicalPath = ValidatePath(path, roots);
                if (clientRoots is not null) ValidatePath(canonicalPath, clientRoots);
                var length = new FileInfo(canonicalPath).Length;
                if (length > settings.MaximumAssemblyBytes)
                    throw new InvalidOperationException("The assembly exceeds the configured file size limit.");
                var opened = await backend.OpenAsync(canonicalPath, timeout.Token);
                try
                {
                    ValidatePath(canonicalPath, roots);
                    result = ToDescriptor(opened);
                }
                catch
                {
                    await backend.CloseAsync(opened.SessionId);
                    throw;
                }
            }
            finally { openConcurrency.Release(); }
            workspace.SetBusy(false, $"Opened {result.Name} through MCP");
            activity.Add(new(started, "open_assembly", safeTarget, "succeeded", DateTimeOffset.UtcNow - started));
            return result;
        }
        catch (Exception exception)
        {
            var state = exception is OperationCanceledException ? "cancelled" : "failed";
            activity.Add(new(started, "open_assembly", safeTarget, state, DateTimeOffset.UtcNow - started, SafeError(exception)));
            if (exception is OperationCanceledException) throw;
            throw McpErrors.Assembly(exception);
        }
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "Request cancelled.",
        UnauthorizedAccessException => "Access denied.",
        ArgumentException => "Invalid path.",
        InvalidOperationException => exception.Message,
        _ => "Assembly inspection failed."
    };

    private static string ValidatePath(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.", nameof(path));
        var canonicalPath = ResolveExistingPath(path, file: true);
        var allowed = roots.Any(root => IsWithin(canonicalPath, ResolveExistingPath(root, file: false)));
        if (!allowed) throw new UnauthorizedAccessException("The path is outside the configured MCP roots.");
        return canonicalPath;
    }

    private static async Task<IReadOnlyList<string>?> GetClientRootsAsync(McpServer server, CancellationToken cancellationToken)
    {
        if (server.ClientCapabilities?.Roots is null) return null;
        var response = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken);
        return response.Roots
            .Select(root => TryGetFileRoot(root.Uri))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToArray();
    }

    private static string? TryGetFileRoot(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && Path.IsPathFullyQualified(uri.LocalPath)
            ? uri.LocalPath
            : null;

    private static string ResolveExistingPath(string path, bool file)
    {
        var fullPath = Path.GetFullPath(path);
        if (file && !File.Exists(fullPath)) throw new FileNotFoundException("The assembly file was not found.", fullPath);
        if (!file && !Directory.Exists(fullPath)) throw new DirectoryNotFoundException("An allowed MCP root was not found.");

        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileSystemInfo entry = index == parts.Length - 1 && file ? new FileInfo(current) : new DirectoryInfo(current);
            if (entry.LinkTarget is not null)
                current = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("A symbolic link could not be resolved.");
        }
        return Path.GetFullPath(current);
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
