using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenMount.Core.Engine;
using OpenMount.Core.Errors;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;
using OpenMount.Core.State;

namespace OpenMount.Rclone;

/// <summary>rclone implementation of the engine contract (design §3.4, §4.2, §6).</summary>
public sealed class RcloneMountEngine : IMountEngine
{
    private readonly RcloneLocator _locator;
    private readonly IMountPointInspector _inspector;
    private readonly IChildProcessJobFactory _jobs;
    private readonly IProxyResolver _proxy;
    private readonly Redactor _redactor;
    private readonly IAppLog _log;
    private readonly AppPaths _paths;
    private readonly CacheInspector _cache = new();
    private readonly Dictionary<Guid, FileAppLog> _driveLogs = new();
    private readonly object _logGate = new();

    public RcloneMountEngine(RcloneLocator locator, IMountPointInspector inspector, IChildProcessJobFactory jobs, IProxyResolver proxy, Redactor redactor, IAppLog log, AppPaths paths)
    {
        _locator = locator;
        _inspector = inspector;
        _jobs = jobs;
        _proxy = proxy;
        _redactor = redactor;
        _log = log;
        _paths = paths;
    }

    public string AppVersion { get; set; } = typeof(RcloneMountEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public async Task<MountStartResult> StartAsync(MountRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var drive = request.Drive;
        var settings = request.Settings;
        var src = "engine:" + drive.ShortId;

        var exe = _locator.Resolve(settings);
        if (exe is null) return new MountStartResult.Failed(MountError.Create(MountErrorKind.RcloneMissing, "rclone.exe not found"), string.Empty);
        _lastNetworkSharePrefix = settings.Advanced.NetworkSharePrefix;

        // Pre-flight: mount point must be free / non-existent.
        var mountPoint = drive.Mount.ToMountPoint();
        if (drive.Mount.Kind == MountKind.DriveLetter)
        {
            if (!_inspector.IsDriveLetterFree(mountPoint[0]))
            {
                // A previous OpenMount mount that died without unmounting may have left a dead WinFsp network mapping behind.
                if (_inspector.TryRemoveStaleNetworkMapping(mountPoint[0], settings.Advanced.NetworkSharePrefix))
                    _log.Warn(src, $"removed stale network mapping on {mountPoint}");
                if (!_inspector.IsDriveLetterFree(mountPoint[0]))
                    return new MountStartResult.Failed(MountError.Create(MountErrorKind.MountPointInUse, $"drive letter {mountPoint} is in use"), string.Empty);
            }
        }
        else
        {
            if (_inspector.IsDanglingReparsePoint(mountPoint))
            {
                _log.Warn(src, "removing dangling reparse point at " + mountPoint);
                _inspector.TryRemoveDanglingReparsePoint(mountPoint);
            }
            if (_inspector.Exists(mountPoint))
                return new MountStartResult.Failed(MountError.Create(MountErrorKind.MountPointInUse, $"mount folder {mountPoint} already exists"), string.Empty);
        }

        foreach (var s in request.Secrets.All()) _redactor.RegisterSecret(s);

        var port = FreePort();
        var rcUser = "om" + RandomNumberGenerator.GetHexString(8);
        var rcPass = RandomNumberGenerator.GetHexString(32);
        _redactor.RegisterSecret(rcPass);

        var args = RcloneArgsBuilder.Build(drive, settings, request.CacheDir, port);
        var proxy = _proxy.ResolveFor(drive, settings, request.Secrets.ProxyPassword);
        var env = ProcessEnvironmentBuilder.Build(drive, request.Secrets, settings, proxy, rcUser, rcPass, request.KnownHostsPath);

        Directory.CreateDirectory(request.CacheDir);
        Directory.CreateDirectory(request.RunDir);
        if (CacheInspector.ReadIdentity(request.CacheDir) is null) CacheInspector.WriteIdentity(request.CacheDir, drive, AppVersion);

        var driveLog = GetDriveLog(drive);

        // Pre-flight: touch the remote root BEFORE creating the mount, so bad credentials / a dead host fail fast and clearly
        // (rclone's WebDAV backend only notices a 401 on first real access) instead of producing a drive that errors on first
        // use (FR-23.1). Explorer's ghost entries have a different cause and fix; see ShellIntegration.NotifyDriveChanged.
        progress?.Report("checking remote");
        var preflight = await PreflightAsync(exe, args, env, request.RunDir, settings, ct);
        if (preflight is not null)
        {
            driveLog.Warn("mount", $"pre-flight failed: {preflight.Kind}");
            return new MountStartResult.Failed(preflight, preflight.TechnicalDetails);
        }

        driveLog.Info("mount", "starting: " + args.CommandLine);
        progress?.Report("starting rclone");

        RcloneProcess process;
        try
        {
            process = RcloneProcess.Start(exe, args.Args, env, request.RunDir, _jobs.Create(), _redactor.Redact,
                line => LogRcloneLine(driveLog, line));
        }
        catch (Exception ex)
        {
            return new MountStartResult.Failed(MountError.Create(MountErrorKind.RcloneMissing, _redactor.Redact(ex.ToString())), string.Empty);
        }
        foreach (var kv in env) if (ProcessEnvironmentBuilder.IsSecretKey(kv.Key)) env[kv.Key] = string.Empty; // drop secret copies

        var rc = new RcClient(port, rcUser, rcPass);
        var allowedFs = AllowedFileSystemNames(drive);
        var isNetworkLetter = drive.Mount.NetworkMode && drive.Mount.Kind == MountKind.DriveLetter;
        var expectedIdentity = isNetworkLetter ? args.VolumeName : args.VolumeLabel;
        var timeout = TimeSpan.FromSeconds(Math.Max(5, settings.Advanced.MountReadyTimeoutSeconds));
        var sw = Stopwatch.StartNew();
        var rcOk = false;
        var pidChecked = false;
        string identityDetail = string.Empty;
        while (sw.Elapsed < timeout)
        {
            if (ct.IsCancellationRequested)
            {
                await ShutdownAsync(process, rc, mountPoint, drive);
                rc.Dispose();
                process.Dispose();
                throw new OperationCanceledException(ct);
            }
            if (process.HasExited)
            {
                var tail = process.Tail;
                var error = ErrorClassifier.Classify(tail, process.ExitCode, ClassifyPhase.Startup);
                driveLog.Warn("mount", $"rclone exited during startup: {error.Kind}");
                rc.Dispose();
                process.Dispose();
                return new MountStartResult.Failed(error, string.Join('\n', tail));
            }
            if (!rcOk) rcOk = await rc.NoopAsync(TimeSpan.FromSeconds(1), ct);
            if (rcOk && !pidChecked)
            {
                // The port was free when we picked it, but another process may have bound it since: the endpoint must be ours.
                pidChecked = true;
                var pid = await rc.CorePidAsync(ct);
                if (pid is { } answered && answered != process.Id)
                {
                    driveLog.Warn("mount", $"rc endpoint answered with pid {answered}, expected {process.Id}");
                    await ShutdownAsync(process, rc, mountPoint, drive);
                    var tailPid = process.Tail;
                    rc.Dispose();
                    process.Dispose();
                    return new MountStartResult.Failed(MountError.Create(MountErrorKind.IdentityMismatch, $"RC 端口 127.0.0.1:{port} 由进程 {answered} 应答，不是本驱动器的 rclone 进程 {process.Id}"), string.Join('\n', tailPid));
                }
            }
            if (rcOk && _inspector.Exists(mountPoint))
            {
                if (_inspector.VerifyIdentity(mountPoint, expectedIdentity, allowedFs, isNetworkLetter, out identityDetail))
                {
                    progress?.Report("probing remote");
                    var health = await ProbeAsync(rc, settings, ct, args.MountSource);
                    var info = new MountSessionInfo(drive.Id, mountPoint, process.Id, port, request.CacheDir, DateTimeOffset.Now, args.CommandLine);
                    driveLog.Info("mount", $"ready in {sw.ElapsedMilliseconds} ms; remote={health.Health} {_redactor.Redact(health.Error ?? string.Empty)}");
                    var session = new RcloneMountSession(this, drive, process, rc, info, args, _inspector, allowedFs, driveLog, _redactor);
                    _inspector.NotifyMountPointChanged(mountPoint, added: true, network: isNetworkLetter);
                    return new MountStartResult.Ready(session, health);
                }
            }
            await Task.Delay(300, ct);
        }

        // Timed out (or mount point present but not ours).
        var mismatch = _inspector.Exists(mountPoint) && !string.IsNullOrEmpty(identityDetail);
        driveLog.Warn("mount", mismatch ? "identity mismatch: " + identityDetail : "ready timeout");
        await ShutdownAsync(process, rc, mountPoint, drive);
        var tailAfter = process.Tail;
        rc.Dispose();
        process.Dispose();
        var kind = mismatch ? MountErrorKind.IdentityMismatch : MountErrorKind.Timeout;
        return new MountStartResult.Failed(MountError.Create(kind, (mismatch ? identityDetail + "\n" : string.Empty) + string.Join('\n', tailAfter)), string.Join('\n', tailAfter));
    }

    /// <summary>
    /// Lists the remote root (depth 1) with the exact environment the mount will use. Returns null when the remote is
    /// reachable and readable, otherwise the classified error (Auth / Unreachable / NotFound / TlsCert / ...).
    /// </summary>
    private async Task<MountError?> PreflightAsync(string exe, MountArguments args, IDictionary<string, string> env, string runDir, AppSettings settings, CancellationToken ct)
    {
        var list = new List<string> { "lsjson", args.MountSource, "--max-depth", "1", "--config=", "--contimeout", "10s", "--timeout", "20s", "--low-level-retries", "2", "--retries", "1", "--log-level", "ERROR", "--use-json-log" };
        var tls = args.Args.ToList();
        if (tls.Contains("--no-check-certificate")) list.Add("--no-check-certificate");
        var ca = tls.IndexOf("--ca-cert");
        if (ca >= 0 && ca + 1 < tls.Count) { list.Add("--ca-cert"); list.Add(tls[ca + 1]); }
        RcloneRunResult result;
        try
        {
            result = await RcloneRunner.RunAsync(exe, list, env, runDir, TimeSpan.FromSeconds(Math.Max(15, settings.Advanced.RemoteProbeTimeoutSeconds * 2)), _redactor.Redact, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return MountError.Create(MountErrorKind.RcloneMissing, _redactor.Redact(ex.Message));
        }
        if (result.ExitCode == 0 && !result.TimedOut) return null;
        var lines = result.StderrLines.Select(l => RcloneLogParser.Parse(l).Message).ToList();
        if (result.TimedOut) lines.Add("i/o timeout: pre-flight listing exceeded the time limit");
        return ErrorClassifier.Classify(lines, result.ExitCode, ClassifyPhase.Startup);
    }

    /// <summary>WinFsp reports FUSE file systems as "FUSE-&lt;name&gt;"; rclone passes --FileSystemName=rclone. The NTFS override is exact.</summary>
    internal static string[] AllowedFileSystemNames(Drive drive) =>
        drive.Advanced.FileSystemName == FileSystemNameMode.Ntfs ? new[] { "NTFS" } : new[] { "FUSE-rclone", "rclone" };

    /// <summary>Remote health probe: backend root listing via rc (never through the VFS directory cache, see RcClient.ListRootAsync).</summary>
    internal async Task<RemoteHealthResult> ProbeAsync(RcClient rc, AppSettings settings, CancellationToken ct, string fs)
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(5, settings.Advanced.RemoteProbeTimeoutSeconds));
        var error = await rc.ListRootAsync(fs, timeout, ct);
        return error is null ? RemoteHealthResult.Healthy(sw.Elapsed) : new RemoteHealthResult(ErrorClassifier.ClassifyProbe(_redactor.Redact(error)), _redactor.Redact(error), sw.Elapsed);
    }

