using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenMount.App.ViewModels;
using OpenMount.Core.Errors;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using UiButton = Wpf.Ui.Controls.Button;

namespace OpenMount.App.Views;

/// <summary>
/// Shared shell for the small dialogs (design row 3): white body with a 40 px icon tile in front of the message,
/// a grey footer strip with an optional note on the left and the buttons on the right (primary last).
/// </summary>
internal static class DialogFrame
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static void Setup(Window w, string title, double width)
    {
        w.Style = (Style)Application.Current.Resources["DialogWindow"];
        w.Title = title;
        w.Width = width;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    public static Border Tile(SymbolRegular symbol, ChipKind kind)
    {
        var (bg, fg) = kind switch
        {
            ChipKind.Warn => ("ChipWarnBg", "ChipWarnFg"),
            ChipKind.Error => ("ChipErrorBg", "ChipErrorFg"),
            ChipKind.Ok => ("ChipOkBg", "ChipOkFg"),
            _ => ("AccentSoftBrush", "ChipInfoFg"),
        };
        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = Res(bg),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new SymbolIcon { Symbol = symbol, FontSize = 22, Foreground = Res(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    public static TextBlock Headline(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush") };

    public static TextBlock Body(string text, double topMargin = 8) => new()
    {
        Text = text, FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, topMargin, 0, 0),
    };

    public static TextBlock Help(string text, double topMargin = 8) => new()
    {
        Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(0, topMargin, 0, 0),
    };

    public static TextBlock Mono(string text) => new()
    {
        Text = text, FontSize = 12, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextBrush"),
    };

    public static UiButton Button(string text, ControlAppearance appearance = ControlAppearance.Secondary) => new() { Content = text, Appearance = appearance };

    public static UiButton DangerButton(string text) => new() { Content = text, Style = (Style)Application.Current.Resources["DangerButton"] };

    /// <summary>Tile + message column.</summary>
    public static Grid Message(Border tile, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(20, 4, 20, 18) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(tile);
        Grid.SetColumn(content, 2);
        g.Children.Add(content);
        return g;
    }

    public static Border Footer(IEnumerable<UIElement> right, UIElement? left = null)
    {
        var dock = new DockPanel();
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var first = true;
        foreach (var b in right)
        {
            if (b is FrameworkElement fe && !first) fe.Margin = new Thickness(8, 0, 0, 0);
            first = false;
            rightPanel.Children.Add(b);
        }
        DockPanel.SetDock(rightPanel, Dock.Right);
        dock.Children.Add(rightPanel);
        if (left is not null)
        {
            if (left is FrameworkElement lfe) { lfe.VerticalAlignment = VerticalAlignment.Center; lfe.Margin = new Thickness(0, 0, 12, 0); }
            dock.Children.Add(left);
        }
        else
        {
            dock.Children.Add(new Border());
        }
        return new Border { Style = (Style)Application.Current.Resources["DialogFooter"], Child = dock };
    }

    public static void Compose(Window w, UIElement body, Border footer)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(body);
        w.Content = dock;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}

/// <summary>Generic confirmation with one primary (or danger) action.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string headline, string body, string confirmLabel, bool danger = false)
    {
        DialogFrame.Setup(this, title, 520);
        var tile = DialogFrame.Tile(danger ? SymbolRegular.Delete24 : SymbolRegular.QuestionCircle24, danger ? ChipKind.Warn : ChipKind.Info);
        var content = DialogFrame.Column(DialogFrame.Headline(headline), DialogFrame.Body(body));
        var cancel = DialogFrame.Button("取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        var ok = danger ? DialogFrame.DangerButton(confirmLabel) : DialogFrame.Button(confirmLabel, ControlAppearance.Primary);
        ok.IsDefault = !danger;
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, ok }));
    }
}

public sealed class UnsyncedDataDialog : Window
{
    public GuardChoice Choice { get; private set; } = GuardChoice.Cancel;

