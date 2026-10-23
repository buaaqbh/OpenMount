using OpenMount.Core.Engine;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.State;

namespace OpenMount.Core.Services;

/// <summary>Collection of supervisors plus the cross-drive operations (startup, exit, delete, edit).</summary>
public sealed class DriveManager : IAsyncDisposable
{
    private readonly DriveRepository _repo;
    private readonly SettingsService _settings;
    private readonly IMountEngine _engine;
    private readonly CredentialResolver _credentials;
    private readonly ICredentialStore _store;
    private readonly KnownHostsFile _knownHosts;
    private readonly IHostKeyScanner _scanner;
    private readonly IUserInteraction _ui;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly Redactor _redactor;
    private readonly IShellIntegration? _shell;
    private readonly ISessionLockFactory _locks;
    private readonly IProxyResolver _proxy;
    private readonly Dictionary<Guid, DriveSupervisor> _supervisors = new();
    private readonly object _gate = new();

    public DriveManager(
        DriveRepository repo,
        SettingsService settings,
        IMountEngine engine,
        CredentialResolver credentials,
        ICredentialStore store,
        KnownHostsFile knownHosts,
        IHostKeyScanner scanner,
        IUserInteraction ui,
        AppPaths paths,
        IAppLog log,
        Redactor redactor,
        IShellIntegration? shell,
        ISessionLockFactory locks,
        IProxyResolver proxy)
    {
        _repo = repo;
        _settings = settings;
        _engine = engine;
        _credentials = credentials;
        _store = store;
        _knownHosts = knownHosts;
        _scanner = scanner;
        _ui = ui;
        _paths = paths;
        _log = log;
        _redactor = redactor;
        _shell = shell;
        _locks = locks;
        _proxy = proxy;
    }

    public event Action<DriveSupervisor>? SupervisorAdded;
    public event Action<DriveSupervisor>? SupervisorRemoved;
    public event Action<DriveSupervisor, DriveSnapshot>? SnapshotChanged;

    public IReadOnlyList<DriveSupervisor> Supervisors
    {
        get { lock (_gate) return _repo.Drives.Select(d => _supervisors[d.Id]).ToList(); }
    }

    public DriveSupervisor? Get(Guid id)
    {
        lock (_gate) return _supervisors.TryGetValue(id, out var s) ? s : null;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _repo.Load();
        foreach (var drive in _repo.Drives)
        {
            // A drive that was still mounted when the previous OpenMount process ended abnormally (Job Object killed rclone)
            // leaves a "disconnected network drive" record in Explorer; a removal broadcast for the letter clears it and is
            // harmless otherwise, so do it for every configured drive letter before the first connect attempt (design §13).
            if (drive.Mount.Kind == MountKind.DriveLetter && !string.IsNullOrEmpty(drive.Mount.DriveLetter))
            {
                try { await _engine.RepairExplorerGhostAsync(drive, _settings.Current, ct); }
                catch (Exception ex) { _log.Warn("manager", $"ghost repair failed for {drive.Name}: {ex.Message}"); }
            }
            var sup = CreateSupervisor(drive);
            await sup.InitializeAsync(ct);
        }
    }

