using OpenMount.Core.Errors;

namespace OpenMount.Core.State;

/// <summary>Primary (lifecycle) state of a drive.</summary>
public enum LifecycleState
{
    Disconnected,
    Connecting,
    NeedsCredentials,
    NeedsHostKeyConfirm,
    Mounted,
    Disconnecting,
    Reconnecting,
    Error,
}

/// <summary>Result of the most recent real access to the remote. Never drives lifecycle transitions.</summary>
public enum RemoteHealth { Unknown, Healthy, Unreachable, AuthRequired, Forbidden, NotFound }

public enum SyncKind { Clean, Pending, Uploading, Unknown }

public enum SyncSource { None, Live, Offline }

public sealed record SyncState(
    SyncKind Kind,
    int PendingCount,
    long PendingBytes,
    int UploadingCount,
    SyncSource Source,
    DateTimeOffset EvaluatedAt,
    bool AbnormalStop = false,
    string? Detail = null)
{
    public static SyncState None { get; } = new(SyncKind.Unknown, 0, 0, 0, SyncSource.None, DateTimeOffset.MinValue);

    public static SyncState Unknown(SyncSource source, string? detail = null, bool abnormalStop = false) =>
        new(SyncKind.Unknown, 0, 0, 0, source, DateTimeOffset.UtcNow, abnormalStop, detail);

    public static SyncState Clean(SyncSource source, bool abnormalStop = false) =>
        new(SyncKind.Clean, 0, 0, 0, source, DateTimeOffset.UtcNow, abnormalStop);

    public static SyncState Pending(SyncSource source, int count, long bytes, bool abnormalStop = false) =>
        new(SyncKind.Pending, count, bytes, 0, source, DateTimeOffset.UtcNow, abnormalStop);

    public static SyncState Uploading(int uploading, int queuedTotal, long bytes) =>
        new(SyncKind.Uploading, queuedTotal, bytes, uploading, SyncSource.Live, DateTimeOffset.UtcNow);

    /// <summary>Anything that is not confirmed clean must be protected by the unsynced-data guard.</summary>
    public bool RequiresProtection => Kind != SyncKind.Clean;
}

public sealed record MountInfo(string MountPoint, int ProcessId, int RcPort, string CacheDir, DateTimeOffset StartedAt);

public sealed record SpaceInfo(long? Total, long? Free);

public sealed record DriveSnapshot(
    Guid DriveId,
    LifecycleState Lifecycle,
    RemoteHealth RemoteHealth,
    SyncState Sync,
    MountInfo? Mount,
    MountError? Error,
    int ReconnectAttempt,
    DateTimeOffset? NextRetryAt,
    bool OwnedByOtherSession,
    SpaceInfo? Space,
    DateTimeOffset? LastProbeAt,
    StopProgressInfo? StopProgress)
{
    public static DriveSnapshot Initial(Guid driveId) =>
        new(driveId, LifecycleState.Disconnected, RemoteHealth.Unknown, SyncState.None, null, null, 0, null, false, null, null, null);

    public bool CanEdit => Lifecycle is LifecycleState.Disconnected or LifecycleState.Error or LifecycleState.NeedsCredentials or LifecycleState.NeedsHostKeyConfirm;
    public bool CanConnect => CanEdit && !OwnedByOtherSession;
    public bool CanDisconnect => Lifecycle is LifecycleState.Mounted or LifecycleState.Reconnecting or LifecycleState.Connecting;
    public bool CanOpen => Lifecycle == LifecycleState.Mounted && Mount is not null;
}

public sealed record StopProgressInfo(int Remaining, long RemainingBytes, int Uploading, TimeSpan Elapsed);
