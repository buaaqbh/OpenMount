namespace OpenMount.Core.Models;

public sealed class GeneralSettings
{
    public bool RunAtStartup { get; set; }
    public bool OpenExplorerOnConnect { get; set; }
    public bool UseStorageIcons { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "auto";
    public bool MinimizeToTrayHintShown { get; set; }
}

public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public ProxyScheme Scheme { get; set; } = ProxyScheme.Http;
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public List<string> Bypass { get; set; } = new();
}

public sealed class CacheSettings
{
    public string? RootPath { get; set; }
    public string CleanupTargetPerDrive { get; set; } = "10G";
    public string MaxAge { get; set; } = "24h";
    public string MinFreeSpace { get; set; } = "2G";
    public string LowSpaceWarning { get; set; } = "5G";
}

public sealed class AdvancedSettings
{
    public RcloneMode RcloneMode { get; set; } = RcloneMode.Bundled;
    public string? RclonePath { get; set; }
    public string LogLevel { get; set; } = "NOTICE";
    public int LogRetainDays { get; set; } = 7;
    public bool BlockShutdownWhileUnsynced { get; set; } = true;
    public int MountReadyTimeoutSeconds { get; set; } = 30;
    public int RemoteProbeTimeoutSeconds { get; set; } = 15;
    /// <summary>UNC server part used in network mode: \\<prefix>\<name>.</summary>
    public string NetworkSharePrefix { get; set; } = "OpenMount";
}

public sealed class UpdateSettings
{
    public bool CheckAutomatically { get; set; } = true;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? SkippedVersion { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public GeneralSettings General { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
}
