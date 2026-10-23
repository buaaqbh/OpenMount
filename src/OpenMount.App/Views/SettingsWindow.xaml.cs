using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenMount.App.Services;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Rclone;
using OpenMount.Windows;

namespace OpenMount.App.Views;

/// <summary>Settings (FR-12). Every change is persisted immediately; the footer only closes the window.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _loading;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        _loading = true;
        InitializeComponent();
        var s = services.Settings.Current;
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        RunAtStartup.IsChecked = AutoStartService.IsEnabled();
        OpenExplorer.IsChecked = s.General.OpenExplorerOnConnect;
        StorageIcons.IsChecked = s.General.UseStorageIcons;
        Notifications.IsChecked = s.General.Notifications;
        DataDirText.Text = (AppPaths.IsPortable ? "便携模式 · " : string.Empty) + "数据目录：" + services.Paths.Root;

        ProxyMode.SelectedIndex = (int)s.Proxy.Mode;
        ProxyScheme.SelectedIndex = (int)s.Proxy.Scheme;
        ProxyHost.Text = s.Proxy.Host ?? string.Empty;
        ProxyPort.Text = s.Proxy.Port?.ToString() ?? string.Empty;
        ProxyUser.Text = s.Proxy.Username ?? string.Empty;
        UpdateProxyEnabled();

        CacheRoot.Text = services.Paths.CacheRoot(s);
        CleanupTarget.Text = s.Cache.CleanupTargetPerDrive;
        MaxAge.Text = s.Cache.MaxAge;
        MinFree.Text = s.Cache.MinFreeSpace;

        LogLevel.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "ERROR", "NOTICE", "INFO", "DEBUG" }, s.Advanced.LogLevel));
        BlockShutdown.IsChecked = s.Advanced.BlockShutdownWhileUnsynced;
        LogsDirText.Text = services.Paths.LogsDir;
        SharePrefixText.Text = $@"\\{s.Advanced.NetworkSharePrefix}\<驱动器名称>";

        RcloneMode.SelectedIndex = (int)s.Advanced.RcloneMode;
        RclonePath.Text = s.Advanced.RclonePath ?? string.Empty;
        RefreshDependencies();

        AboutTitle.Text = "OpenMount " + version;
        VersionText.Text = $"OpenMount {version} · rclone {(services.Locator.BundledPath is null ? "未内置" : "内置")} · WinFsp {(WinFspInfo.Detect() is { Installed: true } w ? w.Version : "未安装")}";
        _loading = false;
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAbout is null) return; // SelectedIndex="0" fires during InitializeComponent, before the pages exist
        var pages = new[] { PageGeneral, PageProxy, PageCache, PageAdvanced, PageDeps, PageAbout };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ persistence

    private void Persist_Changed(object sender, RoutedEventArgs e) => Persist();
    private void Persist_Selection(object sender, SelectionChangedEventArgs e) => Persist();

    private void Persist()
    {
        if (_loading) return;
        var n = _services.Settings.Current;
        n.General.RunAtStartup = RunAtStartup.IsChecked == true;
        n.General.OpenExplorerOnConnect = OpenExplorer.IsChecked == true;
        n.General.UseStorageIcons = StorageIcons.IsChecked == true;
        n.General.Notifications = Notifications.IsChecked == true;
        n.Proxy.Mode = (ProxyMode)Math.Max(0, ProxyMode.SelectedIndex);
        n.Proxy.Scheme = (ProxyScheme)Math.Max(0, ProxyScheme.SelectedIndex);
        n.Proxy.Host = string.IsNullOrWhiteSpace(ProxyHost.Text) ? null : ProxyHost.Text.Trim();
        n.Proxy.Port = int.TryParse(ProxyPort.Text, out var pp) ? pp : null;
        n.Proxy.Username = string.IsNullOrWhiteSpace(ProxyUser.Text) ? null : ProxyUser.Text.Trim();
        var cacheErrors = new List<string>();
        n.Cache.CleanupTargetPerDrive = SizeOrDefault(CleanupTarget, n.Cache.CleanupTargetPerDrive, "10G", cacheErrors, "清理目标");
        n.Cache.MaxAge = DurationOrDefault(MaxAge, n.Cache.MaxAge, "24h", cacheErrors, "保留时间");
        n.Cache.MinFreeSpace = SizeOrDefault(MinFree, n.Cache.MinFreeSpace, "2G", cacheErrors, "最小剩余空间");
        CacheError.Text = string.Join("；", cacheErrors);
        n.Advanced.LogLevel = (LogLevel.SelectedItem as ComboBoxItem)?.Content as string ?? "NOTICE";
        n.Advanced.BlockShutdownWhileUnsynced = BlockShutdown.IsChecked == true;
        n.Advanced.RcloneMode = (RcloneMode)Math.Max(0, RcloneMode.SelectedIndex);
        n.Advanced.RclonePath = string.IsNullOrWhiteSpace(RclonePath.Text) ? null : RclonePath.Text.Trim();
        _services.Settings.Save(n);
        UpdateProxyEnabled();
        try { AutoStartService.Set(n.General.RunAtStartup, _services.ExePath); }
        catch (Exception ex) { MessageBox.Show(this, "开机启动设置失败：" + ex.Message, "OpenMount", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string SizeOrDefault(TextBox box, string current, string fallback, List<string> errors, string label)
    {
        var t = box.Text.Trim();
        if (t.Length == 0) { box.Text = fallback; return fallback; }
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+(\.\d+)?[kKmMgGtTpP]?(i?[bB])?$")) return t;
        errors.Add($"{label}“{t}”不是有效的大小（如 10G）");
        box.Text = current;
        return current;
    }

    private static string DurationOrDefault(TextBox box, string current, string fallback, List<string> errors, string label)
    {
        var t = box.Text.Trim();
        if (t.Length == 0) { box.Text = fallback; return fallback; }
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+(ms|s|m|h|d|w)$")) return t;
        errors.Add($"{label}“{t}”不是有效的时长（如 24h）");
        box.Text = current;
        return current;
    }

    private void UpdateProxyEnabled() => ProxyCustom.IsEnabled = ProxyMode.SelectedIndex == (int)Core.Models.ProxyMode.Custom;

    private void ProxyPassword_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || string.IsNullOrEmpty(ProxyPassword.Password)) return;
        try
        {
            _services.CredentialStore.Set(CredentialFields.ProxyTarget, _services.Settings.Current.Proxy.Username, ProxyPassword.Password);
            ProxyPassword.Clear();
            ProxyPasswordHint.Text = "已更新";
        }
        catch (Exception ex)
        {
            ProxyPasswordHint.Text = "保存失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ dependencies

    private void RefreshDependencies()
    {
        var winfsp = WinFspInfo.Detect();
        WinFspDot.Fill = DialogFrame.Res(winfsp.Installed ? "SuccessBrush" : "WarnBrush");
        WinFspDesc.Text = winfsp.Installed
            ? $"已安装 {winfsp.Version} · {winfsp.InstallDir}"
            : "未安装；安装后若提示重启，请重启后再打开 OpenMount。";
        var path = _services.Locator.Resolve(_services.Settings.Current);
        RcloneDot.Fill = DialogFrame.Res(path is null ? "WarnBrush" : "SuccessBrush");
        RcloneDesc.Text = path is null ? "未找到 rclone.exe，请在下面指定路径。" : $"当前使用 {path}";
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshDependencies();

    private void WinFspDownload_Click(object sender, RoutedEventArgs e) => OpenUrl("https://winfsp.dev/rel/");

    private void BrowseRclone_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择 rclone.exe", Filter = "rclone|rclone.exe|可执行文件|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        RclonePath.Text = dlg.FileName;
        RcloneMode.SelectedIndex = (int)Core.Models.RcloneMode.Custom;
        Persist();
        RefreshDependencies();
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        var path = RcloneMode.SelectedIndex == (int)Core.Models.RcloneMode.Custom ? RclonePath.Text.Trim() : _services.Locator.BundledPath ?? string.Empty;
        if (string.IsNullOrEmpty(path)) { ProbeResult.Text = "没有可探测的 rclone 路径"; return; }
        ProbeResult.Text = "探测中…";
        try
        {
            var r = await CapabilityProbe.ProbeAsync(path, CancellationToken.None);
            ProbeResult.Text = r.Ok ? $"可用：rclone {r.Version}" : $"不可用：{r.Error}";
        }
        catch (Exception ex) { ProbeResult.Text = "探测失败：" + ex.Message; }
        RefreshDependencies();
    }

    // ------------------------------------------------------------------ misc

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(_services.Paths.Root);
    private void OpenCache_Click(object sender, RoutedEventArgs e) => OpenFolder(CacheRoot.Text);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_services.Paths.LogsDir);

    private void ThirdParty_Click(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(file)) OpenUrl(file);
        else OpenUrl("https://github.com/rclone/rclone/blob/master/COPYING");
    }

    private void Export_Click(object sender, RoutedEventArgs e) => _ = ConfigTransfer.ExportAsync(this, _services);
    private void Import_Click(object sender, RoutedEventArgs e) => _ = ConfigTransfer.ImportAsync(this, _services);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
