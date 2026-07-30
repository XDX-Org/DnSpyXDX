using DnSpyXDX.Application;
using PhotinoEx.Core;

namespace DnSpyXDX.Host;

/// <summary>Applies application zoom through Photino's native webview API.</summary>
public sealed class PhotinoZoomService : IApplicationZoomService
{
    private PhotinoExWindow? window;
    public int ZoomPercent { get; private set; } = 100;

    public void Attach(PhotinoExWindow mainWindow)
    {
        window = mainWindow;
        window.SetZoom(ZoomPercent);
        window.RegisterWindowCreatedHandler((_, _) => SetZoom(ZoomPercent));
    }

    public void SetZoom(int percent)
    {
        ZoomPercent = Math.Clamp(percent, 50, 200);
        window?.SetZoom(ZoomPercent);
    }
}
