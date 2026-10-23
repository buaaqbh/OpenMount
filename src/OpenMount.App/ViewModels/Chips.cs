using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using OpenMount.Windows;
using Wpf.Ui.Controls;

namespace OpenMount.App.ViewModels;

/// <summary>Colour family of a status chip; fixed per dimension (design "三维状态的表达").</summary>
public enum ChipKind { Neutral, Ok, Warn, Info, Error }

/// <summary>Leading glyph of a chip. Colour alone never carries the meaning.</summary>
public enum ChipMark { None, Dot, Ring, Spinner, Alert, Upload, Check }

public sealed record Chip(string Text, ChipKind Kind = ChipKind.Neutral, ChipMark Mark = ChipMark.None, string? ToolTip = null);

/// <summary>Maps the three-dimensional snapshot to chips and plain-text summaries. Pure functions so the tray and the list agree.</summary>
public static class StatusPresentation
{
    public static Chip Lifecycle(DriveSnapshot s, DateTimeOffset now)
    {
        if (s.OwnedByOtherSession) return new Chip("已在其它会话连接", ChipKind.Neutral, ChipMark.Ring);
        switch (s.Lifecycle)
        {
            case LifecycleState.Mounted: return new Chip("已连接", ChipKind.Ok, ChipMark.Dot);
            case LifecycleState.Connecting: return new Chip("连接中", ChipKind.Info, ChipMark.Spinner);
            case LifecycleState.Disconnecting:
                return s.StopProgress is { } sp
                    ? new Chip($"正在等待上传 {sp.Remaining} 个文件（{SizeSuffix.Format(sp.RemainingBytes)}）", ChipKind.Info, ChipMark.Spinner)
                    : new Chip("断开中", ChipKind.Info, ChipMark.Spinner);
            case LifecycleState.Reconnecting:
                var wait = s.NextRetryAt is { } at ? Math.Max(0, (int)Math.Ceiling((at - now).TotalSeconds)) : 0;
                return new Chip($"重连中 · 第 {s.ReconnectAttempt} 次，{wait} s 后重试", ChipKind.Warn, ChipMark.Spinner, s.Error?.Title);
            case LifecycleState.NeedsCredentials: return new Chip("需要登录", ChipKind.Warn, ChipMark.Alert);
            case LifecycleState.NeedsHostKeyConfirm: return new Chip("需要确认主机密钥", ChipKind.Warn, ChipMark.Alert);
            case LifecycleState.Error: return new Chip("连接失败 · " + (s.Error?.Title ?? "未知错误"), ChipKind.Error, ChipMark.Alert, s.Error?.Message);
            default: return new Chip("已断开", ChipKind.Neutral, ChipMark.Ring);
        }
    }

    /// <summary>Remote health is only meaningful while mounted; null means "nothing to show".</summary>
    public static Chip? Health(DriveSnapshot s)
    {
        if (s.Lifecycle != LifecycleState.Mounted) return null;
        return s.RemoteHealth switch
        {
            RemoteHealth.Unreachable => new Chip("远端暂不可达 · 重试中", ChipKind.Warn, ChipMark.Alert),
            RemoteHealth.AuthRequired => new Chip("远端要求重新认证", ChipKind.Warn, ChipMark.Alert),
            RemoteHealth.Forbidden => new Chip("远端拒绝访问", ChipKind.Warn, ChipMark.Alert),
            RemoteHealth.NotFound => new Chip("远端路径不存在", ChipKind.Warn, ChipMark.Alert),
            _ => null,
        };
    }

