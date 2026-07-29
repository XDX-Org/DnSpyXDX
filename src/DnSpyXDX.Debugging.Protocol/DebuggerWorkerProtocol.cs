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

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
}
