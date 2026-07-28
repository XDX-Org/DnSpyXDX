using DnSpyXDX.Application;
using PhotinoEx.Core;
using PhotinoEx.Core.Models;

namespace DnSpyXDX.Host;

public sealed class PhotinoFileDropService : IFileDropService
{
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public void Attach(PhotinoExWindow window) => window.RegisterFilesDroppedHandler(OnFilesDropped);

    private void OnFilesDropped(object? sender, FilesDroppedEventArgs args) =>
        FilesDropped?.Invoke(args.Paths);
}
