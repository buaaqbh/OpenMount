using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenMount.App.Services;
using OpenMount.Core.Logging;
using OpenMount.Windows;
using Wpf.Ui.Appearance;

namespace OpenMount.App;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly SingleInstance _instance;
    private AppServices? _services;
    private TrayService? _tray;
    private MainWindow? _main;
    private bool _exiting;

    public App(string[] args, SingleInstance instance)
    {
        _args = args;
        _instance = instance;
        InitializeComponent();
    }

    /// <summary>Only exists for the XAML-generated entry point, which is never used (see <see cref="Program"/>).</summary>
    public App() : this(Array.Empty<string>(), new SingleInstance())
    {
    }

    public AppServices Services => _services ?? throw new InvalidOperationException("services not initialised");
    public MainWindow MainWindowInstance => _main ?? throw new InvalidOperationException("main window not created");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Brand blue drives WPF-UI's accent resources (primary buttons, focus, selection).
        ApplicationAccentColorManager.Apply((Color)Resources["AccentColor"], ApplicationTheme.Light);
        DispatcherUnhandledException += (_, ex) =>
        {
            _services?.Log.Error("app", "unhandled UI exception", ex.Exception);
            _services?.Log.Error("app", "stack: " + ex.Exception.StackTrace?.Replace(Environment.NewLine, " | "));
            MessageBox.Show(ex.Exception.Message, "OpenMount 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        _services = AppServices.Create();
        _services.Interaction.Attach(this);
        _tray = new TrayService(this, _services);
        _services.Interaction.Tray = _tray;
        _main = new MainWindow(this, _services);

        // Missing WinFsp / rclone is shown as a banner in the main window (FR-13); drives can still be added and saved.
        var winfsp = WinFspInfo.Detect();
        var rclone = _services.Locator.Resolve(_services.Settings.Current);
        _main.SetDependencyState(winfsp, rclone);
        _main.ViewModel.DependenciesBecameReady += () => _ = _services.Manager.ConnectAtStartupAsync(CancellationToken.None);

        try
        {
            // Residue from a previous crash: dead WinFsp network mappings under our own UNC namespace (design §13).
            var swept = new MountPointInspector().SweepStaleNetworkMappings(_services.Settings.Current.Advanced.NetworkSharePrefix);
            if (swept.Count > 0) _services.Log.Warn("app", "removed stale network mappings: " + string.Join(", ", swept.Select(c => c + ":")));
        }
        catch (Exception ex)
        {
            _services.Log.Warn("app", "stale mapping sweep failed: " + ex.Message);
        }

        try
        {
            await _services.Manager.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Error("app", "initialisation failed", ex);
            MessageBox.Show("配置加载失败：" + ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (!_args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || !winfsp.Installed) _main.Show();
        _instance.StartServer(args => Dispatcher.BeginInvoke(() => HandleArgs(args)));

        _services.Network.AvailabilityChanged += available => _services.Manager.OnNetworkAvailabilityChanged(available);
        SystemEvents.PowerModeChanged += (_, pe) => { if (pe.Mode == PowerModes.Resume) _services.Manager.OnResumeOrUnlock(); };
        SystemEvents.SessionSwitch += (_, se) => { if (se.Reason == SessionSwitchReason.SessionUnlock) _services.Manager.OnResumeOrUnlock(); };
        SessionEnding += OnSessionEnding;

        if (winfsp.Installed && rclone is not null)
            _ = _services.Manager.ConnectAtStartupAsync(CancellationToken.None);
        HandleArgs(_args);
    }

    private void HandleArgs(string[] args)
    {
        if (_services is null || _main is null) return;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--show":
                    ShowMain();
                    break;
                case "--exit":
                    _ = ExitAsync();
                    break;
                case "--connect-all":
                    _ = _services.Manager.ConnectAllAsync(CancellationToken.None);
                    break;
                case "--disconnect-all":
                    _ = _services.Manager.DisconnectAllAsync(true, CancellationToken.None);
                    break;
                case "--connect" when i + 1 < args.Length:
                    _ = FindSupervisor(args[++i])?.ConnectAsync(true);
                    break;
                case "--disconnect" when i + 1 < args.Length:
                    _ = FindSupervisor(args[++i])?.DisconnectAsync(true);
                    break;
            }
        }
    }

    private Core.Services.DriveSupervisor? FindSupervisor(string nameOrLetter) =>
        _services?.Manager.Supervisors.FirstOrDefault(s =>
            string.Equals(s.Drive.Name, nameOrLetter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Drive.Mount.MountPointDisplay.TrimEnd(':'), nameOrLetter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase));

    public void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (_services is null || _exiting) return;
        if (_services.Settings.Current.Advanced.BlockShutdownWhileUnsynced && _services.Manager.HasUnsyncedData())
        {
            e.Cancel = true;
            ShowMain();
            ShutdownBlocker.Block(_main!, "OpenMount 仍有文件未确认上传完成");
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var ok = await _services.Manager.PrepareExitAsync(interactive: true, CancellationToken.None);
                ShutdownBlocker.Unblock(_main!);
                if (ok) await ExitAsync(skipPrepare: true);
            });
        }
    }

    public async Task ExitAsync(bool skipPrepare = false)
    {
        if (_services is null || _exiting) return;
        if (!skipPrepare && !await _services.Manager.PrepareExitAsync(interactive: true, CancellationToken.None)) return;
        _exiting = true;
        _tray?.Dispose();
        _main?.AllowClose();
        _main?.Close();
        await _services.DisposeAsync();
        Shutdown();
    }

    public bool IsExiting => _exiting;
}
