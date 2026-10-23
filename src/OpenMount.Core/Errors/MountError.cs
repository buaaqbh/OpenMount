using System.Text.RegularExpressions;
using OpenMount.Core.State;

namespace OpenMount.Core.Errors;

public enum MountErrorKind
{
    Auth,
    HostKeyMismatch,
    HostKeyUnknown,
    Unreachable,
    TlsCert,
    Forbidden,
    NotFound,
    MountPointInUse,
    WinFspMissing,
    RcloneMissing,
    CacheDiskFull,
    Timeout,
    IdentityMismatch,
    CacheIdentityMismatch,
    Crashed,
    Cancelled,
    HostKeyScanUnavailable,
    Unknown,
}

public sealed record MountError(
    MountErrorKind Kind,
    string Title,
    string Message,
    string Suggestion,
    string TechnicalDetails,
    bool CanRetry,
    bool CanEdit)
{
    /// <summary>Whether the supervisor may retry automatically after this error.</summary>
    public bool AllowsAutoReconnect => Kind is MountErrorKind.Unreachable or MountErrorKind.Timeout or MountErrorKind.Crashed or MountErrorKind.Unknown;

    public static MountError Create(MountErrorKind kind, string technicalDetails)
    {
        var (title, message, suggestion, canRetry, canEdit) = kind switch
        {
            MountErrorKind.Auth => ("账号或密码错误", "服务器拒绝了提供的账号、密码或密钥。", "请检查账号与密码后重试。若服务器启用了两步验证，请使用应用专用密码。", true, true),
            MountErrorKind.HostKeyMismatch => ("服务器身份与上次不同", "SFTP 服务器提供的主机密钥与已记录的不一致，可能存在中间人风险。", "若服务器确实更换了密钥，可在驱动器详情中查看新指纹并替换；否则请不要连接。", false, true),
            MountErrorKind.HostKeyUnknown => ("需要确认主机密钥", "首次连接该 SFTP 服务器，需要确认其主机密钥指纹。", "重新连接并在弹出的对话框中确认指纹。", true, true),
            MountErrorKind.Unreachable => ("无法连接到服务器", "无法建立到服务器的网络连接。", "请检查地址、端口、网络连接、防火墙以及服务器上的服务是否已开启。", true, true),
            MountErrorKind.TlsCert => ("服务器证书不受信任", "TLS 证书校验失败。", "可在证书策略中导入自定义 CA 证书，或选择忽略校验（不推荐）。", false, true),
            MountErrorKind.Forbidden => ("没有访问权限", "服务器拒绝访问该路径。", "请检查账号对该路径的权限。", false, true),
            MountErrorKind.NotFound => ("远端路径不存在", "服务器上找不到指定的路径。", "请检查路径是否正确。", false, true),
            MountErrorKind.MountPointInUse => ("挂载点已被占用", "盘符或目标文件夹已被其它程序使用。", "请更换盘符，或选择一个尚不存在的文件夹。", false, true),
            MountErrorKind.WinFspMissing => ("未安装 WinFsp 组件", "OpenMount 依赖 WinFsp 才能创建盘符。", "请在设置中安装 WinFsp，安装后如提示需要重启请重启。", false, false),
            MountErrorKind.RcloneMissing => ("找不到 rclone", "内置或自定义的 rclone 可执行文件缺失或校验失败。", "请重新安装 OpenMount，或在设置中指定有效的 rclone 路径。", false, false),
            MountErrorKind.CacheDiskFull => ("缓存磁盘空间不足", "缓存目录所在的磁盘已没有可用空间。", "请清理磁盘空间或在设置中把缓存目录迁移到更大的磁盘。未上传的数据不会被自动删除。", true, false),
            MountErrorKind.Timeout => ("挂载超时", "挂载进程在规定时间内没有就绪。", "请查看日志；若服务器响应缓慢，可在设置中延长挂载就绪超时。", true, true),
            MountErrorKind.IdentityMismatch => ("挂载点身份校验失败", "挂载点出现了，但不是本驱动器创建的卷。", "请检查是否有其它程序使用了相同的盘符或文件夹。", true, true),
            MountErrorKind.CacheIdentityMismatch => ("缓存归属不匹配", "该驱动器的缓存目录记录的连接目标与当前配置不同。", "请在驱动器详情中处理待恢复的缓存（查看或丢弃）后再连接。", false, true),
            MountErrorKind.Crashed => ("挂载进程意外退出", "rclone 进程在运行中退出。", "请查看日志了解原因；OpenMount 会按设置自动重连。", true, true),
            MountErrorKind.Cancelled => ("已取消", "连接操作已被取消。", string.Empty, true, true),
            MountErrorKind.HostKeyScanUnavailable => ("主机密钥探测不可用", "当前版本尚未集成主机密钥探测组件。", "请在驱动器设置中暂时选择“不校验主机密钥”（不推荐），或等待后续版本。", false, true),
            _ => ("连接失败", "发生了未分类的错误。", "请查看日志中的技术详情。", true, true),
        };
        return new MountError(kind, title, message, suggestion, technicalDetails, canRetry, canEdit);
    }

    public RemoteHealth ToRemoteHealth() => Kind switch
    {
        MountErrorKind.Auth => RemoteHealth.AuthRequired,
        MountErrorKind.Forbidden => RemoteHealth.Forbidden,
        MountErrorKind.NotFound => RemoteHealth.NotFound,
        MountErrorKind.Unreachable or MountErrorKind.Timeout => RemoteHealth.Unreachable,
        _ => RemoteHealth.Unknown,
    };
}

