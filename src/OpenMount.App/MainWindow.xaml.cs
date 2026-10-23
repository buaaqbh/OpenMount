using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenMount.App.ViewModels;
using OpenMount.Windows;

namespace OpenMount.App;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly AppServices _services;
    private readonly MainViewModel _vm;
    private bool _allowClose;

    public MainWindow(App app, AppServices services)
    {
        _app = app;
        _services = services;
        InitializeComponent();
        _vm = new MainViewModel(app, services, Dispatcher, () => IsVisible ? this : null);
        DataContext = _vm;
    }

    public MainViewModel ViewModel => _vm;

    public void SetDependencyState(WinFspInfo winfsp, string? rclonePath) => _vm.SetDependencyState(winfsp, rclonePath);
    public void AddDrive() => _vm.AddDrive();
    public void EditDrive(Guid id) => _vm.EditById(id);
    public void OpenSettings() => _vm.OpenSettings();
    public void ShowDetails(Guid id) => _vm.ShowDetailsById(id);
    public void AllowClose() => _allowClose = true;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } fe) return;
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DriveRowViewModel row })
        {
            if (row.CanOpen) row.OpenCommand.Execute(null);
            else if (row.CanToggle && row.PrimaryLabel == "连接") row.ToggleCommand.Execute(null);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose || _app.IsExiting) { base.OnClosing(e); return; }
        e.Cancel = true;
        Hide();
        var settings = _services.Settings.Current;
        if (!settings.General.MinimizeToTrayHintShown)
        {
            _services.Interaction.Notify("OpenMount 仍在后台运行", "驱动器保持连接。右键托盘图标可退出。", Core.Services.NotificationKind.Info);
            settings.General.MinimizeToTrayHintShown = true;
            _services.Settings.Save(settings);
        }
    }
}
