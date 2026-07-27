using ModelContextProtocol;

namespace DnSpyXDX.Host.Mcp;

internal static class McpErrors
{
    public static McpException Assembly(Exception exception) => exception switch
    {
        UnauthorizedAccessException => Create("path_not_allowed", "The path is outside the allowed roots.", exception),
        FileNotFoundException => Create("assembly_not_found", "The assembly file was not found.", exception),
        DirectoryNotFoundException => Create("root_not_found", "An allowed root was not found.", exception),
        ArgumentException => Create("invalid_path", "The assembly path is invalid.", exception),
        InvalidOperationException => Create("limit_exceeded", exception.Message, exception),
        _ => Create("assembly_inspection_failed", "Assembly inspection failed.", exception)
    };

    public static McpException Symbol(Exception exception) => exception switch
    {
        ArgumentException => Create("invalid_request", "The symbol request is invalid.", exception),
        KeyNotFoundException when exception.Message.Contains("assembly", StringComparison.OrdinalIgnoreCase) =>
            Create("assembly_not_open", "The assembly is not open.", exception),
        KeyNotFoundException => Create("symbol_not_found", "The symbol was not found.", exception),
        _ => Create("symbol_inspection_failed", "Symbol inspection failed.", exception)
    };

    public static McpException Resource(Exception exception) => exception switch
    {
        ArgumentException => Create("invalid_language", "The source language is invalid.", exception),
        KeyNotFoundException => Create("assembly_not_open", "The assembly is not open.", exception),
        _ => Create("decompilation_failed", "Source decompilation failed.", exception)
    };

    public static McpException InvalidToken() => Create("invalid_token", "The metadata token must be positive.");
    public static McpException InvalidNode() => Create("invalid_node", "The node ID is invalid.");
    public static McpException StaleCursor() => Create("stale_cursor", "The cursor is invalid or no longer applies to this node.");

    private static McpException Create(string code, string message, Exception? inner = null) =>
        inner is null ? new($"{code}: {message}") : new($"{code}: {message}", inner);
}
