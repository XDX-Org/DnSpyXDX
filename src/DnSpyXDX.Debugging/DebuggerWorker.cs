using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed record DebuggerWorkerOptions(
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? GracefulShutdownTimeout = null,
    int MaximumHeaderBytes = DapMessageFramer.DefaultMaximumHeaderBytes,
    int MaximumPayloadBytes = DapMessageFramer.DefaultMaximumPayloadBytes);

public sealed record DebuggerWorkerExit(
    int ExitCode,
    bool Expected,
    bool WasKilled);

/// <summary>
/// Owns one debugger adapter process and its DAP connection. Shutdown first requests DAP
/// disconnect, then kills the process tree when the deadline expires.
/// </summary>
public sealed class DebuggerWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly TimeSpan gracefulShutdownTimeout;
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private readonly Task stderrLoop;
    private readonly Task<DebuggerWorkerExit> completion;
    private int stopRequested;
    private int wasKilled;
    private int disposeState;

    private DebuggerWorker(Process process, DebuggerWorkerOptions options)
    {
        this.process = process;
        gracefulShutdownTimeout =
            options.GracefulShutdownTimeout ?? DefaultShutdownTimeout;
        var framer = new DapMessageFramer(
            options.MaximumHeaderBytes,
            options.MaximumPayloadBytes);
        Connection = new DapConnection(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            framer);
        stderrLoop = ReadStandardErrorAsync();
        completion = MonitorExitAsync();
    }

    public DapConnection Connection { get; }
    public int ProcessId => process.Id;
    public Task<DebuggerWorkerExit> Completion => completion;

    public event Action<DebugOutputMessage>? OutputReceived;
    public event Action<DebuggerWorkerExit>? Exited;

    public static Task<DebuggerWorker> StartAsync(
        DebuggerWorkerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = options.GracefulShutdownTimeout ?? DefaultShutdownTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Graceful shutdown timeout must be positive.");

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            startInfo.WorkingDirectory = Path.GetFullPath(options.WorkingDirectory);
        foreach (var argument in options.Arguments ?? [])
            startInfo.ArgumentList.Add(argument);
        foreach (var variable in options.Environment ??
            new Dictionary<string, string?>())
            startInfo.Environment[variable.Key] = variable.Value;

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    $"Debugger worker '{options.ExecutablePath}' did not start.");
            return Task.FromResult(new DebuggerWorker(process, options));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task<DebuggerWorkerExit> StopAsync(
        bool terminateDebuggee,
        CancellationToken cancellationToken = default)
    {
        await stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref stopRequested, 1);
            if (process.HasExited) return await completion.ConfigureAwait(false);

            using var graceful = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            graceful.CancelAfter(gracefulShutdownTimeout);
            try
            {
                await Connection.SendRequestAsync(
                    "disconnect",
                    new JsonObject { ["terminateDebuggee"] = terminateDebuggee },
                    graceful.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or
                    EndOfStreamException or
                    IOException or
                    ObjectDisposedException)
            {
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }

            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (graceful.IsCancellationRequested)
                {
                    KillProcessTree();
                }
            }

            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        try
        {
            await StopAsync(terminateDebuggee: false).ConfigureAwait(false);
        }
        finally
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
        }
    }

    public void ForceClose() => KillProcessTree();

    private async Task<DebuggerWorkerExit> MonitorExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await stderrLoop.ConfigureAwait(false);
        var result = new DebuggerWorkerExit(
            process.ExitCode,
            Expected: Volatile.Read(ref stopRequested) != 0,
            WasKilled: Volatile.Read(ref wasKilled) != 0);
        InvokeSafely(Exited, result);
        return result;
    }

    private async Task ReadStandardErrorAsync()
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            InvokeSafely(OutputReceived, new DebugOutputMessage("stderr", line));
    }

    private void KillProcessTree()
    {
        if (process.HasExited) return;
        Interlocked.Exchange(ref wasKilled, 1);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // Worker monitoring must survive consumer callback failures.
            }
        }
    }
}
