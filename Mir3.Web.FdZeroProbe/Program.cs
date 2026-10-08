using System.Runtime.InteropServices;
using System.Text.Json;
using AccountPortal.Contracts;
using Microsoft.Extensions.Options;
using Mir3.Web.Options;
using Mir3.Web.Services;

internal static class Program
{
    private const int O_RDONLY = 0;
    private const int F_GETFD = 1;
    private const int EBADF = 9;

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 2;
        if (args is ["verification-lock-hold", var connectionString, var lockDirectory, var registrationText])
            return await HoldVerificationLockAsync(connectionString, lockDirectory, registrationText);

        var root = Path.Combine(Path.GetTempPath(), $"mir3-fd-zero-{Guid.NewGuid():N}");
        try
        {
            EnsureStandardInputDescriptorIsOpen();

            var options = Options.Create(new QueueOptions
            {
                RootPath = root,
                ResultPollSeconds = 2,
                HmacKeyBase64 = Convert.ToBase64String(new byte[32])
            });
            var resultDescriptor = -1;
            var operations = new AccountQueueClientOperations
            {
                BeforeResultOpen = () =>
                {
                    if (close(0) != 0)
                    {
                        throw new InvalidOperationException("Could not close standard input in the fd-zero probe.");
                    }
                },
                AfterResultOpenDescriptor = descriptor => resultDescriptor = descriptor
            };
            var client = new AccountQueueClient(options, TimeProvider.System, operations);
            var requestId = Guid.NewGuid();
            var resultPath = Path.Combine(root, "results", $"{requestId:D}.json");
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(new AccountCommandResult(
                    requestId,
                    AccountCommandStatus.Success,
                    "created",
                    "player@example.test",
                    DateTimeOffset.UtcNow)));

            var read = await client.ReadResultAsync(
                requestId,
                "player@example.test",
                CancellationToken.None);
            var descriptorClosed = fcntl(0, F_GETFD) == -1 && Marshal.GetLastPInvokeError() == EBADF;

            var reusedDescriptor = open("/dev/null", O_RDONLY);
            var reused = reusedDescriptor == 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            reused = reused && fcntl(0, F_GETFD) >= 0;
            if (reusedDescriptor >= 0) _ = close(reusedDescriptor);

            Console.Write(
                $"descriptor={resultDescriptor};status={read.Status};closed={descriptorClosed};reused={reused}");
            return resultDescriptor == 0 &&
                   read.Status == AccountQueueReadStatus.Ready &&
                   descriptorClosed &&
                   reused
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            Console.Error.Write(exception.GetType().Name);
            return 1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> HoldVerificationLockAsync(
        string connectionString,
        string lockDirectory,
        string registrationText)
    {
        try
        {
            using var manager = new VerificationTransportLockManager(connectionString, lockDirectory);
            await using var transportLock = manager.TryAcquire(Guid.Parse(registrationText));
            if (transportLock is null) return 1;
            Console.WriteLine("locked");
            await Console.In.ReadLineAsync();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.Write(exception.GetType().Name);
            return 1;
        }
    }

    private static void EnsureStandardInputDescriptorIsOpen()
    {
        var descriptor = open("/dev/null", O_RDONLY);
        if (descriptor < 0)
        {
            throw new InvalidOperationException("Could not open /dev/null in the fd-zero probe.");
        }

        if (descriptor == 0) return;
        if (dup2(descriptor, 0) != 0)
        {
            _ = close(descriptor);
            throw new InvalidOperationException("Could not reserve descriptor zero in the fd-zero probe.");
        }

        _ = close(descriptor);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int dup2(int oldDescriptor, int newDescriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command);
}
