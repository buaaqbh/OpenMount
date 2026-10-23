using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;
using OpenMount.Rclone;
using Xunit;

namespace OpenMount.Tests;

public class ArgsAndEnvironmentTests
{
    private static readonly Dictionary<string, string> PollutedParent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["TEMP"] = @"C:\Temp",
        ["RCLONE_VERBOSE"] = "2",
        ["RCLONE_NO_CHECK_CERTIFICATE"] = "true",
        ["RCLONE_CONFIG"] = @"C:\evil\rclone.conf",
        ["HTTPS_PROXY"] = "http://leak:1",
        ["HTTP_PROXY"] = "http://leak:1",
        ["ALL_PROXY"] = "socks5://leak:1",
    };

    public static Drive WebDav(WebDavAuthMode auth = WebDavAuthMode.Basic, bool network = true) => new()
    {
        Name = "NAS Photos",
        StorageType = StorageType.WebDav,
        Mount = new MountOptions { DriveLetter = "Z", NetworkMode = network },
        WebDav = new WebDavConnection { Host = "nas.example.com", Port = 5006, Path = "/photos", AuthMode = auth, Username = "john", TimeoutSeconds = 45 },
    };

    public static Drive Sftp(SftpAuthMode auth = SftpAuthMode.Password, string path = "reports") => new()
    {
        Name = "Backup",
        StorageType = StorageType.Sftp,
        Mount = new MountOptions { DriveLetter = "Y", NetworkMode = true },
        Sftp = new SftpConnection { Host = "10.0.0.2", Port = 2222, Username = "john", AuthMode = auth, Path = path, KeyFilePath = @"C:\keys\id_ed25519" },
    };

    [Theory]
    [InlineData(WebDavAuthMode.Basic, true)]
    [InlineData(WebDavAuthMode.Basic, false)]
    [InlineData(WebDavAuthMode.Anonymous, true)]
    [InlineData(WebDavAuthMode.Bearer, true)]
    public void WebDav_ArgsNeverContainSecrets_AndEnvIsControlled(WebDavAuthMode auth, bool network)
    {
        var drive = WebDav(auth, network);
        var secrets = new DriveSecrets(Password: "S3cret-Pass", BearerToken: "T0ken-Value");
        var settings = new AppSettings();
        var args = RcloneArgsBuilder.Build(drive, settings, @"C:\cache\abc", 55123);
        var env = ProcessEnvironmentBuilder.Build(drive, secrets, settings, null, "rcu", "rcp-secret", @"C:\kh", PollutedParent);

        foreach (var s in secrets.All()) Assert.DoesNotContain(args.Args, a => a.Contains(s));
        Assert.DoesNotContain(args.CommandLine, "S3cret");
        Assert.Contains("--config=", args.Args);
        Assert.Equal(network, args.Args.Contains("--network-mode"));
        Assert.Equal(network ? @"\\OpenMount\NAS Photos" : "NAS Photos", args.VolumeName);
        Assert.Equal("NAS Photos", args.VolumeLabel);
        Assert.Contains("--vfs-cache-min-free-space", args.Args);
        Assert.Contains("45s", args.Args);
        foreach (var f in new[] { "--log-file", "--daemon", "--rc-serve", "--rc-no-auth", "--password-command" })
            Assert.DoesNotContain(args.Args, a => a.StartsWith(f));

        var prefix = "RCLONE_CONFIG_" + drive.RemoteName.ToUpperInvariant() + "_";
        Assert.Equal("webdav", env[prefix + "TYPE"]);
        Assert.Equal("https://nas.example.com:5006/photos", env[prefix + "URL"]);
        switch (auth)
        {
            case WebDavAuthMode.Basic:
                Assert.Equal("john", env[prefix + "USER"]);
                Assert.NotEqual("S3cret-Pass", env[prefix + "PASS"]);
                Assert.Equal("S3cret-Pass", Obscure.Decode(env[prefix + "PASS"]));
                Assert.False(env.ContainsKey(prefix + "BEARER_TOKEN"));
                break;
            case WebDavAuthMode.Anonymous:
                Assert.False(env.ContainsKey(prefix + "USER"));
                Assert.False(env.ContainsKey(prefix + "PASS"));
                break;
            case WebDavAuthMode.Bearer:
                Assert.Equal("T0ken-Value", env[prefix + "BEARER_TOKEN"]);
                Assert.False(env.ContainsKey(prefix + "USER"));
                break;
        }
        // Nothing from the polluted parent environment leaks through.
        Assert.False(env.ContainsKey("RCLONE_VERBOSE"));
        Assert.False(env.ContainsKey("RCLONE_NO_CHECK_CERTIFICATE"));
        Assert.False(env.ContainsKey("RCLONE_CONFIG"));
        Assert.False(env.ContainsKey("HTTPS_PROXY"));
        Assert.False(env.ContainsKey("ALL_PROXY"));
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.Equal("rcp-secret", env["RCLONE_RC_PASS"]);
        // Secrets only live under keys the redactor knows about.
        foreach (var kv in env)
            if (kv.Value.Contains("S3cret-Pass") || kv.Value.Contains("T0ken-Value") || kv.Value == "rcp-secret")
                Assert.True(ProcessEnvironmentBuilder.IsSecretKey(kv.Key), kv.Key);
    }

    [Fact]
    public void WebDav_ProxyOnlyWhenEnabled()
    {
        var drive = WebDav();
        var proxy = new EffectiveProxy(ProxyScheme.Http, "proxy.corp", 3128, "u", "proxy-pw", new[] { "localhost" });
        var env = ProcessEnvironmentBuilder.Build(drive, DriveSecrets.Empty, new AppSettings(), proxy, "a", "b", "kh", PollutedParent);
        Assert.Equal("http://u:proxy-pw@proxy.corp:3128", env["HTTPS_PROXY"]);
        Assert.Equal("localhost", env["NO_PROXY"]);
        Assert.True(ProcessEnvironmentBuilder.IsSecretKey("HTTPS_PROXY"));
    }

    [Theory]
    [InlineData(SftpAuthMode.Password)]
    [InlineData(SftpAuthMode.Key)]
    [InlineData(SftpAuthMode.KeyAndPassword)]
    public void Sftp_EnvFollowsAuthMode(SftpAuthMode auth)
    {
        var drive = Sftp(auth);
        var secrets = new DriveSecrets(Password: "login-pw", KeyPassphrase: "key-pp");
        var env = ProcessEnvironmentBuilder.Build(drive, secrets, new AppSettings(), null, "a", "b", @"C:\kh\known_hosts", PollutedParent);
        var prefix = "RCLONE_CONFIG_" + drive.RemoteName.ToUpperInvariant() + "_";
        Assert.Equal("sftp", env[prefix + "TYPE"]);
        Assert.Equal("2222", env[prefix + "PORT"]);
        Assert.Equal(@"C:\kh\known_hosts", env[prefix + "KNOWN_HOSTS_FILE"]);
        Assert.Equal(auth != SftpAuthMode.Key, env.ContainsKey(prefix + "PASS"));
        Assert.Equal(auth != SftpAuthMode.Password, env.ContainsKey(prefix + "KEY_FILE"));
        Assert.Equal(auth != SftpAuthMode.Password, env.ContainsKey(prefix + "KEY_FILE_PASS"));
        if (env.TryGetValue(prefix + "PASS", out var p)) Assert.Equal("login-pw", Obscure.Decode(p));
        if (env.TryGetValue(prefix + "KEY_FILE_PASS", out var k)) Assert.Equal("key-pp", Obscure.Decode(k));
    }

    [Theory]
    [InlineData("", "om", ":")]
    [InlineData("reports", "om", ":reports")]
    [InlineData("/srv/data", "om", ":/srv/data")]
    public void Sftp_MountSourceFollowsPathSemantics(string path, string _, string expectedSuffix)
    {
        var drive = Sftp(path: path);
        var args = RcloneArgsBuilder.Build(drive, new AppSettings(), @"C:\cache\x", 1);
        Assert.Equal(drive.RemoteName + expectedSuffix, args.MountSource);
    }

    [Fact]
    public void Sftp_ProxyUsesBackendOptionsNotEnvironment()
    {
        var drive = Sftp();
        drive.Mount.UseProxy = true;
        var socks = new EffectiveProxy(ProxyScheme.Socks5, "proxy.corp", 1080, "pu", "pp", Array.Empty<string>());
        var env = ProcessEnvironmentBuilder.Build(drive, DriveSecrets.Empty, new AppSettings(), socks, "a", "b", "kh", PollutedParent);
        var prefix = "RCLONE_CONFIG_" + drive.RemoteName.ToUpperInvariant() + "_";
        Assert.Equal("pu:pp@proxy.corp:1080", env[prefix + "SOCKS_PROXY"]);
        Assert.False(env.ContainsKey("HTTPS_PROXY"));
        var http = new EffectiveProxy(ProxyScheme.Http, "proxy.corp", 3128, null, null, Array.Empty<string>());
        env = ProcessEnvironmentBuilder.Build(drive, DriveSecrets.Empty, new AppSettings(), http, "a", "b", "kh", PollutedParent);
        Assert.Equal("http://proxy.corp:3128", env[prefix + "HTTP_PROXY"]);
    }

    [Fact]
    public void AdvancedOptionsMapToFlags()
    {
        var drive = WebDav();
        drive.Mount.ReadOnly = true;
        drive.Advanced = new AdvancedOptions
        {
            VfsCacheMode = VfsCacheMode.Writes, CacheCleanupTarget = "20G", ReportedCapacity = "1T", FileSystemName = FileSystemNameMode.Ntfs,
            DotHidden = true, NoModTime = true, BwLimitUp = "10M", Transfers = 8,
        };
        var args = RcloneArgsBuilder.Build(drive, new AppSettings(), @"C:\cache\x", 1).Args.ToList();
        Assert.Contains("--read-only", args);
        Assert.Equal("writes", args[args.IndexOf("--vfs-cache-mode") + 1]);
        Assert.Equal("20G", args[args.IndexOf("--vfs-cache-max-size") + 1]);
        Assert.Equal("1T", args[args.IndexOf("--vfs-disk-space-total-size") + 1]);
        Assert.Equal("--ExactFileSystemName=NTFS", args[args.IndexOf("--fuse-flag") + 1]);
        Assert.Equal("dothidden", args[args.IndexOf("-o") + 1]);
        Assert.Contains("--no-modtime", args);
        Assert.Equal("10M:off", args[args.IndexOf("--bwlimit") + 1]);
        Assert.Equal("8", args[args.IndexOf("--transfers") + 1]);
    }

    [Fact]
    public void FolderMountPointAndLocalDisk()
    {
        var drive = WebDav(network: false);
        drive.Mount.Kind = MountKind.Folder;
        drive.Mount.FolderPath = @"D:\Mounts\NAS";
        var args = RcloneArgsBuilder.Build(drive, new AppSettings(), @"C:\cache\x", 1);
        Assert.Equal(@"D:\Mounts\NAS", args.MountPoint);
        Assert.DoesNotContain("--network-mode", args.Args);
    }
}
