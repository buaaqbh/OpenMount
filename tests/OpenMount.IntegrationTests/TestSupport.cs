using OpenMount.Core.Models;
using Xunit.Abstractions;

namespace OpenMount.IntegrationTests;

internal static class TestSupport
{
    /// <summary>Drive letter for test mounts: OPENMOUNT_IT_LETTER when set (pins the letter), otherwise the service's suggestion.</summary>
    public static char PickLetter()
    {
        var pinned = Environment.GetEnvironmentVariable("OPENMOUNT_IT_LETTER");
        if (!string.IsNullOrWhiteSpace(pinned)) return char.ToUpperInvariant(pinned.Trim()[0]);
        return new OpenMount.Windows.DriveLetterService().Suggest() ?? throw new InvalidOperationException("no free drive letter");
    }

    /// <summary>Copies per-drive rclone logs next to the repository (TestResults\it-logs) and echoes their tail to the test output.</summary>
    public static void DumpDriveLogs(ITestOutputHelper output, AppPaths paths, string testName)
    {
        try
        {
            if (!Directory.Exists(paths.DriveLogsDir)) return;
            var target = Path.Combine(FindRepoRoot(), "TestResults", "it-logs", testName);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(paths.DriveLogsDir, "*.log"))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                var lines = File.ReadAllLines(file);
                output.WriteLine($"----- {Path.GetFileName(file)} ({lines.Length} lines) -----");
                foreach (var l in lines.TakeLast(80)) output.WriteLine(l);
            }
        }
        catch (Exception ex)
        {
            output.WriteLine("log dump failed: " + ex.Message);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenMount.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
