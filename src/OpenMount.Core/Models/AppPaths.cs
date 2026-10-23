namespace OpenMount.Core.Models;

/// <summary>All per-user file locations. A custom root is used by tests.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        ConfigDir = Path.Combine(root, "config");
        LogsDir = Path.Combine(root, "logs");
        DriveLogsDir = Path.Combine(LogsDir, "drives");
        RunDir = Path.Combine(root, "run");
        DefaultCacheRoot = Path.Combine(root, "cache");
    }

    /// <summary>Environment variable that relocates the whole data root (tests, side-by-side verification).</summary>
    public const string RootOverrideVariable = "OPENMOUNT_ROOT";

    /// <summary>
    /// Portable mode: when this file sits next to OpenMount.exe, configuration, logs and the cache live in a "data" folder
    /// beside it instead of %LOCALAPPDATA%\OpenMount. Delete the file to go back to the per-user location.
    /// </summary>
    public const string PortableMarkerFile = "portable.txt";

    public static AppPaths Default { get; } = new(ResolveDefaultRoot());

    /// <summary>True when the data root was chosen by the portable marker (shown in the UI so users know where data lives).</summary>
    public static bool IsPortable { get; private set; }

    private static string ResolveDefaultRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var marker = File.Exists(Path.Combine(baseDir, PortableMarkerFile));
        var root = ResolveRoot(Environment.GetEnvironmentVariable(RootOverrideVariable), baseDir, marker,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        IsPortable = marker && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootOverrideVariable));
        return root;
    }

    /// <summary>Precedence: explicit OPENMOUNT_ROOT, then the portable marker next to the executable, then the per-user folder.</summary>
    internal static string ResolveRoot(string? envOverride, string baseDirectory, bool portableMarkerExists, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(envOverride) && Path.IsPathFullyQualified(envOverride)) return envOverride;
        if (portableMarkerExists) return Path.Combine(baseDirectory, "data");
        return Path.Combine(localAppData, "OpenMount");
    }

    public string Root { get; }
    public string ConfigDir { get; }
    public string LogsDir { get; }
    public string DriveLogsDir { get; }
    public string RunDir { get; }
    public string DefaultCacheRoot { get; }

    public string DrivesFile => Path.Combine(ConfigDir, "drives.json");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string KnownHostsFile => Path.Combine(ConfigDir, "known_hosts");
    public string ConfigLockFile => Path.Combine(ConfigDir, ".lock");

    public string CacheRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Cache.RootPath) ? DefaultCacheRoot : settings.Cache.RootPath!;

    public string CacheDirFor(Drive drive, AppSettings settings) => Path.Combine(CacheRoot(settings), drive.ShortId);
    public string RunDirFor(Drive drive) => Path.Combine(RunDir, drive.ShortId);
    public string SessionLockFileFor(Drive drive) => Path.Combine(RunDirFor(drive), "session.lock");
    public string DriveLogPrefix(Drive drive) => drive.ShortId;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(DriveLogsDir);
        Directory.CreateDirectory(RunDir);
        Directory.CreateDirectory(DefaultCacheRoot);
    }
}
