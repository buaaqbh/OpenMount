using OpenMount.Core.Services;
using Xunit;

namespace OpenMount.Tests;

public class NotificationThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameDriveSameKindIsShownOncePerWindow()
    {
        var t = new NotificationThrottle(TimeSpan.FromMinutes(10));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Warning, T0));
        Assert.False(t.ShouldShow("NAS", NotificationKind.Warning, T0.AddMinutes(1)));
        Assert.False(t.ShouldShow("NAS", NotificationKind.Warning, T0.AddMinutes(9)));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Warning, T0.AddMinutes(10)));
    }

    [Fact]
    public void DifferentDrivesAndKindsAreIndependent()
    {
        var t = new NotificationThrottle(TimeSpan.FromMinutes(10));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Warning, T0));
        Assert.True(t.ShouldShow("Office", NotificationKind.Warning, T0));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Error, T0));
    }

    [Fact]
    public void SuccessAndInfoAreNeverThrottled()
    {
        var t = new NotificationThrottle(TimeSpan.FromMinutes(10));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Success, T0));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Success, T0));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Info, T0));
    }

    [Fact]
    public void ResetForgetsOneDrive()
    {
        var t = new NotificationThrottle(TimeSpan.FromMinutes(10));
        Assert.True(t.ShouldShow("NAS", NotificationKind.Warning, T0));
        t.Reset("NAS");
        Assert.True(t.ShouldShow("NAS", NotificationKind.Warning, T0.AddSeconds(1)));
    }
}