public enum ClassifyPhase { Startup, Running, Probe }

/// <summary>Maps rclone output to a <see cref="MountError"/>. Rules are ordered; first match wins.</summary>
public static class ErrorClassifier
{
    private static readonly (MountErrorKind Kind, Regex Pattern)[] Rules =
    {
        (MountErrorKind.HostKeyMismatch, new Regex(@"knownhosts: key mismatch|host key mismatch|REMOTE HOST IDENTIFICATION HAS CHANGED", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.HostKeyUnknown, new Regex(@"knownhosts: key is unknown|key is unknown", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.WinFspMissing, new Regex(@"cannot find winfsp|winfsp.*not (found|installed)|cgofuse: .*winfsp|Unable to load library|winfsp-x64\.dll", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.TlsCert, new Regex(@"x509:|tls: failed to verify|certificate (signed by unknown authority|is not valid|has expired)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.Auth, new Regex(@"\b401\b|Unauthorized|unable to authenticate|permission denied \(publickey|invalid credentials|authentication failed|no supported methods remain|auth(entication)? error", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.CacheDiskFull, new Regex(@"no space left on device|not enough space on the disk|There is not enough space|disk full", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.MountPointInUse, new Regex(@"mountpoint path already exists|mount point .*already (exists|in use)|is in use|The directory is not empty|drive letter .*in use", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.Unreachable, new Regex(@"no such host|connection refused|i/o timeout|network is unreachable|dial tcp|context deadline exceeded|connection reset|EOF\b.*(connect|dial)|TLS handshake timeout|connect: ", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.Forbidden, new Regex(@"\b403\b|Forbidden|permission denied", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (MountErrorKind.NotFound, new Regex(@"\b404\b|directory not found|couldn't find|no such file or directory|object not found|file does not exist", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    public static MountError Classify(IEnumerable<string> redactedLines, int? exitCode, ClassifyPhase phase)
    {
        var lines = redactedLines.ToList();
        var text = string.Join('\n', lines);
        var technical = exitCode.HasValue ? $"exit code {exitCode}\n{text}" : text;
        foreach (var (kind, pattern) in Rules)
        {
            if (pattern.IsMatch(text)) return MountError.Create(kind, technical);
        }
        if (phase == ClassifyPhase.Running && exitCode is not null && exitCode != 0)
            return MountError.Create(MountErrorKind.Crashed, technical);
        return MountError.Create(MountErrorKind.Unknown, technical);
    }

    /// <summary>Classify a probe error string into a remote health value.</summary>
    public static RemoteHealth ClassifyProbe(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return RemoteHealth.Healthy;
        var kind = Classify(new[] { error }, null, ClassifyPhase.Probe).Kind;
        return kind switch
        {
            MountErrorKind.Auth => RemoteHealth.AuthRequired,
            MountErrorKind.Forbidden => RemoteHealth.Forbidden,
            MountErrorKind.NotFound => RemoteHealth.NotFound,
            MountErrorKind.Unreachable => RemoteHealth.Unreachable,
            _ => RemoteHealth.Unreachable,
        };
    }
}
