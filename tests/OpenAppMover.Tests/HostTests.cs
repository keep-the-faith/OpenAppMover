// Reflection and resource checks only: they run on any OS although OpenAppMover.Host targets Windows.
#pragma warning disable CA1416

using OpenAppMover.Host;

namespace OpenAppMover.Tests;

/// <summary>Usage recording in the single-file executable depends on TraceEvent internals: fail here, not on a user's PC.</summary>
public class KernelTraceControlLoaderTests
{
    [Fact]
    public void TraceEventLoaderFlagExists()
    {
        Assert.NotNull(KernelTraceControlLoader.LoadedFlag);
        Assert.Equal(typeof(bool), KernelTraceControlLoader.LoadedFlag!.FieldType);
    }

    [Fact]
    public void KernelTraceControlIsEmbedded()
    {
        using var resource = typeof(KernelTraceControlLoader).Assembly.GetManifestResourceStream("OpenAppMover.Tools.KernelTraceControl.dll");
        Assert.NotNull(resource);
        Assert.True(resource!.Length > 0);
    }
}
