using System.Drawing;
using System.Text.Json;
using PhotinoEx.Core;

namespace DnSpyXDX.Host;

internal static class WindowStateManager
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DnSpyXDX",
        "window.json"
    );
    private static readonly object Sync = new();
    private static Timer? timer;

    public static void Attach(PhotinoExWindow window)
    {
        var state = Load();
        if (state is not null && state.Width >= 860 && state.Height >= 560)
        {
            window
                .SetUseOsDefaultSize(false)
                .SetSize(state.Width, state.Height)
                .SetUseOsDefaultLocation(false)
                .SetLocation(new Point(state.Left, state.Top));
            if (state.Maximized)
                window.SetMaximized(true);
        }
        void QueueSave(object? sender, EventArgs args)
        {
            lock (Sync)
            {
                timer?.Dispose();
                timer = new Timer(_ => Save(window), null, 400, Timeout.Infinite);
            }
        }
        window.RegisterSizeChangedHandler((sender, size) => QueueSave(sender, EventArgs.Empty));
        window.RegisterLocationChangedHandler(
            (sender, point) => QueueSave(sender, EventArgs.Empty)
        );
        window.RegisterMaximizedHandler(QueueSave);
    }

    private static WindowState? Load()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath))
                : null;
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(PhotinoExWindow window)
    {
        try
        {
            var state = new WindowState(
                window.Left,
                window.Top,
                window.Width,
                window.Height,
                window.Maximized
            );
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, StatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record WindowState(int Left, int Top, int Width, int Height, bool Maximized);
}
