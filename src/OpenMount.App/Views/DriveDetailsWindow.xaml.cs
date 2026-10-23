using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OpenMount.App.ViewModels;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Rclone;
using OpenMount.Windows;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace OpenMount.App.Views;

/// <summary>Three-dimensional state of one drive (lifecycle / remote health / sync) plus cache and the separately named discard command.</summary>
public partial class DriveDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly DriveSupervisor _sup;
    private readonly Action<DriveSupervisor, DriveSnapshot> _handler;
    private readonly DispatcherTimer _timer;
    private string? _cacheDir;
    private long? _cacheBytes;

    public DriveDetailsWindow(AppServices services, DriveSupervisor sup)
    {
        _services = services;
        _sup = sup;
        InitializeComponent();
        var d = sup.Drive;
        Title = $"驱动器详情 · {d.Name} ({d.Mount.MountPointDisplay})";
        TypeIcon.Symbol = StatusPresentation.TypeSymbol(d);
        NameText.Text = d.Name;
        LetterText.Text = d.Mount.MountPointDisplay;
        HeaderChips.ItemsSource = new[] { new Chip(StatusPresentation.TypeLabel(d)), new Chip(d.Mount.NetworkMode ? "网络驱动器" : "本地磁盘") }
            .Concat(d.Mount.ReadOnly ? new[] { new Chip("只读") } : Array.Empty<Chip>()).ToList();
        _cacheDir = services.Paths.CacheDirFor(d, services.Settings.Current);
        CachePath.Text = _cacheDir;
        CommandText.Text = RcloneArgsBuilder.Build(d, services.Settings.Current, _cacheDir, sup.Snapshot.Mount?.RcPort ?? 0).CommandLine;
        Render(sup.Snapshot);
        _handler = (s, snap) => { if (s.Drive.Id == d.Id) Dispatcher.BeginInvoke(() => Render(snap)); };
        services.Manager.SnapshotChanged += _handler;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => RenderSubline(_sup.Snapshot), Dispatcher);
        _timer.Start();
        Closed += (_, _) => { services.Manager.SnapshotChanged -= _handler; _timer.Stop(); };
        _ = MeasureCacheAsync();
    }

    private void Render(DriveSnapshot s)
    {
        var d = _sup.Drive;
        var mounted = s.Lifecycle == LifecycleState.Mounted;
        TypeTile.Background = DialogFrame.Res(mounted ? "AccentSoftBrush" : "TileBrush");
        TypeIcon.Foreground = DialogFrame.Res(mounted ? "AccentBrush" : "MutedBrush");
        RenderSubline(s);

        OpenButton.IsEnabled = s.CanOpen;
        ToggleButton.IsEnabled = s.CanDisconnect || s.CanConnect;
        ToggleButton.Content = s.CanDisconnect ? (s.Lifecycle is LifecycleState.Connecting or LifecycleState.Reconnecting ? "取消" : "断开") : "连接";
        ToggleButton.Appearance = s.CanDisconnect ? ControlAppearance.Secondary : ControlAppearance.Primary;

        // lifecycle
        var life = StatusPresentation.Lifecycle(s, DateTimeOffset.Now);
        LifecycleText.Text = life.Text;
        LifecycleText.Foreground = DialogFrame.Res(life.Kind switch { ChipKind.Ok => "TextBrush", ChipKind.Warn => "ChipWarnFg", ChipKind.Error => "ChipErrorFg", ChipKind.Info => "ChipInfoFg", _ => "TextBrush" });
        LifecycleDot.Fill = DialogFrame.Res(life.Kind switch { ChipKind.Ok => "SuccessBrush", ChipKind.Warn => "WarnBrush", ChipKind.Error => "DangerBrush", ChipKind.Info => "AccentBrush", _ => "RingBrush" });
        LifecycleDetail.Text = s.Mount is { } m
            ? $"挂载点 {m.MountPoint} · PID {m.ProcessId} · 自 {m.StartedAt.ToLocalTime():HH:mm:ss}"
            : s.Error is { } e ? e.Title : s.OwnedByOtherSession ? "另一个 Windows 会话持有这个驱动器" : "未连接";

        // remote health
        var (healthText, healthKind, healthSymbol) = !mounted
            ? ("未探测", ChipKind.Neutral, SymbolRegular.Circle16)
            : s.RemoteHealth switch
            {
                RemoteHealth.Healthy => ("正常", ChipKind.Ok, SymbolRegular.Checkmark16),
                RemoteHealth.Unreachable => ("暂不可达", ChipKind.Warn, SymbolRegular.ErrorCircle16),
                RemoteHealth.AuthRequired => ("要求重新认证", ChipKind.Warn, SymbolRegular.ErrorCircle16),
                RemoteHealth.Forbidden => ("拒绝访问", ChipKind.Warn, SymbolRegular.ErrorCircle16),
                RemoteHealth.NotFound => ("路径不存在", ChipKind.Warn, SymbolRegular.ErrorCircle16),
                _ => ("尚未探测", ChipKind.Neutral, SymbolRegular.Circle16),
            };
        HealthText.Text = healthText;
        HealthIcon.Symbol = healthSymbol;
        Tint(HealthCard, HealthText, HealthIcon, healthKind);
        HealthDetail.Text = !mounted
            ? "未连接"
            : s.LastProbeAt is { } at ? $"最近探测 {at.ToLocalTime():HH:mm:ss}" : "尚未探测";

        // sync
        var sync = s.Sync;
        var (syncText, syncKind, syncSymbol) = sync.Kind switch
        {
            SyncKind.Clean => ("已确认无待上传数据", ChipKind.Ok, SymbolRegular.Checkmark16),
            SyncKind.Pending => ($"{sync.PendingCount} 个待上传（约 {SizeSuffix.Format(sync.PendingBytes)}）", ChipKind.Info, SymbolRegular.ArrowUp16),
            SyncKind.Uploading => ($"正在上传 {sync.UploadingCount}/{sync.PendingCount}", ChipKind.Info, SymbolRegular.ArrowUp16),
            _ => (sync.Source == SyncSource.None ? "尚未检查" : "无法确认", sync.Source == SyncSource.None ? ChipKind.Neutral : ChipKind.Warn, SymbolRegular.Circle16),
        };
        SyncText.Text = syncText;
        SyncIcon.Symbol = syncSymbol;
        Tint(SyncCard, SyncText, SyncIcon, syncKind);
        SyncProgress.Visibility = sync.Kind == SyncKind.Uploading ? Visibility.Visible : Visibility.Collapsed;
        var parts = new List<string>();
        if (s.StopProgress is { } sp) parts.Add($"等待上传 {sp.Remaining} 个文件（{SizeSuffix.Format(sp.RemainingBytes)}）");
        if (sync.AbnormalStop) parts.Add("上次非正常停止");
        if (!string.IsNullOrEmpty(sync.Detail)) parts.Add(sync.Detail);
        if (sync.EvaluatedAt > DateTimeOffset.MinValue) parts.Add($"检查于 {sync.EvaluatedAt.ToLocalTime():HH:mm:ss}");
        SyncDetail.Text = string.Join(" · ", parts);

        // error card
        if (s.Error is { } err && s.Lifecycle is LifecycleState.Error or LifecycleState.Reconnecting or LifecycleState.NeedsCredentials or LifecycleState.NeedsHostKeyConfirm)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorTitle.Text = "最近错误：" + err.Title;
            ErrorText.Text = err.Message + (string.IsNullOrWhiteSpace(err.Suggestion) ? string.Empty : " " + err.Suggestion);
        }
        else ErrorCard.Visibility = Visibility.Collapsed;

        // footer
        var canDiscard = s.CanEdit && sync.RequiresProtection;
        DiscardButton.IsEnabled = canDiscard;
        FooterNote.Text = canDiscard
            ? (sync.Kind == SyncKind.Pending
                ? $"丢弃会永久删除 {sync.PendingCount} 个尚未上传文件的本地副本。"
                : "无法确认缓存里是否还有未上传内容；丢弃会永久删除本地副本。")
            : mounted ? "断开后才能丢弃未上传数据。" : "没有未上传的数据。";
        RenderCache();
    }

    private void RenderSubline(DriveSnapshot s)
    {
        var d = _sup.Drive;
        var parts = new List<string> { d.AccountDisplay };
        if (s.Mount is { } m)
        {
            var span = DateTimeOffset.UtcNow - m.StartedAt;
            parts.Add("已连接 " + (span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分" : $"{(int)span.TotalSeconds} 秒"));
        }
        SubText.Text = string.Join(" · ", parts);
    }

    private static void Tint(System.Windows.Controls.Border card, System.Windows.Controls.TextBlock text, Wpf.Ui.Controls.SymbolIcon icon, ChipKind kind)
    {
        var (bg, border, fg) = kind switch
        {
            ChipKind.Warn => ("WarnCardBg", "ChipWarnBorder", "ChipWarnFg"),
            ChipKind.Info => ("InfoCardBg", "ChipInfoBorder", "ChipInfoFg"),
            ChipKind.Error => ("ChipErrorBg", "ChipErrorBorder", "ChipErrorFg"),
            ChipKind.Ok => ("CardBrush", "CardBorderBrush", "TextBrush"),
            _ => ("CardBrush", "CardBorderBrush", "MutedBrush"),
        };
        card.Background = DialogFrame.Res(bg);
        card.BorderBrush = DialogFrame.Res(border);
        text.Foreground = DialogFrame.Res(fg);
        icon.Foreground = DialogFrame.Res(kind == ChipKind.Ok ? "SuccessBrush" : fg);
    }

    private void RenderCache()
    {
        var settings = _services.Settings.Current;
        var target = _sup.Drive.Advanced.CacheCleanupTarget ?? settings.Cache.CleanupTargetPerDrive;
        var usage = _cacheBytes is { } b ? "占用 " + SizeSuffix.Format(b) : Directory.Exists(_cacheDir) ? "正在统计占用…" : "尚未创建";
        string free = string.Empty;
        try
        {
            var root = Path.GetPathRoot(_cacheDir);
            if (root is not null) free = " · 磁盘剩余 " + SizeSuffix.Format(new DriveInfo(root).AvailableFreeSpace);
        }
        catch { }
        CacheText.Text = $"{usage} · 清理目标 {target}{free}";
    }

    private async Task MeasureCacheAsync()
    {
        if (_cacheDir is null || !Directory.Exists(_cacheDir)) return;
        var dir = _cacheDir;
        try
        {
            var bytes = await Task.Run(() =>
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            });
            _cacheBytes = bytes;
            RenderCache();
        }
        catch (Exception ex)
        {
            _services.Log.Warn("ui", "cache size failed: " + ex.Message);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!_sup.Snapshot.CanOpen) return;
        new ShellIntegration(_services.ExePath).OpenInExplorer(_sup.Drive.Mount.ToMountPoint());
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var s = _sup.Snapshot;
        try
        {
            if (s.CanDisconnect) await _sup.DisconnectAsync(interactive: true);
            else if (s.CanConnect) await _sup.ConnectAsync(interactive: true);
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "toggle failed", ex);
            MessageBox.Show(this, ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_cacheDir is not null && Directory.Exists(_cacheDir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _cacheDir + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(CommandText.Text); } catch { }
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await _sup.DiscardUnsyncedDataAsync(CancellationToken.None)) Render(_sup.Snapshot);
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui", "discard failed", ex);
            MessageBox.Show(this, ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
