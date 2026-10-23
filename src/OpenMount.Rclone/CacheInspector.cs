using System.Text.Json;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;

namespace OpenMount.Rclone;

public sealed record CacheIdentityInfo(Guid DriveId, string RemoteName, string IdentityHash, DateTimeOffset CreatedAt, string AppVersion);

/// <summary>
/// Reads rclone's on-disk VFS cache metadata (cache-dir\vfsMeta\**) to decide, without a running process, whether
/// dirty (not yet uploaded) entries exist. rclone persists Dirty=true the moment a file is first modified.
/// Also owns the identity.json that binds a cache directory to its remote target (FR-22.7).
/// </summary>
public sealed class CacheInspector
{
    public const string IdentityFileName = "identity.json";

    public static string IdentityPath(string cacheDir) => Path.Combine(cacheDir, IdentityFileName);

    public static CacheIdentityInfo? ReadIdentity(string cacheDir)
    {
        var p = IdentityPath(cacheDir);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<CacheIdentityInfo>(File.ReadAllText(p), JsonDefaults.Options); }
        catch { return null; }
    }

    public static void WriteIdentity(string cacheDir, Drive drive, string appVersion)
    {
        Directory.CreateDirectory(cacheDir);
        var info = new CacheIdentityInfo(drive.Id, drive.RemoteName, drive.RemoteIdentityHash ?? RemoteIdentity.ComputeHash(drive), DateTimeOffset.UtcNow, appVersion);
        File.WriteAllText(IdentityPath(cacheDir), JsonSerializer.Serialize(info, JsonDefaults.Options));
    }

    public Task<OfflineSyncReport> ScanAsync(Drive drive, string cacheDir, bool abnormalStop, CancellationToken ct) =>
        Task.Run(() => Scan(drive, cacheDir, abnormalStop, ct), ct);

    public static OfflineSyncReport Scan(Drive drive, string cacheDir, bool abnormalStop, CancellationToken ct)
    {
        if (!Directory.Exists(cacheDir))
            return new OfflineSyncReport(SyncState.Clean(SyncSource.Offline, abnormalStop), true, null, 0);

        var identity = ReadIdentity(cacheDir);
        var expected = drive.RemoteIdentityHash ?? RemoteIdentity.ComputeHash(drive);
        var identityMatches = identity is null || string.Equals(identity.IdentityHash, expected, StringComparison.Ordinal);

        var metaRoot = Path.Combine(cacheDir, "vfsMeta");
        var dirty = 0;
        var dirtyBytes = 0L;
        var unreadable = 0;
        var totalBytes = 0L;
        if (Directory.Exists(metaRoot))
        {
            foreach (var file in Directory.EnumerateFiles(metaRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var isDirty = root.TryGetProperty("Dirty", out var d) && d.ValueKind == JsonValueKind.True;
                    var size = root.TryGetProperty("Size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    if (isDirty) { dirty++; dirtyBytes += size; }
                }
                catch (Exception)
                {
                    unreadable++;
                }
            }
        }
        var dataRoot = Path.Combine(cacheDir, "vfs");
        if (Directory.Exists(dataRoot))
        {
            try { totalBytes = Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch { }
        }

        SyncState state;
        if (!identityMatches) state = SyncState.Unknown(SyncSource.Offline, "缓存归属与当前连接目标不一致", abnormalStop);
        else if (unreadable > 0) state = SyncState.Unknown(SyncSource.Offline, $"{unreadable} 个缓存元数据文件无法读取", abnormalStop);
        else if (dirty > 0) state = SyncState.Pending(SyncSource.Offline, dirty, dirtyBytes, abnormalStop);
        else state = SyncState.Clean(SyncSource.Offline, abnormalStop);
        return new OfflineSyncReport(state, identityMatches, identity?.IdentityHash, totalBytes);
    }

    /// <summary>Deletes the data and metadata trees (the explicit "discard unsynced data" command). Keeps identity.json.</summary>
    public static void Discard(string cacheDir)
    {
        foreach (var sub in new[] { "vfs", "vfsMeta" })
        {
            var p = Path.Combine(cacheDir, sub);
            if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
        }
    }

    public static void DeleteAll(string cacheDir)
    {
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
    }
}
