using System;

namespace Server.AccountQueue;

public interface IQueueDurability
{
    void SyncDirectory(string path);
}
