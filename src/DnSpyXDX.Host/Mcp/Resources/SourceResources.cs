using System.ComponentModel;
using DnSpyXDX.Application;
using ModelContextProtocol.Server;

namespace DnSpyXDX.Host.Mcp.Resources;

[McpServerResourceType]
public sealed class SourceResources(IDecompilerBackend backend, McpActivityLog activity)
{
    private const int MaximumCharacters = 200_000;

    [McpServerResource(
        Name = "symbol_source",
        UriTemplate = "dnspyxdx://assembly/{moduleMvid}/symbol/{metadataToken}/source/{language}",
        MimeType = "text/plain")]
    [Description("Bounded decompiled C#, IL, or mapped IL-with-C# for an exact open symbol.")]
    public async Task<string> ReadSourceAsync(Guid moduleMvid, int metadataToken, string language, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var target = $"{moduleMvid:D}/0x{metadataToken:X8}/{language}";
        activity.Begin();
        try
        {
            if (backend.Assemblies.All(assembly => assembly.ModuleMvid != moduleMvid))
                throw new KeyNotFoundException("The assembly is not open.");
            var selectedLanguage = language.ToLowerInvariant() switch
            {
                "csharp" => DecompilerLanguage.CSharp,
                "il" => DecompilerLanguage.IL,
                "il-csharp" => DecompilerLanguage.ILWithCSharp,
                _ => throw new ArgumentException("Language must be csharp, il, or il-csharp.", nameof(language))
            };
            var document = await backend.DecompileAsync(new(moduleMvid, metadataToken), selectedLanguage, cancellationToken);
            var result = document.Text.Length <= MaximumCharacters
                ? document.Text
                : document.Text[..MaximumCharacters] + $"\n\n// Truncated at {MaximumCharacters:N0} characters by the MCP response limit.";
            activity.Add(new(started, "resources/read", target, "succeeded", DateTimeOffset.UtcNow - started));
            return result;
        }
        catch (Exception exception)
        {
            var state = exception is OperationCanceledException ? "cancelled" : "failed";
            var message = exception switch
            {
                OperationCanceledException => "Request cancelled.",
                ArgumentException => "Invalid language.",
                KeyNotFoundException => exception.Message,
                _ => "Decompilation failed."
            };
            activity.Add(new(started, "resources/read", target, state, DateTimeOffset.UtcNow - started, message));
            throw;
        }
    }
}
