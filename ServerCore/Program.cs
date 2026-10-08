using Library;
using Server.AccountQueue;
using Server.Envir;
using System;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    class Program
    {
        static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            string stopPath = Path.Combine(AppContext.BaseDirectory, "STOP.SERVER");
            if (File.Exists(stopPath)) File.Delete(stopPath);
            var assembly = Assembly.GetAssembly(typeof(Config));
            ConfigReader.Load(assembly);
            Config.LoadVersion();

            var accountQueuePath = Environment.GetEnvironmentVariable("MIR3_ACCOUNT_QUEUE_PATH");
            var accountQueueKey = Environment.GetEnvironmentVariable("MIR3_ACCOUNT_QUEUE_HMAC_KEY");
            if (!string.IsNullOrWhiteSpace(accountQueuePath) &&
                !string.IsNullOrWhiteSpace(accountQueueKey))
            {
                AccountQueueRuntime.Configure(accountQueuePath, accountQueueKey);
            }
            else
            {
                AccountQueueRuntime.Disable();
            }

            SEnvir.ExternalSecondProcess = () =>
                AccountQueueRuntime.ProcessAvailable(new DateTimeOffset(SEnvir.Now));

            try
            {
                if (!string.IsNullOrEmpty(Config.EncryptionKey))
                    SEnvir.CryptoKey = Convert.FromBase64String(Config.EncryptionKey);
            }
            catch (Exception)
            {
                throw new ApplicationException($"Invalid format encryption key, expected a base64 with 32 bytes");
            }

            if (Config.EncryptionEnabled && SEnvir.CryptoKey == null)
                throw new ApplicationException($"Encryption is enabled but not specified key [System] => DatabaseKey");

            if (Config.EncryptionEnabled)
                Encryption.SetKey(SEnvir.CryptoKey);

            ServerDataInitializer.EnsureDefaultCurrencies();

            SEnvir.UseLogConsole = true;
            SEnvir.StartServer();

            Console.CancelKeyPress += Console_CancelKeyPress;

            // We check EnvirThread why when SEnvir is full stoped, set this to null...
            var inputTask = Task.Run(() => Console.ReadLine());
            while (SEnvir.EnvirThread != null)
            {
                string command = inputTask.IsCompleted ? inputTask.GetAwaiter().GetResult() : null;
                if (command != null) inputTask = Task.Run(() => Console.ReadLine());
                if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase) || File.Exists(stopPath))
                {
                    if (File.Exists(stopPath)) File.Delete(stopPath);
                    SEnvir.Started = false;
                }

                // systemd connects stdin to /dev/null by default. Avoid a hot
                // loop when no interactive console is attached.
                if (command == null)
                    Thread.Sleep(1000);
            }

            ConfigReader.Save(typeof(Config).Assembly);
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            SEnvir.Started = false;
        }
    }
}
