using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mir3.Web.Services;

internal sealed class SafeUnixFileDescriptor : SafeHandleMinusOneIsInvalid
{
    internal SafeUnixFileDescriptor(int descriptor)
        : base(ownsHandle: true)
    {
        SetHandle((IntPtr)descriptor);
    }

    internal int Descriptor => handle.ToInt32();

    protected override bool ReleaseHandle()
    {
        // close(2) can report EINTR after the descriptor has already been closed.
        // Never retry: a retry could close an unrelated descriptor that reused it.
        _ = close(handle.ToInt32());
        return true;
    }

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int descriptor);
}
