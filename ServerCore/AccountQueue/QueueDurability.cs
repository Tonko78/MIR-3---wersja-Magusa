using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Server.AccountQueue;

public sealed class QueueDurability : IQueueDurability
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int O_DIRECTORY = 0x10000;

    public void SyncDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsLinux()) return;

        var descriptor = open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (descriptor < 0)
        {
            throw CreateIOException("open", path);
        }

        try
        {
            if (fsync(descriptor) != 0)
            {
                throw CreateIOException("fsync", path);
            }
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    private static IOException CreateIOException(string operation, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(
            $"Directory durability operation {operation} failed for the queue path.",
            new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
