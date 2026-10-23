using OpenMount.Core.Errors;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Rclone;
using Xunit;

namespace OpenMount.Tests;

public class SftpPathResolverTests
{
    [Theory]
    [InlineData(null, "", SftpPathMeaning.Home)]
    [InlineData("", "", SftpPathMeaning.Home)]
    [InlineData("  ", "", SftpPathMeaning.Home)]
    [InlineData("reports", "reports", SftpPathMeaning.RelativeToHome)]
    [InlineData("/reports", "/reports", SftpPathMeaning.Absolute)]
    [InlineData("a/b/c", "a/b/c", SftpPathMeaning.RelativeToHome)]
    public void Resolves(string? input, string suffix, SftpPathMeaning meaning)
    {
        var r = SftpPathResolver.Resolve(input);
        Assert.Equal(suffix, r.RemoteSuffix);
        Assert.Equal(meaning, r.Meaning);
    }

    [Fact]
    public void DoesNotAddSlashes()
    {
        Assert.Equal("reports", SftpPathResolver.Resolve("reports").RemoteSuffix);
        Assert.Contains("家目录下", SftpPathResolver.Resolve("reports").Display);
        Assert.Contains("根目录下", SftpPathResolver.Resolve("/reports").Display);
    }
}

public class RemoteIdentityAndSafeNameTests
{
    [Fact]
    public void HashIsStableAndCaseInsensitiveOnHost()
    {
        var a = ArgsAndEnvironmentTests.WebDav();
        var b = ArgsAndEnvironmentTests.WebDav();
        b.WebDav!.Host = "NAS.EXAMPLE.COM";
        b.Name = "different name does not matter";
        Assert.Equal(RemoteIdentity.ComputeHash(a), RemoteIdentity.ComputeHash(b));
        b.WebDav.Path = "/other";
        Assert.NotEqual(RemoteIdentity.ComputeHash(a), RemoteIdentity.ComputeHash(b));
    }

    [Fact]
    public void SftpIdentityUsesResolvedPath()
    {
        var a = ArgsAndEnvironmentTests.Sftp(path: "reports");
        var b = ArgsAndEnvironmentTests.Sftp(path: "/reports");
        Assert.NotEqual(RemoteIdentity.ComputeHash(a), RemoteIdentity.ComputeHash(b));
    }

