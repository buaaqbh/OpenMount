using OpenMount.Core.Models;
using Xunit;

namespace OpenMount.Tests;

public class AppPathsTests
{
    [Fact]
    public void EnvironmentOverrideWinsOverEverything() =>
        Assert.Equal(@"D:\om-root", AppPaths.ResolveRoot(@"D:\om-root", @"C:\app\", portableMarkerExists: true, @"C:\Users\u\AppData\Local"));

    [Fact]
    public void PortableMarkerPutsDataNextToTheExecutable() =>
        Assert.Equal(@"C:\app\data", AppPaths.ResolveRoot(null, @"C:\app\", portableMarkerExists: true, @"C:\Users\u\AppData\Local"));

    [Fact]
    public void DefaultIsThePerUserFolder() =>
        Assert.Equal(@"C:\Users\u\AppData\Local\OpenMount", AppPaths.ResolveRoot("  ", @"C:\app\", portableMarkerExists: false, @"C:\Users\u\AppData\Local"));

    [Fact]
    public void RelativeOverrideIsIgnored() =>
        Assert.Equal(@"C:\Users\u\AppData\Local\OpenMount", AppPaths.ResolveRoot("relative\\path", @"C:\app\", portableMarkerExists: false, @"C:\Users\u\AppData\Local"));
}
