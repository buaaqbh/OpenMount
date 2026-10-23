using System.Windows;
using OpenMount.App.Views;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;

namespace OpenMount.App.Services;

/// <summary>WPF implementation of <see cref="IUserInteraction"/>: dialogs on the UI thread, notifications via the tray.</summary>
public sealed class UserInteraction : IUserInteraction
{
    private App? _app;

    public TrayService? Tray { get; set; }

    public void Attach(App app) => _app = app;

    private Window? Owner => _app?.MainWindow is { IsVisible: true } w ? w : null;

    private Task<T> OnUi<T>(Func<T> f)
    {
        var app = _app ?? throw new InvalidOperationException("not attached");
        if (app.Dispatcher.CheckAccess()) return Task.FromResult(f());
        return app.Dispatcher.InvokeAsync(f).Task;
    }

    public Task<CredentialPromptResult?> PromptCredentialsAsync(Drive drive, CancellationToken ct) =>
        OnUi(() =>
        {
            var dlg = new CredentialPromptDialog(drive) { Owner = Owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        });

    public Task<bool> ConfirmHostKeysAsync(Drive drive, SshEndpoint endpoint, IReadOnlyList<HostKeyEntry> keys, bool isChange, CancellationToken ct) =>
        OnUi(() =>
        {
            var dlg = new HostKeyDialog(drive, endpoint, keys, isChange) { Owner = Owner };
            return dlg.ShowDialog() == true;
        });

    public Task<GuardChoice> ChooseUnsyncedActionAsync(Drive drive, DestructiveOperation op, SyncState state, IReadOnlyList<GuardChoice> choices, string reason, CancellationToken ct) =>
        OnUi(() =>
        {
            var dlg = new UnsyncedDataDialog(drive, op, state, choices, reason) { Owner = Owner };
            dlg.ShowDialog();
            return dlg.Choice;
        });

    public Task<GuardChoice> ChooseWhenNoProgressAsync(Drive drive, StopProgressInfo progress, CancellationToken ct) =>
        OnUi(() =>
        {
            var dlg = UnsyncedDataDialog.ForNoProgress(drive, progress);
            dlg.Owner = Owner;
            dlg.ShowDialog();
            return dlg.Choice;
        });

    public Task<bool> ConfirmDiscardAsync(Drive drive, SyncState state, CancellationToken ct) =>
        OnUi(() =>
        {
            var text = state.Kind == SyncKind.Unknown
                ? "无法确认该驱动器的缓存是否含有未上传内容。丢弃后这些内容将永久丢失。"
                : $"将永久丢弃 {state.PendingCount} 个未上传的文件（约 {SizeSuffix.Format(state.PendingBytes)}）。此操作不可恢复。";
            return MessageBox.Show(Owner ?? new Window(), text + "\n\n确定要丢弃未上传数据吗？", "丢弃未上传数据 — " + drive.Name,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        });

    private readonly NotificationThrottle _throttle = new();

    public void Notify(string title, string message, NotificationKind kind)
    {
        var tray = Tray;
        if (tray is null) return;
        if (!_throttle.ShouldShow(title, kind, DateTimeOffset.Now)) return; // FR-09.6: same drive, same kind, once per 10 min
        _app?.Dispatcher.BeginInvoke(() => tray.ShowBalloon(title, message, kind));
    }
}