    [Theory]
    [InlineData("NAS: Photos/2024?", "NAS Photos2024")]
    [InlineData("   ", "Drive-1a2b3c4d")]
    [InlineData("<>|\"", "Drive-1a2b3c4d")]
    public void SafeNameStripsInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, SafeName.From(input, "1a2b3c4d"));
    }

    [Fact]
    public void SafeNameIsCappedAt32()
    {
        Assert.Equal(32, SafeName.From(new string('a', 50), "x").Length);
    }

    [Theory]
    [InlineData("10G", 10L * 1024 * 1024 * 1024)]
    [InlineData("512M", 512L * 1024 * 1024)]
    [InlineData("1.5K", 1536)]
    [InlineData("100", 100)]
    public void SizeSuffixParses(string text, long bytes)
    {
        Assert.True(SizeSuffix.TryParseBytes(text, out var v));
        Assert.Equal(bytes, v);
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("Failed to create file system for \"om1:\": 401 Unauthorized", MountErrorKind.Auth)]
    [InlineData("ssh: handshake failed: ssh: unable to authenticate, attempted methods [none password]", MountErrorKind.Auth)]
    [InlineData("knownhosts: key mismatch", MountErrorKind.HostKeyMismatch)]
    [InlineData("knownhosts: key is unknown", MountErrorKind.HostKeyUnknown)]
    [InlineData("dial tcp 10.0.0.2:22: connect: connection refused", MountErrorKind.Unreachable)]
    [InlineData("Get \"https://h/\": dial tcp: lookup h: no such host", MountErrorKind.Unreachable)]
    [InlineData("x509: certificate signed by unknown authority", MountErrorKind.TlsCert)]
    [InlineData("cgofuse: cannot find winfsp", MountErrorKind.WinFspMissing)]
    [InlineData("mountpoint path already exists: Z:", MountErrorKind.MountPointInUse)]
    [InlineData("vfs cache: failed to open: There is not enough space on the disk.", MountErrorKind.CacheDiskFull)]
    [InlineData("error listing: directory not found", MountErrorKind.NotFound)]
    [InlineData("something odd happened", MountErrorKind.Unknown)]
    public void ClassifiesStartupLines(string line, MountErrorKind expected)
    {
        Assert.Equal(expected, ErrorClassifier.Classify(new[] { line }, 1, ClassifyPhase.Startup).Kind);
    }

    [Fact]
    public void RunningExitWithoutMatchIsCrash()
    {
        var e = ErrorClassifier.Classify(new[] { "goroutine 1 [running]:" }, 2, ClassifyPhase.Running);
        Assert.Equal(MountErrorKind.Crashed, e.Kind);
        Assert.True(e.AllowsAutoReconnect);
    }

    [Theory]
    [InlineData(null, RemoteHealth.Healthy)]
    [InlineData("error listing: 401 Unauthorized", RemoteHealth.AuthRequired)]
    [InlineData("i/o timeout", RemoteHealth.Unreachable)]
    [InlineData("403 Forbidden", RemoteHealth.Forbidden)]
    [InlineData("directory not found", RemoteHealth.NotFound)]
    public void ClassifiesProbeResults(string? error, RemoteHealth expected)
    {
        Assert.Equal(expected, ErrorClassifier.ClassifyProbe(error));
    }

    [Fact]
    public void AuthErrorsNeverAutoReconnect()
    {
        Assert.False(MountError.Create(MountErrorKind.Auth, "").AllowsAutoReconnect);
        Assert.False(MountError.Create(MountErrorKind.HostKeyMismatch, "").AllowsAutoReconnect);
        Assert.True(MountError.Create(MountErrorKind.Unreachable, "").AllowsAutoReconnect);
    }
}

public class UnsyncedDataGuardTests
{
    private static readonly SyncState Clean = SyncState.Clean(SyncSource.Live);
    private static readonly SyncState Pending = SyncState.Pending(SyncSource.Offline, 3, 1000);
    private static readonly SyncState Unknown = SyncState.Unknown(SyncSource.Offline);

    [Theory]
    [InlineData(DestructiveOperation.Disconnect)]
    [InlineData(DestructiveOperation.Delete)]
    [InlineData(DestructiveOperation.MigrateCache)]
    [InlineData(DestructiveOperation.ChangeIdentity)]
    public void CleanAlwaysAllows(DestructiveOperation op)
    {
        Assert.IsType<GuardDecision.Allow>(UnsyncedDataGuard.Evaluate(op, Clean));
    }

    [Fact]
    public void DisconnectWithPendingOffersWaitOrRetain_NeverDiscard()
    {
        var d = Assert.IsType<GuardDecision.RequireChoice>(UnsyncedDataGuard.Evaluate(DestructiveOperation.Disconnect, Pending));
        Assert.Contains(GuardChoice.WaitForUploads, d.Choices);
        Assert.Contains(GuardChoice.StopAndRetain, d.Choices);
        Assert.Contains(GuardChoice.Cancel, d.Choices);
        Assert.DoesNotContain(GuardChoice.Discard, d.Choices);
    }

    [Fact]
    public void DeleteWithPendingOffersExplicitDiscard()
    {
        var d = Assert.IsType<GuardDecision.RequireChoice>(UnsyncedDataGuard.Evaluate(DestructiveOperation.Delete, Pending));
        Assert.Contains(GuardChoice.Discard, d.Choices);
        Assert.Contains(GuardChoice.ConnectAndUpload, d.Choices);
    }

    [Fact]
    public void UnknownIsTreatedLikePending()
    {
        Assert.IsType<GuardDecision.RequireChoice>(UnsyncedDataGuard.Evaluate(DestructiveOperation.Exit, Unknown));
        Assert.IsType<GuardDecision.Deny>(UnsyncedDataGuard.Evaluate(DestructiveOperation.CleanCache, Unknown));
    }

