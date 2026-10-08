using Library;
using Server.DBModels;
using Server.Envir;
using System;
using System.Linq;
using G = Library.Network.GeneralPackets;

namespace Server.AccountQueue;

/// <summary>
/// Must only be called on the ServerCore environment thread; MirDB bindings and mutations are not thread-safe.
/// </summary>
public sealed class MirDbAccountDirectory : IAccountDirectory
{
    public bool Exists(string email) => Find(email) is not null;

    public void Create(string email, byte[] passwordHash)
    {
        var account = SEnvir.AccountInfoList.CreateNewObject();
        account.EMailAddress = email;
        account.Password = passwordHash;
        account.RealName = string.Empty;
        account.BirthDate = DateTime.MinValue;
        account.CreationIP = "WebPortal";
        account.CreationDate = SEnvir.Now;
        account.Activated = true;
        account.ActivationKey = null;
        account.WrongPasswordCount = 0;
    }

    public bool SetActivated(string email, bool activated)
    {
        var account = Find(email);
        if (account is null) return false;

        account.Activated = activated;
        return true;
    }

    public bool SetPassword(string email, byte[] passwordHash)
    {
        var account = Find(email);
        if (account is null) return false;

        account.Password = passwordHash;
        account.WrongPasswordCount = 0;
        return true;
    }

    public void Disconnect(string email)
    {
        var connection = Find(email)?.Connection;
        connection?.TrySendDisconnect(new G.Disconnect { Reason = DisconnectReason.ServerClosing });
    }

    public void Commit()
    {
        SEnvir.Session.Save(true);
    }

    private static AccountInfo Find(string email)
    {
        return SEnvir.AccountInfoList.Binding.FirstOrDefault(
            account => string.Equals(account.EMailAddress, email, StringComparison.OrdinalIgnoreCase));
    }
}