    /// <summary>Graceful stop: core/quit, wait, kill as fallback, wait for the mount point to vanish, tidy dangling reparse points.</summary>
    internal async Task<bool> ShutdownAsync(RcloneProcess process, RcClient rc, string mountPoint, Drive drive)
    {
        var graceful = false;
        if (!process.HasExited)
        {
            await rc.QuitAsync(CancellationToken.None);
            graceful = await process.WaitForExitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            if (!graceful)
            {
                process.Kill();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5) && _inspector.Exists(mountPoint)) await Task.Delay(200);
        if (drive.Mount.Kind == MountKind.Folder && _inspector.IsDanglingReparsePoint(mountPoint)) _inspector.TryRemoveDanglingReparsePoint(mountPoint);
        if (drive.Mount.Kind == MountKind.DriveLetter && drive.Mount.NetworkMode && _inspector.Exists(mountPoint))
        {
            // Killed process: WinFsp could not cancel its network mapping; do it on its behalf.
            if (_inspector.TryRemoveStaleNetworkMapping(mountPoint[0], _lastNetworkSharePrefix))
                _log.Warn("engine", $"cancelled stale network mapping left on {mountPoint}");
        }
        // Now that the letter is gone, tell the shell (synchronous device-change broadcast + SHCNE): without it Explorer keeps
        // a permanent "disconnected network drive" record for the letter, graceful unmount or not (design §13).
        _inspector.NotifyMountPointChanged(mountPoint, added: false, network: drive.Mount.Kind == MountKind.DriveLetter && drive.Mount.NetworkMode);
        return graceful;
    }

