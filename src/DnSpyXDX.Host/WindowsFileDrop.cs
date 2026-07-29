using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace DnSpyXDX.Host;

/// <summary>
/// Spike: give drag-and-drop the real file paths that the WebView's HTML drop event hides. Registers a
/// native OLE <c>IDropTarget</c> on the Photino window handle and reads <c>CF_HDROP</c> (the shell's list of
/// dropped file paths) so a dropped assembly opens from its actual folder — letting its siblings resolve the
/// way they do in dnSpy. Windows only.
///
/// Open question this spike answers: WebView2 is hosted as a child window and, with its default
/// <c>AllowExternalDrop</c>, owns the drop surface over the page. If that swallows the drop, our target on the
/// host window never fires and the real fix is a native Photino change (disable WebView2's drop). The log line
/// on each drop tells us which case we are in.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileDrop
{
    private static DropTarget? registered; // held so the COM callback object is never collected while in use
    private static ILogger? log;
    private static IntPtr rootHwnd;
    private static int applyAttempts;
    // Kept alive for the lifetime of the process: the native timer holds a raw pointer to this delegate.
    private static readonly TimerProc TimerCallback = OnTimer;
    private static readonly EnumWindowsProc EnumCallback = OnChildWindow;
    private const uint TimerId = 0xD507; // arbitrary, unique-enough id for KillTimer

    public static void Attach(PhotinoWindow window, Action<IReadOnlyList<string>> onFilesDropped, Action<bool> onDragActive, ILogger logger)
    {
        // WindowCreated fires on the STA UI thread that owns the handle and runs the message loop — the only
        // thread RegisterDragDrop may be called from.
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            try
            {
                OleInitialize(IntPtr.Zero); // no-op / S_FALSE if the thread is already initialised
                registered = new DropTarget(onFilesDropped, onDragActive, logger);
                log = logger;
                rootHwnd = window.WindowHandle;
                Register(rootHwnd);
                // WebView2 hosts the page in child windows created after ours, and by default it owns the drop
                // surface over the page — so a target on our top-level window never fires. Take over the drop
                // targets of WebView2's child windows too. They appear (and can be re-created) a beat later, so
                // re-apply on a UI-thread timer for a few seconds. WM_TIMER dispatches on this same STA thread,
                // which is the only place RegisterDragDrop is legal.
                SetTimer(rootHwnd, TimerId, 500, TimerCallback);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Native file drop registration failed."); }
        });
    }

    private static void OnTimer(IntPtr hwnd, uint msg, nuint id, uint time)
    {
        try
        {
            EnumChildWindows(rootHwnd, EnumCallback, IntPtr.Zero);
            // WebView2 is up within the first couple of seconds; stop after ~6s of re-applying.
            if (++applyAttempts >= 12) KillTimer(rootHwnd, TimerId);
        }
        catch (Exception ex) { log?.LogWarning(ex, "Native file drop child registration failed."); }
    }

    private static bool OnChildWindow(IntPtr child, IntPtr param) { Register(child); return true; }

    private static void Register(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || registered is null) return;
        RevokeDragDrop(hwnd); // drop any existing target (ours or WebView2's) so RegisterDragDrop can't fail as-already-registered
        RegisterDragDrop(hwnd, registered);
    }

    private const int CF_HDROP = 15;
    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;

    private sealed class DropTarget(Action<IReadOnlyList<string>> onFilesDropped, Action<bool> onDragActive, ILogger logger) : IDropTarget
    {
        private long lastActiveNotify;

        public int DragEnter(IDataObject data, int keyState, POINTL point, ref int effect)
        {
            var files = HasFiles(data);
            effect = files ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            if (files) NotifyActive();
            return 0;
        }

        public int DragOver(int keyState, POINTL point, ref int effect)
        {
            effect = DROPEFFECT_COPY;
            // DragOver fires continuously while hovering, across every child window we took over. Throttle the
            // "still dragging" ping so the overlay stays lit without flooding the UI bridge.
            if (Environment.TickCount64 - lastActiveNotify > 150) NotifyActive();
            return 0;
        }

        public int DragLeave() { SafeInvoke(() => onDragActive(false)); return 0; }

        public int Drop(IDataObject data, int keyState, POINTL point, ref int effect)
        {
            effect = DROPEFFECT_COPY;
            SafeInvoke(() => onDragActive(false));
            try
            {
                var files = GetFiles(data);
                logger.LogInformation("Native file drop received {Count} path(s): {Files}", files.Count, string.Join("; ", files));
                if (files.Count > 0) onFilesDropped(files);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Native file drop handling failed."); }
            return 0;
        }

        private void NotifyActive() { lastActiveNotify = Environment.TickCount64; SafeInvoke(() => onDragActive(true)); }
        private void SafeInvoke(Action action) { try { action(); } catch (Exception ex) { logger.LogWarning(ex, "Native drag overlay update failed."); } }

        private static bool HasFiles(IDataObject data)
        {
            var format = HDropFormat();
            try { return data.QueryGetData(ref format) == 0; }
            catch { return false; }
        }

        private static IReadOnlyList<string> GetFiles(IDataObject data)
        {
            var format = HDropFormat();
            data.GetData(ref format, out var medium);
            try
            {
                var hDrop = medium.unionmember;
                if (hDrop == IntPtr.Zero) return [];
                var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                var files = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(hDrop, i, null, 0);
                    var buffer = new StringBuilder((int)length + 1);
                    if (DragQueryFile(hDrop, i, buffer, length + 1) > 0) files.Add(buffer.ToString());
                }
                return files;
            }
            finally { ReleaseStgMedium(ref medium); }
        }

        private static FORMATETC HDropFormat() => new()
        {
            cfFormat = CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int X; public int Y; }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    private delegate void TimerProc(IntPtr hwnd, uint msg, nuint id, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint file, StringBuilder? buffer, uint length);
    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr hwnd, uint id, uint elapseMs, TimerProc callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, uint id);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);
}
