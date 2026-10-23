namespace OpenMount.Core.Services;

public enum SftpPathMeaning { Home, RelativeToHome, Absolute }

public sealed record SftpPathResolution(string RemoteSuffix, SftpPathMeaning Meaning, string Display, string Normalized);

/// <summary>
/// Single source of truth for SFTP path semantics (review R08): the same result feeds the rclone mount source and the UI preview.
/// Input is trimmed only; no slash is added or removed, no Windows normalisation.
/// </summary>
public static class SftpPathResolver
{
    public static SftpPathResolution Resolve(string? input)
    {
        var p = (input ?? string.Empty).Trim();
        if (p.Length == 0)
            return new SftpPathResolution(string.Empty, SftpPathMeaning.Home, "账号家目录", string.Empty);
        if (p.StartsWith('/'))
            return new SftpPathResolution(p, SftpPathMeaning.Absolute, $"服务器根目录下的 {p}", p);
        return new SftpPathResolution(p, SftpPathMeaning.RelativeToHome, $"账号家目录下的 {p}", "~/" + p);
    }
}