    private string _lastNetworkSharePrefix = "OpenMount";

    /// <summary>
    /// Explorer drops its "disconnected network drive" record for a letter only when it receives a synchronous
    /// WM_DEVICECHANGE removal for that letter after the volume is gone (root-caused 2026-10-23). Sending it for a free letter
    /// is harmless, so this runs at startup for every configured drive letter and whenever a mount ended abnormally.
    /// </summary>
    public Task RepairExplorerGhostAsync(Drive drive, AppSettings settings, CancellationToken ct)
    {
        if (drive.Mount.Kind != MountKind.DriveLetter || string.IsNullOrEmpty(drive.Mount.DriveLetter)) return Task.CompletedTask;
        var letter = char.ToUpperInvariant(drive.Mount.DriveLetter[0]);
        if (!_inspector.IsDriveLetterFree(letter)) return Task.CompletedTask;
        _inspector.NotifyMountPointChanged(letter + ":", added: false, network: drive.Mount.NetworkMode);
        _log.Info("engine", $"removal broadcast sent for {letter}:");
        return Task.CompletedTask;
    }

    public Task<OfflineSyncReport> InspectOfflineAsync(Drive drive, string cacheDir, CancellationToken ct) =>
        _cache.ScanAsync(drive, cacheDir, abnormalStop: false, ct);

