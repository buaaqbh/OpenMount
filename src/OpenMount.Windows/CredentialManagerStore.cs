using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using OpenMount.Core.Services;

namespace OpenMount.Windows;

/// <summary>Windows Credential Manager (generic credentials, per-user, DPAPI protected).</summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int MaxBlobBytes = 2560;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Get(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return string.Empty;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string target, string? userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > MaxBlobBytes) throw new ArgumentException("凭据过长（最多 2560 字节）");
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName ?? string.Empty);
        var commentPtr = Marshal.StringToCoTaskMemUni("OpenMount");
        var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(commentPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }
}

/// <summary>In-memory credential store for tests and headless scenarios.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    public string? Get(string target) { lock (_map) return _map.TryGetValue(target, out var v) ? v : null; }
    public void Set(string target, string? userName, string secret) { lock (_map) _map[target] = secret; }
    public void Delete(string target) { lock (_map) _map.Remove(target); }
}