    public async Task ConnectAtStartupAsync(CancellationToken ct)
    {
        var targets = Supervisors.Where(s => s.Drive.Mount.AutoConnectAtStart && !s.Snapshot.OwnedByOtherSession).ToList();
        using var throttle = new SemaphoreSlim(3, 3);
        var tasks = targets.Select(async s =>
        {
            await throttle.WaitAsync(ct);
            try { await s.ConnectAsync(interactive: false, ct); }
            catch (Exception ex) { _log.Error("manager", $"startup connect failed for {s.Drive.Name}", ex); }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private DriveSupervisor CreateSupervisor(Drive drive)
    {
        var sup = new DriveSupervisor(drive, _engine, _credentials, _knownHosts, _scanner, _ui, () => _settings.Current, _paths, _log, _redactor, _shell, _locks, _proxy, ProxyPassword);
        sup.SnapshotChanged += (s, snap) => SnapshotChanged?.Invoke(s, snap);
        sup.PersistRequested += s => { try { _repo.Save(); } catch (Exception ex) { _log.Error("manager", "persist failed", ex); } };
        lock (_gate) _supervisors[drive.Id] = sup;
        SupervisorAdded?.Invoke(sup);
        return sup;
    }

    private string? ProxyPassword() => _store.Get(CredentialFields.ProxyTarget);

    /// <summary>Add a validated drive; secrets are saved according to the drive's credential policy.</summary>
    public async Task<DriveSupervisor> AddAsync(Drive drive, DriveSecrets? secrets, bool rememberForSession, CancellationToken ct)
    {
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        _repo.Upsert(drive);
        if (secrets is not null) _credentials.Save(drive, secrets, rememberForSession);
        var sup = CreateSupervisor(drive);
        await sup.InitializeAsync(ct);
        return sup;
    }

    /// <summary>Apply an edit. Identity changes are gated by the unsynced-data guard (FR-22.7).</summary>
    public async Task<bool> UpdateAsync(Drive edited, DriveSecrets? newSecrets, bool rememberForSession, bool interactive, CancellationToken ct)
    {
        var sup = Get(edited.Id) ?? throw new InvalidOperationException("unknown drive");
        if (!sup.Snapshot.CanEdit) return false;
        var current = sup.Drive;
        var identityChanged = RemoteIdentity.ComputeHash(edited) != RemoteIdentity.ComputeHash(current);
        var cacheModeUnsafe = (edited.Advanced.VfsCacheMode ?? VfsCacheMode.Full) is VfsCacheMode.Off or VfsCacheMode.Minimal
                              && (current.Advanced.VfsCacheMode ?? VfsCacheMode.Full) is VfsCacheMode.Writes or VfsCacheMode.Full;
        if (identityChanged || cacheModeUnsafe)
        {
            var report = await sup.InspectOfflineAsync(ct);
            var op = identityChanged ? DestructiveOperation.ChangeIdentity : DestructiveOperation.ChangeCacheModeToUnsafe;
            var decision = UnsyncedDataGuard.Evaluate(op, report.State);
            if (decision is GuardDecision.RequireChoice rc)
            {
                if (!interactive) return false;
                var choice = await _ui.ChooseUnsyncedActionAsync(current, op, report.State, rc.Choices, rc.Reason, ct);
                switch (choice)
                {
                    case GuardChoice.Discard:
                        if (!await sup.DiscardUnsyncedDataAsync(ct)) return false;
                        break;
                    case GuardChoice.ConnectAndUpload:
                        _ = sup.ConnectAsync(interactive: true, ct);
                        return false;
                    default:
                        return false;
                }
            }
            else if (decision is GuardDecision.Deny)
            {
                return false;
            }
        }

        CopyInto(edited, current);
        _repo.Upsert(current);
        if (current.CredentialPolicy == CredentialPolicy.Store)
        {
            if (newSecrets is not null) _credentials.Save(current, newSecrets, rememberForSession);
        }
        else
        {
            _credentials.DeleteStored(current.Id);
            if (newSecrets is not null) _credentials.Save(current, newSecrets, rememberForSession);
        }
        await sup.InitializeAsync(ct);
        return true;
    }

    private static void CopyInto(Drive from, Drive to)
    {
        to.Name = from.Name;
        to.StorageType = from.StorageType;
        to.Preset = from.Preset;
        to.Mount = from.Mount;
        to.WebDav = from.WebDav;
        to.Sftp = from.Sftp;
        to.CredentialPolicy = from.CredentialPolicy;
        to.Advanced = from.Advanced;
        to.RemoteIdentityHash = RemoteIdentity.ComputeHash(from);
    }

    /// <summary>Delete a drive. Returns false when cancelled or blocked by the guard.</summary>
    public async Task<bool> DeleteAsync(Guid id, bool interactive, CancellationToken ct)
    {
        var sup = Get(id);
        if (sup is null) return false;
        if (sup.Snapshot.Lifecycle is LifecycleState.Mounted or LifecycleState.Connecting or LifecycleState.Reconnecting)
        {
            if (!await sup.DisconnectAsync(interactive, null, ct)) return false;
        }
        var report = await sup.InspectOfflineAsync(ct);
        var decision = UnsyncedDataGuard.Evaluate(DestructiveOperation.Delete, report.State);
        if (decision is GuardDecision.RequireChoice rc)
        {
            if (!interactive) return false;
            var choice = await _ui.ChooseUnsyncedActionAsync(sup.Drive, DestructiveOperation.Delete, report.State, rc.Choices, rc.Reason, ct);
            switch (choice)
            {
                case GuardChoice.Discard:
                    if (!await sup.DiscardUnsyncedDataAsync(ct)) return false;
                    break;
                case GuardChoice.ConnectAndUpload:
                    _ = sup.ConnectAsync(interactive: true, ct);
                    return false;
                default:
                    return false;
            }
        }
        else if (decision is GuardDecision.Deny)
        {
            return false;
        }

        var drive = sup.Drive;
        await sup.DisposeAsync();
        lock (_gate) _supervisors.Remove(id);
        _credentials.DeleteAll(id);
        try { await _engine.DeleteCacheAsync(drive, _paths.CacheDirFor(drive, _settings.Current), ct); }
        catch (Exception ex) { _log.Warn("manager", "cache delete failed: " + ex.Message); }
        try { if (drive.Mount.Kind == MountKind.DriveLetter && !string.IsNullOrEmpty(drive.Mount.DriveLetter)) _shell?.RemoveDriveIcon(drive.Mount.DriveLetter[0]); } catch { }
        _repo.Remove(id);
        SupervisorRemoved?.Invoke(sup);
        return true;
    }

    /// <summary>Exit flow: aggregate live sync states, ask once, then stop everything. Returns false when cancelled.</summary>
    public async Task<bool> PrepareExitAsync(bool interactive, CancellationToken ct)
    {
        var mounted = Supervisors.Where(s => s.Snapshot.Lifecycle == LifecycleState.Mounted).ToList();
        if (mounted.Count == 0)
        {
            foreach (var s in Supervisors.Where(s => s.Snapshot.Lifecycle is LifecycleState.Reconnecting or LifecycleState.Connecting))
                await s.DisconnectAsync(false, StopPolicy.StopAndRetainCache, ct);
            return true;
        }
        var pendingCount = 0;
        var pendingBytes = 0L;
        var unknown = false;
        foreach (var s in mounted)
        {
            var st = s.Snapshot.Sync.Source == SyncSource.Live ? s.Snapshot.Sync : SyncState.Unknown(SyncSource.None);
            if (st.Kind == SyncKind.Unknown) unknown = true;
            pendingCount += st.PendingCount;
            pendingBytes += st.PendingBytes;
        }
        var aggregate = unknown ? SyncState.Unknown(SyncSource.Live) : pendingCount > 0 ? SyncState.Pending(SyncSource.Live, pendingCount, pendingBytes) : SyncState.Clean(SyncSource.Live);
        var decision = UnsyncedDataGuard.Evaluate(DestructiveOperation.Exit, aggregate);
        var policy = StopPolicy.StopAndRetainCache;
        if (decision is GuardDecision.RequireChoice rc)
        {
            var choice = interactive
                ? await _ui.ChooseUnsyncedActionAsync(mounted[0].Drive, DestructiveOperation.Exit, aggregate, rc.Choices, rc.Reason, ct)
                : GuardChoice.StopAndRetain;
            switch (choice)
            {
                case GuardChoice.Cancel: return false;
                case GuardChoice.WaitForUploads: policy = StopPolicy.WaitForUploads; break;
            }
        }
        foreach (var s in mounted)
        {
            var ok = await s.DisconnectAsync(interactive, policy, ct);
            if (!ok) return false;
        }
        foreach (var s in Supervisors.Where(s => s.Snapshot.Lifecycle is LifecycleState.Reconnecting or LifecycleState.Connecting))
            await s.DisconnectAsync(false, StopPolicy.StopAndRetainCache, ct);
        return true;
    }

    public async Task DisconnectAllAsync(bool interactive, CancellationToken ct)
    {
        foreach (var s in Supervisors.Where(s => s.Snapshot.CanDisconnect))
            await s.DisconnectAsync(interactive, null, ct);
    }

    public async Task ConnectAllAsync(CancellationToken ct)
    {
        foreach (var s in Supervisors.Where(s => s.Snapshot.CanConnect))
            await s.ConnectAsync(interactive: true, ct);
    }

    public void OnNetworkAvailabilityChanged(bool available)
    {
        foreach (var s in Supervisors) s.OnNetworkAvailabilityChanged(available);
    }

    public void OnResumeOrUnlock()
    {
        foreach (var s in Supervisors) s.OnResumeOrUnlock();
    }

    /// <summary>True when any mounted drive still has (or may have) unsynced data — used by the shutdown blocker.</summary>
    public bool HasUnsyncedData() => Supervisors.Any(s => s.Snapshot.Lifecycle == LifecycleState.Mounted && s.Snapshot.Sync.RequiresProtection);

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Supervisors) await s.DisposeAsync();
    }
}
