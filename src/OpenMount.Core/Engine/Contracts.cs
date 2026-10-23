using OpenMount.Core.Errors;
using OpenMount.Core.Models;
using OpenMount.Core.State;

namespace OpenMount.Core.Engine;

/// <summary>Secrets for one mount. Never persisted by the engine; only injected into the child process environment.</summary>
public sealed record DriveSecrets(string? Password = null, string? KeyPassphrase = null, string? BearerToken = null, string? ProxyPassword = null)
{
    public static DriveSecrets Empty { get; } = new();

    public IEnumerable<string> All()
    {
        if (!string.IsNullOrEmpty(Password)) yield return Password;
        if (!string.IsNullOrEmpty(KeyPassphrase)) yield return KeyPassphrase;
        if (!string.IsNullOrEmpty(BearerToken)) yield return BearerToken;
        if (!string.IsNullOrEmpty(ProxyPassword)) yield return ProxyPassword;
    }
}

public sealed record MountRequest(
    Drive Drive,
    DriveSecrets Secrets,
    AppSettings Settings,
    string CacheDir,
    string RunDir,
    string KnownHostsPath);

public abstract record MountStartResult
{
    private MountStartResult() { }

    /// <summary>Locally ready (process, RC, mount point and identity verified). The remote may still be degraded.</summary>
    public sealed record Ready(IMountSession Session, RemoteHealthResult InitialHealth) : MountStartResult;

    /// <summary>Failed before a verified mount existed. Any partial process has been cleaned up; the cache is retained.</summary>
    public sealed record Failed(MountError Error, string RedactedTail) : MountStartResult;
}

public enum StopPolicy
{
    /// <summary>Expedite the upload queue and wait until it drains (or the caller cancels), then quit.</summary>
    WaitForUploads,
    /// <summary>Quit now; dirty cache entries stay on disk and are resumed on the next connect.</summary>
    StopAndRetainCache,
}

public enum StopOutcome { CleanStop, StoppedWithPendingData, Killed, Timeout }

public sealed record StopResult(
    StopOutcome Outcome,
    SyncState SyncStateAfterStop,
    bool CacheMustRetain,
    bool WasGraceful,
    MountError? Error);

public sealed record RemoteHealthResult(RemoteHealth Health, string? Error, TimeSpan Elapsed)
{
    public static RemoteHealthResult Healthy(TimeSpan elapsed) => new(RemoteHealth.Healthy, null, elapsed);
}

public sealed record MountSessionInfo(
    Guid DriveId,
    string MountPoint,
    int ProcessId,
    int RcPort,
    string CacheDir,
    DateTimeOffset StartedAt,
    string RedactedCommandLine);

public sealed record OfflineSyncReport(SyncState State, bool IdentityMatches, string? IdentityHashFound, long CacheBytes);

public sealed class SessionExitedEventArgs : EventArgs
{
    public SessionExitedEventArgs(int exitCode, string redactedTail, bool intentional)
    {
        ExitCode = exitCode;
        RedactedTail = redactedTail;
        Intentional = intentional;
    }

    public int ExitCode { get; }
    public string RedactedTail { get; }
    public bool Intentional { get; }
}

/// <summary>
/// Outcome of one lifecycle check. A failure names the step (process / rc / mountpoint / identity) and carries the
/// raw detail, so a lost mount can be explained from the logs afterwards instead of guessing.
/// </summary>
public sealed record AliveCheckResult(bool Alive, string Step, string Detail, TimeSpan Elapsed)
{
    public static AliveCheckResult Ok(TimeSpan elapsed) => new(true, "ok", string.Empty, elapsed);
    public static AliveCheckResult Failed(string step, string detail, TimeSpan elapsed) => new(false, step, detail, elapsed);
    public override string ToString() => Alive ? $"ok in {Elapsed.TotalMilliseconds:F0} ms" : $"{Step} failed after {Elapsed.TotalMilliseconds:F0} ms: {Detail}";
}

public interface IMountSession : IAsyncDisposable
{
    MountSessionInfo Info { get; }
    bool IsAlive { get; }
    /// <summary>Real access to the remote root through the mount's own file system (rc vfs/refresh).</summary>
    Task<RemoteHealthResult> ProbeRemoteAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>Live sync state from rc vfs/stats + vfs/queue. Returns Unknown when RC is unavailable.</summary>
    Task<SyncState> QuerySyncStateAsync(CancellationToken ct);
    /// <summary>Lifecycle health: process alive, RC responding, mount point present and identity intact. Never throws.</summary>
    Task<AliveCheckResult> CheckAliveAsync(CancellationToken ct);
    Task ExpediteUploadsAsync(CancellationToken ct);
    Task<SpaceInfo?> QuerySpaceAsync(CancellationToken ct);
    Task<StopResult> StopAsync(StopPolicy policy, IProgress<StopProgressInfo>? progress, CancellationToken ct);
    event EventHandler<SessionExitedEventArgs>? Exited;
}

