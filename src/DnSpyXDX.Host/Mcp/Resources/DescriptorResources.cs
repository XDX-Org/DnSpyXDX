using System.ComponentModel;
using System.Text.Json;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Resources;

[McpServerResourceType]
public sealed class DescriptorResources(IDecompilerBackend backend, McpActivityLog activity)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerResource(Name = "assembly_summary", UriTemplate = "dnspyxdx://assembly/{moduleMvid}", MimeType = "application/json")]
    [Description("Identity, platform, references, and browse root for an open assembly.")]
    public async Task<string> ReadAssemblyAsync(Guid moduleMvid, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin("resources/read", moduleMvid.ToString("D"), countRequest: false);
        try
        {
            var assembly = backend.Assemblies.FirstOrDefault(candidate => candidate.ModuleMvid == moduleMvid)
                ?? throw new KeyNotFoundException("The assembly is not open.");
            var rootChildren = await backend.GetChildrenAsync(assembly.RootNode, cancellationToken);
            var referencesGroup = rootChildren.FirstOrDefault(node => node.Kind == TreeNodeKind.Group && node.Name == "References");
            var references = referencesGroup is null
                ? []
                : (await backend.GetChildrenAsync(referencesGroup.Id, cancellationToken)).Select(node => node.Name).ToArray();
            var result = new AssemblyResourceDescriptor(
                assembly.ModuleMvid, assembly.Name, assembly.TargetFramework, assembly.Architecture,
                McpNodeIds.Encode(assembly.ModuleMvid, assembly.RootNode.Value), references);
            activity.Add(new(started, "resources/read", moduleMvid.ToString("D"), "succeeded", DateTimeOffset.UtcNow - started));
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception exception)
        {
            CompleteFailure(started, moduleMvid.ToString("D"), exception);
            if (exception is OperationCanceledException) throw;
            throw McpErrors.Resource(exception);
        }
    }

    [McpServerResource(Name = "symbol_descriptor", UriTemplate = "dnspyxdx://assembly/{moduleMvid}/symbol/{metadataToken}", MimeType = "application/json")]
    [Description("Identity and resource links for an exact symbol in an open assembly.")]
    public async Task<string> ReadSymbolAsync(Guid moduleMvid, int metadataToken, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var target = $"{moduleMvid:D}/0x{metadataToken:X8}";
        activity.Begin("resources/read", target, countRequest: false);
        try
        {
            if (metadataToken <= 0) throw McpErrors.InvalidToken();
            if (backend.Assemblies.All(assembly => assembly.ModuleMvid != moduleMvid))
                throw new KeyNotFoundException("The assembly is not open.");
            var symbol = await backend.DescribeSymbolAsync(new(moduleMvid, metadataToken), cancellationToken)
                ?? throw new KeyNotFoundException("The symbol was not found.");
            var uri = $"dnspyxdx://assembly/{moduleMvid:D}/symbol/{metadataToken}";
            var result = new SymbolResourceDescriptor(
                moduleMvid, metadataToken, symbol.Name, symbol.Kind.ToString(), symbol.AssemblyName, symbol.Namespace,
                symbol.QualifiedName, symbol.DeclaringType.ModuleMvid, symbol.DeclaringType.MetadataToken,
                $"{uri}/source/csharp", $"{uri}/source/il", $"{uri}/source/il-csharp");
            activity.Add(new(started, "resources/read", target, "succeeded", DateTimeOffset.UtcNow - started));
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception exception)
        {
            CompleteFailure(started, target, exception);
            if (exception is OperationCanceledException or ModelContextProtocol.McpException) throw;
            throw McpErrors.Symbol(exception);
        }
    }

    private void CompleteFailure(DateTimeOffset started, string target, Exception exception) =>
        activity.Add(new(started, "resources/read", target, exception is OperationCanceledException ? "cancelled" : "failed",
            DateTimeOffset.UtcNow - started, exception is OperationCanceledException ? "Request cancelled." : "Descriptor read failed."));
}

public sealed record AssemblyResourceDescriptor(Guid ModuleMvid, string Name, string TargetFramework, string Architecture, string RootNodeId, IReadOnlyList<string> References);
public sealed record SymbolResourceDescriptor(Guid ModuleMvid, int MetadataToken, string Name, string Kind, string AssemblyName, string Namespace, string? QualifiedName, Guid DeclaringTypeModuleMvid, int DeclaringTypeMetadataToken, string CSharpResourceUri, string IlResourceUri, string IlWithCSharpResourceUri);