    [Fact]
    public void ForceKillIsNeverBlocked()
    {
        Assert.IsType<GuardDecision.Allow>(UnsyncedDataGuard.Evaluate(DestructiveOperation.ForceKill, Pending));
    }
}

public class CacheInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openmount-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void WriteMeta(string relative, bool dirty, long size)
    {
        var p = Path.Combine(_dir, "vfsMeta", "om1", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, $"{{\"ModTime\":\"2026-10-22T00:00:00Z\",\"ATime\":\"2026-10-22T00:00:00Z\",\"Size\":{size},\"Rs\":[],\"Fingerprint\":\"x\",\"Dirty\":{(dirty ? "true" : "false")}}}");
    }

    [Fact]
    public void MissingCacheIsClean()
    {
        var r = CacheInspector.Scan(ArgsAndEnvironmentTests.WebDav(), _dir, false, CancellationToken.None);
        Assert.Equal(SyncKind.Clean, r.State.Kind);
        Assert.True(r.IdentityMatches);
    }

    [Fact]
    public void CountsDirtyEntries()
    {
        WriteMeta("a.txt", true, 100);
        WriteMeta("sub/b.bin", true, 250);
        WriteMeta("c.txt", false, 999);
        var r = CacheInspector.Scan(ArgsAndEnvironmentTests.WebDav(), _dir, false, CancellationToken.None);
        Assert.Equal(SyncKind.Pending, r.State.Kind);
        Assert.Equal(2, r.State.PendingCount);
        Assert.Equal(350, r.State.PendingBytes);
        Assert.Equal(SyncSource.Offline, r.State.Source);
    }

    [Fact]
    public void CorruptMetadataIsUnknown()
    {
        WriteMeta("a.txt", false, 1);
        var p = Path.Combine(_dir, "vfsMeta", "om1", "broken");
        File.WriteAllText(p, "{not json");
        var r = CacheInspector.Scan(ArgsAndEnvironmentTests.WebDav(), _dir, false, CancellationToken.None);
        Assert.Equal(SyncKind.Unknown, r.State.Kind);
    }

    [Fact]
    public void IdentityMismatchIsUnknownAndFlagged()
    {
        var drive = ArgsAndEnvironmentTests.WebDav();
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        CacheInspector.WriteIdentity(_dir, drive, "test");
        drive.WebDav!.Host = "other.example.com";
        drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
        var r = CacheInspector.Scan(drive, _dir, false, CancellationToken.None);
        Assert.False(r.IdentityMatches);
        Assert.Equal(SyncKind.Unknown, r.State.Kind);
    }

    [Fact]
    public void AbnormalStopIsPropagated()
    {
        WriteMeta("a.txt", true, 1);
        var r = CacheInspector.Scan(ArgsAndEnvironmentTests.WebDav(), _dir, true, CancellationToken.None);
        Assert.True(r.State.AbnormalStop);
    }

    [Fact]
    public void DiscardRemovesDataButKeepsIdentity()
    {
        var drive = ArgsAndEnvironmentTests.WebDav();
        CacheInspector.WriteIdentity(_dir, drive, "test");
        WriteMeta("a.txt", true, 1);
        CacheInspector.Discard(_dir);
        Assert.False(Directory.Exists(Path.Combine(_dir, "vfsMeta")));
        Assert.NotNull(CacheInspector.ReadIdentity(_dir));
    }
}

public class RcloneLogParserTests
{
    [Fact]
    public void ParsesJsonLine()
    {
        var e = RcloneLogParser.Parse("{\"level\":\"error\",\"msg\":\"boom\",\"source\":\"x.go:1\",\"time\":\"2026-10-22T10:00:00+08:00\",\"object\":\"file.txt\"}");
        Assert.True(e.IsJson);
        Assert.True(e.IsError);
        Assert.Equal("boom", e.Message);
        Assert.Equal("file.txt", e.Object);
        Assert.NotNull(e.Time);
    }