public interface IMountEngine
{
    Task<MountStartResult> StartAsync(MountRequest request, IProgress<string>? progress, CancellationToken ct);
    /// <summary>Scan the on-disk cache metadata without a running process.</summary>
    Task<OfflineSyncReport> InspectOfflineAsync(Drive drive, string cacheDir, CancellationToken ct);
    /// <summary>Explicit, separately-named destructive operation: delete dirty cache data.</summary>
    Task DiscardUnsyncedDataAsync(Drive drive, string cacheDir, CancellationToken ct);
    /// <summary>Delete the whole cache directory. Caller must have verified the cache is clean.</summary>
    Task DeleteCacheAsync(Drive drive, string cacheDir, CancellationToken ct);
    /// <summary>
    /// Clear a stale "disconnected network drive" record Explorer may hold for the drive's letter (left by a killed mount or
    /// a crashed app): a synchronous WM_DEVICECHANGE removal broadcast for the letter. Does nothing for folder mounts or
    /// letters that are currently in use; harmless when there is nothing to clear.
    /// </summary>
    Task RepairExplorerGhostAsync(Drive drive, AppSettings settings, CancellationToken ct);
}

/// <summary>Windows-specific checks on mount points; implemented in OpenMount.Windows.</summary>
public interface IMountPointInspector
{
    bool Exists(string mountPoint);
    bool IsDriveLetterFree(char letter);
    /// <summary>
    /// Verify that the volume at the mount point is ours. <paramref name="expectedIdentity"/> is the UNC share path
    /// (\\server\share) for network-mode drive letters and the volume label otherwise; the file system name must be one of
    /// <paramref name="allowedFileSystemNames"/> and the drive type must match the mode.
    /// </summary>
    bool VerifyIdentity(string mountPoint, string expectedIdentity, IReadOnlyCollection<string> allowedFileSystemNames, bool expectNetworkDrive, out string detail);
    bool IsReparsePoint(string path);
    bool IsDanglingReparsePoint(string path);
    /// <summary>Remove a dangling reparse point directory left by a dead mount. Never deletes a real directory.</summary>
    bool TryRemoveDanglingReparsePoint(string path);
    /// <summary>
    /// If <paramref name="letter"/> is a network mapping under \\<paramref name="uncServerPrefix"/>\ whose volume is no longer
    /// reachable (the rclone process died without unmounting), cancel the mapping and tell Explorer. Never touches other mappings.
    /// </summary>
    bool TryRemoveStaleNetworkMapping(char letter, string uncServerPrefix);
    /// <summary>Apply <see cref="TryRemoveStaleNetworkMapping"/> to every drive letter; returns the letters that were cleaned.</summary>
    IReadOnlyList<char> SweepStaleNetworkMappings(string uncServerPrefix);
    /// <summary>
    /// Tell the shell that a mount point appeared or vanished: a synchronous WM_DEVICECHANGE volume broadcast (DBTF_NET when
    /// <paramref name="network"/>) followed by SHCNE_DRIVEADD / SHCNE_DRIVEREMOVED. Without the broadcast Explorer keeps a
    /// permanent "disconnected network drive" record for every WinFsp network drive that goes away (design §13).
    /// </summary>
    void NotifyMountPointChanged(string mountPoint, bool added, bool network = false);
}

public interface IChildProcessJob : IDisposable
{
    void Attach(System.Diagnostics.Process process);
}

public interface IChildProcessJobFactory
{
    IChildProcessJob Create();
}

public interface IShellIntegration
{
    void SetDriveIcon(char letter, StorageType type, string? preset);
    void RemoveDriveIcon(char letter);
    void NotifyMountPointChanged(string mountPoint, bool added, bool network = false);
    void OpenInExplorer(string mountPoint);
}

public interface ISessionLock : IDisposable
{
}

public interface ISessionLockFactory
{
    /// <summary>Returns null when another session holds the lock.</summary>
    ISessionLock? TryAcquire(string lockFilePath);
}
