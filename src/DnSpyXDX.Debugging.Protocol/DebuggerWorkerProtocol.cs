using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnSpyXDX.Debugging.Protocol;

public enum DebuggerWorkerMessageKind
{
    Request,
    Response,
    Event
}

public sealed record DebuggerWorkerError(string Code, string Message);

public sealed record DebuggerWorkerMessage(
    int ProtocolVersion,
    DebuggerWorkerMessageKind Kind,
    Guid SessionId,
    long Generation,
    long Sequence,
    string Name,
    long? ReplyTo = null,
    long? BreakpointRevision = null,
    bool? Success = null,
    JsonElement? Body = null,
    DebuggerWorkerError? Error = null);

public static class DebuggerWorkerProtocol
{
    public const int Version = 1;
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumDepth = 32;
    public const int MaximumCollectionItems = 10_000;
    public const int MaximumStringCharacters = 1_048_576;

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = MaximumDepth
    };

    public static void Validate(DebuggerWorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ProtocolVersion != Version)
            throw new InvalidDataException(
                $"Unsupported debugger worker protocol version {message.ProtocolVersion}.");
        if (message.SessionId == Guid.Empty)
            throw new InvalidDataException("Debugger worker message requires a session ID.");
        if (message.Generation <= 0)
            throw new InvalidDataException("Debugger worker message generation must be positive.");
        if (message.Sequence <= 0)
            throw new InvalidDataException("Debugger worker message sequence must be positive.");
        if (string.IsNullOrWhiteSpace(message.Name))
            throw new InvalidDataException("Debugger worker message requires a name.");
        if (message.Name.Length > 128)
            throw new InvalidDataException("Debugger worker message name is too long.");
        if (message.BreakpointRevision is < 0)
            throw new InvalidDataException("Breakpoint revision cannot be negative.");
        if (message.Error is { } error &&
            (error.Code.Length > 256 || error.Message.Length > 65_536))
            throw new InvalidDataException("Debugger worker error text exceeds its limit.");
        if (message.Body is { } body) ValidateBody(body, 0);

        switch (message.Kind)
        {
            case DebuggerWorkerMessageKind.Request:
            case DebuggerWorkerMessageKind.Event:
                if (message.ReplyTo is not null || message.Success is not null ||
                    message.Error is not null)
                    throw new InvalidDataException(
                        $"Debugger worker {message.Kind.ToString().ToLowerInvariant()} contains response fields.");
                break;
            case DebuggerWorkerMessageKind.Response:
                if (message.ReplyTo is not > 0 || message.Success is null)
                    throw new InvalidDataException(
                        "Debugger worker response requires replyTo and success fields.");
                if (message.Success == true && message.Error is not null)
                    throw new InvalidDataException(
                        "Successful debugger worker response cannot contain an error.");
                if (message.Success == false && message.Error is null)
                    throw new InvalidDataException(
                        "Failed debugger worker response requires an error.");
                break;
            default:
                throw new InvalidDataException("Debugger worker message kind is invalid.");
        }
    }

    public static byte[] Serialize(DebuggerWorkerMessage message)
    {
        Validate(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > DefaultMaximumPayloadBytes)
            throw new InvalidDataException("Debugger worker message exceeds the payload limit.");
        return payload;
    }

    public static DebuggerWorkerMessage Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > DefaultMaximumPayloadBytes)
            throw new InvalidDataException("Debugger worker payload length is invalid.");
        var message = JsonSerializer.Deserialize<DebuggerWorkerMessage>(payload, SerializerOptions) ??
            throw new InvalidDataException("Debugger worker payload is null.");
        Validate(message);
        return message;
    }

    private static void ValidateBody(JsonElement value, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("Debugger worker JSON nesting exceeds its limit.");
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if ((value.GetString()?.Length ?? 0) > MaximumStringCharacters)
                    throw new InvalidDataException("Debugger worker string exceeds its limit.");
                break;
            case JsonValueKind.Array:
            {
                var count = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (++count > MaximumCollectionItems)
                        throw new InvalidDataException(
                            "Debugger worker collection exceeds its item limit.");
                    ValidateBody(item, depth + 1);
                }
                break;
            }
            case JsonValueKind.Object:
            {
                var count = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++count > MaximumCollectionItems)
                        throw new InvalidDataException(
                            "Debugger worker object exceeds its property limit.");
                    if (property.Name.Length > 256)
                        throw new InvalidDataException(
                            "Debugger worker property name exceeds its limit.");
                    ValidateBody(property.Value, depth + 1);
                }
                break;
            }
        }
    }
}