    public UnsyncedDataDialog(Drive drive, DestructiveOperation op, SyncState state, IReadOnlyList<GuardChoice> choices, string reason)
    {
        var where = $"{drive.Name} ({drive.Mount.MountPointDisplay})";
        var title = op switch
        {
            DestructiveOperation.Exit => "退出 OpenMount",
            DestructiveOperation.Delete => "删除 " + where,
            DestructiveOperation.ChangeIdentity => "修改连接目标 · " + where,
            DestructiveOperation.ChangeCacheModeToUnsafe => "更改缓存模式 · " + where,
            _ => "断开 " + where,
        };
        DialogFrame.Setup(this, title, 600);
        var tile = DialogFrame.Tile(SymbolRegular.ArrowUpload24, ChipKind.Info);
        var content = DialogFrame.Column(DialogFrame.Headline(reason));
        content.Children.Add(DialogFrame.Body(op switch
        {
            DestructiveOperation.Delete => "删除会一并删除本地缓存，未上传的内容将永久丢失。",
            DestructiveOperation.ChangeIdentity or DestructiveOperation.ChangeCacheModeToUnsafe => "修改前需要先完成上传，或明确丢弃未上传的数据。",
            _ => "现在停止会保留本地缓存，下次连接时自动继续上传。",
        }));
        if (state.AbnormalStop) content.Children.Add(DialogFrame.Help("上次为非正常停止，最后写入的内容可能不完整。"));
        if (state.Kind == SyncKind.Uploading)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var text = DialogFrame.Help($"正在上传 {state.UploadingCount}/{state.PendingCount} · 待传 {SizeSuffix.Format(state.PendingBytes)}", 0);
            text.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(text, Dock.Right);
            row.Children.Add(text);
            row.Children.Add(new ProgressBar { Height = 6, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(row);
        }

        CheckBox? ack = null;
        if (choices.Contains(GuardChoice.Discard))
        {
            ack = new CheckBox
            {
                Content = state.Kind == SyncKind.Unknown ? "我已了解可能丢失未上传的内容" : $"我已了解这 {state.PendingCount} 个文件将永久丢失",
                Margin = new Thickness(0, 10, 0, 0),
            };
            content.Children.Add(ack);
        }

        var buttons = new List<UIElement>();
        UiButton Add(GuardChoice c, string label, ControlAppearance appearance, bool danger = false)
        {
            var b = danger ? DialogFrame.DangerButton(label) : DialogFrame.Button(label, appearance);
            b.Click += (_, _) =>
            {
                if (c == GuardChoice.Discard && ack is { IsChecked: not true }) { System.Windows.MessageBox.Show(this, "请先勾选确认。", Title); return; }
                Choice = c;
                DialogResult = true;
                Close();
            };
            buttons.Add(b);
            return b;
        }
        if (choices.Contains(GuardChoice.Cancel)) Add(GuardChoice.Cancel, "取消", ControlAppearance.Transparent).IsCancel = true;
        if (choices.Contains(GuardChoice.StopAndRetain)) Add(GuardChoice.StopAndRetain, "停止并保留待上传数据", ControlAppearance.Secondary);
        if (choices.Contains(GuardChoice.Discard)) Add(GuardChoice.Discard, op == DestructiveOperation.Delete ? "丢弃未上传数据并删除" : "丢弃未上传数据", ControlAppearance.Secondary, danger: true);
        if (choices.Contains(GuardChoice.ConnectAndUpload)) Add(GuardChoice.ConnectAndUpload, "先连接并完成上传", ControlAppearance.Primary).IsDefault = true;
        if (choices.Contains(GuardChoice.WaitForUploads)) Add(GuardChoice.WaitForUploads, op == DestructiveOperation.Exit ? "等待上传完成后退出" : "等待上传完成后断开", ControlAppearance.Primary).IsDefault = true;

        var note = choices.Contains(GuardChoice.WaitForUploads) ? DialogFrame.Help("长时间无进展时会再次询问。", 0) : null;
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(buttons, note));
    }

    private UnsyncedDataDialog() { }

