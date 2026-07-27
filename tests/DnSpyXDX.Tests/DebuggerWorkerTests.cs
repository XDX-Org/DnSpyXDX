using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class DebuggerWorkerTests
{
    [Fact]
    public async Task Starts_worker_exchanges_dap_and_stops_gracefully()
    {
        await using var worker = await StartWorkerAsync("normal");

        var response = await worker.Connection.SendRequestAsync("initialize");
        var exit = await worker.StopAsync(terminateDebuggee: false);

        Assert.True(response.Success);
        Assert.Equal("initialize", response.Body!.Value.GetProperty("echo").GetString());
        Assert.True(exit.Expected);
        Assert.False(exit.WasKilled);
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public async Task Kills_worker_tree_after_graceful_deadline()
    {
        await using var worker = await StartWorkerAsync(
            "hang",
            TimeSpan.FromMilliseconds(300));
        _ = await worker.Connection.SendRequestAsync("initialize");

        var exit = await worker.StopAsync(terminateDebuggee: true);

        Assert.True(exit.Expected);
        Assert.True(exit.WasKilled);
    }

    [Fact]
    public async Task Reports_unexpected_worker_crash_and_stderr()
    {
        await using var worker = await StartWorkerAsync("crash");

        var exit = await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(exit.Expected);
        Assert.False(exit.WasKilled);
        Assert.Equal(17, exit.ExitCode);
    }

    private static Task<DebuggerWorker> StartWorkerAsync(
        string mode,
        TimeSpan? timeout = null)
    {
        var worker = Path.Combine(
            AppContext.BaseDirectory,
            "DnSpyXDX.Debugger.TestWorker.dll");
        Assert.True(File.Exists(worker), $"Missing test worker: {worker}");
        return DebuggerWorker.StartAsync(new(
            DotnetHost(),
            [worker, mode],
            GracefulShutdownTimeout: timeout));
    }

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
}
