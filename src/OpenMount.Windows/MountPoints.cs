using System.Runtime.InteropServices;
using System.Text;
using OpenMount.Core.Engine;
using OpenMount.Core.Services;

namespace OpenMount.Windows;

/// <summary>Drive-letter enumeration (Z → D, excluding A/B/C and anything in use).</summary>
public sealed class DriveLetterService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, uint max);

    public bool IsFree(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        if (letter < 'C' || letter > 'Z') return false;
        var mask = GetLogicalDrives();
        if ((mask & (1u << (letter - 'A'))) != 0) return false;
        var sb = new StringBuilder(1024);
        return QueryDosDeviceW(letter + ":", sb, (uint)sb.Capacity) == 0;
    }

    /// <summary>Free letters from Z downwards.</summary>
    public IReadOnlyList<char> GetAvailable()
    {
        var list = new List<char>();
        for (var c = 'Z'; c >= 'D'; c--) if (IsFree(c)) list.Add(c);
        return list;
    }

    public char? Suggest() => GetAvailable().FirstOrDefault();
}

/// <summary>Identity and existence checks on mount points (design §13).</summary>
public sealed class MountPointInspector : IMountPointInspector
{
    private const uint DRIVE_REMOTE = 4;
    private const uint DRIVE_FIXED = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, uint volumeNameSize, out uint serial, out uint maxComponentLength, out uint flags, StringBuilder fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPath);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, uint flags, bool force);

    private readonly DriveLetterService _letters = new();

    public bool TryRemoveStaleNetworkMapping(char letter, string uncServerPrefix)
    {
        letter = char.ToUpperInvariant(letter);
        var unc = GetNetworkRemoteName(letter);
        if (unc is null) return false;
        var prefix = @"\\" + uncServerPrefix.Trim('\\') + @"\";
        if (!unc.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false; // not ours: never touch
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (GetVolumeInformationW(letter + @":\", label, (uint)label.Capacity, out _, out _, out _, fs, (uint)fs.Capacity))
            return false; // still a live WinFsp volume
        var rc = WNetCancelConnection2W(letter + ":", 0, true);
        NotifyMountPointChanged(letter + ":", added: false, network: true);
        return rc == 0;
    }

    public IReadOnlyList<char> SweepStaleNetworkMappings(string uncServerPrefix)
    {
        var cleaned = new List<char>();
        for (var c = 'D'; c <= 'Z'; c++)
            if (TryRemoveStaleNetworkMapping(c, uncServerPrefix)) cleaned.Add(c);
        return cleaned;
    }

    public void NotifyMountPointChanged(string mountPoint, bool added, bool network = false) => ShellIntegration.NotifyDriveChanged(mountPoint, added, network);

    /// <summary>UNC path a mapped network drive points at (e.g. \\OpenMount\Name), or null.</summary>
    public static string? GetNetworkRemoteName(char letter)
    {
        var sb = new StringBuilder(1024);
        var len = sb.Capacity;
        return WNetGetConnectionW(char.ToUpperInvariant(letter) + ":", sb, ref len) == 0 ? sb.ToString() : null;
    }

    public static string RootOf(string mountPoint)
    {
        var mp = mountPoint.Trim();
        if (mp.Length == 2 && mp[1] == ':') return mp + "\\";
        return mp.EndsWith('\\') ? mp : mp + "\\";
    }

    public bool Exists(string mountPoint)
    {
        var mp = mountPoint.Trim();
        if (mp.Length == 2 && mp[1] == ':') return !_letters.IsFree(mp[0]);
        return Directory.Exists(mp);
    }

    public bool IsDriveLetterFree(char letter) => _letters.IsFree(letter);

    /// <summary>
    /// Verified against real rclone 1.75 mounts: the file system name is "FUSE-rclone" ("NTFS" with --ExactFileSystemName),
    /// a local-disk mount reports the --volname as its label, while a network-mode mount reports an EMPTY label and
    /// exposes the share as the drive's UNC remote name (\\server\share). <paramref name="expectedIdentity"/> is therefore
    /// the UNC path for network mode and the volume label otherwise.
    /// </summary>
    public bool VerifyIdentity(string mountPoint, string expectedIdentity, IReadOnlyCollection<string> allowedFileSystemNames, bool expectNetworkDrive, out string detail)
    {
        var root = RootOf(mountPoint);
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (!GetVolumeInformationW(root, label, (uint)label.Capacity, out _, out _, out _, fs, (uint)fs.Capacity))
        {
            detail = "GetVolumeInformation failed: " + Marshal.GetLastWin32Error();
            return false;
        }
        var fsOk = allowedFileSystemNames.Any(n => string.Equals(n, fs.ToString(), StringComparison.OrdinalIgnoreCase));
        var type = GetDriveTypeW(root);
        var isLetter = mountPoint.Trim().Length == 2;
        bool identityOk;
        string identityDetail;
        if (expectNetworkDrive && isLetter)
        {
            var unc = GetNetworkRemoteName(mountPoint[0]) ?? string.Empty;
            identityOk = string.Equals(unc.TrimEnd('\\'), expectedIdentity.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && type == DRIVE_REMOTE;
            identityDetail = $"unc='{unc}'";
        }
        else
        {
            identityOk = string.Equals(label.ToString(), expectedIdentity, StringComparison.OrdinalIgnoreCase) && (!isLetter || type == DRIVE_FIXED);
            identityDetail = $"label='{label}'";
        }
        detail = $"{identityDetail} fs='{fs}' type={type} expected='{expectedIdentity}'";
        return identityOk && fsOk;
    }

    public bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    public bool IsDanglingReparsePoint(string path)
    {
        if (!Directory.Exists(path) || !IsReparsePoint(path)) return false;
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return false; // enumerable → a live volume
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool TryRemoveDanglingReparsePoint(string path)
    {
        if (!IsDanglingReparsePoint(path)) return false;
        try { Directory.Delete(path, recursive: false); return true; }
        catch { return false; }
    }
}

/// <summary>Validation environment backed by the real file system.</summary>
public sealed class WindowsValidationEnvironment : IValidationEnvironment
{
    private readonly DriveLetterService _letters = new();

    public bool IsDriveLetterFree(char letter) => _letters.IsFree(letter);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

    public bool IsLocalNtfsFixedVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false;
            var di = new DriveInfo(root);
            return di.DriveType == DriveType.Fixed && string.Equals(di.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
