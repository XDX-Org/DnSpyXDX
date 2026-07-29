using Xunit;

namespace DnSpyXDX.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveDebuggerTestCollection
{
    public const string Name = "Live debugger";
}
