using System.Net;
using System.Text.RegularExpressions;
using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

public interface IValidationEnvironment
{
    bool IsDriveLetterFree(char letter);
    bool DirectoryExists(string path);
    bool FileExists(string path);
    bool IsLocalNtfsFixedVolume(string path);
}

public sealed record ValidationError(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>Shared by the UI and by import (review R03): the same rules apply to every entry point.</summary>
public sealed class DriveValidator
{
    private static readonly Regex SizePattern = new(@"^\d+(\.\d+)?[kKmMgGtTpP]?(i?[bB])?$", RegexOptions.Compiled);
    private static readonly Regex DurationPattern = new(@"^\d+(ms|s|m|h|d|w)$", RegexOptions.Compiled);
    private static readonly Regex HostPattern = new(@"^(?=.{1,253}$)([a-zA-Z0-9_]([a-zA-Z0-9_\-]{0,61}[a-zA-Z0-9_])?\.)*[a-zA-Z0-9_]([a-zA-Z0-9_\-]{0,61}[a-zA-Z0-9_])?$", RegexOptions.Compiled);

    private readonly IValidationEnvironment _env;

    public DriveValidator(IValidationEnvironment env) => _env = env;

    public IReadOnlyList<ValidationError> Validate(Drive drive, IEnumerable<Drive> existing)
    {
        var errors = new List<ValidationError>();
        var others = existing.Where(d => d.Id != drive.Id).ToList();

        // Name
        var name = drive.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors.Add(new("name", "请输入名称"));
        else if (name.Length > 32) errors.Add(new("name", "名称不能超过 32 个字符"));
        else
        {
            var safe = SafeName.From(name, drive.ShortId);
            if (others.Any(o => string.Equals(SafeName.From(o.Name, o.ShortId), safe, StringComparison.OrdinalIgnoreCase)))
                errors.Add(new("name", "名称已被使用"));
        }

        // Mount point
        if (drive.Mount.Kind == MountKind.DriveLetter)
        {
            var letter = drive.Mount.DriveLetter?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(letter) || letter.Length != 1 || letter[0] < 'C' || letter[0] > 'Z')
                errors.Add(new("driveLetter", "请选择盘符（C–Z）"));
            else
            {
                if (others.Any(o => o.Mount.Kind == MountKind.DriveLetter && string.Equals(o.Mount.DriveLetter, letter, StringComparison.OrdinalIgnoreCase)))
                    errors.Add(new("driveLetter", $"盘符 {letter}: 已被另一个驱动器使用"));
                else if (!_env.IsDriveLetterFree(letter[0]))
                    errors.Add(new("driveLetter", $"盘符 {letter}: 已被占用"));
            }
        }
        else
        {
            var path = drive.Mount.FolderPath?.Trim();
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                errors.Add(new("folderPath", "请输入文件夹挂载点的完整路径"));
            else
            {
                if (drive.Mount.NetworkMode) errors.Add(new("folderPath", "文件夹挂载点仅支持“作为本地磁盘”模式"));
                var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(parent) || !_env.DirectoryExists(parent))
                    errors.Add(new("folderPath", "父文件夹不存在"));
                else if (_env.DirectoryExists(path) || _env.FileExists(path))
                    errors.Add(new("folderPath", "请选择本地 NTFS 磁盘上尚不存在的新文件夹"));
                else if (!_env.IsLocalNtfsFixedVolume(parent))
                    errors.Add(new("folderPath", "文件夹挂载点必须位于本地 NTFS 固定磁盘"));
                if (others.Any(o => o.Mount.Kind == MountKind.Folder && string.Equals(o.Mount.FolderPath?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    errors.Add(new("folderPath", "该文件夹已被另一个驱动器使用"));
            }
        }

        switch (drive.StorageType)
        {
            case StorageType.WebDav:
                ValidateWebDav(drive, errors);
                break;
            case StorageType.Sftp:
                ValidateSftp(drive, errors);
                break;
        }

        ValidateAdvanced(drive.Advanced, errors);
        return errors;
    }

    private void ValidateWebDav(Drive drive, List<ValidationError> errors)
    {
        var c = drive.WebDav;
        if (c is null) { errors.Add(new("connection", "缺少 WebDAV 连接信息")); return; }
        if (c.Scheme is not ("http" or "https")) errors.Add(new("scheme", "协议只能是 http 或 https"));
        if (!IsValidHost(c.Host)) errors.Add(new("host", "请输入有效的地址"));
        if (c.Port is { } p && (p < 1 || p > 65535)) errors.Add(new("port", "端口必须在 1–65535 之间"));
        if ((c.Path ?? string.Empty).Contains('\\')) errors.Add(new("path", "路径不能包含反斜杠"));
        if (c.TimeoutSeconds is < 5 or > 600) errors.Add(new("timeout", "超时必须在 5–600 秒之间"));
        switch (c.AuthMode)
        {
            case WebDavAuthMode.Basic when string.IsNullOrWhiteSpace(c.Username):
                errors.Add(new("username", "账号密码方式需要填写账号"));
                break;
        }
        if (c.TlsMode == TlsMode.CustomCa && (string.IsNullOrWhiteSpace(c.CaCertPath) || !_env.FileExists(c.CaCertPath)))
            errors.Add(new("caCert", "请选择存在的 CA 证书文件"));
    }

    private void ValidateSftp(Drive drive, List<ValidationError> errors)
    {
        var c = drive.Sftp;
        if (c is null) { errors.Add(new("connection", "缺少 SFTP 连接信息")); return; }
        if (!IsValidHost(c.Host)) errors.Add(new("host", "请输入有效的地址"));
        if (c.Port is < 1 or > 65535) errors.Add(new("port", "端口必须在 1–65535 之间"));
        if (string.IsNullOrWhiteSpace(c.Username)) errors.Add(new("username", "请输入账号"));
        if ((c.Path ?? string.Empty).Contains('\\')) errors.Add(new("path", "路径不能包含反斜杠"));
        if (c.AuthMode is SftpAuthMode.Key or SftpAuthMode.KeyAndPassword)
        {
            if (string.IsNullOrWhiteSpace(c.KeyFilePath)) errors.Add(new("keyFile", "请选择私钥文件"));
            else if (!_env.FileExists(c.KeyFilePath)) errors.Add(new("keyFile", "私钥文件不存在"));
            else if (c.KeyFilePath.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
                errors.Add(new("keyFile", "请使用 OpenSSH 格式私钥（PuTTYgen → Conversions → Export OpenSSH key）"));
        }
        if (c.AuthMode == SftpAuthMode.Agent) errors.Add(new("authMode", "SSH Agent 认证将在后续版本提供"));
        if (c.IdleTimeoutSeconds is < 5 or > 3600) errors.Add(new("idleTimeout", "空闲超时必须在 5–3600 秒之间"));
    }

    private static void ValidateAdvanced(AdvancedOptions a, List<ValidationError> errors)
    {
        CheckSize(a.CacheCleanupTarget, "cacheCleanupTarget", errors);
        CheckSize(a.CacheMinFreeSpace, "cacheMinFreeSpace", errors);
        CheckSize(a.ReadChunkSize, "readChunkSize", errors);
        CheckSize(a.ReadAhead, "readAhead", errors);
        CheckSize(a.BufferSize, "bufferSize", errors);
        CheckSize(a.ReportedCapacity, "reportedCapacity", errors);
        CheckSize(a.BwLimitUp, "bwLimitUp", errors);
        CheckSize(a.BwLimitDown, "bwLimitDown", errors);
        CheckDuration(a.CacheMaxAge, "cacheMaxAge", errors);
        CheckDuration(a.DirCacheTime, "dirCacheTime", errors);
        CheckDuration(a.WriteBack, "writeBack", errors);
        if (a.Transfers is { } t && (t < 1 || t > 64)) errors.Add(new("transfers", "并行传输数必须在 1–64 之间"));
    }

    private static void CheckSize(string? v, string field, List<ValidationError> errors)
    {
        if (!string.IsNullOrEmpty(v) && !SizePattern.IsMatch(v)) errors.Add(new(field, "大小格式应为数字加 K/M/G/T，如 10G"));
    }

    private static void CheckDuration(string? v, string field, List<ValidationError> errors)
    {
        if (!string.IsNullOrEmpty(v) && !DurationPattern.IsMatch(v)) errors.Add(new(field, "时间格式应为数字加 s/m/h/d，如 24h"));
    }

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (IPAddress.TryParse(h, out _)) return true;
        return HostPattern.IsMatch(h);
    }
}
