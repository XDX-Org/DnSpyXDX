using System.Text.Json;
using System.Text.Json.Nodes;
using DnSpyXDX.Debugging;

var mode = args.FirstOrDefault() ?? "normal";
if (mode == "crash")
{
    Console.Error.WriteLine("test worker crash");
    return 17;
}
if (mode.StartsWith("netcoredbg", StringComparison.Ordinal))
    return await RunNetCoreDbgAsync(mode);

var framer = new DapMessageFramer();
while (await framer.ReadAsync(Console.OpenStandardInput()) is { } payload)
{
    using var document = JsonDocument.Parse(payload);
    var request = document.RootElement;
    var sequence = request.GetProperty("seq").GetInt32();
    var command = request.GetProperty("command").GetString()!;
    if (command == "disconnect" && mode == "hang")
        await Task.Delay(Timeout.InfiniteTimeSpan);

    var response = new JsonObject
    {
        ["seq"] = sequence + 1000,
        ["type"] = "response",
        ["request_seq"] = sequence,
        ["success"] = true,
        ["command"] = command,
        ["body"] = new JsonObject { ["echo"] = command }
    };
    await framer.WriteAsync(
        Console.OpenStandardOutput(),
        JsonSerializer.SerializeToUtf8Bytes(response));
    if (command == "disconnect") return 0;
}

return 0;

