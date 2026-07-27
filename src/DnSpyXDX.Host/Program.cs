using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.Export;
using DnSpyXDX.Host;
using DnSpyXDX.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;

internal static class Program
{
    // WebView2 can only be created from an STA thread
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        var loggingSettings = new RuntimeLoggingSettings();
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter((category, level) =>
                category?.StartsWith("DnSpyXDX", StringComparison.Ordinal) == true &&
                (level >= LogLevel.Information || loggingSettings.DebugEnabled));
        });
        builder.Services.AddSingleton(loggingSettings);
        builder.Services.AddSingleton<RuntimeDisplaySettings>();
        // A disk-backed decompile cache so restoring a session (or reopening a type) loads its source without
        // re-running ILSpy. Injected into DecompilerBackend's two-argument constructor.
        builder.Services.AddSingleton(PersistentDecompileCache.Default());
        builder.Services.AddSingleton<IDecompilerBackend, DecompilerBackend>();
        builder.Services.AddSingleton<IProjectExportService, ProjectExportService>();
        builder.Services.AddSingleton<WorkspaceState>();
        builder.Services.AddSingleton<SourceViewStateStore>();
        builder.Services.AddSingleton<SourcePresentationCache>();
        builder.Services.AddSingleton<IFileDialogService, PhotinoFileDialogService>();
        builder.Services.AddSingleton<IWorkspaceSessionService, WorkspaceSessionService>();
        var zoomService = new PhotinoZoomService();
        builder.Services.AddSingleton<IApplicationZoomService>(zoomService);
        var applicationLifetime = new PhotinoApplicationLifetime();
        builder.Services.AddSingleton<IApplicationLifetime>(applicationLifetime);
        builder.RootComponents.Add<App>("app");
        var app = builder.Build();
        // WebView2's user-data folder defaults to a location shared by all Photino apps
        // (%LOCALAPPDATA%\Photino\EBWebView). A second Photino app (e.g. the SPT launcher)
        // holding that folder open leaves us with a black window. Use a private folder so we
        // never collide with another Photino process.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DnSpyXDX", "WebView2");
        Directory.CreateDirectory(userDataFolder);
        // Windows uses a multi-resolution .ico for the window/taskbar icon; GTK on Linux wants a .png.
        var iconFile = OperatingSystem.IsWindows() ? "dnspyxdx.ico" : "dnspyxdx.png";
        app.MainWindow.SetLogVerbosity(0).SetTemporaryFilesPath(userDataFolder).SetTitle("DnSpyXDX").SetIconFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", iconFile)).SetSize(1320, 840).SetMinSize(860, 560).SetUseOsDefaultSize(false);
        zoomService.Attach(app.MainWindow);
        applicationLifetime.Attach(app.MainWindow);
        WindowStateManager.Attach(app.MainWindow);
        app.Run();
    }
}
