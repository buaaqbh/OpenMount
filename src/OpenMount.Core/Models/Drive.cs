using System.Text;

namespace OpenMount.Core.Models;

public sealed class MountOptions
{
    public MountKind Kind { get; set; } = MountKind.DriveLetter;
    /// <summary>Single upper-case letter, e.g. "Z".</summary>
    public string? DriveLetter { get; set; }
    public string? FolderPath { get; set; }
    public bool NetworkMode { get; set; } = true;
    public bool ReadOnly { get; set; }
    public bool AutoConnectAtStart { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool? OpenExplorer { get; set; }
    public bool UseProxy { get; set; }

    public string MountPointDisplay => Kind == MountKind.DriveLetter
        ? (string.IsNullOrEmpty(DriveLetter) ? "?:" : DriveLetter.ToUpperInvariant() + ":")
        : FolderPath ?? string.Empty;

    /// <summary>The mount point passed to rclone ("Z:" or a folder path).</summary>
    public string ToMountPoint() => Kind == MountKind.DriveLetter
        ? (DriveLetter ?? throw new InvalidOperationException("Drive letter not set")).ToUpperInvariant() + ":"
        : FolderPath ?? throw new InvalidOperationException("Folder path not set");
}

public sealed class WebDavConnection
{
    public string Scheme { get; set; } = "https";
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string Path { get; set; } = "/";
    public WebDavAuthMode AuthMode { get; set; } = WebDavAuthMode.Basic;
    public string? Username { get; set; }
    public WebDavVendor Vendor { get; set; } = WebDavVendor.Other;
    public TlsMode TlsMode { get; set; } = TlsMode.Verify;
    public string? CaCertPath { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    public int EffectivePort => Port ?? (string.Equals(Scheme, "http", StringComparison.OrdinalIgnoreCase) ? 80 : 443);

    public static string NormalizePath(string? path)
    {
        var p = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/");
        return p;
    }

    public string BuildUrl()
    {
        var sb = new StringBuilder();
        sb.Append(Scheme.ToLowerInvariant()).Append("://");
        var host = Host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]"; // IPv6 literal
        sb.Append(host);
        if (Port.HasValue) sb.Append(':').Append(Port.Value);
        var path = NormalizePath(Path);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
            sb.Append('/').Append(Uri.EscapeDataString(seg));
        if (segments.Length == 0 || path.EndsWith('/')) sb.Append('/');
        return sb.ToString();
    }
}

public sealed class SftpConnection
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    /// <summary>Raw user input; semantics resolved by <see cref="Services.SftpPathResolver"/>.</summary>
    public string Path { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public SftpAuthMode AuthMode { get; set; } = SftpAuthMode.Password;
    public string? KeyFilePath { get; set; }
    public HostKeyCheckMode HostKeyCheck { get; set; } = HostKeyCheckMode.Tofu;
    public bool SetModTime { get; set; } = true;
    public bool? DisableHashCheck { get; set; }
    public int IdleTimeoutSeconds { get; set; } = 60;
    public string? ShellType { get; set; }
    public int? Connections { get; set; }
    public int? Concurrency { get; set; }
}

/// <summary>Structured advanced options. There is deliberately no free-text argument field (review R03).</summary>
public sealed class AdvancedOptions
{
    public VfsCacheMode? VfsCacheMode { get; set; }
    /// <summary>rclone size suffix, e.g. "10G". Cleanup target, not a hard quota.</summary>
    public string? CacheCleanupTarget { get; set; }
    public string? CacheMaxAge { get; set; }
    public string? CacheMinFreeSpace { get; set; }
    public string? DirCacheTime { get; set; }
    public string? WriteBack { get; set; }
    public string? ReadChunkSize { get; set; }
    public string? ReadAhead { get; set; }
    public string? BufferSize { get; set; }
    public int? Transfers { get; set; }
    public string? ReportedCapacity { get; set; }
    public FileSystemNameMode? FileSystemName { get; set; }
    public bool DotHidden { get; set; }
    public bool NoModTime { get; set; }
    public string? BwLimitUp { get; set; }
    public string? BwLimitDown { get; set; }
}

public sealed class LastStatus
{
    public string? State { get; set; }
    public string? RemoteHealth { get; set; }
    public string? SyncState { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? At { get; set; }
}

public sealed class RecoveryInfo
{
    public string Reason { get; set; } = "pendingUploads";
    public int PendingCount { get; set; }
    public long PendingBytes { get; set; }
    public string? IdentityHash { get; set; }
    public bool AbnormalStop { get; set; }
    public DateTimeOffset Since { get; set; }
}

public sealed class Drive
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public StorageType StorageType { get; set; }
    public string? Preset { get; set; }
    public MountOptions Mount { get; set; } = new();
    public WebDavConnection? WebDav { get; set; }
    public SftpConnection? Sftp { get; set; }
    public CredentialPolicy CredentialPolicy { get; set; } = CredentialPolicy.Store;
    public AdvancedOptions Advanced { get; set; } = new();
    public int SortIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public LastStatus? Last { get; set; }
    public RecoveryInfo? Recovery { get; set; }
    /// <summary>Persisted copy of the identity hash; recomputed on save.</summary>
    public string? RemoteIdentityHash { get; set; }

    public string ShortId => Id.ToString("N")[..8];
    /// <summary>rclone remote name; only letters/digits so it can be expressed as an environment variable.</summary>
    public string RemoteName => "om" + ShortId;

    /// <summary>"user@host:port" style description used in the drive list.</summary>
    public string AccountDisplay => StorageType switch
    {
        StorageType.WebDav when WebDav is not null =>
            (WebDav.AuthMode == WebDavAuthMode.Basic && !string.IsNullOrEmpty(WebDav.Username) ? WebDav.Username + "@" : string.Empty)
            + WebDav.Host + ":" + WebDav.EffectivePort + " " + WebDavConnection.NormalizePath(WebDav.Path),
        StorageType.Sftp when Sftp is not null =>
            Sftp.Username + "@" + Sftp.Host + ":" + Sftp.Port + " " + Services.SftpPathResolver.Resolve(Sftp.Path).Display,
        _ => string.Empty
    };

    public Drive Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, JsonDefaults.Options);
        return System.Text.Json.JsonSerializer.Deserialize<Drive>(json, JsonDefaults.Options)!;
    }
}