static async Task<int> RunNetCoreDbgAsync(string mode)
{
    var framer = new DapMessageFramer();
    var input = Console.OpenStandardInput();
    var output = Console.OpenStandardOutput();
    var nextSequence = 1000;
    int? startSequence = null;
    string? startCommand = null;
    var stopAtEntry = false;
    JsonObject? pendingIlBinding = null;

    async ValueTask SendAsync(JsonObject message)
    {
        message["seq"] = nextSequence++;
        await framer.WriteAsync(
            output,
            JsonSerializer.SerializeToUtf8Bytes(message));
    }

    ValueTask RespondAsync(
        int requestSequence,
        string command,
        JsonNode? body = null,
        bool success = true,
        string? message = null)
    {
        var response = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = requestSequence,
            ["success"] = success,
            ["command"] = command
        };
        if (body is not null) response["body"] = body;
        if (message is not null) response["message"] = message;
        return SendAsync(response);
    }

    ValueTask EventAsync(string name, JsonNode? body = null)
    {
        var value = new JsonObject
        {
            ["type"] = "event",
            ["event"] = name
        };
        if (body is not null) value["body"] = body;
        return SendAsync(value);
    }

    while (await framer.ReadAsync(input) is { } payload)
    {
        using var document = JsonDocument.Parse(payload);
        var request = document.RootElement;
        var sequence = request.GetProperty("seq").GetInt32();
        var command = request.GetProperty("command").GetString()!;
        var arguments = request.TryGetProperty("arguments", out var foundArguments)
            ? foundArguments
            : default;

        switch (command)
        {
            case "initialize":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["supportsConfigurationDoneRequest"] = true,
                        ["supportsFunctionBreakpoints"] = true,
                        ["supportsConditionalBreakpoints"] = true,
                        ["supportsSetVariable"] = true,
                        ["supportsXdxIlBreakpoints"] =
                            mode.StartsWith("netcoredbg-il", StringComparison.Ordinal),
                        ["supportsExceptionFilterOptions"] = true,
                        ["exceptionBreakpointFilters"] = new JsonArray(
                            new JsonObject
                            {
                                ["filter"] = "all",
                                ["label"] = "all"
                            })
                    });
                break;
            case "xdx/setIlBreakpoints":
                if (!mode.StartsWith("netcoredbg-il", StringComparison.Ordinal))
                {
                    await RespondAsync(
                        sequence,
                        command,
                        success: false,
                        message: "IL breakpoints are unavailable");
                    break;
                }
                var breakpointBindings = new JsonArray();
                foreach (var breakpoint in arguments.GetProperty("breakpoints")
                    .EnumerateArray())
                {
                    var enabled = breakpoint.GetProperty("enabled").GetBoolean();
                    var binding = new JsonObject
                    {
                        ["id"] = breakpoint.GetProperty("id").GetString(),
                        ["verified"] = enabled &&
                                mode != "netcoredbg-il-rebind",
                        ["message"] = enabled &&
                                mode == "netcoredbg-il-rebind"
                                    ? "Pending: module is not loaded."
                                    : enabled
                                        ? null
                                        : "Breakpoint is disabled.",
                        ["moduleMvid"] = breakpoint.GetProperty("moduleMvid").GetString(),
                        ["methodToken"] = breakpoint.GetProperty("methodToken").GetInt32(),
                        ["ilOffset"] = breakpoint.GetProperty("ilOffset").GetInt32()
                    };
                    breakpointBindings.Add(binding);
                    if (enabled && mode == "netcoredbg-il-rebind")
                    {
                        pendingIlBinding = binding.DeepClone().AsObject();
                        pendingIlBinding["verified"] = true;
                        pendingIlBinding.Remove("message");
                    }
                }
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject { ["breakpoints"] = breakpointBindings });
                break;
            case "launch":
            case "attach":
                startSequence = sequence;
                startCommand = command;
                stopAtEntry = command == "launch" &&
                    arguments.TryGetProperty("stopAtEntry", out var stop) &&
                    stop.GetBoolean();
                if (command == "launch")
                {
                    await EventAsync(
                        "process",
                        new JsonObject { ["systemProcessId"] = 4242 });
                }
                if (mode != "netcoredbg-no-initialized")
                    await EventAsync("initialized", new JsonObject());
                break;
            case "configurationDone":
                await RespondAsync(sequence, command, new JsonObject());
                if (startSequence is { } pendingSequence &&
                    startCommand is { } pendingCommand)
                {
                    await RespondAsync(
                        pendingSequence,
                        pendingCommand,
                        new JsonObject());
                    startSequence = null;
                    startCommand = null;
                }
                if (stopAtEntry)
                {
                    stopAtEntry = false;
                    await EventAsync(
                        "stopped",
                        new JsonObject
                        {
                            ["reason"] = "entry",
                            ["threadId"] = 7,
                            ["allThreadsStopped"] = true
                        });
                }
                if (pendingIlBinding is not null)
                {
                    await EventAsync("xdx/ilBreakpoint", pendingIlBinding);
                    pendingIlBinding = null;
                }
                break;
            case "threads":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["threads"] = new JsonArray(
                            new JsonObject
                            {
                                ["id"] = 7,
                                ["name"] = "Main Thread"
                            })
                    });
                break;
            case "stackTrace":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["stackFrames"] = new JsonArray(
                            new JsonObject
                            {
                                ["id"] = 70,
                                ["name"] = "Sample.Program.Main()",
                                ["line"] = 12,
                                ["column"] = 5,
                                ["moduleId"] = "Sample.dll",
                                ["xdxLocation"] = mode == "netcoredbg-il"
                                ? new JsonObject
                                {
                                    ["moduleMvid"] =
                                        "11111111-2222-3333-4444-555555555555",
                                    ["methodToken"] = 0x06000001,
                                    ["ilOffset"] = 4
                                }
                                : null,
                                ["source"] = new JsonObject
                                {
                                    ["path"] = "/src/Program.cs"
                                }
                            }),
                        ["totalFrames"] = 1
                    });
                break;
            case "modules":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["modules"] = new JsonArray(
                            new JsonObject
                            {
                                ["id"] = "Sample.dll",
                                ["name"] = "Sample.dll",
                                ["path"] = "/debug/Sample.dll"
                            }),
                        ["totalModules"] = 1
                    });
                break;
            case "scopes":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["scopes"] = new JsonArray(
                            new JsonObject
                            {
                                ["name"] = "Locals",
                                ["variablesReference"] = 99,
                                ["expensive"] = false
                            })
                    });
                break;
            case "variables":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["variables"] = new JsonArray(
                            new JsonObject
                            {
                                ["name"] = "answer",
                                ["value"] = "42",
                                ["type"] = "int",
                                ["evaluateName"] = "answer",
                                ["variablesReference"] = 0
                            })
                    });
                break;
            case "evaluate":
                await RespondAsync(
                    sequence,
                    command,
                    new JsonObject
                    {
                        ["result"] = "42",
                        ["type"] = "int",
                        ["variablesReference"] = 0
                    });
                break;
            case "continue":
                await RespondAsync(sequence, command, new JsonObject());
                await EventAsync(
                    "continued",
                    new JsonObject
                    {
                        ["threadId"] = 7,
                        ["allThreadsContinued"] = true
                    });
                break;
            case "pause":
                await RespondAsync(sequence, command, new JsonObject());
                await EventAsync(
                    "stopped",
                    new JsonObject
                    {
                        ["reason"] = "pause",
                        ["threadId"] = 7,
                        ["xdxLocation"] = mode == "netcoredbg-il"
                                ? new JsonObject
                                {
                                    ["moduleMvid"] =
                                        "11111111-2222-3333-4444-555555555555",
                                    ["methodToken"] = 0x06000001,
                                    ["ilOffset"] = 4
                                }
                                : null,
                        ["allThreadsStopped"] = true
                    });
                break;
            case "next":
            case "stepIn":
            case "stepOut":
                await RespondAsync(sequence, command, new JsonObject());
                await EventAsync(
                    "stopped",
                    new JsonObject
                    {
                        ["reason"] = "step",
                        ["threadId"] = 7,
                        ["allThreadsStopped"] = true
                    });
                break;
            case "disconnect":
                await RespondAsync(sequence, command, new JsonObject());
                return 0;
            default:
                await RespondAsync(
                    sequence,
                    command,
                    success: false,
                    message: $"unsupported test command: {command}");
                break;
        }
    }

    return 0;
}
