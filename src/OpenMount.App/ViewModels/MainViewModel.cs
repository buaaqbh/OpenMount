using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMount.App.Services;
using OpenMount.App.Views;
using OpenMount.Core.Engine;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Rclone;
using OpenMount.Windows;

namespace OpenMount.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string WinFspDownloadUrl = "https://winfsp.dev/rel/";
    private const string HelpUrl = "https://rclone.org/commands/rclone_mount/";

    private readonly App _app;
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Window?> _owner;
    private readonly DispatcherTimer _ticker;
    private WinFspInfo _winfsp = new(false, null, null, null);
    private string? _rclonePath;
    private string? _rcloneVersion;
    private bool _dependenciesWereMissing;

    public MainViewModel(App app, AppServices services, Dispatcher dispatcher, Func<Window?> owner)
    {
        _app = app;
        _services = services;
        _dispatcher = dispatcher;
        _owner = owner;
        foreach (var s in services.Manager.Supervisors) Drives.Add(CreateRow(s));
        services.Manager.SupervisorAdded += s => _dispatcher.BeginInvoke(() => { if (Drives.All(r => r.Id != s.Drive.Id)) Drives.Add(CreateRow(s)); UpdateStatus(); });
        services.Manager.SupervisorRemoved += s => _dispatcher.BeginInvoke(() => { var row = Drives.FirstOrDefault(r => r.Id == s.Drive.Id); if (row is not null) Drives.Remove(row); UpdateStatus(); });
        services.Manager.SnapshotChanged += (s, snap) => _dispatcher.BeginInvoke(() => { Drives.FirstOrDefault(r => r.Id == s.Drive.Id)?.Apply(snap); UpdateStatus(); });
        services.Network.AvailabilityChanged += available => _dispatcher.BeginInvoke(() => SetNetwork(available));
        SetNetwork(services.Network.IsAvailable);
        IsPortable = AppPaths.IsPortable;
        PortableToolTip = "数据目录：" + services.Paths.Root;
        _ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { foreach (var r in Drives) r.Tick(); }, dispatcher);
        _ticker.Start();
        UpdateStatus();
    }

    /// <summary>Raised once when WinFsp and rclone become available after having been missing at startup.</summary>
    public event Action? DependenciesBecameReady;

    public ObservableCollection<DriveRowViewModel> Drives { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _driveCountText = string.Empty;
    [ObservableProperty] private string _winFspText = string.Empty;
    [ObservableProperty] private bool _winFspOk;
    [ObservableProperty] private string _rcloneText = string.Empty;
    [ObservableProperty] private bool _rcloneOk;
    [ObservableProperty] private bool _networkOk = true;
    [ObservableProperty] private string _networkText = "网络正常";
    [ObservableProperty] private bool _isPortable;
    [ObservableProperty] private string _portableText = "便携模式 · data\\";
    [ObservableProperty] private string _portableToolTip = string.Empty;
    [ObservableProperty] private bool _showDependencyBanner;
    [ObservableProperty] private string _dependencyTitle = string.Empty;
    [ObservableProperty] private string _dependencyText = string.Empty;
    [ObservableProperty] private bool _showWinFspActions;
    [ObservableProperty] private bool _canInstallWinFsp;
    [ObservableProperty] private bool _installing;

    private DriveRowViewModel CreateRow(DriveSupervisor s) => new(s, new DriveRowActions
    {
        Toggle = ToggleAsync,
        Edit = Edit,
        Duplicate = Duplicate,
        Open = Open,
        Delete = DeleteAsync,
        Details = ShowDetails,
    });

    private void SetNetwork(bool available)
    {
        NetworkOk = available;
        NetworkText = available ? "网络正常" : "网络不可用 · 恢复后自动重连";
    }

    private void UpdateStatus()
    {
        var all = _services.Manager.Supervisors.Select(s => s.Snapshot).ToList();
        var mounted = all.Count(s => s.Lifecycle == LifecycleState.Mounted);
        DriveCountText = all.Count == 0 ? "0 个驱动器" : $"{all.Count} 个驱动器，{mounted} 个已连接";
        IsEmpty = all.Count == 0;
    }

    public void SetDependencyState(WinFspInfo winfsp, string? rclonePath)
    {
        _winfsp = winfsp;
        WinFspOk = winfsp.Installed;
        WinFspText = winfsp.Installed ? "WinFsp " + (winfsp.Version ?? "已安装") : "WinFsp 未安装";
        if (rclonePath != _rclonePath)
        {
            _rclonePath = rclonePath;
            _rcloneVersion = null;
            if (rclonePath is not null) _ = ProbeRcloneAsync(rclonePath);
        }
        RcloneOk = rclonePath is not null;
        RcloneText = rclonePath is null ? "rclone 未找到" : "rclone " + (_rcloneVersion ?? "就绪");

        var problems = new List<string>();
        if (!winfsp.Installed) problems.Add("WinFsp");
        if (rclonePath is null) problems.Add("rclone");
        ShowWinFspActions = !winfsp.Installed;
        CanInstallWinFsp = !winfsp.Installed && FindBundledWinFspMsi() is not null;
        if (problems.Count > 0)
        {
            _dependenciesWereMissing = true;
            DependencyTitle = !winfsp.Installed ? "未检测到 WinFsp 文件系统组件" : "未找到 rclone.exe";
            DependencyText = !winfsp.Installed
                ? "安装 WinFsp 后才能连接驱动器；安装需要管理员权限，若提示重启请重启后再打开 OpenMount。"
                : "请把 rclone.exe 放到程序目录的 bin\\ 下，或在设置 → 依赖中指定路径。";
            ShowDependencyBanner = true;
        }
        else
        {
            ShowDependencyBanner = false;
            if (_dependenciesWereMissing)
            {
                _dependenciesWereMissing = false;
                DependenciesBecameReady?.Invoke();
            }
        }
        UpdateStatus();
    }

    private async Task ProbeRcloneAsync(string path)
    {
        try
        {
            var report = await CapabilityProbe.ProbeAsync(path, CancellationToken.None);
            if (path != _rclonePath) return;
            _rcloneVersion = report.Version;
            RcloneText = report.Ok ? "rclone " + report.Version : "rclone 不可用 · " + report.Error;
            RcloneOk = report.Ok;
        }
        catch (Exception ex)
        {
            _services.Log.Warn("ui", "rclone probe failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ toolbar

    [RelayCommand]
    public void AddDrive()
    {
        var dlg = new NewDriveDialog(_services, null) { Owner = _owner() };
        if (dlg.ShowDialog() != true || dlg.ResultDrive is null) return;
        _ = AddAsync(dlg.ResultDrive, dlg.ResultSecrets, dlg.RememberForSession, dlg.ConnectRequested);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        var dlg = new SettingsWindow(_services) { Owner = _owner() };
        dlg.ShowDialog();
        SetDependencyState(WinFspInfo.Detect(), _services.Locator.Resolve(_services.Settings.Current));
    }

    [RelayCommand]
    public void ShowLog() => new LogWindow(_services) { Owner = _owner() }.Show();

    [RelayCommand]
    private void Help() => OpenUrl(HelpUrl);

    [RelayCommand]
    private Task ImportConfig() => ConfigTransfer.ImportAsync(_owner(), _services);

    // ------------------------------------------------------------------ dependencies (FR-13)

    private static string? FindBundledWinFspMsi()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "redist");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "winfsp-*.msi").FirstOrDefault() : null;
    }

    [RelayCommand]
    private async Task InstallWinFsp()
    {
        var msi = FindBundledWinFspMsi();
        if (msi is null) { OpenUrl(WinFspDownloadUrl); return; }
        Installing = true;
        try
        {
            var p = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qb") { UseShellExecute = true, Verb = "runas" });
            if (p is not null) await p.WaitForExitAsync();
            if (p?.ExitCode == 3010) MessageBox.Show(_owner() ?? new Window(), "WinFsp 已安装，需要重启 Windows 后才能使用。", "OpenMount", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("ui", "WinFsp install failed: " + ex.Message);
            MessageBox.Show(_owner() ?? new Window(), "安装未完成：" + ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Installing = false;
        }
        RecheckDependencies();
    }

    [RelayCommand]
    private void OpenWinFspDownload() => OpenUrl(WinFspDownloadUrl);

    [RelayCommand]
    private void RecheckDependencies() => SetDependencyState(WinFspInfo.Detect(), _services.Locator.Resolve(_services.Settings.Current));

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { _services.Log.Warn("ui", "open url failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ rows

    private async Task ToggleAsync(DriveRowViewModel row)
    {
        var snap = row.Supervisor.Snapshot;
        try
        {
            if (snap.CanDisconnect)
            {
                await row.Supervisor.DisconnectAsync(interactive: true);
            }
            else if (snap.CanConnect)
            {
                await row.Supervisor.ConnectAsync(interactive: true);
                await ShowErrorIfAnyAsync(row);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "toggle failed", ex);
            MessageBox.Show(_owner() ?? new Window(), ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ShowErrorIfAnyAsync(DriveRowViewModel row)
    {
        var snap = row.Supervisor.Snapshot;
        if (snap.Lifecycle != LifecycleState.Error || snap.Error is null) return;
        var dlg = new ErrorDialog(row.Drive, snap.Error) { Owner = _owner() };
        dlg.ShowDialog();
        switch (dlg.Result)
        {
            case ErrorDialogResult.Retry:
                await row.Supervisor.ConnectAsync(interactive: true);
                await ShowErrorIfAnyAsync(row);
                break;
            case ErrorDialogResult.Edit:
                Edit(row);
                break;
            case ErrorDialogResult.ViewLog:
                ShowLog();
                break;
        }
    }

    private async Task AddAsync(Drive drive, DriveSecrets? secrets, bool rememberForSession, bool connect)
    {
        try
        {
            var sup = await _services.Manager.AddAsync(drive, secrets, rememberForSession, CancellationToken.None);
            if (connect)
            {
                await sup.ConnectAsync(interactive: true);
                var row = Drives.FirstOrDefault(r => r.Id == drive.Id);
                if (row is not null) await ShowErrorIfAnyAsync(row);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "add failed", ex);
            MessageBox.Show(_owner() ?? new Window(), ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Edit(DriveRowViewModel row) => EditById(row.Id);

    public void EditById(Guid id)
    {
        var sup = _services.Manager.Get(id);
        if (sup is null) return;
        if (!sup.Snapshot.CanEdit)
        {
            MessageBox.Show(_owner() ?? new Window(), "请先断开驱动器再编辑。", "OpenMount", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new NewDriveDialog(_services, sup.Drive) { Owner = _owner() };
        if (dlg.ShowDialog() != true || dlg.ResultDrive is null) return;
        _ = UpdateAsync(dlg.ResultDrive, dlg.ResultSecrets, dlg.RememberForSession, dlg.ConnectRequested);
    }

    /// <summary>FR-01.8: new id, same connection and options, auto-connect off, letter re-chosen, credentials re-entered.</summary>
    private void Duplicate(DriveRowViewModel row)
    {
        var dlg = new NewDriveDialog(_services, null, template: row.Drive) { Owner = _owner() };
        if (dlg.ShowDialog() != true || dlg.ResultDrive is null) return;
        _ = AddAsync(dlg.ResultDrive, dlg.ResultSecrets, dlg.RememberForSession, dlg.ConnectRequested);
    }

    private async Task UpdateAsync(Drive edited, DriveSecrets? secrets, bool rememberForSession, bool connect)
    {
        try
        {
            var ok = await _services.Manager.UpdateAsync(edited, secrets, rememberForSession, interactive: true, CancellationToken.None);
            if (!ok) return;
            var sup = _services.Manager.Get(edited.Id);
            if (connect && sup is not null)
            {
                await sup.ConnectAsync(interactive: true);
                var row = Drives.FirstOrDefault(r => r.Id == edited.Id);
                if (row is not null) await ShowErrorIfAnyAsync(row);
            }
            else
            {
                Drives.FirstOrDefault(r => r.Id == edited.Id)?.Apply(sup?.Snapshot ?? DriveSnapshot.Initial(edited.Id));
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "update failed", ex);
            MessageBox.Show(_owner() ?? new Window(), ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Open(DriveRowViewModel row)
    {
        if (!row.Supervisor.Snapshot.CanOpen) return;
        new ShellIntegration(_services.ExePath).OpenInExplorer(row.Drive.Mount.ToMountPoint());
    }

    private async Task DeleteAsync(DriveRowViewModel row)
    {
        var snap = row.Supervisor.Snapshot;
        if (!snap.Sync.RequiresProtection)
        {
            var dlg = new ConfirmDialog("删除驱动器", $"删除“{row.Drive.Name}”（{row.MountPoint}）？",
                "只会删除 OpenMount 中的条目与本地缓存，远端数据不受影响。", "删除", danger: true) { Owner = _owner() };
            if (dlg.ShowDialog() != true) return;
        }
        try
        {
            await _services.Manager.DeleteAsync(row.Id, interactive: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "delete failed", ex);
            MessageBox.Show(_owner() ?? new Window(), ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowDetails(DriveRowViewModel row) => ShowDetailsById(row.Id);

    public void ShowDetailsById(Guid id)
    {
        var sup = _services.Manager.Get(id);
        if (sup is null) return;
        var dlg = new DriveDetailsWindow(_services, sup) { Owner = _owner() };
        dlg.ShowDialog();
    }
}