    [Fact]
    public void FallsBackToText()
    {
        var e = RcloneLogParser.Parse("2026/10/22 10:00:00 ERROR : something");
        Assert.False(e.IsJson);
        Assert.Equal("error", e.Level);
    }
}

public class KnownHostsFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "openmount-kh-" + Guid.NewGuid().ToString("N"), "known_hosts");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { } }

    private static readonly HostKeyEntry Ed = new("ssh-ed25519", Convert.ToBase64String(new byte[32]));
    private static readonly HostKeyEntry Rsa = new("ssh-rsa", Convert.ToBase64String(new byte[64]));

    [Fact]
    public void TrustAndLookupWithNonDefaultPort()
    {
        var kh = new KnownHostsFile(_path);
        Assert.False(kh.HasEntries("Host.Example", 2222));
        kh.Trust("Host.Example", 2222, new[] { Ed, Rsa });
        var entries = kh.GetEntries("host.example", 2222);
        Assert.Equal(2, entries.Count);
        Assert.Contains("[host.example]:2222 ssh-ed25519 ", File.ReadAllText(_path));
        Assert.False(kh.HasEntries("host.example", 22));
        Assert.StartsWith("SHA256:", Ed.Sha256Fingerprint);
        Assert.StartsWith("MD5:", Ed.Md5Fingerprint);
    }

    [Fact]
    public void ReplaceDropsOldKeysOnly()
    {
        var kh = new KnownHostsFile(_path);
        kh.Trust("a", 22, new[] { Ed });
        kh.Trust("b", 22, new[] { Ed });
        kh.Replace("a", 22, new[] { Rsa });
        Assert.Equal("ssh-rsa", Assert.Single(kh.GetEntries("a", 22)).Algorithm);
        Assert.Single(kh.GetEntries("b", 22));
    }
}

public class DriveRepositoryTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "openmount-repo-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { try { Directory.Delete(_paths.Root, true); } catch { } }

    [Fact]
    public void RoundTripsAndKeepsBackup()
    {
        _paths.EnsureCreated();
        var repo = new DriveRepository(_paths, new MemoryAppLog());
        var d = ArgsAndEnvironmentTests.Sftp(SftpAuthMode.KeyAndPassword, "/srv");
        d.Advanced.CacheCleanupTarget = "20G";
        repo.Upsert(d);
        repo.Upsert(ArgsAndEnvironmentTests.WebDav());
        Assert.True(File.Exists(_paths.DrivesFile));
        Assert.True(File.Exists(_paths.DrivesFile + ".bak"));

        var repo2 = new DriveRepository(_paths, new MemoryAppLog());
        repo2.Load();
        Assert.Equal(2, repo2.Drives.Count);
        var loaded = repo2.Find(d.Id)!;
        Assert.Equal(StorageType.Sftp, loaded.StorageType);
        Assert.Equal(SftpAuthMode.KeyAndPassword, loaded.Sftp!.AuthMode);
        Assert.Equal("/srv", loaded.Sftp.Path);
        Assert.Equal("20G", loaded.Advanced.CacheCleanupTarget);
        Assert.Equal(RemoteIdentity.ComputeHash(d), loaded.RemoteIdentityHash);
        var json = File.ReadAllText(_paths.DrivesFile);
        Assert.Contains("\"connection\"", json);
        Assert.DoesNotContain("extraArgs", json);
    }

    [Fact]
    public void MigratesSchemaV1WithoutAuthMode()
    {
        const string v1 = """
        {"schemaVersion":1,"drives":[{"id":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","name":"old","storageType":"webdav",
          "mount":{"kind":"driveLetter","driveLetter":"Z"},
          "connection":{"scheme":"https","host":"h","port":443,"path":"/","username":"bob"},
          "advanced":{"extraArgs":["--evil"]}}]}
        """;
        var drives = DriveRepository.Parse(v1);
        var d = Assert.Single(drives);
        Assert.Equal(WebDavAuthMode.Basic, d.WebDav!.AuthMode);
        Assert.Equal("bob", d.WebDav.Username);
    }
}

