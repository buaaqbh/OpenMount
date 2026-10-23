using System.Drawing;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenMount.App.ViewModels;
using OpenMount.Core.Logging;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Ellipse = System.Windows.Shapes.Ellipse;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace OpenMount.App.Services;

/// <summary>
/// Notification-area icon and menu (FR-11). The icon is a WinForms NotifyIcon (the only reliable host); the menu is a WPF
/// ContextMenu so it gets the same Fluent look as the rest of the app. Icon states follow the design: idle (grey),
/// connected (white / ink), attention (amber dot), uploading (blue arrow), each for dark and light taskbars.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly AppServices _services;
    private readonly NotifyIcon _icon;
    private readonly Dictionary<string, Icon> _icons = new();
    private readonly string _version;
    private System.Windows.Window? _menuHost;
    private bool _lightTaskbar;
    private string _state = "idle";
    private bool _disposed;
    private readonly Action<DriveSupervisor, DriveSnapshot> _onSnapshot;
    private readonly Action<DriveSupervisor> _onSupervisors;

    public TrayService(App app, AppServices services)
    {
        _app = app;
        _services = services;
        _version = typeof(TrayService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _onSnapshot = (_, _) => _app.Dispatcher.BeginInvoke(UpdateIcon);
        _onSupervisors = _ => _app.Dispatcher.BeginInvoke(UpdateIcon);
        _lightTaskbar = DetectLightTaskbar();
        _icon = new NotifyIcon
        {
            Icon = GetIcon("idle"),
            Text = "OpenMount",
            Visible = true,
        };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _app.Dispatcher.BeginInvoke(ShowMenu); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) _app.ShowMain(); };
        _services.Manager.SnapshotChanged += _onSnapshot;
        _services.Manager.SupervisorAdded += _onSupervisors;
        _services.Manager.SupervisorRemoved += _onSupervisors;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateIcon();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _app.Dispatcher.BeginInvoke(() =>
        {
            var light = DetectLightTaskbar();
            if (light == _lightTaskbar) return;
            _lightTaskbar = light;
            _icon.Icon = GetIcon(_state);
        });
    }

    /// <summary>Windows uses a light taskbar when SystemUsesLightTheme = 1; the glyph must then be ink instead of white.</summary>
    private static bool DetectLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    private Icon GetIcon(string state)
    {
        var key = $"{state}-{(_lightTaskbar ? "light" : "dark")}";
        if (_icons.TryGetValue(key, out var cached)) return cached;
        var uri = new Uri($"pack://application:,,,/Assets/tray-{key}.ico", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)?.Stream ?? throw new InvalidOperationException("tray icon missing: " + key);
        var icon = new Icon(stream, SystemInformation.SmallIconSize);
        _icons[key] = icon;
        return icon;
    }

    private void UpdateIcon()
    {
        // Drives still change state while the app shuts down (reconnect timers are cancelled); a disposed NotifyIcon
        // throws from its Icon/Text setters, and the tray must never take the UI thread down.
        if (_disposed) return;
        try { UpdateIconCore(); }
        catch (Exception ex) { _services.Log.Warn("tray", "icon update failed: " + ex.Message); }
    }

    private void UpdateIconCore()
    {
        var snaps = _services.Manager.Supervisors.Select(s => s.Snapshot).ToList();
        var mounted = snaps.Count(s => s.Lifecycle == LifecycleState.Mounted);
        var attention = snaps.Count(StatusPresentation.NeedsAttention);
        var uploading = snaps.Any(StatusPresentation.IsUploading);
        _state = attention > 0 ? "attention" : uploading ? "uploading" : mounted > 0 ? "connected" : "idle";
        _icon.Icon = GetIcon(_state);
        var text = snaps.Count == 0 ? "OpenMount · 没有驱动器" : $"OpenMount · {mounted}/{snaps.Count} 已连接" + (attention > 0 ? $" · {attention} 个需要处理" : uploading ? " · 上传中" : string.Empty);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu()
    {
        var menu = BuildMenu();
        // A WPF ContextMenu needs an active window of ours to close on an outside click; a 0x0 off-screen window does that.
        _menuHost ??= new System.Windows.Window
        {
            Width = 0, Height = 0, Left = -10000, Top = -10000, WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = true, AllowsTransparency = true, Opacity = 0, ResizeMode = System.Windows.ResizeMode.NoResize,
        };
        _menuHost.Show();
        _menuHost.Activate();
        menu.PlacementTarget = _menuHost; // keyboard handling and focus restore need a target; MousePoint still decides the position
        menu.Placement = PlacementMode.MousePoint;
        menu.StaysOpen = false;
        menu.Closed += (_, _) => _menuHost?.Hide();
        menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { MinWidth = 200 };
        menu.Items.Add(new MenuItem { Header = "OpenMount " + _version, IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开 OpenMount", () => _app.ShowMain()));
        menu.Items.Add(Item("添加驱动器…", () => { _app.ShowMain(); _app.MainWindowInstance.AddDrive(); }));
        menu.Items.Add(Item("设置…", () => { _app.ShowMain(); _app.MainWindowInstance.OpenSettings(); }));
        menu.Items.Add(new Separator());
        var sups = _services.Manager.Supervisors;
        foreach (var sup in sups)
        {
            var snap = sup.Snapshot;
            var item = new MenuItem
            {
                Header = $"{sup.Drive.Name} ({sup.Drive.Mount.MountPointDisplay})",
                Icon = StatusDot(snap),
                ToolTip = StatusPresentation.Summary(snap, DateTimeOffset.Now),
            };
            if (snap.CanDisconnect)
                item.Items.Add(Item(snap.Lifecycle is LifecycleState.Connecting or LifecycleState.Reconnecting ? "取消" : "断开", () => _ = sup.DisconnectAsync(true)));
            else
                item.Items.Add(Item("连接", () => _ = sup.ConnectAsync(true), snap.CanConnect));
            item.Items.Add(Item("编辑…", () => { _app.ShowMain(); _app.MainWindowInstance.EditDrive(sup.Drive.Id); }, snap.CanEdit));
            item.Items.Add(Item("在资源管理器中打开", () => new OpenMount.Windows.ShellIntegration(_services.ExePath).OpenInExplorer(sup.Drive.Mount.ToMountPoint()), snap.CanOpen));
            item.Items.Add(Item("详情…", () => { _app.ShowMain(); _app.MainWindowInstance.ShowDetails(sup.Drive.Id); }));
            menu.Items.Add(item);
        }
        if (sups.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item("全部连接", () => _ = _services.Manager.ConnectAllAsync(CancellationToken.None), sups.Count > 0));
        menu.Items.Add(Item("全部断开", () => _ = _services.Manager.DisconnectAllAsync(true, CancellationToken.None), sups.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出", () => _ = _app.ExitAsync()));
        return menu;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static Ellipse StatusDot(DriveSnapshot s)
    {
        var dot = new Ellipse { Width = 8, Height = 8 };
        if (StatusPresentation.NeedsAttention(s)) dot.Fill = Brush("WarnBrush");
        else if (s.Lifecycle == LifecycleState.Mounted) dot.Fill = Brush("SuccessBrush");
        else if (s.Lifecycle is LifecycleState.Connecting or LifecycleState.Disconnecting) dot.Fill = Brush("AccentBrush");
        else { dot.Stroke = Brush("RingBrush"); dot.StrokeThickness = 1.5; }
        return dot;
    }

    private static System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)Application.Current.Resources[key];

    // ------------------------------------------------------------------ notifications

    public void ShowBalloon(string title, string message, NotificationKind kind)
    {
        if (!_services.Settings.Current.General.Notifications) return;
        var icon = kind switch
        {
            NotificationKind.Error => ToolTipIcon.Error,
            NotificationKind.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(4000, title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Manager.SnapshotChanged -= _onSnapshot;
        _services.Manager.SupervisorAdded -= _onSupervisors;
        _services.Manager.SupervisorRemoved -= _onSupervisors;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _icons.Clear();
        _menuHost?.Close();
    }
}
