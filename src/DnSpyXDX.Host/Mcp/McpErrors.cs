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

    public static McpException Debugger(Exception exception) => exception switch
    {
        UnauthorizedAccessException when exception.Message.StartsWith("debug_session_owned:", StringComparison.Ordinal) =>
            Create("debug_session_owned", "The debugger session belongs to another MCP connection.", exception),
        UnauthorizedAccessException => Create("debug_target_not_allowed", "The debug target or session is not authorized.", exception),
        FileNotFoundException => Create("debug_target_not_found", "The debug target was not found.", exception),
        ArgumentException => Create("invalid_debug_request", exception.Message, exception),
        KeyNotFoundException => Create("debug_session_not_found", exception.Message, exception),
        TimeoutException => Create("debug_timeout", exception.Message, exception),
        InvalidOperationException when exception.Message.StartsWith("stale_reference:", StringComparison.Ordinal) =>
            Create("stale_reference", "The paused-state reference is stale.", exception),
        InvalidOperationException when exception.Message.StartsWith("debug_capability_unsupported:", StringComparison.Ordinal) =>
            Create("debug_capability_unsupported", exception.Message["debug_capability_unsupported:".Length..].Trim(), exception),
        InvalidOperationException when exception.Message.StartsWith("debug_session_active:", StringComparison.Ordinal) =>
            Create("debug_session_active", "A debugger automation session is already active.", exception),
        InvalidOperationException when exception.Message.StartsWith("debug_target_not_paused:", StringComparison.Ordinal) =>
            Create("debug_target_not_paused", "The debug target must be paused.", exception),
        InvalidOperationException when exception.Message.StartsWith("debug_wait_active:", StringComparison.Ordinal) =>
            Create("debug_wait_active", "A stop wait is already active.", exception),
        InvalidOperationException => Create("invalid_debug_state", exception.Message, exception),
        _ => Create("debugger_failed", "Debugger operation failed.", exception)
    };

    private static McpException Create(string code, string message, Exception? inner = null) =>
        inner is null ? new($"{code}: {message}") : new($"{code}: {message}", inner);
}
