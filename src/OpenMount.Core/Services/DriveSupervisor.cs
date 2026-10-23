using System.Diagnostics;
using OpenMount.Core.Engine;
using OpenMount.Core.Errors;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.State;

namespace OpenMount.Core.Services;

/// <summary>
/// Owns one drive: keeps the three-dimensional snapshot (lifecycle / remote health / sync state), orchestrates
/// connect, disconnect, probing and reconnect, and routes every destructive step through <see cref="UnsyncedDataGuard"/>.
/// Engine details (processes, RC, WinFsp) never leak above this class; the UI only consumes <see cref="DriveSnapshot"/>.
/// </summary>
public sealed class DriveSupervisor : IAsyncDisposable
{
    /// <summary>Reconnect backoff sequence (FR-09.3). Settable so tests can shorten it.</summary>
    public static TimeSpan[] ReconnectDelays { get; set; } =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
    };

    private readonly Drive _drive;
    private readonly IMountEngine _engine;
    private readonly CredentialResolver _credentials;
    private readonly KnownHostsFile _knownHosts;
    private readonly IHostKeyScanner _scanner;
    private readonly IUserInteraction _ui;
    private readonly Func<AppSettings> _settings;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly Redactor _redactor;
    private readonly IShellIntegration? _shell;
    private readonly ISessionLockFactory _locks;
    private readonly IProxyResolver _proxy;
    private readonly Func<string?> _proxyPassword;

    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _snapshotGate = new();
    private DriveSnapshot _snapshot;
    private IMountSession? _session;
    private ISessionLock? _sessionLock;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _reconnectCts;
    private bool _autoReconnectPausedUntilManual;
    private bool _intentionalStop;
    private volatile bool _networkAvailable = true;
    private int _probeFailures;

    public DriveSupervisor(
        Drive drive,
        IMountEngine engine,
        CredentialResolver credentials,
        KnownHostsFile knownHosts,
        IHostKeyScanner scanner,
        IUserInteraction ui,
        Func<AppSettings> settings,
        AppPaths paths,
        IAppLog log,
        Redactor redactor,
        IShellIntegration? shell,
        ISessionLockFactory locks,
        IProxyResolver proxy,
        Func<string?> proxyPassword)
    {
        _drive = drive;
        _engine = engine;
        _credentials = credentials;
        _knownHosts = knownHosts;
        _scanner = scanner;
        _ui = ui;
        _settings = settings;
        _paths = paths;
        _log = log;
        _redactor = redactor;
        _shell = shell;
        _locks = locks;
        _proxy = proxy;
        _proxyPassword = proxyPassword;
        _snapshot = DriveSnapshot.Initial(drive.Id);
    }

    public Drive Drive => _drive;
    public DriveSnapshot Snapshot { get { lock (_snapshotGate) return _snapshot; } }

    public event Action<DriveSupervisor, DriveSnapshot>? SnapshotChanged;
    /// <summary>Raised when drive metadata (last status / recovery record) should be persisted.</summary>
    public event Action<DriveSupervisor>? PersistRequested;

    private string Src => "drive:" + _drive.ShortId;

    // ------------------------------------------------------------------ initialisation

    /// <summary>Offline scan + other-session check. Called at startup and after edits.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var settings = _settings();
        var cacheDir = _paths.CacheDirFor(_drive, settings);
        var report = await SafeInspectAsync(cacheDir, abnormalStop: _drive.Last?.State == nameof(LifecycleState.Mounted), ct);
        var owned = false;
        if (_sessionLock is null)
        {
            using var probe = _locks.TryAcquire(_paths.SessionLockFileFor(_drive));
            owned = probe is null;
        }
        Update(s => s with { Sync = report.State, OwnedByOtherSession = owned, Error = report.IdentityMatches ? s.Error : MountError.Create(MountErrorKind.CacheIdentityMismatch, report.IdentityHashFound ?? string.Empty) });
        UpdateRecoveryRecord(report.State, report.IdentityMatches);
    }

    // ------------------------------------------------------------------ connect

    public async Task ConnectAsync(bool interactive, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            var snap = Snapshot;
            if (!(snap.CanConnect || snap.Lifecycle == LifecycleState.Reconnecting)) return;
            if (interactive) _autoReconnectPausedUntilManual = false;

            _sessionLock ??= _locks.TryAcquire(_paths.SessionLockFileFor(_drive));
            if (_sessionLock is null)
            {
                Update(s => s with { OwnedByOtherSession = true, Lifecycle = LifecycleState.Disconnected });
                return;
            }

            var settings = _settings();
            var cacheDir = _paths.CacheDirFor(_drive, settings);

            var report = await SafeInspectAsync(cacheDir, abnormalStop: false, ct);
            Update(s => s with { Sync = report.State, OwnedByOtherSession = false });
            if (!report.IdentityMatches)
            {
                SetError(MountError.Create(MountErrorKind.CacheIdentityMismatch, "cache identity: " + (report.IdentityHashFound ?? "?") + "\ndrive identity: " + RemoteIdentity.ComputeHash(_drive)));
                ReleaseLock();
                return;
            }

            // Credentials (policy aware; never saves unless the policy says so).
            var proxyPassword = _proxyPassword();
            var secrets = _credentials.Resolve(_drive, proxyPassword);
            if (secrets is null)
            {
                if (!interactive)
                {
                    Update(s => s with { Lifecycle = LifecycleState.NeedsCredentials, Error = null, NextRetryAt = null });
                    _ui.Notify(_drive.Name, "需要登录后才能连接", NotificationKind.Warning);
                    ReleaseLock();
                    return;
                }
                var prompt = await _ui.PromptCredentialsAsync(_drive, ct);
                if (prompt is null)
                {
                    _autoReconnectPausedUntilManual = true;
                    Update(s => s with { Lifecycle = LifecycleState.Disconnected, Error = null });
                    ReleaseLock();
                    return;
                }
                _credentials.Save(_drive, prompt.Secrets, prompt.RememberForSession);
                secrets = prompt.Secrets with { ProxyPassword = proxyPassword };
            }
            foreach (var s in secrets.All()) _redactor.RegisterSecret(s);

            var proxy = _proxy.ResolveFor(_drive, settings, proxyPassword);

            // SFTP trust-on-first-use, using the same endpoint context (incl. proxy) as the mount.
            if (_drive.StorageType == StorageType.Sftp && _drive.Sftp is { HostKeyCheck: HostKeyCheckMode.Tofu } sftp
                && !_knownHosts.HasEntries(sftp.Host, sftp.Port))
            {
                if (!interactive)
                {
                    Update(s => s with { Lifecycle = LifecycleState.NeedsHostKeyConfirm, Error = null });
                    _ui.Notify(_drive.Name, "首次连接需要确认服务器主机密钥", NotificationKind.Warning);
                    ReleaseLock();
                    return;
                }
                var endpoint = SshEndpointResolver.Resolve(_drive, settings, proxy);
                IReadOnlyList<HostKeyEntry> keys;
                try
                {
                    keys = await _scanner.ScanAsync(endpoint, ct);
                }
                catch (NotSupportedException ex)
                {
                    SetError(MountError.Create(MountErrorKind.HostKeyScanUnavailable, ex.Message));
                    ReleaseLock();
                    return;
                }
                catch (OperationCanceledException) { Update(s => s with { Lifecycle = LifecycleState.Disconnected }); ReleaseLock(); return; }
                catch (Exception ex)
                {
                    SetError(ErrorClassifier.Classify(new[] { _redactor.Redact(ex.Message) }, null, ClassifyPhase.Startup));
                    ReleaseLock();
                    return;
                }
                if (keys.Count == 0)
                {
                    SetError(MountError.Create(MountErrorKind.Unreachable, "host key scan returned no keys"));
                    ReleaseLock();
                    return;
                }
                if (!await _ui.ConfirmHostKeysAsync(_drive, endpoint, keys, isChange: false, ct))
                {
                    Update(s => s with { Lifecycle = LifecycleState.Disconnected, Error = null });
                    ReleaseLock();
                    return;
                }
                _knownHosts.Trust(sftp.Host, sftp.Port, keys);
            }

            Update(s => s with { Lifecycle = LifecycleState.Connecting, Error = null, NextRetryAt = null });
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            MountStartResult result;
            try
            {
                var request = new MountRequest(_drive, secrets, settings, cacheDir, _paths.RunDirFor(_drive), _knownHosts.Path);
                result = await _engine.StartAsync(request, null, _connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                Update(s => s with { Lifecycle = LifecycleState.Disconnected, Error = null });
                ReleaseLock();
                return;
            }
            catch (Exception ex)
            {
                _log.Error(Src, "engine start threw", ex);
                HandleStartFailure(MountError.Create(MountErrorKind.Unknown, _redactor.Redact(ex.ToString())), interactive);
                return;
            }
            finally
            {
                _connectCts?.Dispose();
                _connectCts = null;
            }

            switch (result)
            {
                case MountStartResult.Ready ready when IsConfigurationFailure(ready.InitialHealth.Health):
                {
                    // The mount came up but the very first real access shows a configuration problem (bad credentials,
                    // no permission, missing path). Keeping an unusable mount helps nobody: stop it (cache retained) and
                    // surface it as a connect failure so the user gets the error / credential dialog (FR-23.1 note).
                    var kind = ready.InitialHealth.Health switch
                    {
                        RemoteHealth.AuthRequired => MountErrorKind.Auth,
                        RemoteHealth.Forbidden => MountErrorKind.Forbidden,
                        _ => MountErrorKind.NotFound,
                    };
                    _log.Warn(Src, $"mounted but first remote access failed with {ready.InitialHealth.Health}; stopping");
                    try { await ready.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None); } catch { }
                    try { await ready.Session.DisposeAsync(); } catch { }
                    HandleStartFailure(MountError.Create(kind, _redactor.Redact(ready.InitialHealth.Error ?? string.Empty)), interactive);
                    break;
                }
                case MountStartResult.Ready ready:
                    _session = ready.Session;
                    _intentionalStop = false;
                    _session.Exited += OnSessionExited;
                    _probeFailures = 0;
                    var info = ready.Session.Info;
                    Update(s => s with
                    {
                        Lifecycle = LifecycleState.Mounted,
                        Mount = new MountInfo(info.MountPoint, info.ProcessId, info.RcPort, info.CacheDir, info.StartedAt),
                        RemoteHealth = ready.InitialHealth.Health,
                        Error = null,
                        ReconnectAttempt = 0,
                        NextRetryAt = null,
                        LastProbeAt = DateTimeOffset.Now,
                        StopProgress = null,
                    });
                    _log.Info(Src, $"mounted at {info.MountPoint}; remote={ready.InitialHealth.Health}");
                    AfterMounted(settings, interactive);
                    _ = RefreshLiveStateAsync(CancellationToken.None);
                    StartLoop();
                    break;
                case MountStartResult.Failed failed:
                    _log.Warn(Src, $"mount failed: {failed.Error.Kind} {failed.Error.Title}");
                    HandleStartFailure(failed.Error, interactive);
                    break;
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    public void CancelConnect() => _connectCts?.Cancel();

    /// <summary>Remote states that cannot be fixed by waiting; Unreachable is transient and keeps the mount (FR-23.1).</summary>
    private static bool IsConfigurationFailure(RemoteHealth health) =>
        health is RemoteHealth.AuthRequired or RemoteHealth.Forbidden or RemoteHealth.NotFound;

    private void HandleStartFailure(MountError error, bool interactive)
    {
        ReleaseLock();
        if (error.Kind == MountErrorKind.Auth && _drive.CredentialPolicy != CredentialPolicy.Store)
        {
            _credentials.DeleteAll(_drive.Id);
            Update(s => s with { Lifecycle = LifecycleState.NeedsCredentials, Error = error, NextRetryAt = null });
            return;
        }
        if (error.Kind == MountErrorKind.HostKeyUnknown)
        {
            Update(s => s with { Lifecycle = LifecycleState.NeedsHostKeyConfirm, Error = error, NextRetryAt = null });
            return;
        }
        var canAuto = !interactive && _drive.Mount.AutoReconnect && error.AllowsAutoReconnect && !_autoReconnectPausedUntilManual;
        if (canAuto)
        {
            Update(s => s with { Lifecycle = LifecycleState.Reconnecting, Error = error });
            ScheduleReconnect();
        }
        else
        {
            SetError(error);
        }
    }

    private void AfterMounted(AppSettings settings, bool interactive)
    {
        try
        {
            if (_drive.Mount.Kind == MountKind.DriveLetter && _shell is not null && !string.IsNullOrEmpty(_drive.Mount.DriveLetter))
            {
                var letter = _drive.Mount.DriveLetter![0];
                if (settings.General.UseStorageIcons) _shell.SetDriveIcon(letter, _drive.StorageType, _drive.Preset);
                _shell.NotifyMountPointChanged(_drive.Mount.ToMountPoint(), added: true, network: _drive.Mount.NetworkMode);
            }
            // Only a connect the user asked for opens a window (FR-06): auto-connect at startup and auto-reconnect stay silent.
            var open = _drive.Mount.OpenExplorer ?? settings.General.OpenExplorerOnConnect;
            if (open && interactive) _shell?.OpenInExplorer(_drive.Mount.ToMountPoint());
            if (settings.General.Notifications) _ui.Notify(_drive.Name, "已连接", NotificationKind.Success);
        }
        catch (Exception ex)
        {
            _log.Warn(Src, "shell integration failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ disconnect

    /// <summary>Returns true when the drive ended up disconnected; false when the user cancelled.</summary>
    public async Task<bool> DisconnectAsync(bool interactive, StopPolicy? forcedPolicy = null, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            var snap = Snapshot;
            if (snap.Lifecycle == LifecycleState.Connecting)
            {
                _connectCts?.Cancel();
                return true;
            }
            if (snap.Lifecycle == LifecycleState.Reconnecting)
            {
                StopReconnect();
                Update(s => s with { Lifecycle = LifecycleState.Disconnected, NextRetryAt = null, ReconnectAttempt = 0 });
                ReleaseLock();
                return true;
            }
            if (_session is null || snap.Lifecycle != LifecycleState.Mounted) return true;

            StopLoop();
            var policy = forcedPolicy;
            if (policy is null)
            {
                var live = await _session.QuerySyncStateAsync(ct);
                Update(s => s with { Sync = live });
                var decision = UnsyncedDataGuard.Evaluate(DestructiveOperation.Disconnect, live);
                if (decision is GuardDecision.RequireChoice rc)
                {
                    var choice = interactive
                        ? await _ui.ChooseUnsyncedActionAsync(_drive, DestructiveOperation.Disconnect, live, rc.Choices, rc.Reason, ct)
                        : GuardChoice.StopAndRetain;
                    switch (choice)
                    {
                        case GuardChoice.Cancel:
                            StartLoop();
                            return false;
                        case GuardChoice.WaitForUploads:
                            policy = StopPolicy.WaitForUploads;
                            break;
                        default:
                            policy = StopPolicy.StopAndRetainCache;
                            break;
                    }
                }
                else
                {
                    policy = StopPolicy.StopAndRetainCache;
                }
            }

            Update(s => s with { Lifecycle = LifecycleState.Disconnecting });
            StopResult? result;
            if (policy == StopPolicy.WaitForUploads)
            {
                result = await WaitForUploadsThenStopAsync(interactive, ct);
                if (result is null)
                {
                    Update(s => s with { Lifecycle = LifecycleState.Mounted, StopProgress = null });
                    StartLoop();
                    return false;
                }
            }
            else
            {
                _intentionalStop = true;
                result = await _session.StopAsync(StopPolicy.StopAndRetainCache, null, ct);
            }
            await FinishStopAsync(result);
            return true;
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task<StopResult?> WaitForUploadsThenStopAsync(bool interactive, CancellationToken ct)
    {
        var session = _session!;
        await Task.Delay(1500, ct); // closes are delivered asynchronously; let the queue settle before judging it
        try { await session.ExpediteUploadsAsync(ct); } catch (Exception ex) { _log.Debug(Src, "expedite failed: " + ex.Message); }
        var sw = Stopwatch.StartNew();
        var lastChange = sw.Elapsed;
        var lastCount = -1;
        var lastBytes = -1L;
        var cleanStreak = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var st = await session.QuerySyncStateAsync(ct);
            if (st.Kind == SyncKind.Unknown) break;
            if (!st.RequiresProtection)
            {
                if (++cleanStreak >= 2) break; // clean twice in a row: nothing left to upload
                await Task.Delay(1500, ct);
                continue;
            }
            cleanStreak = 0;
            if (st.PendingCount != lastCount || st.PendingBytes != lastBytes)
            {
                lastCount = st.PendingCount;
                lastBytes = st.PendingBytes;
                lastChange = sw.Elapsed;
            }
            var progress = new StopProgressInfo(st.PendingCount, st.PendingBytes, st.UploadingCount, sw.Elapsed);
            Update(s => s with { Sync = st, StopProgress = progress });
            if (sw.Elapsed - lastChange > TimeSpan.FromSeconds(60))
            {
                var choice = interactive ? await _ui.ChooseWhenNoProgressAsync(_drive, progress, ct) : GuardChoice.StopAndRetain;
                if (choice == GuardChoice.Cancel) return null;
                if (choice == GuardChoice.StopAndRetain) break;
                lastChange = sw.Elapsed; // keep waiting
            }
            await Task.Delay(2000, ct);
        }
        _intentionalStop = true;
        return await session.StopAsync(StopPolicy.StopAndRetainCache, null, ct);
    }

    private async Task FinishStopAsync(StopResult result)
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.Exited -= OnSessionExited;
            try { await session.DisposeAsync(); } catch { }
        }
        AfterUnmounted();
        Update(s => s with
        {
            Lifecycle = LifecycleState.Disconnected,
            Mount = null,
            Sync = result.SyncStateAfterStop,
            RemoteHealth = RemoteHealth.Unknown,
            StopProgress = null,
            Error = result.Error,
            Space = null,
        });
        UpdateRecoveryRecord(result.SyncStateAfterStop, identityMatches: true);
        if (result.SyncStateAfterStop.RequiresProtection && _settings().General.Notifications)
            _ui.Notify(_drive.Name, $"已断开，保留了 {result.SyncStateAfterStop.PendingCount} 个待上传文件", NotificationKind.Warning);
        ReleaseLock();
    }

    private void AfterUnmounted()
    {
        try
        {
            if (_drive.Mount.Kind == MountKind.DriveLetter && _shell is not null && !string.IsNullOrEmpty(_drive.Mount.DriveLetter))
            {
                _shell.RemoveDriveIcon(_drive.Mount.DriveLetter![0]);
                _shell.NotifyMountPointChanged(_drive.Mount.ToMountPoint(), added: false, network: _drive.Mount.NetworkMode);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(Src, "shell cleanup failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ unexpected exit / mount loss

    private void OnSessionExited(object? sender, SessionExitedEventArgs e)
    {
        if (e.Intentional || _intentionalStop) return;
        _ = Task.Run(() => HandleMountLostAsync(ErrorClassifier.Classify(e.RedactedTail.Split('\n'), e.ExitCode, ClassifyPhase.Running)));
    }

    private async Task HandleMountLostAsync(MountError error)
    {
        await _opLock.WaitAsync();
        try
        {
            if (_session is null || _intentionalStop) return;
            StopLoop();
            var session = _session;
            _session = null;
            session.Exited -= OnSessionExited;
            try { await session.DisposeAsync(); } catch { }
            AfterUnmounted();
            _log.Warn(Src, $"mount lost: {error.Kind} {error.Title}" + (string.IsNullOrWhiteSpace(error.TechnicalDetails) ? string.Empty : " | " + error.TechnicalDetails.Replace('\n', ' ')));

            var report = await SafeInspectAsync(_paths.CacheDirFor(_drive, _settings()), abnormalStop: true, CancellationToken.None);
            UpdateRecoveryRecord(report.State, report.IdentityMatches);

            var canAuto = _drive.Mount.AutoReconnect && error.AllowsAutoReconnect && !_autoReconnectPausedUntilManual;
            Update(s => s with
            {
                Lifecycle = canAuto ? LifecycleState.Reconnecting : LifecycleState.Error,
                Mount = null,
                Sync = report.State,
                RemoteHealth = RemoteHealth.Unknown,
                Error = error,
                Space = null,
            });
            if (_settings().General.Notifications) _ui.Notify(_drive.Name, canAuto ? "连接意外断开，正在自动重连" : "连接意外断开：" + error.Title, NotificationKind.Warning);
            if (canAuto) ScheduleReconnect();
            else ReleaseLock();
        }
        finally
        {
            _opLock.Release();
        }
    }

    // ------------------------------------------------------------------ reconnect backoff

    private void ScheduleReconnect()
    {
        StopReconnect();
        var attempt = Snapshot.ReconnectAttempt + 1;
        var delays = ReconnectDelays;
        var delay = delays[Math.Min(attempt - 1, delays.Length - 1)];
        // +-15% jitter (FR-09.8) so many drives do not retry in lockstep; sub-5 s delays (tests) are left exact.
        if (delay >= TimeSpan.FromSeconds(5)) delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.85 + Random.Shared.NextDouble() * 0.3));
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        Update(s => s with { Lifecycle = LifecycleState.Reconnecting, ReconnectAttempt = attempt, NextRetryAt = DateTimeOffset.Now + delay });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                while (!_networkAvailable) await Task.Delay(1000, cts.Token); // pause while offline; TriggerReconnectNow resumes
                await ConnectAsync(interactive: false, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error(Src, "reconnect failed", ex); }
        });
    }

    private void StopReconnect()
    {
        var cts = _reconnectCts;
        _reconnectCts = null;
        if (cts is null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    /// <summary>Network came back: retry immediately when reconnecting, probe when mounted.</summary>
    public void OnNetworkAvailabilityChanged(bool available)
    {
        _networkAvailable = available;
        if (!available) return;
        var snap = Snapshot;
        if (snap.Lifecycle == LifecycleState.Reconnecting && _reconnectCts is not null)
        {
            StopReconnect();
            _ = Task.Run(() => ConnectAsync(interactive: false));
        }
        else if (snap.Lifecycle == LifecycleState.Mounted)
        {
            _ = ProbeNowAsync(CancellationToken.None);
        }
    }

    public void OnResumeOrUnlock() => _ = Task.Run(async () =>
    {
        await Task.Delay(5000);
        await ProbeNowAsync(CancellationToken.None);
    });

    // ------------------------------------------------------------------ probing loop

    private void StartLoop()
    {
        StopLoop();
        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var health = Snapshot.RemoteHealth;
                    var seconds = health == RemoteHealth.Healthy ? 60 : Math.Min(15 * (1 << Math.Min(_probeFailures, 2)), 60);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                    await ProbeNowAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error(Src, "probe loop stopped", ex); }
        });
    }

    private void StopLoop()
    {
        var cts = _loopCts;
        _loopCts = null;
        if (cts is null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    private int _aliveFailuresTotal;

    private static async Task<AliveCheckResult> SafeCheckAliveAsync(IMountSession session, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return await session.CheckAliveAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return AliveCheckResult.Failed("check", ex.GetType().Name + ": " + ex.Message, sw.Elapsed); }
    }

    /// <summary>What the drive looked like when a check failed: uptime, last remote probe, sync state, recovered glitches so far.</summary>
    private string DescribeMountContext(IMountSession session)
    {
        var s = Snapshot;
        var uptime = DateTimeOffset.UtcNow - session.Info.StartedAt;
        var lastProbe = s.LastProbeAt is { } at ? $"{(DateTimeOffset.Now - at).TotalSeconds:F0} s ago" : "never";
        return $"context: pid {session.Info.ProcessId}, rc port {session.Info.RcPort}, mounted {uptime.TotalMinutes:F1} min ago, " +
               $"remote={s.RemoteHealth} (probed {lastProbe}), sync={s.Sync.Kind}, network={(_networkAvailable ? "up" : "down")}, " +
               $"transient failures recovered so far={_aliveFailuresTotal}";
    }

    /// <summary>Pause between a failed lifecycle check and the confirming re-check (tests shorten it).</summary>
    public static TimeSpan AliveRecheckDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Lifecycle health check + real remote access. Remote failures never trigger a remount.</summary>
    public async Task ProbeNowAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null || Snapshot.Lifecycle != LifecycleState.Mounted) return;
        var first = await SafeCheckAliveAsync(session, ct);
        if (!first.Alive)
        {
            if (!session.IsAlive) return; // the Exited handler takes over
            // One failed check is not a lost mount: RC can be slow under load and a volume query can fail transiently
            // (2026-10-23: a healthy mount was torn down after a single failure). Confirm before acting.
            _log.Warn(Src, $"alive check failed ({first}); re-checking in {AliveRecheckDelay.TotalSeconds:F0} s. {DescribeMountContext(session)}");
            try { await Task.Delay(AliveRecheckDelay, ct); }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(_session, session) || Snapshot.Lifecycle != LifecycleState.Mounted) return;
            var second = await SafeCheckAliveAsync(session, ct);
            if (second.Alive)
            {
                _log.Info(Src, $"alive check recovered ({second}); keeping the mount");
                _aliveFailuresTotal++;
            }
            else
            {
                if (!session.IsAlive) return;
                var technical = $"first: {first}; second: {second}; {DescribeMountContext(session)}";
                _log.Warn(Src, "mount point lost while process alive; treating as mount loss. " + technical);
                _ = HandleMountLostAsync(MountError.Create(MountErrorKind.Crashed, technical));
                return;
            }
        }
        var previous = Snapshot.RemoteHealth;
        RemoteHealthResult result;
        try { result = await session.ProbeRemoteAsync(TimeSpan.FromSeconds(_settings().Advanced.RemoteProbeTimeoutSeconds), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { result = new RemoteHealthResult(RemoteHealth.Unreachable, ex.Message, TimeSpan.Zero); }
        _probeFailures = result.Health == RemoteHealth.Healthy ? 0 : _probeFailures + 1;
        Update(s => s with { RemoteHealth = result.Health, LastProbeAt = DateTimeOffset.Now });
        if (previous != RemoteHealth.Healthy && previous != RemoteHealth.Unknown && result.Health == RemoteHealth.Healthy && _settings().General.Notifications)
            _ui.Notify(_drive.Name, "远端已恢复可访问", NotificationKind.Success);
        if (previous == RemoteHealth.Healthy && result.Health != RemoteHealth.Healthy)
            _log.Warn(Src, $"remote degraded: {result.Health} {_redactor.Redact(result.Error ?? string.Empty)}");
        await RefreshLiveStateAsync(ct);
    }

    private async Task RefreshLiveStateAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var sync = await session.QuerySyncStateAsync(ct);
            SpaceInfo? space = null;
            try { space = await session.QuerySpaceAsync(ct); } catch { }
            Update(s => s with { Sync = sync, Space = space ?? s.Space });
        }
        catch (Exception ex)
        {
            _log.Debug(Src, "live state refresh failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ cache operations

    /// <summary>Separately named destructive command. Requires the drive to be disconnected.</summary>
    public async Task<bool> DiscardUnsyncedDataAsync(CancellationToken ct)
    {
        await _opLock.WaitAsync(ct);
        try
        {
            if (Snapshot.Lifecycle is not (LifecycleState.Disconnected or LifecycleState.Error or LifecycleState.NeedsCredentials or LifecycleState.NeedsHostKeyConfirm)) return false;
            var cacheDir = _paths.CacheDirFor(_drive, _settings());
            var report = await SafeInspectAsync(cacheDir, abnormalStop: false, ct);
            if (!await _ui.ConfirmDiscardAsync(_drive, report.State, ct)) return false;
            await _engine.DiscardUnsyncedDataAsync(_drive, cacheDir, ct);
            _log.Warn(Src, $"user discarded unsynced data ({report.State.PendingCount} files)");
            var after = await SafeInspectAsync(cacheDir, abnormalStop: false, ct);
            Update(s => s with { Sync = after.State, Error = null });
            UpdateRecoveryRecord(after.State, after.IdentityMatches);
            return true;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public Task<OfflineSyncReport> InspectOfflineAsync(CancellationToken ct) =>
        SafeInspectAsync(_paths.CacheDirFor(_drive, _settings()), abnormalStop: false, ct);

    private async Task<OfflineSyncReport> SafeInspectAsync(string cacheDir, bool abnormalStop, CancellationToken ct)
    {
        try
        {
            var r = await _engine.InspectOfflineAsync(_drive, cacheDir, ct);
            if (abnormalStop && r.State.Kind != SyncKind.Unknown)
                return r with { State = r.State with { AbnormalStop = true } };
            return r;
        }
        catch (Exception ex)
        {
            _log.Warn(Src, "offline inspect failed: " + ex.Message);
            return new OfflineSyncReport(SyncState.Unknown(SyncSource.Offline, ex.Message, abnormalStop), true, null, 0);
        }
    }

    private void UpdateRecoveryRecord(SyncState state, bool identityMatches)
    {
        var snap = Snapshot;
        _drive.Last = new LastStatus
        {
            State = snap.Lifecycle.ToString(),
            RemoteHealth = snap.RemoteHealth.ToString(),
            SyncState = state.Kind.ToString(),
            Error = snap.Error?.Title,
            At = DateTimeOffset.UtcNow,
        };
        _drive.Recovery = state.RequiresProtection || !identityMatches
            ? new RecoveryInfo
            {
                Reason = !identityMatches ? "identityMismatch" : (state.Kind == SyncKind.Unknown ? "unknown" : "pendingUploads"),
                PendingCount = state.PendingCount,
                PendingBytes = state.PendingBytes,
                IdentityHash = _drive.RemoteIdentityHash,
                AbnormalStop = state.AbnormalStop,
                Since = _drive.Recovery?.Since ?? DateTimeOffset.UtcNow,
            }
            : null;
        PersistRequested?.Invoke(this);
    }

    // ------------------------------------------------------------------ helpers

    private void SetError(MountError error)
    {
        Update(s => s with { Lifecycle = LifecycleState.Error, Error = error, NextRetryAt = null, Mount = null });
    }

    private void ReleaseLock()
    {
        var l = _sessionLock;
        _sessionLock = null;
        try { l?.Dispose(); } catch { }
    }

    private void Update(Func<DriveSnapshot, DriveSnapshot> f)
    {
        DriveSnapshot next;
        lock (_snapshotGate)
        {
            next = f(_snapshot);
            _snapshot = next;
        }
        SnapshotChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        StopLoop();
        StopReconnect();
        _connectCts?.Cancel();
        if (_session is not null)
        {
            _intentionalStop = true;
            try { await _session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None); } catch { }
            try { await _session.DisposeAsync(); } catch { }
            _session = null;
            AfterUnmounted();
        }
        ReleaseLock();
        _opLock.Dispose();
    }
}
