using System.Diagnostics;
using DnSpyXDX.Debugging;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class WorkerDebuggerProcessTests
{
    [Fact]
    public async Task Detached_worker_crash_is_reported_without_affecting_host()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = CreateScript("sleep 0.1\necho crash-diagnostic >&2\nexit 17\n");
        try
        {
            var process = await DebuggerWorkerProcess.StartAsync(
                script, Guid.NewGuid(), 1, TimeSpan.FromMilliseconds(300), default);
            var fault = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            process.Faulted += message =>
            {
                if (message.Contains("exited unexpectedly", StringComparison.Ordinal))
                    fault.TrySetResult(message);
            };

            Assert.Contains("code 17", await fault.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            await process.DisposeAsync();
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task Detached_worker_hang_is_force_killed_within_deadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        var script = CreateScript("sleep 30\n");
        try
        {
            var process = await DebuggerWorkerProcess.StartAsync(
                script, Guid.NewGuid(), 1, TimeSpan.FromMilliseconds(200), default);
            var elapsed = Stopwatch.StartNew();
            await process.DisposeAsync();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), elapsed.Elapsed.ToString());
        }
        finally
        {
            File.Delete(script);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateScript(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnspyxdx-worker-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
