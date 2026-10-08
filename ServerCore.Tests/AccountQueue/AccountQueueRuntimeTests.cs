using Server.AccountQueue;

namespace ServerCore.Tests.AccountQueue;

public sealed class AccountQueueRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zircon-account-queue-runtime-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Runtime_IsDisabledUntilConfigured()
    {
        AccountQueueRuntime.Disable();

        AccountQueueRuntime.ProcessAvailable(DateTimeOffset.UtcNow);

        Assert.False(AccountQueueRuntime.IsConfigured);
    }

    [Fact]
    public void Runtime_ConfigureEnablesSynchronousProcessorAndCreatesQueue()
    {
        var key = Convert.ToBase64String(new byte[32]);

        try
        {
            AccountQueueRuntime.Configure(_root, key);
            AccountQueueRuntime.ProcessAvailable(DateTimeOffset.UtcNow);

            Assert.True(AccountQueueRuntime.IsConfigured);
            Assert.True(Directory.Exists(Path.Combine(_root, "incoming")));
            Assert.True(Directory.Exists(Path.Combine(_root, "uncertain")));
        }
        finally
        {
            AccountQueueRuntime.Disable();
        }
    }

    public void Dispose()
    {
        AccountQueueRuntime.Disable();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
