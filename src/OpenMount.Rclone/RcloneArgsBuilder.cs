using System.Text;
using OpenMount.Core.Models;
using OpenMount.Core.Services;

namespace OpenMount.Rclone;

public sealed record MountArguments(IReadOnlyList<string> Args, string MountSource, string MountPoint, string VolumeName, string VolumeLabel)
{
    /// <summary>Display form; args never contain secrets.</summary>
    public string CommandLine => "rclone.exe " + string.Join(' ', Args.Select(Quote));

    private static string Quote(string a)
    {
        if (a == "--config=") return "--config=\"\"";
        return a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a;
    }
}

/// <summary>
/// The only place where rclone command-line arguments are produced (design §7). Structured input only; no free text.
/// Secrets never appear here — they are injected by <see cref="ProcessEnvironmentBuilder"/>.
/// </summary>
public static class RcloneArgsBuilder
{
    public static readonly string[] ManagedFlags =
    {
        "--config", "--log-file", "--daemon", "--rc-serve", "--rc-no-auth", "--password-command", "--rc-web-gui", "--rc-addr", "--cache-dir",
    };

    public static MountArguments Build(Drive drive, AppSettings settings, string cacheDir, int rcPort)
    {
        var adv = drive.Advanced;
        var source = drive.StorageType switch
        {
            StorageType.WebDav => drive.RemoteName + ":",
            StorageType.Sftp => drive.RemoteName + ":" + SftpPathResolver.Resolve(drive.Sftp?.Path).RemoteSuffix,
            _ => throw new NotSupportedException(drive.StorageType.ToString()),
        };
        var mountPoint = drive.Mount.ToMountPoint();
        var label = SafeName.From(drive.Name, drive.ShortId);
        var volName = drive.Mount.NetworkMode ? $"\\\\{settings.Advanced.NetworkSharePrefix}\\{label}" : label;

        var a = new List<string> { "mount", source, mountPoint, "--config=" };
        if (drive.Mount.NetworkMode) a.Add("--network-mode");
        a.Add("--volname"); a.Add(volName);
        if (drive.Mount.ReadOnly) a.Add("--read-only");

        a.Add("--vfs-cache-mode"); a.Add((adv.VfsCacheMode ?? VfsCacheMode.Full).ToString().ToLowerInvariant());
        a.Add("--cache-dir"); a.Add(cacheDir);
        a.Add("--vfs-cache-max-size"); a.Add(adv.CacheCleanupTarget ?? settings.Cache.CleanupTargetPerDrive);
        a.Add("--vfs-cache-max-age"); a.Add(adv.CacheMaxAge ?? settings.Cache.MaxAge);
        a.Add("--vfs-cache-min-free-space"); a.Add(adv.CacheMinFreeSpace ?? settings.Cache.MinFreeSpace);
        a.Add("--vfs-write-back"); a.Add(adv.WriteBack ?? "5s");
        a.Add("--dir-cache-time"); a.Add(adv.DirCacheTime ?? "1m");
        a.Add("--poll-interval"); a.Add("0");
        a.Add("--vfs-read-chunk-size"); a.Add(adv.ReadChunkSize ?? "32M");
        a.Add("--vfs-read-ahead"); a.Add(adv.ReadAhead ?? "64M");
        a.Add("--buffer-size"); a.Add(adv.BufferSize ?? "16M");
        a.Add("--vfs-fast-fingerprint");
        a.Add("--transfers"); a.Add((adv.Transfers ?? 4).ToString());
        if (!string.IsNullOrEmpty(adv.ReportedCapacity)) { a.Add("--vfs-disk-space-total-size"); a.Add(adv.ReportedCapacity); }
        if (adv.FileSystemName == FileSystemNameMode.Ntfs) { a.Add("--fuse-flag"); a.Add("--ExactFileSystemName=NTFS"); }
        if (adv.DotHidden) { a.Add("-o"); a.Add("dothidden"); }
        if (adv.NoModTime) a.Add("--no-modtime");
        if (!string.IsNullOrEmpty(adv.BwLimitUp) || !string.IsNullOrEmpty(adv.BwLimitDown))
        {
            a.Add("--bwlimit");
            a.Add((string.IsNullOrEmpty(adv.BwLimitUp) ? "off" : adv.BwLimitUp) + ":" + (string.IsNullOrEmpty(adv.BwLimitDown) ? "off" : adv.BwLimitDown));
        }

        if (drive.StorageType == StorageType.WebDav && drive.WebDav is { } w)
        {
            if (w.TlsMode == TlsMode.Ignore) a.Add("--no-check-certificate");
            else if (w.TlsMode == TlsMode.CustomCa && !string.IsNullOrEmpty(w.CaCertPath)) { a.Add("--ca-cert"); a.Add(w.CaCertPath); }
            a.Add("--timeout"); a.Add(w.TimeoutSeconds + "s");
        }
        else
        {
            a.Add("--timeout"); a.Add("60s");
        }
        a.Add("--contimeout"); a.Add("30s");
        a.Add("--low-level-retries"); a.Add("10");
        a.Add("--retries"); a.Add("3");

        a.Add("--rc");
        a.Add("--rc-addr"); a.Add("127.0.0.1:" + rcPort);

        a.Add("--log-level"); a.Add(NormalizeLogLevel(settings.Advanced.LogLevel));
        a.Add("--use-json-log");
        a.Add("--stats"); a.Add("0");

        AssertNoForbidden(a);
        return new MountArguments(a, source, mountPoint, volName, label);
    }

    private static string NormalizeLogLevel(string? level) => (level ?? "NOTICE").ToUpperInvariant() switch
    {
        "DEBUG" => "DEBUG",
        "INFO" => "INFO",
        "ERROR" => "ERROR",
        _ => "NOTICE",
    };

    private static void AssertNoForbidden(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg == "--config=" || arg == "--rc-addr" || arg == "--cache-dir") continue;
            foreach (var f in ManagedFlags)
            {
                if (arg == f || arg.StartsWith(f + "=", StringComparison.Ordinal))
                    throw new InvalidOperationException("managed flag emitted unexpectedly: " + arg);
            }
        }
    }
}