    public static UnsyncedDataDialog ForNoProgress(Drive drive, StopProgressInfo progress)
    {
        var dlg = new UnsyncedDataDialog();
        DialogFrame.Setup(dlg, $"等待上传 · {drive.Name} ({drive.Mount.MountPointDisplay})", 600);
        var tile = DialogFrame.Tile(SymbolRegular.ArrowUpload24, ChipKind.Warn);
        var content = DialogFrame.Column(
            DialogFrame.Headline("上传似乎没有进展"),
            DialogFrame.Body($"仍有 {progress.Remaining} 个文件（约 {SizeSuffix.Format(progress.RemainingBytes)}）等待上传，已等待 {(int)progress.Elapsed.TotalSeconds} 秒，远端可能不可达。停止会保留本地缓存，下次连接时继续上传。"));
        var buttons = new List<UIElement>();
        void Add(GuardChoice c, string label, ControlAppearance appearance)
        {
            var b = DialogFrame.Button(label, appearance);
            b.Click += (_, _) => { dlg.Choice = c; dlg.DialogResult = true; dlg.Close(); };
            if (c == GuardChoice.Cancel) b.IsCancel = true;
            if (c == GuardChoice.WaitForUploads) b.IsDefault = true;
            buttons.Add(b);
        }
        Add(GuardChoice.Cancel, "取消", ControlAppearance.Transparent);
        Add(GuardChoice.StopAndRetain, "停止并保留待上传数据", ControlAppearance.Secondary);
        Add(GuardChoice.WaitForUploads, "继续等待", ControlAppearance.Primary);
        DialogFrame.Compose(dlg, DialogFrame.Message(tile, content), DialogFrame.Footer(buttons));
        return dlg;
    }
}

public enum ErrorDialogResult { Cancel, Retry, Edit, ViewLog }

public sealed class ErrorDialog : Window
{
    public ErrorDialogResult Result { get; private set; } = ErrorDialogResult.Cancel;

    public ErrorDialog(Drive drive, MountError error)
    {
        DialogFrame.Setup(this, $"{drive.Name} 连接失败", 600);
        var configProblem = error.Kind is MountErrorKind.Auth or MountErrorKind.HostKeyMismatch or MountErrorKind.HostKeyUnknown or MountErrorKind.TlsCert
            or MountErrorKind.Forbidden or MountErrorKind.NotFound or MountErrorKind.MountPointInUse;
        var tile = DialogFrame.Tile(SymbolRegular.ErrorCircle24, configProblem ? ChipKind.Warn : ChipKind.Error);
        var body = error.Message;
        if (!string.IsNullOrWhiteSpace(error.Suggestion)) body += " " + error.Suggestion;
        var content = DialogFrame.Column(DialogFrame.Headline(error.Title), DialogFrame.Body(body));
        if (!string.IsNullOrWhiteSpace(error.TechnicalDetails))
        {
            var details = new TextBox
            {
                Text = error.TechnicalDetails, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            content.Children.Add(new Expander { Header = "技术详情（已脱敏）", Content = details, Margin = new Thickness(0, 10, 0, 0) });
        }

        var viewLog = DialogFrame.Button("查看日志", ControlAppearance.Transparent);
        viewLog.Click += (_, _) => { Result = ErrorDialogResult.ViewLog; DialogResult = true; Close(); };
        var cancel = DialogFrame.Button("取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { Result = ErrorDialogResult.Cancel; Close(); };
        var retry = DialogFrame.Button("重试");
        retry.IsEnabled = error.CanRetry;
        retry.Click += (_, _) => { Result = ErrorDialogResult.Retry; DialogResult = true; Close(); };
        var edit = DialogFrame.Button(error.Kind == MountErrorKind.Auth ? "编辑凭据" : "编辑驱动器", ControlAppearance.Primary);
        edit.IsEnabled = error.CanEdit;
        edit.IsDefault = error.CanEdit;
        edit.Click += (_, _) => { Result = ErrorDialogResult.Edit; DialogResult = true; Close(); };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, retry, edit }, viewLog));
    }
}

public sealed class CredentialPromptDialog : Window
{
    public CredentialPromptResult? Result { get; private set; }

