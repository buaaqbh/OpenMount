using OpenMount.Core.Services;
using Xunit;

namespace OpenMount.Tests;

public class RepositoryVersionTests
{
    [Fact]
    public void NewerSchemaIsRejectedInsteadOfSilentlyDowngraded()
    {
        var json = "{ \"schemaVersion\": " + (DriveRepository.CurrentSchemaVersion + 1) + ", \"drives\": [] }";
        var ex = Assert.Throws<ConfigNewerVersionException>(() => DriveRepository.Parse(json));
        Assert.Contains("更新版本", ex.Message);
    }

    [Fact]
    public void CurrentAndOlderSchemasStillParse()
    {
        Assert.Empty(DriveRepository.Parse("{ \"schemaVersion\": " + DriveRepository.CurrentSchemaVersion + ", \"drives\": [] }"));
        Assert.Empty(DriveRepository.Parse("{ \"schemaVersion\": 1, \"drives\": [] }"));
    }
}