public class DriveValidatorTests
{
    private sealed class Env : IValidationEnvironment
    {
        public HashSet<char> Used { get; } = new() { 'C', 'D' };
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { @"D:", @"D:\Mounts" };
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { @"C:\keys\id_ed25519", @"C:\keys\putty.ppk" };
        public bool IsDriveLetterFree(char letter) => !Used.Contains(char.ToUpperInvariant(letter));
        public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\'));
        public bool FileExists(string path) => Files.Contains(path);
        public bool IsLocalNtfsFixedVolume(string path) => true;
    }

    private readonly DriveValidator _v = new(new Env());

    [Fact]
    public void ValidWebDavPasses()
    {
        Assert.Empty(_v.Validate(ArgsAndEnvironmentTests.WebDav(), Array.Empty<Drive>()));
    }

    [Fact]
    public void BearerDoesNotRequireUsername_BasicDoes()
    {
        var bearer = ArgsAndEnvironmentTests.WebDav(WebDavAuthMode.Bearer);
        bearer.WebDav!.Username = null;
        Assert.Empty(_v.Validate(bearer, Array.Empty<Drive>()));
        var basic = ArgsAndEnvironmentTests.WebDav();
        basic.WebDav!.Username = "";
        Assert.Contains(_v.Validate(basic, Array.Empty<Drive>()), e => e.Field == "username");
    }

    [Fact]
    public void DetectsLetterAndNameConflicts()
    {
        var existing = ArgsAndEnvironmentTests.WebDav();
        var d = ArgsAndEnvironmentTests.WebDav();
        d.Id = Guid.NewGuid();
        var errors = _v.Validate(d, new[] { existing });
        Assert.Contains(errors, e => e.Field == "driveLetter");
        Assert.Contains(errors, e => e.Field == "name");
        d.Mount.DriveLetter = "D";
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Field == "driveLetter" && e.Message.Contains("占用"));
    }

    [Fact]
    public void FolderMountMustNotExistAndRequiresLocalDisk()
    {
        var d = ArgsAndEnvironmentTests.WebDav(network: false);
        d.Mount.Kind = MountKind.Folder;
        d.Mount.FolderPath = @"D:\Mounts";
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Field == "folderPath" && e.Message.Contains("尚不存在"));
        d.Mount.FolderPath = @"D:\Mounts\NAS";
        Assert.Empty(_v.Validate(d, Array.Empty<Drive>()));
        d.Mount.NetworkMode = true;
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Field == "folderPath");
        d.Mount.NetworkMode = false;
        d.Mount.FolderPath = @"D:\Nope\NAS";
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Message.Contains("父文件夹"));
    }

    [Fact]
    public void SftpKeyModeNeedsOpenSshKey()
    {
        var d = ArgsAndEnvironmentTests.Sftp(SftpAuthMode.Key);
        Assert.Empty(_v.Validate(d, Array.Empty<Drive>()));
        d.Sftp!.KeyFilePath = @"C:\keys\putty.ppk";
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Message.Contains("OpenSSH"));
        d.Sftp.KeyFilePath = @"C:\keys\missing";
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Field == "keyFile");
        d.Sftp.AuthMode = SftpAuthMode.Agent;
        Assert.Contains(_v.Validate(d, Array.Empty<Drive>()), e => e.Field == "authMode");
    }

    [Fact]
    public void RejectsBadSizesAndDurations()
    {
        var d = ArgsAndEnvironmentTests.WebDav();
        d.Advanced.CacheCleanupTarget = "ten gigs";
        d.Advanced.CacheMaxAge = "24 hours";
        var errors = _v.Validate(d, Array.Empty<Drive>());
        Assert.Contains(errors, e => e.Field == "cacheCleanupTarget");
        Assert.Contains(errors, e => e.Field == "cacheMaxAge");
    }

    [Theory]
    [InlineData("nas.example.com", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("fe80::1", true)]
    [InlineData("", false)]
    [InlineData("bad host", false)]
    public void HostValidation(string host, bool ok)
    {
        Assert.Equal(ok, DriveValidator.IsValidHost(host));
    }
}
