using System.ComponentModel;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Tools;

[McpServerToolType]
public sealed class SymbolTools(IDecompilerBackend backend, McpActivityLog activity)
{
    [McpServerTool(Name = "search_symbols", ReadOnly = true, UseStructuredContent = true)]
    [Description("Searches types and members in open assemblies and returns exact MVID/token identities. Results are capped at 200.")]
    public async Task<SymbolSearchResponse> SearchSymbolsAsync(
        [Description("Type or member name query.")] string query,
        [Description("Optional module MVID filter.")] Guid? moduleMvid = null,
        [Description("Optional kind filter such as Type, Method, Field, Property, or Event.")] string? kind = null,
        [Description("Maximum results from 1 to 200.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        activity.Begin();
        try
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
            var limit = Math.Clamp(maxResults, 1, 200);
            var found = await backend.SearchAsync(query.Trim(), cancellationToken);
            var filtered = found
                .Where(result => moduleMvid is null || result.Symbol.ModuleMvid == moduleMvid)
                .Where(result => string.IsNullOrWhiteSpace(kind) || result.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Take(limit + 1)
                .ToArray();
            var results = filtered.Take(limit).Select(ToSummary).ToArray();
            activity.Add(new(started, "search_symbols", query.Trim(), "succeeded", DateTimeOffset.UtcNow - started));
            return new(results, filtered.Length > limit);
        }
        catch (Exception exception)
        {
            CompleteFailure(started, "search_symbols", exception);
            if (exception is OperationCanceledException) throw;
            throw McpErrors.Symbol(exception);
        }
    }

    [McpServerTool(Name = "get_symbol", ReadOnly = true, UseStructuredContent = true)]
    [Description("Gets one open symbol by its exact module MVID and metadata token.")]
    public async Task<McpSymbolDescriptor> GetSymbolAsync(Guid moduleMvid, int metadataToken, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var target = $"{moduleMvid:D}/0x{metadataToken:X8}";
        activity.Begin();
        try
        {
            if (metadataToken <= 0) throw McpErrors.InvalidToken();
            EnsureOpen(moduleMvid);
            var result = await backend.DescribeSymbolAsync(new(moduleMvid, metadataToken), cancellationToken)
                ?? throw new KeyNotFoundException("The symbol was not found.");
            activity.Add(new(started, "get_symbol", target, "succeeded", DateTimeOffset.UtcNow - started));
            return ToDescriptor(result);
        }
        catch (Exception exception)
        {
            CompleteFailure(started, "get_symbol", exception, target);
            if (exception is OperationCanceledException or ModelContextProtocol.McpException) throw;
            throw McpErrors.Symbol(exception);
        }
    }

    [McpServerTool(Name = "get_references", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns bounded semantic outgoing references for an exact method or member identity.")]
    public async Task<ReferenceResponse> GetReferencesAsync(Guid moduleMvid, int metadataToken, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var target = $"{moduleMvid:D}/0x{metadataToken:X8}";
        activity.Begin();
        try
        {
            if (metadataToken <= 0) throw McpErrors.InvalidToken();
            EnsureOpen(moduleMvid);
            var limit = Math.Clamp(maxResults, 1, 200);
            var found = await backend.AnalyzeAsync(new(moduleMvid, metadataToken), AnalyzerRelation.Uses, cancellationToken);
            var page = found.Take(limit + 1).ToArray();
            var results = page.Take(limit).Select(ToSummary).ToArray();
            activity.Add(new(started, "get_references", target, "succeeded", DateTimeOffset.UtcNow - started));
            return new(results, page.Length > limit);
        }
        catch (Exception exception)
        {
            CompleteFailure(started, "get_references", exception, target);
            if (exception is OperationCanceledException or ModelContextProtocol.McpException) throw;
            throw McpErrors.Symbol(exception);
        }
    }

    private void EnsureOpen(Guid moduleMvid)
    {
        if (backend.Assemblies.All(assembly => assembly.ModuleMvid != moduleMvid))
            throw new KeyNotFoundException("The assembly is not open.");
    }

    private void CompleteFailure(DateTimeOffset started, string operation, Exception exception, string? target = null)
    {
        var state = exception is OperationCanceledException ? "cancelled" : "failed";
        var message = exception switch
        {
            OperationCanceledException => "Request cancelled.",
            ArgumentException => "Invalid request.",
            KeyNotFoundException => exception.Message,
            _ => "Symbol inspection failed."
        };
        activity.Add(new(started, operation, target, state, DateTimeOffset.UtcNow - started, message));
    }

    private static McpSymbolSummary ToSummary(SearchResult result) => new(
        result.Symbol.ModuleMvid, result.Symbol.MetadataToken, result.Name, result.Kind, result.AssemblyName,
        result.Namespace, result.QualifiedName, SymbolUri(result.Symbol));

    private static McpSymbolSummary ToSummary(AnalyzerResult result) => new(
        result.Symbol.ModuleMvid, result.Symbol.MetadataToken, result.Name, result.Kind.ToString(), result.AssemblyName,
        result.Namespace, result.QualifiedName, SymbolUri(result.Symbol));

    private static McpSymbolDescriptor ToDescriptor(AnalyzerResult result) => new(
        result.Symbol.ModuleMvid, result.Symbol.MetadataToken, result.Name, result.Kind.ToString(), result.AssemblyName,
        result.Namespace, result.QualifiedName, result.DeclaringType.ModuleMvid, result.DeclaringType.MetadataToken,
        SymbolUri(result.Symbol), $"{SymbolUri(result.Symbol)}/source/csharp", $"{SymbolUri(result.Symbol)}/source/il");

    private static string SymbolUri(SymbolId symbol) => $"dnspyxdx://assembly/{symbol.ModuleMvid:D}/symbol/{symbol.MetadataToken}";
}

public sealed record McpSymbolSummary(Guid ModuleMvid, int MetadataToken, string Name, string Kind, string AssemblyName, string Namespace, string? QualifiedName, string ResourceUri);
public sealed record SymbolSearchResponse(IReadOnlyList<McpSymbolSummary> Results, bool Truncated);
public sealed record ReferenceResponse(IReadOnlyList<McpSymbolSummary> Results, bool Truncated);
public sealed record McpSymbolDescriptor(Guid ModuleMvid, int MetadataToken, string Name, string Kind, string AssemblyName, string Namespace, string? QualifiedName, Guid DeclaringTypeModuleMvid, int DeclaringTypeMetadataToken, string ResourceUri, string CSharpResourceUri, string IlResourceUri);
