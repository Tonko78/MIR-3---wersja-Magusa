using Library;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Client.Envir
{
    public static class LoginDetailsStore
    {
        private const string ProtectedPrefix = "dpapi:";

        public static void SetEnabled(bool enabled)
        {
            Config.RememberDetails = enabled;
            if (!enabled)
            {
                Config.RememberedEMail = string.Empty;
                Config.RememberedPassword = string.Empty;
            }
            ConfigReader.Save(typeof(Config).Assembly);
        }

        public static void SaveSuccessfulLogin(string email, string password)
        {
            if (Config.RememberDetails)
            {
                Config.RememberedEMail = email;
                Config.RememberedPassword = Protect(password);
            }
            else
            {
                Config.RememberedEMail = string.Empty;
                Config.RememberedPassword = string.Empty;
            }
            ConfigReader.Save(typeof(Config).Assembly);
        }

        public static string RestorePassword()
        {
            if (!Config.RememberDetails || string.IsNullOrEmpty(Config.RememberedPassword)) return string.Empty;
            if (!Config.RememberedPassword.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                string legacy = Config.RememberedPassword;
                Config.RememberedPassword = Protect(legacy);
                ConfigReader.Save(typeof(Config).Assembly);
                return legacy;
            }
            try
            {
                byte[] encrypted = Convert.FromBase64String(Config.RememberedPassword.Substring(ProtectedPrefix.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                Config.RememberedPassword = string.Empty;
                ConfigReader.Save(typeof(Config).Assembly);
                return string.Empty;
            }
        }

        private static string Protect(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            return ProtectedPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        }
    }
}
