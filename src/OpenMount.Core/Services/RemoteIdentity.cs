using System.Security.Cryptography;
using System.Text;
using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

/// <summary>Canonical identity of the remote target a drive points at; binds cache directories to their origin (FR-22.7).</summary>
public static class RemoteIdentity
{
    public static string Canonical(Drive drive)
    {
        switch (drive.StorageType)
        {
            case StorageType.WebDav:
            {
                var c = drive.WebDav ?? new WebDavConnection();
                return string.Join('|', "webdav", c.Scheme.ToLowerInvariant(), c.Host.Trim().ToLowerInvariant(),
                    c.EffectivePort.ToString(), WebDavConnection.NormalizePath(c.Path), c.AuthMode.ToString().ToLowerInvariant(),
                    c.AuthMode == WebDavAuthMode.Basic ? (c.Username ?? string.Empty) : string.Empty, c.Vendor.ToString().ToLowerInvariant());
            }
            case StorageType.Sftp:
            {
                var c = drive.Sftp ?? new SftpConnection();
                return string.Join('|', "sftp", c.Host.Trim().ToLowerInvariant(), c.Port.ToString(),
                    SftpPathResolver.Resolve(c.Path).RemoteSuffix, c.Username);
            }
            default:
                return drive.StorageType.ToString();
        }
    }

    public static string ComputeHash(Drive drive)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(drive)));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>Volume label / UNC share name derived from the drive name.</summary>
public static class SafeName
{
    private static readonly char[] Invalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public static string From(string? name, string shortId)
    {
        var s = new string((name ?? string.Empty).Where(c => Array.IndexOf(Invalid, c) < 0 && !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) s = "Drive-" + shortId;
        if (s.Length > 32) s = s[..32].TrimEnd();
        return s;
    }
}

/// <summary>rclone size suffix helpers ("10G", "512M", "1.5T").</summary>
public static class SizeSuffix
{
    public static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.EndsWith("iB", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        else if (s.EndsWith('B') || s.EndsWith('b')) s = s[..^1];
        var mult = 1L;
        if (s.Length > 0 && char.IsLetter(s[^1]))
        {
            mult = char.ToUpperInvariant(s[^1]) switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                'T' => 1L << 40,
                'P' => 1L << 50,
                _ => -1,
            };
            if (mult < 0) return false;
            s = s[..^1];
        }
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0) return false;
        bytes = (long)(v * mult);
        return true;
    }

    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
