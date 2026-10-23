using System.Diagnostics;
using OpenMount.Core.Engine;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Rclone;
using OpenMount.Windows;
using Xunit;
using Xunit.Abstractions;

namespace OpenMount.IntegrationTests;

/// <summary>
/// End-to-end tests against a real rclone + WinFsp. They are skipped (pass vacuously with a message) when either
/// dependency is missing. Set OPENMOUNT_RCLONE to a rclone.exe to run them.
/// </summary>
public class MountIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openmount-it-" + Guid.NewGuid().ToString("N"));
    private string? _rclone;
    private Process? _server;
    private int _serverPort;

    public MountIntegrationTests(ITestOutputHelper output) => _out = output;

    private bool Available => _rclone is not null && WinFspInfo.Detect().Installed;

    public async Task InitializeAsync()
    {
        _rclone = new RcloneLocator().Resolve(new AppSettings());
        if (_rclone is null) { _out.WriteLine("SKIPPED: rclone not found (set OPENMOUNT_RCLONE)"); return; }
        if (!WinFspInfo.Detect().Installed) { _out.WriteLine("SKIPPED: WinFsp not installed"); return; }
        Directory.CreateDirectory(Path.Combine(_root, "served"));
        File.WriteAllText(Path.Combine(_root, "served", "hello.txt"), "hello from webdav");
        _serverPort = FreePort();
        var psi = new ProcessStartInfo(_rclone)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "serve", "webdav", Path.Combine(_root, "served"), "--addr", "127.0.0.1:" + _serverPort, "--user", "u", "--pass", "p", "--config=" })
            psi.ArgumentList.Add(a);
        _server = Process.Start(psi);
        await Task.Delay(1500);
    }

    public Task DisposeAsync()
    {
        try { _server?.Kill(true); } catch { }
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    private (RcloneMountEngine Engine, Drive Drive, AppPaths Paths) Build(char letter)
    {
        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        var redactor = new Redactor();
        var engine = new RcloneMountEngine(new RcloneLocator(), new MountPointInspector(), new JobObjectFactory(), new ProxyResolver(null), redactor, new MemoryAppLog(), paths);
        var drive = new Drive
        {
            Name = "IT WebDAV",
            StorageType = StorageType.WebDav,
            Mount = new MountOptions { DriveLetter = letter.ToString(), NetworkMode = true },
            WebDav = new WebDavConnection { Scheme = "http", Host = "127.0.0.1", Port = _serverPort, Path = "/", AuthMode = WebDavAuthMode.Basic, Username = "u" },
        };
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        return (engine, drive, paths);
    }

    [Fact]
    public async Task MountReadWriteStopAndOfflineScan()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var letter = TestSupport.PickLetter();
        var (engine, drive, paths) = Build(letter);
        var settings = new AppSettings();
        var request = new MountRequest(drive, new DriveSecrets(Password: "p"), settings, paths.CacheDirFor(drive, settings), paths.RunDirFor(drive), paths.KnownHostsFile);

        var result = await engine.StartAsync(request, null, CancellationToken.None);
        if (result is MountStartResult.Failed f) _out.WriteLine(f.Error.Kind + ": " + f.Error.TechnicalDetails);
        var ready = Assert.IsType<MountStartResult.Ready>(result);
        try
        {
            Assert.Equal(RemoteHealth.Healthy, ready.InitialHealth.Health);
            var root = letter + ":\\";
            Assert.True(Directory.Exists(root));
            Assert.Equal("hello from webdav", File.ReadAllText(Path.Combine(root, "hello.txt")));
            File.WriteAllText(Path.Combine(root, "written.txt"), "from mount");
            Assert.True(await ready.Session.CheckAliveAsync(CancellationToken.None));
            var live = await ready.Session.QuerySyncStateAsync(CancellationToken.None);
            Assert.Equal(SyncSource.Live, live.Source);
        }
        finally
        {
            var stop = await ready.Session.StopAsync(StopPolicy.WaitForUploads, null, CancellationToken.None);
            _out.WriteLine($"stop: {stop.Outcome} graceful={stop.WasGraceful} sync={stop.SyncStateAfterStop.Kind}");
            await ready.Session.DisposeAsync();
            TestSupport.DumpDriveLogs(_out, paths, nameof(MountReadWriteStopAndOfflineScan));
            Assert.True(stop.WasGraceful);
        }
        Assert.False(Directory.Exists(letter + ":\\"));
        Assert.Equal("from mount", File.ReadAllText(Path.Combine(_root, "served", "written.txt")));
        var offline = await engine.InspectOfflineAsync(drive, request.CacheDir, CancellationToken.None);
        Assert.True(offline.IdentityMatches);
        Assert.Equal(SyncKind.Clean, offline.State.Kind);
    }

    /// <summary>
    /// rclone's WebDAV backend does not fail NewFs on bad credentials, so the engine lists the remote root BEFORE mounting
    /// (pre-flight). A wrong password must therefore fail without ever creating a drive letter — a mount whose root is
    /// inaccessible leaves a permanent "disconnected network drive" ghost in Explorer.
    /// </summary>
    [Fact]
    public async Task WrongPasswordFailsInPreflightWithoutMounting()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var letter = TestSupport.PickLetter();
        var (engine, drive, paths) = Build(letter);
        var settings = new AppSettings();
        var request = new MountRequest(drive, new DriveSecrets(Password: "wrong"), settings, paths.CacheDirFor(drive, settings), paths.RunDirFor(drive), paths.KnownHostsFile);
        var result = await engine.StartAsync(request, null, CancellationToken.None);
        if (result is MountStartResult.Ready r) { await r.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None); await r.Session.DisposeAsync(); }
        var failed = Assert.IsType<MountStartResult.Failed>(result);
        _out.WriteLine($"pre-flight: {failed.Error.Kind} — {failed.RedactedTail}");
        Assert.Equal(Core.Errors.MountErrorKind.Auth, failed.Error.Kind);
        Assert.False(Directory.Exists(letter + ":\\"));
    }

    [Fact]
    public async Task UnreachableHostFailsInPreflightWithoutMounting()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var letter = TestSupport.PickLetter();
        var (engine, drive, paths) = Build(letter);
        drive.WebDav!.Port = FreePort(); // nothing listens here
        var settings = new AppSettings();
        var request = new MountRequest(drive, new DriveSecrets(Password: "p"), settings, paths.CacheDirFor(drive, settings), paths.RunDirFor(drive), paths.KnownHostsFile);
        var result = await engine.StartAsync(request, null, CancellationToken.None);
        if (result is MountStartResult.Ready r) { await r.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None); await r.Session.DisposeAsync(); }
        var failed = Assert.IsType<MountStartResult.Failed>(result);
        _out.WriteLine($"pre-flight: {failed.Error.Kind} — {failed.RedactedTail}");
        Assert.Equal(Core.Errors.MountErrorKind.Unreachable, failed.Error.Kind);
        Assert.True(failed.Error.AllowsAutoReconnect);
        Assert.False(Directory.Exists(letter + ":\\"));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
