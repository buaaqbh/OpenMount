using System.IO;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;
using OpenMount.Rclone;
using OpenMount.Windows;

namespace OpenMount.App;

/// <summary>Composition root. Everything the UI needs, wired exactly once.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public required AppPaths Paths { get; init; }
    public required Redactor Redactor { get; init; }
    public required FileAppLog Log { get; init; }
    public required SettingsService Settings { get; init; }
    public required DriveRepository Repository { get; init; }
    public required ICredentialStore CredentialStore { get; init; }
    public required SessionCredentialCache SessionCache { get; init; }
    public required CredentialResolver Credentials { get; init; }
    public required KnownHostsFile KnownHosts { get; init; }
    public required RcloneLocator Locator { get; init; }
    public required RcloneMountEngine Engine { get; init; }
    public required DriveManager Manager { get; init; }
    public required Services.UserInteraction Interaction { get; init; }
    public required NetworkMonitor Network { get; init; }
    public required DriveValidator Validator { get; init; }
    public required DriveLetterService DriveLetters { get; init; }
    public required string ExePath { get; init; }

    public static AppServices Create()
    {
        var paths = AppPaths.Default;
        paths.EnsureCreated();
        var redactor = new Redactor();
        var settings = new SettingsService(paths, new MemoryAppLog());
        settings.Load();
        var log = new FileAppLog(paths.LogsDir, "app", redactor, settings.Current.Advanced.LogRetainDays);
        log.Info("app", $"OpenMount {typeof(AppServices).Assembly.GetName().Version} starting");
        var repo = new DriveRepository(paths, log);
        var store = new CredentialManagerStore();
        var sessionCache = new SessionCredentialCache();
        var credentials = new CredentialResolver(store, sessionCache);
        var knownHosts = new KnownHostsFile(paths.KnownHostsFile);
        var locator = new RcloneLocator();
        var proxy = new ProxyResolver(new SystemProxyReader());
        var engine = new RcloneMountEngine(locator, new MountPointInspector(), new JobObjectFactory(), proxy, redactor, log, paths)
        {
            AppVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        };
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenMount.exe");
        var interaction = new Services.UserInteraction();
        var manager = new DriveManager(repo, settings, engine, credentials, store, knownHosts, new SshNetHostKeyScanner(), interaction, paths, log, redactor,
            new ShellIntegration(exePath), new FileSessionLockFactory(), proxy);
        return new AppServices
        {
            Paths = paths,
            Redactor = redactor,
            Log = log,
            Settings = settings,
            Repository = repo,
            CredentialStore = store,
            SessionCache = sessionCache,
            Credentials = credentials,
            KnownHosts = knownHosts,
            Locator = locator,
            Engine = engine,
            Manager = manager,
            Interaction = interaction,
            Network = new NetworkMonitor(),
            Validator = new DriveValidator(new WindowsValidationEnvironment()),
            DriveLetters = new DriveLetterService(),
            ExePath = exePath,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Network.Dispose();
        await Manager.DisposeAsync();
        Log.Dispose();
    }
}
