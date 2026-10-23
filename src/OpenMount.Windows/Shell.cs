using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;

namespace OpenMount.Windows;

/// <summary>Per-user drive icons, Explorer refresh notifications, opening Explorer.</summary>
public sealed class ShellIntegration : IShellIntegration
{
    private const int SHCNE_DRIVEADD = 0x00000100;
    private const int SHCNE_DRIVEREMOVED = 0x00000080;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;
    /// <summary>Deliver synchronously so the shell has processed the change before the caller moves on.</summary>
    private const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private readonly string _exePath;

    public ShellIntegration(string exePath) => _exePath = exePath;

    private static string IconKey(char letter) => $@"Software\Classes\Applications\Explorer.exe\Drives\{char.ToUpperInvariant(letter)}\DefaultIcon";

    public void SetDriveIcon(char letter, StorageType type, string? preset)
    {
        using var key = Registry.CurrentUser.CreateSubKey(IconKey(letter));
        key?.SetValue(string.Empty, IconResource(type, preset));
    }

    /// <summary>
    /// Per-type Explorer icons ship as Assets\drive-*.ico next to the exe (rendered by tools\IconGen from the design);
    /// falls back to the application icon when the asset is missing (e.g. a trimmed deployment).
    /// </summary>
    internal string IconResource(StorageType type, string? preset)
    {
        var kind = DriveIconKind(type, preset);
        var dir = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory;
        var ico = Path.Combine(dir, "Assets", $"drive-{kind}.ico");
        return File.Exists(ico) ? $"\"{ico}\",0" : $"\"{_exePath}\",0";
    }

    public static string DriveIconKind(StorageType type, string? preset)
    {
        if (type == StorageType.Sftp) return "sftp";
        return preset?.ToLowerInvariant() switch
        {
            "nextcloud" or "owncloud" => "cloud",
            "synology" or "qnap" or "asustor" => "nas",
            _ => "webdav",
        };
    }

