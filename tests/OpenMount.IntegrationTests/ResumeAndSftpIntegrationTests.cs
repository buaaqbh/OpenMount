using System.Diagnostics;
using OpenMount.Core.Engine;
using OpenMount.Core.Errors;
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
/// FR-22 end to end (A06/A07): a file written while the server is unreachable survives "stop and retain",
/// is reported by the offline scan, and is uploaded automatically on the next connect. Plus an SFTP mount.
/// </summary>
public class ResumeAndSftpIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openmount-it2-" + Guid.NewGuid().ToString("N"));
    private readonly string? _rclone = new RcloneLocator().Resolve(new AppSettings());
    private readonly List<Process> _servers = new();

    public ResumeAndSftpIntegrationTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    private bool Available => _rclone is not null && WinFspInfo.Detect().Installed;

    public void Dispose()
    {
        foreach (var s in _servers) { try { s.Kill(true); } catch { } }
        try { Directory.Delete(_root, true); } catch { }
    }

    private Process StartServer(string kind, string dir, int port, params string[] extra)
    {
        var psi = new ProcessStartInfo(_rclone!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "serve", kind, dir, "--addr", "127.0.0.1:" + port, "--user", "u", "--pass", "p", "--config=" }.Concat(extra)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _servers.Add(p);
        return p;
    }

    private (RcloneMountEngine Engine, AppPaths Paths) BuildEngine()
    {
        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        var engine = new RcloneMountEngine(new RcloneLocator(), new MountPointInspector(), new JobObjectFactory(), new ProxyResolver(null), new Redactor(), new MemoryAppLog(), paths);
        return (engine, paths);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    [Fact]
    public async Task PendingUploadSurvivesStopAndResumesOnReconnect()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var served = Path.Combine(_root, "served");
        Directory.CreateDirectory(served);
        var port = FreePort();
        var server = StartServer("webdav", served, port);
        await Task.Delay(1500);

        var (engine, paths) = BuildEngine();
        var letter = TestSupport.PickLetter();
        var drive = new Drive
        {
            Name = "IT Resume",
            StorageType = StorageType.WebDav,
            Mount = new MountOptions { DriveLetter = letter.ToString(), NetworkMode = true },
            WebDav = new WebDavConnection { Scheme = "http", Host = "127.0.0.1", Port = port, Path = "/", AuthMode = WebDavAuthMode.Basic, Username = "u" },
            Advanced = new AdvancedOptions { WriteBack = "3s" },
        };
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        var settings = new AppSettings();
        var cacheDir = paths.CacheDirFor(drive, settings);
        MountRequest Request() => new(drive, new DriveSecrets(Password: "p"), settings, cacheDir, paths.RunDirFor(drive), paths.KnownHostsFile);

        // 1) mount, write a file, then take the server down before the write-back delay elapses
        var first = Assert.IsType<MountStartResult.Ready>(await engine.StartAsync(Request(), null, CancellationToken.None));
        var content = new string('x', 256 * 1024);
        var root = letter + ":\\";
        try
        {
            File.WriteAllText(Path.Combine(root, "resume.txt"), content);
            server.Kill(true);
            _servers.Remove(server);
            await Task.Delay(4000); // write-back fires and the upload fails/retries
            var live = await first.Session.QuerySyncStateAsync(CancellationToken.None);
            _out.WriteLine($"live after server down: {live.Kind} pending={live.PendingCount}");
            Assert.True(live.RequiresProtection, "upload should be pending while the server is down");
            var health = await first.Session.ProbeRemoteAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            _out.WriteLine($"remote health with server down: {health.Health} {health.Error}");
            Assert.NotEqual(RemoteHealth.Healthy, health.Health);
        }
        finally
        {
            var stop = await first.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None);
            await first.Session.DisposeAsync();
            _out.WriteLine($"stop #1: {stop.Outcome} graceful={stop.WasGraceful} sync={stop.SyncStateAfterStop.Kind} pending={stop.SyncStateAfterStop.PendingCount}");
            Assert.True(stop.CacheMustRetain, "cache must be retained when uploads are pending");
            Assert.Equal(SyncKind.Pending, stop.SyncStateAfterStop.Kind);
        }
        Assert.False(File.Exists(Path.Combine(served, "resume.txt")), "file must not have reached the server yet");

        // 2) offline scan sees the dirty entry
        _out.WriteLine("cache subdirs after stop #1: " + string.Join(", ", Directory.GetDirectories(Path.Combine(cacheDir, "vfs")).Select(Path.GetFileName)));
        var offline = await engine.InspectOfflineAsync(drive, cacheDir, CancellationToken.None);
        Assert.True(offline.IdentityMatches);
        Assert.Equal(SyncKind.Pending, offline.State.Kind);
        Assert.Equal(1, offline.State.PendingCount);

        // 3) server back, same identity + same cache dir → rclone resumes the upload by itself
        StartServer("webdav", served, port);
        await Task.Delay(1500);
        var second = Assert.IsType<MountStartResult.Ready>(await engine.StartAsync(Request(), null, CancellationToken.None));
        var uploaded = false;
        try
        {
            _out.WriteLine("cache subdirs after mount #2: " + string.Join(", ", Directory.GetDirectories(Path.Combine(cacheDir, "vfs")).Select(Path.GetFileName)));
            Assert.Equal(RemoteHealth.Healthy, second.InitialHealth.Health);
            uploaded = await WaitUntilAsync(async () =>
            {
                var st = await second.Session.QuerySyncStateAsync(CancellationToken.None);
                _out.WriteLine($"  resume wait: {st.Kind} pending={st.PendingCount} uploading={st.UploadingCount} fileOnServer={File.Exists(Path.Combine(served, "resume.txt"))}");
                return !st.RequiresProtection && File.Exists(Path.Combine(served, "resume.txt"));
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            var stop = await second.Session.StopAsync(StopPolicy.WaitForUploads, null, CancellationToken.None);
            await second.Session.DisposeAsync();
            _out.WriteLine($"stop #2: {stop.Outcome} sync={stop.SyncStateAfterStop.Kind}");
            TestSupport.DumpDriveLogs(_out, paths, nameof(PendingUploadSurvivesStopAndResumesOnReconnect));
        }
        Assert.True(uploaded, "pending upload should resume automatically after reconnect");
        Assert.Equal(content, File.ReadAllText(Path.Combine(served, "resume.txt")));
        var offlineAfter = await engine.InspectOfflineAsync(drive, cacheDir, CancellationToken.None);
        Assert.Equal(SyncKind.Clean, offlineAfter.State.Kind);
    }

    [Fact]
    public async Task SftpMountReadsAndWrites()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var served = Path.Combine(_root, "sftp");
        Directory.CreateDirectory(Path.Combine(served, "sub"));
        File.WriteAllText(Path.Combine(served, "sub", "hello.txt"), "hello from sftp");
        var port = FreePort();
        StartServer("sftp", served, port);
        await Task.Delay(2500); // host key generation

        var (engine, paths) = BuildEngine();
        var letter = TestSupport.PickLetter();
        var drive = new Drive
        {
            Name = "IT SFTP",
            StorageType = StorageType.Sftp,
            Mount = new MountOptions { DriveLetter = letter.ToString(), NetworkMode = false },
            Sftp = new SftpConnection { Host = "127.0.0.1", Port = port, Username = "u", AuthMode = SftpAuthMode.Password, Path = "sub", HostKeyCheck = HostKeyCheckMode.None, DisableHashCheck = true },
        };
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        var settings = new AppSettings();
        var request = new MountRequest(drive, new DriveSecrets(Password: "p"), settings, paths.CacheDirFor(drive, settings), paths.RunDirFor(drive), paths.KnownHostsFile);
        var result = await engine.StartAsync(request, null, CancellationToken.None);
        if (result is MountStartResult.Failed f) _out.WriteLine(f.Error.Kind + ": " + f.RedactedTail);
        var ready = Assert.IsType<MountStartResult.Ready>(result);
        try
        {
            Assert.Equal(RemoteHealth.Healthy, ready.InitialHealth.Health);
            var root = letter + ":\\";
            Assert.Equal("hello from sftp", File.ReadAllText(Path.Combine(root, "hello.txt"))); // path "sub" is relative to the served root
            File.WriteAllText(Path.Combine(root, "written.txt"), "via sftp");
            Assert.True((await ready.Session.CheckAliveAsync(CancellationToken.None)).Alive);
            await Task.Delay(1500); // WinFsp delivers Close asynchronously; give rclone a moment to queue the write-back
        }
        finally
        {
            var stop = await ready.Session.StopAsync(StopPolicy.WaitForUploads, null, CancellationToken.None);
            await ready.Session.DisposeAsync();
            _out.WriteLine($"sftp stop: {stop.Outcome} sync={stop.SyncStateAfterStop.Kind}");
            TestSupport.DumpDriveLogs(_out, paths, nameof(SftpMountReadsAndWrites));
        }
        Assert.Equal("via sftp", File.ReadAllText(Path.Combine(served, "sub", "written.txt")));
    }

    [Fact]
    public async Task SftpHostKeyScanMatchesRcloneTrust()
    {
        if (!Available) { _out.WriteLine("SKIPPED"); return; }
        var served = Path.Combine(_root, "sftp-hk");
        Directory.CreateDirectory(served);
        File.WriteAllText(Path.Combine(served, "hello.txt"), "hk");
        var port = FreePort();
        StartServer("sftp", served, port);
        await Task.Delay(2500); // host key generation

        // 1) the scanner collects the server's keys without any credentials
        var scanner = new OpenMount.Windows.SshNetHostKeyScanner();
        var endpoint = new SshEndpoint("127.0.0.1", port, null, null, null, null, null, TimeSpan.FromSeconds(10));
        var keys = await scanner.ScanAsync(endpoint, CancellationToken.None);
        _out.WriteLine("scanned host keys: " + string.Join(", ", keys.Select(k => k.Algorithm + " " + k.Sha256Fingerprint)));
        Assert.NotEmpty(keys);
        var allowed = new[] { "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521" };
        Assert.All(keys, k => Assert.Contains(k.Algorithm, allowed));

        var (engine, paths) = BuildEngine();
        var knownHosts = new KnownHostsFile(paths.KnownHostsFile);
        var letter = TestSupport.PickLetter();
        var drive = new Drive
        {
            Name = "IT SFTP HK",
            StorageType = StorageType.Sftp,
            Mount = new MountOptions { DriveLetter = letter.ToString(), NetworkMode = false },
            Sftp = new SftpConnection { Host = "127.0.0.1", Port = port, Username = "u", AuthMode = SftpAuthMode.Password, Path = "", HostKeyCheck = HostKeyCheckMode.Tofu, DisableHashCheck = true },
        };
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        var settings = new AppSettings();
        MountRequest Request() => new(drive, new DriveSecrets(Password: "p"), settings, paths.CacheDirFor(drive, settings), paths.RunDirFor(drive), paths.KnownHostsFile);

        // 2) a key of the right shape but wrong value must stop the mount in pre-flight
        var sample = keys.FirstOrDefault(k => k.Algorithm == "ssh-ed25519") ?? keys.First(k => k.Algorithm == "ssh-rsa");
        var bytes = Convert.FromBase64String(sample.Base64Key);
        bytes[^1] ^= 0x5A;
        knownHosts.Replace("127.0.0.1", port, new[] { new HostKeyEntry(sample.Algorithm, Convert.ToBase64String(bytes)) });
        var wrong = await engine.StartAsync(Request(), null, CancellationToken.None);
        if (wrong is MountStartResult.Ready r) { await r.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None); await r.Session.DisposeAsync(); }
        var failed = Assert.IsType<MountStartResult.Failed>(wrong);
        _out.WriteLine($"wrong key: {failed.Error.Kind} / {failed.Error.TechnicalDetails}");
        Assert.Equal(MountErrorKind.HostKeyMismatch, failed.Error.Kind);

        // 3) the scanned keys let rclone verify the server and mount
        knownHosts.Replace("127.0.0.1", port, keys);
        var result = await engine.StartAsync(Request(), null, CancellationToken.None);
        if (result is MountStartResult.Failed f) _out.WriteLine(f.Error.Kind + ": " + f.Error.TechnicalDetails);
        var ready = Assert.IsType<MountStartResult.Ready>(result);
        try
        {
            Assert.Equal(RemoteHealth.Healthy, ready.InitialHealth.Health);
            Assert.Equal("hk", File.ReadAllText(letter + ":\\hello.txt"));
        }
        finally
        {
            await ready.Session.StopAsync(StopPolicy.StopAndRetainCache, null, CancellationToken.None);
            await ready.Session.DisposeAsync();
            TestSupport.DumpDriveLogs(_out, paths, nameof(SftpHostKeyScanMatchesRcloneTrust));
        }
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
