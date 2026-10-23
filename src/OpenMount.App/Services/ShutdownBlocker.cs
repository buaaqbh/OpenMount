using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenMount.App.Services;

/// <summary>ShutdownBlockReasonCreate/Destroy so Windows shows why OpenMount is holding up shutdown (FR-19).</summary>
public static class ShutdownBlocker
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    public static void Block(Window window, string reason)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        ShutdownBlockReasonCreate(handle, reason);
    }

    public static void Unblock(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) ShutdownBlockReasonDestroy(handle);
    }
}
