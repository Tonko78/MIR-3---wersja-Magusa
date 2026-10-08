using System.Runtime.InteropServices;
using Server.AccountQueue;

namespace ServerCore.Tests.AccountQueue;

public sealed class SafeQueueFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zircon-safe-queue-file-tests",
        Guid.NewGuid().ToString("N"));

    public SafeQueueFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ReadSnapshot_WhenFileGrowsAfterLengthObservationReadsOnlyLimitPlusOneAndRejects()
    {
        var path = Path.Combine(_root, "request.json");
        File.WriteAllBytes(path, [(byte)'{']);
        var bytesRead = 0;

        var exception = Assert.Throws<QueueRequestException>(() => SafeQueueFile.ReadSnapshot(
            path,
            afterInitialLengthObserved: () =>
            {
                using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                append.Write(new byte[checked((int)SafeQueueFile.MaximumRequestBytes + 1)]);
                append.Flush(flushToDisk: true);
            },
            bytesReadObserver: count => bytesRead += count));

        Assert.Equal("request-too-large", exception.Code);
        Assert.Equal(SafeQueueFile.MaximumRequestBytes + 1, bytesRead);
    }

    [Fact]
    public async Task ReadSnapshot_NamedPipeRejectsPromptlyWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux()) return;

        var path = Path.Combine(_root, "request.json");
        Assert.Equal(0, mkfifo(path, Convert.ToUInt32("600", 8)));

        var read = Task.Run(() => Record.Exception(() => SafeQueueFile.ReadSnapshot(path)));

        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(read, completed);
        var exception = Assert.IsType<QueueRequestException>(await read);
        Assert.Equal("unsafe-request-file", exception.Code);
    }

    [Fact]
    public void ReadSnapshot_DirectoryRejectsAsUnsafeRequestFile()
    {
        var path = Path.Combine(_root, "request.json");
        Directory.CreateDirectory(path);

        var exception = Assert.Throws<QueueRequestException>(() => SafeQueueFile.ReadSnapshot(path));

        Assert.Equal("unsafe-request-file", exception.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
