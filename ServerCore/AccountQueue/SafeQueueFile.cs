#nullable enable

using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Server.AccountQueue;

internal static class SafeQueueFile
{
    internal const long MaximumRequestBytes = 64 * 1024;

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int ELOOP = 40;

    public static byte[] ReadSnapshot(
        string path,
        Action? afterInitialLengthObserved = null,
        Action<int>? bytesReadObserver = null)
    {
        using var handle = OperatingSystem.IsLinux()
            ? OpenLinuxHandle(path)
            : OpenPortableHandle(path);

        long initialLength;
        try
        {
            initialLength = RandomAccess.GetLength(handle);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new QueueRequestException("unsafe-request-file", exception);
        }

        if (initialLength > MaximumRequestBytes)
        {
            throw new QueueRequestException("request-too-large");
        }

        afterInitialLengthObserved?.Invoke();

        var snapshot = new byte[checked((int)MaximumRequestBytes + 1)];
        var totalRead = 0;
        try
        {
            while (totalRead < snapshot.Length)
            {
                var count = RandomAccess.Read(handle, snapshot.AsSpan(totalRead), totalRead);
                bytesReadObserver?.Invoke(count);
                if (count == 0) break;

                totalRead += count;
            }
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new QueueRequestException("unsafe-request-file", exception);
        }

        if (totalRead > MaximumRequestBytes)
        {
            throw new QueueRequestException("request-too-large");
        }

        return snapshot.AsSpan(0, totalRead).ToArray();
    }

    private static SafeFileHandle OpenLinuxHandle(string path)
    {
        var descriptor = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ELOOP)
        {
            throw new QueueRequestException("unsafe-request-file");
        }

        throw new IOException(
            "The claimed account queue request could not be opened safely.",
            new Win32Exception(error));
    }

    private static SafeFileHandle OpenPortableHandle(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new QueueRequestException("unsafe-request-file");
        }

        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.SequentialScan);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);
}

internal sealed class QueueRequestException : IOException
{
    public QueueRequestException(string code)
        : base(code)
    {
        Code = code;
    }

    public QueueRequestException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