    public CredentialPromptDialog(Drive drive)
    {
        DialogFrame.Setup(this, $"登录 {drive.Name} ({drive.Mount.MountPointDisplay})", 520);
        var policy = drive.CredentialPolicy switch
        {
            CredentialPolicy.Prompt => "每次连接时询问，不保存",
            CredentialPolicy.Session => "仅本次运行期间保留",
            _ => "已保存的凭据无效，请重新输入",
        };
        var tile = DialogFrame.Tile(SymbolRegular.Key24, ChipKind.Info);
        var content = DialogFrame.Column(DialogFrame.Help(drive.AccountDisplay + " · " + policy, 0));

        var needsPassword = drive.StorageType == StorageType.WebDav ? drive.WebDav?.AuthMode == WebDavAuthMode.Basic : drive.Sftp?.AuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword;
        var needsToken = drive.StorageType == StorageType.WebDav && drive.WebDav?.AuthMode == WebDavAuthMode.Bearer;
        var needsPassphrase = drive.StorageType == StorageType.Sftp && drive.Sftp?.AuthMode is SftpAuthMode.Key or SftpAuthMode.KeyAndPassword;
        var username = drive.StorageType == StorageType.WebDav ? drive.WebDav?.Username : drive.Sftp?.Username;

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, UIElement field)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 13, Foreground = DialogFrame.Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 10) };
            Grid.SetRow(l, r);
            grid.Children.Add(l);
            if (field is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 0, 10); fe.HorizontalAlignment = HorizontalAlignment.Left; }
            Grid.SetRow(field, r);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }
        if (!string.IsNullOrEmpty(username)) Row("账号", new TextBlock { Text = username, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        PasswordBox? pw = null, token = null, pp = null;
        if (needsPassword) Row(drive.StorageType == StorageType.Sftp ? "登录密码" : "密码", pw = new PasswordBox { Width = 260 });
        if (needsToken) Row("Token", token = new PasswordBox { Width = 360 });
        if (needsPassphrase) Row("私钥口令", pp = new PasswordBox { Width = 260, ToolTip = "仅用于解密私钥文件，可留空" });
        content.Children.Add(grid);
        var remember = new CheckBox { Content = "本次运行期间记住", IsChecked = drive.CredentialPolicy == CredentialPolicy.Session };
        content.Children.Add(remember);

        var cancel = DialogFrame.Button("取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        var ok = DialogFrame.Button("连接", ControlAppearance.Primary);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            Result = new CredentialPromptResult(new Core.Engine.DriveSecrets(
                Password: string.IsNullOrEmpty(pw?.Password) ? null : pw!.Password,
                KeyPassphrase: string.IsNullOrEmpty(pp?.Password) ? null : pp!.Password,
                BearerToken: string.IsNullOrEmpty(token?.Password) ? null : token!.Password), remember.IsChecked == true);
            DialogResult = true;
            Close();
        };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, ok }));
        Loaded += (_, _) => (pw ?? token ?? pp)?.Focus();
    }
}

public sealed class HostKeyDialog : Window
{
    public HostKeyDialog(Drive drive, SshEndpoint endpoint, IReadOnlyList<HostKeyEntry> keys, bool isChange)
    {
        DialogFrame.Setup(this, (isChange ? "服务器身份与上次不同 · " : "确认服务器身份 · ") + endpoint.Display, 620);
        var tile = DialogFrame.Tile(isChange ? SymbolRegular.ErrorCircle24 : SymbolRegular.ShieldCheckmark24, isChange ? ChipKind.Error : ChipKind.Info);
        var content = DialogFrame.Column(
            DialogFrame.Headline(isChange ? "服务器提供的主机密钥与已记录的不一致" : "第一次连接这台服务器"),
            DialogFrame.Body(isChange
                ? "可能存在中间人风险。除非确认服务器确实更换了密钥，否则请取消。"
                : "请与服务器管理员提供的指纹核对一致后再信任。"));

        var table = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Cell(int row, int col, string text, bool header, string? tooltip = null)
        {
            var tb = header
                ? new TextBlock { Text = text, FontSize = 12, Foreground = DialogFrame.Res("MutedBrush") }
                : DialogFrame.Mono(text);
            var cell = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                Background = header ? DialogFrame.Res("WindowBrush") : Brushes.Transparent,
                BorderBrush = DialogFrame.Res("DividerBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = tb,
                ToolTip = tooltip,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            table.Children.Add(cell);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, "算法", true);
        Cell(0, 1, "SHA256 指纹", true);
        foreach (var k in keys)
        {
            var r = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(r, 0, k.Algorithm, false);
            Cell(r, 1, k.Sha256Fingerprint, false, "MD5: " + k.Md5Fingerprint);
        }
        content.Children.Add(new Border { BorderBrush = DialogFrame.Res("CardBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = table, Margin = new Thickness(0, 12, 0, 0), ClipToBounds = true });

        var copy = DialogFrame.Button("复制指纹", ControlAppearance.Transparent);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, keys.Select(k => $"{k.Algorithm} {k.Sha256Fingerprint}"))); } catch { }
        };
        var cancel = DialogFrame.Button(isChange ? "取消（推荐）" : "取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        cancel.IsDefault = isChange;
        var trust = isChange ? DialogFrame.DangerButton("确认已更换，替换并连接") : DialogFrame.Button("指纹一致，信任并连接", ControlAppearance.Primary);
        trust.IsDefault = !isChange;
        trust.Click += (_, _) => { DialogResult = true; Close(); };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, trust }, copy));
    }
}

