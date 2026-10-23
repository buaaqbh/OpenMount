using OpenMount.Core.Engine;
using OpenMount.Core.Errors;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Windows;
using Xunit;

namespace OpenMount.Tests;

/// <summary>State-machine tests with a fake engine; no rclone/WinFsp involved.</summary>
public class DriveSupervisorTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "openmount-sup-" + Guid.NewGuid().ToString("N")));
    private readonly FakeEngine _engine = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly SessionCredentialCache _session = new();
    private readonly HeadlessInteraction _ui = new();
    private readonly AppSettings _settings = new() { General = { Notifications = false } };

    public DriveSupervisorTests()
    {
        _paths.EnsureCreated();
        DriveSupervisor.ReconnectDelays = new[] { TimeSpan.FromMilliseconds(50) };
    }

    // xUnit 2 only honours IDisposable / IAsyncLifetime on test classes; IAsyncDisposable was silently ignored and left temp dirs behind.
    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, true); } catch { }
    }

    private DriveSupervisor Create(Drive drive)
    {
        var resolver = new CredentialResolver(_store, _session);
        return new DriveSupervisor(drive, _engine, resolver, new KnownHostsFile(_paths.KnownHostsFile), new UnavailableHostKeyScanner(), _ui,
            () => _settings, _paths, new MemoryAppLog(), new Redactor(), null, new AlwaysLockFactory(), new ProxyResolver(null), () => null);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ConnectReportsMountedWithRemoteHealthFromEngine()
    {
        var drive = ArgsAndEnvironmentTests.WebDav();
        _store.Set(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password), "john", "pw");
        _engine.NextHealth = new RemoteHealthResult(RemoteHealth.Unreachable, "i/o timeout", TimeSpan.Zero);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Mounted, sup.Snapshot.Lifecycle);
        Assert.Equal(RemoteHealth.Unreachable, sup.Snapshot.RemoteHealth);
        Assert.NotNull(sup.Snapshot.Mount);
        Assert.Equal("pw", _engine.LastRequest!.Secrets.Password);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task MountedButAuthRequiredOnFirstProbeBecomesConnectFailure()
    {
        var drive = ArgsAndEnvironmentTests.WebDav();
        _store.Set(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password), "john", "wrong");
        _engine.NextHealth = new RemoteHealthResult(RemoteHealth.AuthRequired, "401 Unauthorized", TimeSpan.Zero);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Error, sup.Snapshot.Lifecycle);
        Assert.Equal(MountErrorKind.Auth, sup.Snapshot.Error!.Kind);
        Assert.True(_engine.LastSession!.Stopped);
        Assert.Null(sup.Snapshot.Mount);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task AuthFailureDoesNotReconnect()
    {
        var drive = ArgsAndEnvironmentTests.WebDav();
        _engine.FailWith = MountError.Create(MountErrorKind.Auth, "401");
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: false);
        Assert.Equal(LifecycleState.Error, sup.Snapshot.Lifecycle);
        Assert.Equal(MountErrorKind.Auth, sup.Snapshot.Error!.Kind);
        await Task.Delay(200);
        Assert.Equal(1, _engine.StartCalls);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task PromptPolicyWithoutCachedSecretsNeedsCredentials_NonInteractive()
    {
        var drive = ArgsAndEnvironmentTests.Sftp();
        drive.CredentialPolicy = CredentialPolicy.Prompt;
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: false);
        Assert.Equal(LifecycleState.NeedsCredentials, sup.Snapshot.Lifecycle);
        Assert.Equal(0, _engine.StartCalls);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task PromptPolicyInteractiveUsesProvidedSecretsWithoutPersisting()
    {
        var drive = ArgsAndEnvironmentTests.Sftp();
        drive.Sftp!.HostKeyCheck = HostKeyCheckMode.None;
        drive.CredentialPolicy = CredentialPolicy.Prompt;
        _ui.Credentials = new CredentialPromptResult(new DriveSecrets(Password: "typed"), RememberForSession: false);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Mounted, sup.Snapshot.Lifecycle);
        Assert.Equal("typed", _engine.LastRequest!.Secrets.Password);
        Assert.Null(_store.Get(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password)));
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task TofuWithoutScannerYieldsScanUnavailableError_NotSilentSkip()
    {
        var drive = ArgsAndEnvironmentTests.Sftp();
        _store.Set(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password), "john", "pw");
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Error, sup.Snapshot.Lifecycle);
        Assert.Equal(MountErrorKind.HostKeyScanUnavailable, sup.Snapshot.Error!.Kind);
        Assert.Equal(0, _engine.StartCalls);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedExitReconnectsWithBackoff()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Mounted, sup.Snapshot.Lifecycle);
        _engine.LastSession!.SimulateCrash(2, "panic: boom");
        await WaitUntil(() => sup.Snapshot.Lifecycle == LifecycleState.Reconnecting || _engine.StartCalls >= 2);
        await WaitUntil(() => sup.Snapshot.Lifecycle == LifecycleState.Mounted && _engine.StartCalls == 2);
        Assert.Equal(0, sup.Snapshot.ReconnectAttempt);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedExitWithoutAutoReconnectEndsInError()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        drive.Mount.AutoReconnect = false;
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        _engine.LastSession!.SimulateCrash(2, "panic: boom");
        await WaitUntil(() => sup.Snapshot.Lifecycle == LifecycleState.Error);
        Assert.Equal(MountErrorKind.Crashed, sup.Snapshot.Error!.Kind);
        Assert.True(sup.Snapshot.Sync.AbnormalStop);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectWithPendingUploads_UserChoosesStopAndRetain()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        _engine.LastSession!.LiveState = SyncState.Pending(SyncSource.Live, 2, 500);
        _engine.OfflineState = SyncState.Pending(SyncSource.Offline, 2, 500);
        _ui.UnsyncedChoice = GuardChoice.StopAndRetain;
        var ok = await sup.DisconnectAsync(interactive: true);
        Assert.True(ok);
        Assert.Equal(LifecycleState.Disconnected, sup.Snapshot.Lifecycle);
        Assert.Equal(SyncKind.Pending, sup.Snapshot.Sync.Kind);
        Assert.Equal(2, sup.Snapshot.Sync.PendingCount);
        Assert.Equal(DestructiveOperation.Disconnect, _ui.LastOperation);
        Assert.NotNull(drive.Recovery);
        Assert.Equal(2, drive.Recovery!.PendingCount);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectWithPendingUploads_UserCancelsStaysMounted()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        _engine.LastSession!.LiveState = SyncState.Pending(SyncSource.Live, 1, 10);
        _ui.UnsyncedChoice = GuardChoice.Cancel;
        var ok = await sup.DisconnectAsync(interactive: true);
        Assert.False(ok);
        Assert.Equal(LifecycleState.Mounted, sup.Snapshot.Lifecycle);
        Assert.False(_engine.LastSession.Stopped);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectWaitsForUploadsWhenChosen()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        var session = _engine.LastSession!;
        session.LiveState = SyncState.Pending(SyncSource.Live, 1, 10);
        _ui.UnsyncedChoice = GuardChoice.WaitForUploads;
        var task = sup.DisconnectAsync(interactive: true);
        await WaitUntil(() => session.Expedited);
        Assert.Equal(LifecycleState.Disconnecting, sup.Snapshot.Lifecycle);
        session.LiveState = SyncState.Clean(SyncSource.Live);
        Assert.True(await task);
        Assert.Equal(LifecycleState.Disconnected, sup.Snapshot.Lifecycle);
        Assert.Equal(SyncKind.Clean, sup.Snapshot.Sync.Kind);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task CleanDisconnectNeedsNoDialog()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        _ui.UnsyncedChoice = GuardChoice.Cancel; // would block if asked
        Assert.True(await sup.DisconnectAsync(interactive: true));
        Assert.Null(_ui.LastOperation);
        Assert.Equal(LifecycleState.Disconnected, sup.Snapshot.Lifecycle);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task CacheIdentityMismatchBlocksConnect()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        _engine.IdentityMatches = false;
        var sup = Create(drive);
        await sup.ConnectAsync(interactive: true);
        Assert.Equal(LifecycleState.Error, sup.Snapshot.Lifecycle);
        Assert.Equal(MountErrorKind.CacheIdentityMismatch, sup.Snapshot.Error!.Kind);
        Assert.Equal(0, _engine.StartCalls);
        await sup.DisposeAsync();
    }

    [Fact]
    public async Task InitializeShowsPendingFromOfflineScan()
    {
        var drive = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Anonymous);
        _engine.OfflineState = SyncState.Pending(SyncSource.Offline, 4, 4096);
        var sup = Create(drive);
        await sup.InitializeAsync(CancellationToken.None);
        Assert.Equal(SyncKind.Pending, sup.Snapshot.Sync.Kind);
        Assert.Equal(LifecycleState.Disconnected, sup.Snapshot.Lifecycle);
        Assert.Equal("pendingUploads", drive.Recovery!.Reason);
        await sup.DisposeAsync();
    }

    // ------------------------------------------------------------------ fakes

    private sealed class AlwaysLockFactory : ISessionLockFactory
    {
        public ISessionLock? TryAcquire(string lockFilePath) => new NoopLock();
        private sealed class NoopLock : ISessionLock { public void Dispose() { } }
    }

    internal sealed class HeadlessInteraction : IUserInteraction
    {
        public CredentialPromptResult? Credentials { get; set; }
        public GuardChoice UnsyncedChoice { get; set; } = GuardChoice.Cancel;
        public DestructiveOperation? LastOperation { get; private set; }
        public bool TrustHostKeys { get; set; } = true;

        public Task<CredentialPromptResult?> PromptCredentialsAsync(Drive drive, CancellationToken ct) => Task.FromResult(Credentials);
        public Task<bool> ConfirmHostKeysAsync(Drive drive, SshEndpoint endpoint, IReadOnlyList<HostKeyEntry> keys, bool isChange, CancellationToken ct) => Task.FromResult(TrustHostKeys);
        public Task<GuardChoice> ChooseUnsyncedActionAsync(Drive drive, DestructiveOperation op, SyncState state, IReadOnlyList<GuardChoice> choices, string reason, CancellationToken ct)
        {
            LastOperation = op;
            Assert.Contains(UnsyncedChoice, choices);
            return Task.FromResult(UnsyncedChoice);
        }
        public Task<GuardChoice> ChooseWhenNoProgressAsync(Drive drive, StopProgressInfo progress, CancellationToken ct) => Task.FromResult(GuardChoice.StopAndRetain);
        public Task<bool> ConfirmDiscardAsync(Drive drive, SyncState state, CancellationToken ct) => Task.FromResult(true);
        public void Notify(string title, string message, NotificationKind kind) { }
    }

    internal sealed class FakeEngine : IMountEngine
    {
        public MountError? FailWith { get; set; }
        public RemoteHealthResult NextHealth { get; set; } = RemoteHealthResult.Healthy(TimeSpan.Zero);
        public SyncState OfflineState { get; set; } = SyncState.Clean(SyncSource.Offline);
        public bool IdentityMatches { get; set; } = true;
        public int StartCalls { get; private set; }
        public MountRequest? LastRequest { get; private set; }
        public FakeSession? LastSession { get; private set; }

        public Task<MountStartResult> StartAsync(MountRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            StartCalls++;
            LastRequest = request;
            if (FailWith is not null) return Task.FromResult<MountStartResult>(new MountStartResult.Failed(FailWith, "tail"));
            LastSession = new FakeSession(this, request);
            return Task.FromResult<MountStartResult>(new MountStartResult.Ready(LastSession, NextHealth));
        }

        public Task<OfflineSyncReport> InspectOfflineAsync(Drive drive, string cacheDir, CancellationToken ct) =>
            Task.FromResult(new OfflineSyncReport(IdentityMatches ? OfflineState : SyncState.Unknown(SyncSource.Offline, "identity"), IdentityMatches, null, 0));

        public Task DiscardUnsyncedDataAsync(Drive drive, string cacheDir, CancellationToken ct) { OfflineState = SyncState.Clean(SyncSource.Offline); return Task.CompletedTask; }
        public Task DeleteCacheAsync(Drive drive, string cacheDir, CancellationToken ct) => Task.CompletedTask;
        public int RepairCalls { get; private set; }
        public Task RepairExplorerGhostAsync(Drive drive, AppSettings settings, CancellationToken ct) { RepairCalls++; return Task.CompletedTask; }
    }

    internal sealed class FakeSession : IMountSession
    {
        private readonly FakeEngine _engine;
        public FakeSession(FakeEngine engine, MountRequest request)
        {
            _engine = engine;
            Info = new MountSessionInfo(request.Drive.Id, request.Drive.Mount.ToMountPoint(), 4242, 55555, request.CacheDir, DateTimeOffset.Now, "rclone mount ...");
        }
        public MountSessionInfo Info { get; }
        public bool IsAlive { get; private set; } = true;
        public SyncState LiveState { get; set; } = SyncState.Clean(SyncSource.Live);
        public bool Expedited { get; private set; }
        public bool Stopped { get; private set; }
        public event EventHandler<SessionExitedEventArgs>? Exited;

        public void SimulateCrash(int exitCode, string tail)
        {
            IsAlive = false;
            Exited?.Invoke(this, new SessionExitedEventArgs(exitCode, tail, intentional: false));
        }

        public Task<RemoteHealthResult> ProbeRemoteAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(_engine.NextHealth);
        public Task<SyncState> QuerySyncStateAsync(CancellationToken ct) => Task.FromResult(LiveState);
        public Task<bool> CheckAliveAsync(CancellationToken ct) => Task.FromResult(IsAlive);
        public Task ExpediteUploadsAsync(CancellationToken ct) { Expedited = true; return Task.CompletedTask; }
        public Task<SpaceInfo?> QuerySpaceAsync(CancellationToken ct) => Task.FromResult<SpaceInfo?>(new SpaceInfo(100, 50));
        public Task<StopResult> StopAsync(StopPolicy policy, IProgress<StopProgressInfo>? progress, CancellationToken ct)
        {
            Stopped = true;
            IsAlive = false;
            var after = _engine.OfflineState;
            return Task.FromResult(new StopResult(after.RequiresProtection ? StopOutcome.StoppedWithPendingData : StopOutcome.CleanStop, after, after.RequiresProtection, true, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