    public void RemoveDriveIcon(char letter)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\Explorer.exe\Drives\{char.ToUpperInvariant(letter)}", throwOnMissingSubKey: false); }
        catch { }
    }

    public void NotifyMountPointChanged(string mountPoint, bool added, bool network = false) => NotifyDriveChanged(mountPoint, added, network);

    /// <summary>
    /// Tell Explorer that a mount point appeared or vanished. For drive letters this first sends a synchronous
    /// WM_DEVICECHANGE volume broadcast: WinFsp creates and removes its drive letters with DefineDosDevice, and on Windows 11
    /// the resulting system broadcast does not make Explorer drop its record of a network drive, so every network-mode
    /// mount/unmount cycle left a permanent "disconnected network drive" entry in This PC until this broadcast was added
    /// (root-caused 2026-10-23, design §13). The same broadcast clears such an entry left by a killed mount or a crashed app.
    /// </summary>
    public static void NotifyDriveChanged(string mountPoint, bool added, bool network = false)
    {
        var isLetter = mountPoint.Trim().Length == 2;
        if (isLetter) BroadcastVolumeChange(mountPoint.Trim()[0], added, network);
        var path = MountPointInspector.RootOf(mountPoint);
        var ptr = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(isLetter ? (added ? SHCNE_DRIVEADD : SHCNE_DRIVEREMOVED) : SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, ptr, IntPtr.Zero);
        }
        catch
        {
            // best effort
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private const uint WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;
    private const short DBTF_NET = 0x0002;
    private const uint BSM_APPLICATIONS = 0x0008;
    /// <summary>
    /// Synchronous delivery is essential: posting WM_DEVICECHANGE (BSF_POSTMESSAGE) fails with ERROR_MESSAGE_SYNC_ONLY, and
    /// a removal that Explorer has not processed by the time it next looks at the letter leaves the stale record behind.
    /// </summary>
    private const uint BSF_IGNORECURRENTTASK = 0x0002, BSF_NOHANG = 0x0008, BSF_FORCEIFHUNG = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public int dbcv_unitmask;
        public short dbcv_flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int BroadcastSystemMessageW(uint flags, ref uint recipients, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Send DBT_DEVICEARRIVAL / DBT_DEVICEREMOVECOMPLETE for one drive letter, flagged DBTF_NET for network drives.</summary>
    public static bool BroadcastVolumeChange(char letter, bool arrival, bool network)
    {
        var volume = new DEV_BROADCAST_VOLUME
        {
            dbcv_size = Marshal.SizeOf<DEV_BROADCAST_VOLUME>(),
            dbcv_devicetype = DBT_DEVTYP_VOLUME,
            dbcv_unitmask = 1 << (char.ToUpperInvariant(letter) - 'A'),
            dbcv_flags = network ? DBTF_NET : (short)0,
        };
        var ptr = Marshal.AllocHGlobal(volume.dbcv_size);
        try
        {
            Marshal.StructureToPtr(volume, ptr, false);
            var recipients = BSM_APPLICATIONS;
            var result = BroadcastSystemMessageW(BSF_IGNORECURRENTTASK | BSF_NOHANG | BSF_FORCEIFHUNG, ref recipients, WM_DEVICECHANGE,
                (IntPtr)(arrival ? DBT_DEVICEARRIVAL : DBT_DEVICEREMOVECOMPLETE), ptr);
            return result > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void OpenInExplorer(string mountPoint)
    {
        var path = MountPointInspector.RootOf(mountPoint);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }
}

/// <summary>HKCU Run key ("Startup apps" in Task Manager).</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenMount";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>WinFsp detection via the registry (the installer is a 32-bit MSI, hence WOW6432Node) and the DLL.</summary>
public sealed record WinFspInfo(bool Installed, string? InstallDir, string? DllPath, string? Version)
{
    public static WinFspInfo Detect()
    {
        string? installDir = null;
        foreach (var keyPath in new[] { @"SOFTWARE\WOW6432Node\WinFsp", @"SOFTWARE\WinFsp" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue("InstallDir") is string dir && Directory.Exists(dir)) { installDir = dir; break; }
        }
        if (installDir is null) return new WinFspInfo(false, null, null, null);
        var dllName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "winfsp-a64.dll" : "winfsp-x64.dll";
        var dll = Path.Combine(installDir, "bin", dllName);
        if (!File.Exists(dll)) return new WinFspInfo(false, installDir, null, null);
        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(dll).FileVersion; } catch { }
        return new WinFspInfo(true, installDir, dll, version);
    }
}

/// <summary>Reads the WinINET per-user proxy configuration (Go programs do not read it themselves).</summary>
public sealed class SystemProxyReader : Core.Services.ISystemProxyReader
{
    public Core.Services.SystemProxyInfo? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null) return null;
        if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0) return null;
        if (key.GetValue("ProxyServer") is not string server || string.IsNullOrWhiteSpace(server)) return null;
        // "host:port" or "http=host:port;https=host:port"
        var entry = server;
        if (server.Contains('='))
        {
            var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToList();
            entry = parts.FirstOrDefault(p => p[0].Equals("https", StringComparison.OrdinalIgnoreCase))?[1]
                    ?? parts.FirstOrDefault(p => p[0].Equals("http", StringComparison.OrdinalIgnoreCase))?[1]
                    ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var hostPort = entry.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        var idx = hostPort.LastIndexOf(':');
        var host = idx > 0 ? hostPort[..idx] : hostPort;
        var port = idx > 0 && int.TryParse(hostPort[(idx + 1)..], out var p) ? p : 8080;
        var bypass = key.GetValue("ProxyOverride") is string ov
            ? ov.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0 && s != "<local>").ToList()
            : new List<string>();
        return new Core.Services.SystemProxyInfo(host, port, bypass);
    }
}