/// <summary>Export options (FR-15): plain configuration, or configuration plus credentials under a passphrase.</summary>
public sealed class ExportOptionsDialog : Window
{
    public bool IncludeCredentials { get; private set; }
    public string? Password { get; private set; }

    public ExportOptionsDialog(int driveCount)
    {
        DialogFrame.Setup(this, "导出配置", 540);
        var tile = DialogFrame.Tile(SymbolRegular.ArrowExportLtr24, ChipKind.Info);
        var content = DialogFrame.Column(DialogFrame.Headline($"导出 {driveCount} 个驱动器与全局设置"));
        var plain = new RadioButton { Content = "仅导出配置（不含密码与令牌）", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        var withCreds = new RadioButton { Content = "包含已保存的凭据，用口令加密", Margin = new Thickness(0, 4, 0, 0) };
        var pw = new PasswordBox { Width = 260, Margin = new Thickness(24, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false, ToolTip = "口令" };
        var pw2 = new PasswordBox { Width = 260, Margin = new Thickness(24, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false, ToolTip = "再次输入口令" };
        var hint = DialogFrame.Help("忘记口令将无法恢复。", 8);
        withCreds.Checked += (_, _) => { pw.IsEnabled = pw2.IsEnabled = true; pw.Focus(); };
        withCreds.Unchecked += (_, _) => { pw.IsEnabled = pw2.IsEnabled = false; };
        content.Children.Add(plain);
        content.Children.Add(withCreds);
        content.Children.Add(pw);
        content.Children.Add(pw2);
        content.Children.Add(hint);
        var error = DialogFrame.Help(string.Empty, 6);
        error.Foreground = DialogFrame.Res("DangerBrush");
        content.Children.Add(error);

        var cancel = DialogFrame.Button("取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        var ok = DialogFrame.Button("导出…", ControlAppearance.Primary);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            IncludeCredentials = withCreds.IsChecked == true;
            if (IncludeCredentials)
            {
                if (pw.Password.Length < 8) { error.Text = "口令至少 8 个字符。"; return; }
                if (pw.Password != pw2.Password) { error.Text = "两次输入的口令不一致。"; return; }
                Password = pw.Password;
            }
            DialogResult = true;
            Close();
        };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, ok }));
    }
}

/// <summary>Plain information with a single OK button.</summary>
public sealed class InfoDialog : Window
{
    public InfoDialog(string title, string headline, string body, bool error = false)
    {
        DialogFrame.Setup(this, title, 540);
        var tile = DialogFrame.Tile(error ? SymbolRegular.ErrorCircle24 : SymbolRegular.Checkmark24, error ? ChipKind.Error : ChipKind.Ok);
        var content = DialogFrame.Column(DialogFrame.Headline(headline), DialogFrame.Body(body));
        var ok = DialogFrame.Button("好", ControlAppearance.Primary);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { ok }));
    }
}

/// <summary>One passphrase field (import of an encrypted export).</summary>
public sealed class PasswordPromptDialog : Window
{
    public string? Password { get; private set; }

    public PasswordPromptDialog(string title, string headline, string body)
    {
        DialogFrame.Setup(this, title, 500);
        var tile = DialogFrame.Tile(SymbolRegular.Key24, ChipKind.Info);
        var pw = new PasswordBox { Width = 260, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var content = string.IsNullOrEmpty(body) ? DialogFrame.Column(DialogFrame.Headline(headline), pw) : DialogFrame.Column(DialogFrame.Headline(headline), DialogFrame.Body(body), pw);
        var cancel = DialogFrame.Button("取消", ControlAppearance.Transparent);
        cancel.IsCancel = true;
        var ok = DialogFrame.Button("确定", ControlAppearance.Primary);
        ok.IsDefault = true;
        ok.Click += (_, _) => { Password = pw.Password; DialogResult = true; Close(); };
        DialogFrame.Compose(this, DialogFrame.Message(tile, content), DialogFrame.Footer(new UIElement[] { cancel, ok }));
        Loaded += (_, _) => pw.Focus();
    }
}