    public Task DiscardUnsyncedDataAsync(Drive drive, string cacheDir, CancellationToken ct) =>
        Task.Run(() => CacheInspector.Discard(cacheDir), ct);

    public Task DeleteCacheAsync(Drive drive, string cacheDir, CancellationToken ct) =>
        Task.Run(() => CacheInspector.DeleteAll(cacheDir), ct);

    internal FileAppLog GetDriveLog(Drive drive)
    {
        lock (_logGate)
        {
            if (!_driveLogs.TryGetValue(drive.Id, out var log))
            {
                log = new FileAppLog(_paths.DriveLogsDir, drive.ShortId, _redactor);
                _driveLogs[drive.Id] = log;
            }
            return log;
        }
    }

    private static void LogRcloneLine(IAppLog driveLog, string redactedLine)
    {
        var ev = RcloneLogParser.Parse(redactedLine);
        var level = ev.Level switch
        {
            "error" or "critical" or "fatal" => Core.Logging.LogLevel.Error,
            "warning" or "notice" => Core.Logging.LogLevel.Warn,
            "debug" => Core.Logging.LogLevel.Debug,
            _ => Core.Logging.LogLevel.Info,
        };
        driveLog.Write(level, ev.Source ?? "rclone", ev.Object is null ? ev.Message : ev.Object + ": " + ev.Message);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class RcloneMountSession : IMountSession
{
    private readonly RcloneMountEngine _engine;
    private readonly Drive _drive;
    private readonly RcloneProcess _process;
    private readonly RcClient _rc;
    private readonly MountArguments _args;
    private readonly IMountPointInspector _inspector;
    private readonly IReadOnlyCollection<string> _allowedFs;
    private readonly IAppLog _driveLog;
    private readonly Redactor _redactor;
    private volatile bool _intentional;
    private bool _disposed;

    public RcloneMountSession(RcloneMountEngine engine, Drive drive, RcloneProcess process, RcClient rc, MountSessionInfo info, MountArguments args,
        IMountPointInspector inspector, IReadOnlyCollection<string> allowedFs, IAppLog driveLog, Redactor redactor)
    {
        _engine = engine;
        _drive = drive;
        _process = process;
        _rc = rc;
        Info = info;
        _args = args;
        _inspector = inspector;
        _allowedFs = allowedFs;
        _driveLog = driveLog;
        _redactor = redactor;
        _process.Exited += (_, _) =>
        {
            var tail = string.Join('\n', _process.Tail);
            if (!_intentional) _driveLog.Warn("mount", "rclone exited unexpectedly, exit code " + _process.ExitCode);
            Exited?.Invoke(this, new SessionExitedEventArgs(_process.ExitCode, tail, _intentional));
        };
    }

    public MountSessionInfo Info { get; }
    public bool IsAlive => !_process.HasExited;
    public event EventHandler<SessionExitedEventArgs>? Exited;

    public async Task<RemoteHealthResult> ProbeRemoteAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var error = await _rc.ListRootAsync(_args.MountSource, timeout, ct);
        return error is null ? RemoteHealthResult.Healthy(sw.Elapsed) : new RemoteHealthResult(ErrorClassifier.ClassifyProbe(_redactor.Redact(error)), _redactor.Redact(error), sw.Elapsed);
    }

    public async Task<SyncState> QuerySyncStateAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _rc.VfsStatsAsync(ct);
            var queue = await _rc.VfsQueueAsync(ct);
            var queuedBytes = queue.Sum(q => q.Size);
            if (stats.UploadsInProgress > 0) return SyncState.Uploading(stats.UploadsInProgress, Math.Max(queue.Count, stats.UploadsQueued), queuedBytes);
            var pending = Math.Max(queue.Count, stats.UploadsQueued) + stats.ErroredFiles;
            if (pending > 0) return SyncState.Pending(SyncSource.Live, pending, queuedBytes);
            return SyncState.Clean(SyncSource.Live);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return SyncState.Unknown(SyncSource.Live, "rc unavailable: " + _redactor.Redact(ex.Message));
        }
    }

    public async Task<bool> CheckAliveAsync(CancellationToken ct)
    {
        if (_process.HasExited) return false;
        if (!await _rc.NoopAsync(TimeSpan.FromSeconds(3), ct)) return false;
        if (!_inspector.Exists(Info.MountPoint)) return false;
        var isNetworkLetter = _drive.Mount.NetworkMode && _drive.Mount.Kind == MountKind.DriveLetter;
        return _inspector.VerifyIdentity(Info.MountPoint, isNetworkLetter ? _args.VolumeName : _args.VolumeLabel, _allowedFs, isNetworkLetter, out _);
    }

    public async Task ExpediteUploadsAsync(CancellationToken ct)
    {
        var queue = await _rc.VfsQueueAsync(ct);
        foreach (var item in queue.Where(q => !q.Uploading))
        {
            try { await _rc.VfsQueueSetExpiryAsync(item.Id, -1_000_000_000, ct); } catch (RcException) { }
        }
    }

    public async Task<SpaceInfo?> QuerySpaceAsync(CancellationToken ct)
    {
        var about = await _rc.AboutAsync(_args.MountSource, ct);
        return about is null ? null : new SpaceInfo(about.Value.Total, about.Value.Free);
    }

    public async Task<StopResult> StopAsync(StopPolicy policy, IProgress<StopProgressInfo>? progress, CancellationToken ct)
    {
        if (policy == StopPolicy.WaitForUploads)
        {
            // WinFsp delivers Close asynchronously, so a file that was just written may not be in the upload queue yet:
            // give the VFS a moment, expedite what is queued, and only accept "clean" when it is observed twice in a row.
            var sw = Stopwatch.StartNew();
            try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { }
            try { await ExpediteUploadsAsync(ct); } catch { }
            var cleanStreak = 0;
            while (!ct.IsCancellationRequested)
            {
                var st = await QuerySyncStateAsync(ct);
                if (st.Kind == SyncKind.Unknown) break;
                if (!st.RequiresProtection)
                {
                    if (++cleanStreak >= 2) break;
                }
                else
                {
                    cleanStreak = 0;
                    progress?.Report(new StopProgressInfo(st.PendingCount, st.PendingBytes, st.UploadingCount, sw.Elapsed));
                    try { await ExpediteUploadsAsync(ct); } catch { }
                }
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
            }
            _driveLog.Info("mount", $"wait-for-uploads finished after {sw.Elapsed.TotalSeconds:0.#}s");
        }
        _intentional = true;
        var graceful = await _engine.ShutdownAsync(_process, _rc, Info.MountPoint, _drive);
        var report = await _engine.InspectOfflineAsync(_drive, Info.CacheDir, CancellationToken.None);
        var after = graceful ? report.State : report.State with { AbnormalStop = true };
        var outcome = !graceful ? StopOutcome.Killed : after.RequiresProtection ? StopOutcome.StoppedWithPendingData : StopOutcome.CleanStop;
        _driveLog.Info("mount", $"stopped: {outcome}, sync={after.Kind} pending={after.PendingCount}");
        return new StopResult(outcome, after, after.RequiresProtection, graceful, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_process.HasExited)
        {
            _intentional = true;
            await _engine.ShutdownAsync(_process, _rc, Info.MountPoint, _drive);
        }
        _rc.Dispose();
        _process.Dispose();
    }
}
