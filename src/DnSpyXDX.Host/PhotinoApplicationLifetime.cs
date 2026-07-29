using DnSpyXDX.Application;
using PhotinoEx.Core;

namespace DnSpyXDX.Host;

public sealed class PhotinoApplicationLifetime : IApplicationLifetime
{
    private PhotinoExWindow? window;

    public void Attach(PhotinoExWindow mainWindow) => window = mainWindow;

    public void Exit() => window?.Close();
}