    public static Chip? Sync(DriveSnapshot s)
    {
        var sync = s.Sync;
        if (s.Lifecycle == LifecycleState.Mounted)
        {
            if (s.StopProgress is not null) return null; // shown in the lifecycle chip
            return sync.Kind switch
            {
                SyncKind.Uploading => new Chip($"正在上传 {sync.UploadingCount}/{sync.PendingCount}", ChipKind.Info, ChipMark.Upload),
                SyncKind.Pending => new Chip($"{sync.PendingCount} 个待上传", ChipKind.Info, ChipMark.Upload),
                SyncKind.Unknown => new Chip("同步状态待确认", ChipKind.Neutral, ChipMark.None, sync.Detail),
                _ => null,
            };
        }
        if (s.Lifecycle is LifecycleState.Connecting or LifecycleState.Disconnecting) return null;
        return sync.Kind switch
        {
            SyncKind.Pending => new Chip($"保留了 {sync.PendingCount} 个待上传文件", ChipKind.Info, ChipMark.Upload, "连接后自动继续上传"),
            SyncKind.Unknown when sync.Source != SyncSource.None => new Chip("同步状态待确认", ChipKind.Neutral, ChipMark.None, sync.Detail),
            _ => null,
        };
    }

    /// <summary>One line for tooltips, the tray and logs.</summary>
    public static string Summary(DriveSnapshot s, DateTimeOffset now)
    {
        var parts = new List<string> { Lifecycle(s, now).Text };
        if (Health(s) is { } h) parts.Add(h.Text);
        if (Sync(s) is { } y) parts.Add(y.Text);
        return string.Join(" · ", parts);
    }

    /// <summary>Static facts about the configuration (not state).</summary>
    public static IReadOnlyList<Chip> Badges(Drive d, DriveSnapshot s)
    {
        var b = new List<Chip> { new(d.Mount.NetworkMode ? "网络驱动器" : "本地磁盘") };
        if (d.Mount.ReadOnly) b.Add(new Chip("只读"));
        if (d.Mount.AutoReconnect) b.Add(new Chip("自动重连"));
        if (!d.Mount.AutoConnectAtStart) b.Add(new Chip("不自动连接"));
        if (d.Mount.UseProxy) b.Add(new Chip("代理"));
        if (d.CredentialPolicy != CredentialPolicy.Store) b.Add(new Chip(d.CredentialPolicy == CredentialPolicy.Prompt ? "每次询问凭据" : "凭据仅本次会话"));
        if (d.StorageType == StorageType.WebDav && d.WebDav is { } w)
        {
            if (!string.Equals(w.Scheme, "https", StringComparison.OrdinalIgnoreCase)) b.Add(new Chip("明文 HTTP", ChipKind.Warn, ChipMark.Alert, "账号密码未加密传输"));
            else if (w.TlsMode == TlsMode.Ignore) b.Add(new Chip("忽略证书校验", ChipKind.Warn, ChipMark.Alert));
        }
        if (d.StorageType == StorageType.Sftp && d.Sftp is { } f && f.HostKeyCheck == HostKeyCheckMode.None)
            b.Add(new Chip("不校验主机密钥", ChipKind.Warn, ChipMark.Alert));
        if (s.Sync.AbnormalStop && s.Sync.RequiresProtection) b.Add(new Chip("非正常停止", ChipKind.Warn, ChipMark.Alert, "最后写入的内容可能不完整"));
        return b;
    }

    public static SymbolRegular TypeSymbol(Drive d) => ShellIntegration.DriveIconKind(d.StorageType, d.Preset) switch
    {
        "sftp" => SymbolRegular.Key24,
        "cloud" => SymbolRegular.Cloud24,
        "nas" => SymbolRegular.Server24,
        _ => SymbolRegular.Globe24,
    };

    public static string TypeLabel(Drive d) => Presets.Find(d.Preset)?.DisplayName ?? (d.StorageType == StorageType.WebDav ? "WebDAV" : "SFTP");

    /// <summary>Anything a user should look at: errors, prompts, degraded remote, unconfirmed data.</summary>
    public static bool NeedsAttention(DriveSnapshot s) =>
        s.Lifecycle is LifecycleState.Error or LifecycleState.Reconnecting or LifecycleState.NeedsCredentials or LifecycleState.NeedsHostKeyConfirm
        || (s.Lifecycle == LifecycleState.Mounted && s.RemoteHealth is not (RemoteHealth.Healthy or RemoteHealth.Unknown));

    public static bool IsUploading(DriveSnapshot s) => s.Lifecycle == LifecycleState.Mounted && s.Sync.Kind == SyncKind.Uploading;
}
