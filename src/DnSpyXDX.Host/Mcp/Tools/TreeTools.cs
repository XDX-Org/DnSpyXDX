using System.ComponentModel;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Tools;

[McpServerToolType]
public sealed class TreeTools(IDecompilerBackend backend, McpActivityLog activity, McpCursorCodec cursors)
{
    private const int MaximumPageSize = 100;

    [McpServerTool(Name = "list_children", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists a bounded page of child nodes below an assembly browse node.")]
    public async Task<ListChildrenResponse> ListChildrenAsync(
        [Description("Opaque node ID returned by an assembly resource or previous list_children result.")] string nodeId,
        [Description("Opaque continuation cursor from a previous page.")] string? cursor = null,
        [Description("Page size from 1 to 100.")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin("list_children", nodeId, countRequest: false);
        try
        {
            if (!McpNodeIds.TryDecode(nodeId, out var moduleMvid, out var value)) throw McpErrors.InvalidNode();
            var assembly = backend.Assemblies.FirstOrDefault(candidate => candidate.ModuleMvid == moduleMvid)
                ?? throw new KeyNotFoundException("The assembly is not open.");
            var offset = 0;
            if (cursor is not null && !cursors.TryDecode(cursor, nodeId, out offset)) throw McpErrors.StaleCursor();
            var limit = Math.Clamp(pageSize, 1, MaximumPageSize);
            var children = await backend.GetChildrenAsync(new(assembly.SessionId, value), cancellationToken);
            if (offset > children.Count) throw McpErrors.StaleCursor();
            var page = children.Skip(offset).Take(limit).Select(child => new McpTreeNode(
                McpNodeIds.Encode(moduleMvid, child.Id.Value), child.Name, child.Kind.ToString(), child.HasChildren,
                child.Symbol?.MetadataToken, child.Detail, child.Visibility, child.TypeDisplay)).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < children.Count ? cursors.Encode(nodeId, nextOffset) : null;
            activity.Add(new(started, "list_children", moduleMvid.ToString("D"), "succeeded", DateTimeOffset.UtcNow - started));
            return new(page, nextCursor);
        }
        catch (Exception exception)
        {
            activity.Add(new(started, "list_children", null, exception is OperationCanceledException ? "cancelled" : "failed",
                DateTimeOffset.UtcNow - started, exception is OperationCanceledException ? "Request cancelled." : "Tree enumeration failed."));
            if (exception is OperationCanceledException or ModelContextProtocol.McpException) throw;
            throw McpErrors.Symbol(exception);
        }
    }
}

public sealed record McpTreeNode(string NodeId, string Name, string Kind, bool HasChildren, int? MetadataToken, string? Detail, string? Visibility, string? TypeDisplay);
public sealed record ListChildrenResponse(IReadOnlyList<McpTreeNode> Nodes, string? NextCursor);
