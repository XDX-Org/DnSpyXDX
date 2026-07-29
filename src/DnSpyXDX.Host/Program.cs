using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.Debugging;
using DnSpyXDX.Export;
using DnSpyXDX.Host.Mcp;
using DnSpyXDX.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotinoEx.Blazor;

namespace DnSpyXDX.Host;

internal static class Program
{
    // WebView2 can only be created from an STA thread
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoExBlazorAppBuilder.CreateDefault(args);
        var loggingSettings = new RuntimeLoggingSettings();
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter(
                (category, level) =>
                    category?.StartsWith("DnSpyXDX", StringComparison.Ordinal) == true
                    && (level >= LogLevel.Information || loggingSettings.DebugEnabled)
            );
        });
        builder.Services.AddSingleton(loggingSettings);
        builder.Services.AddSingleton<RuntimeDisplaySettings>();
        // A disk-backed decompile cache so restoring a session (or reopening a type) loads its source without
        // re-running ILSpy. Injected into DecompilerBackend's two-argument constructor.
        builder.Services.AddSingleton(PersistentDecompileCache.Default());
        // dnSpy loads referenced assemblies on demand and surfaces them in the Assembly Explorer; enabling this
        // makes DnSpyXDX promote app-local neighbors to their own sessions as the decompiler resolves them, so
        // cross-assembly analysis and navigation see the whole application.
        builder.Services.AddSingleton(new NeighborLoadingSettings { AutoLoadReferencedAssemblies = true });
        // Explicit factory so the three-argument constructor (and therefore auto-load) is used regardless of
        // how the container ranks constructors.
        builder.Services.AddSingleton<IDecompilerBackend>(services => new DecompilerBackend(
            services.GetRequiredService<RuntimeDisplaySettings>(),
            services.GetRequiredService<PersistentDecompileCache>(),
            services.GetRequiredService<NeighborLoadingSettings>()));
        builder.Services.AddSingleton(new WorkerDebuggerOptions());
        builder.Services.AddSingleton<IDebuggerEngineProvider>(services =>
            new WorkerDebuggerEngineProvider(
                DebugRuntimeKind.CoreClr,
                services.GetRequiredService<WorkerDebuggerOptions>()));
        builder.Services.AddSingleton<IDebuggerEngineProvider>(services =>
            new WorkerDebuggerEngineProvider(
                DebugRuntimeKind.Mono,
                services.GetRequiredService<WorkerDebuggerOptions>()));
        builder.Services.AddSingleton<IDebuggerEngineProvider>(services =>
            new WorkerDebuggerEngineProvider(
                DebugRuntimeKind.UnityMono,
                services.GetRequiredService<WorkerDebuggerOptions>()));
        builder.Services.AddSingleton<IUnityMonoEndpointDiscovery, UnityMonoEndpointDiscovery>();
        builder.Services.AddSingleton<IDebuggerEngineRegistry>(services =>
            new DebuggerEngineRegistry(
                services.GetServices<IDebuggerEngineProvider>()));
        builder.Services.AddSingleton<IDebuggerService, DebuggerService>();
        builder.Services.AddSingleton<DebuggerWorkspace>();
        builder.Services.AddSingleton<DebuggerAutomationService>();
        builder.Services.AddSingleton<IProjectExportService, ProjectExportService>();
        builder.Services.AddSingleton<WorkspaceState>();
        builder.Services.AddSingleton<SourceViewStateStore>();
        builder.Services.AddSingleton<SourcePresentationCache>();
        builder.Services.AddSingleton<WorkspaceAssemblyService>();
        builder.Services.AddSingleton<IFileDialogService, PhotinoFileDialogService>();
        var fileDropService = new PhotinoFileDropService();
        builder.Services.AddSingleton<IFileDropService>(fileDropService);
        builder.Services.AddSingleton<IWorkspaceSessionService, WorkspaceSessionService>();
        builder.Services.AddSingleton<McpServerSettings>();
        builder.Services.AddSingleton<McpActivityLog>();
        builder.Services.AddSingleton<IMcpServerService, McpServerService>();
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
            "DnSpyXDX",
            "WebView2"
        );
        Directory.CreateDirectory(userDataFolder);
        // Windows uses a multi-resolution .ico for the window/taskbar icon; GTK on Linux wants a .png.
        var iconFile = OperatingSystem.IsWindows() ? "dnspyxdx.ico" : "dnspyxdx.png";
        app.MainWindow.SetLogVerbosity(0)
            .SetTemporaryFilesPath(userDataFolder)
            .SetTitle("DnSpyXDX")
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", iconFile))
            .SetSize(1320, 840)
            .SetMinSize(860, 560)
            .SetUseOsDefaultSize(false);
        zoomService.Attach(app.MainWindow);
        fileDropService.Attach(app.MainWindow);
        applicationLifetime.Attach(app.MainWindow);
        WindowStateManager.Attach(app.MainWindow);
        app.Run();
    }
}
