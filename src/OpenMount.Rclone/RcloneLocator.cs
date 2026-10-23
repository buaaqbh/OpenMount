using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenMount.Core.Models;

namespace OpenMount.Rclone;

public sealed record CapabilityReport(bool Ok, string? Version, IReadOnlyList<string> MissingFlags, string? Error)
{
    public static CapabilityReport Fail(string error) => new(false, null, Array.Empty<string>(), error);
}

/// <summary>Finds the rclone executable: bundled (default), custom (advanced, must pass the capability probe) or a developer override.</summary>
public sealed class RcloneLocator
{
    public const string DevOverrideVariable = "OPENMOUNT_RCLONE";
    public static readonly Version MinimumVersion = new(1, 75, 0);

    public string? BundledPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[] { Path.Combine(baseDir, "bin", "rclone.exe"), Path.Combine(baseDir, "rclone.exe") })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }

    public string? Resolve(AppSettings settings)
    {
        var dev = Environment.GetEnvironmentVariable(DevOverrideVariable);
        if (!string.IsNullOrEmpty(dev) && File.Exists(dev)) return dev;
        if (settings.Advanced.RcloneMode == RcloneMode.Custom && !string.IsNullOrEmpty(settings.Advanced.RclonePath) && File.Exists(settings.Advanced.RclonePath))
            return settings.Advanced.RclonePath;
        return BundledPath;
    }
}

/// <summary>Capability probe for a custom rclone (FR-13.4): version floor + every flag the args builder emits.</summary>
public static class CapabilityProbe
{
    public static readonly string[] RequiredFlags =
    {
        "--network-mode", "--volname", "--vfs-cache-mode", "--vfs-cache-max-size", "--vfs-cache-max-age", "--vfs-cache-min-free-space",
        "--vfs-write-back", "--dir-cache-time", "--poll-interval", "--vfs-read-chunk-size", "--vfs-read-ahead", "--buffer-size",
        "--vfs-fast-fingerprint", "--vfs-disk-space-total-size", "--fuse-flag", "--no-modtime", "--bwlimit", "--rc-addr",
        "--use-json-log", "--read-only", "--cache-dir", "--transfers",
    };

    private static readonly Regex VersionPattern = new(@"rclone v(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    public static async Task<CapabilityReport> ProbeAsync(string exe, CancellationToken ct)
    {
        if (!File.Exists(exe)) return CapabilityReport.Fail("文件不存在");
        var versionOut = await RunAsync(exe, new[] { "version" }, ct);
        var m = VersionPattern.Match(versionOut);
        if (!m.Success) return CapabilityReport.Fail("无法识别 rclone 版本输出");
        var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        if (version < RcloneLocator.MinimumVersion) return new CapabilityReport(false, version.ToString(), Array.Empty<string>(), $"版本 {version} 低于最低要求 {RcloneLocator.MinimumVersion}");
        // "mount --help" lists only the command's own flags; the global ones (--bwlimit, --rc-addr, --use-json-log, ...) are under "help flags".
        var help = await RunAsync(exe, new[] { "mount", "--help" }, ct) + "\n" + await RunAsync(exe, new[] { "help", "flags" }, ct);
        var missing = RequiredFlags.Where(f => !help.Contains(f, StringComparison.Ordinal)).ToList();
        return new CapabilityReport(missing.Count == 0, version.ToString(), missing, missing.Count == 0 ? null : "缺少参数: " + string.Join(", ", missing));
    }

    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["RCLONE_CONFIG"] = string.Empty;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start rclone");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return await stdout + "\n" + await stderr;
    }
}
