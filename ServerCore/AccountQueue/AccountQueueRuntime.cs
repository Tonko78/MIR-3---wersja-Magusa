using Server.Envir;
using System;

namespace Server.AccountQueue;

public static class AccountQueueRuntime
{
    private static AccountCommandProcessor _processor;

    public static bool IsConfigured => _processor != null;

    public static void Configure(string root, string keyBase64)
    {
        _processor = new AccountCommandProcessor(
            root,
            keyBase64,
            new AccountCommandExecutor(new MirDbAccountDirectory()),
            message => SEnvir.Log(message));
    }

    public static void Disable()
    {
        _processor = null;
    }

    public static void ProcessAvailable(DateTimeOffset now)
    {
        _processor?.ProcessAvailable(now);
    }
}
