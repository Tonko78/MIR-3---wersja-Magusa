using System;
using System.Diagnostics;
using System.IO;

namespace Patcher
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 3 || !int.TryParse(args[2], out int launcherPid) || launcherPid <= 0)
                return 2;

            string source = Path.GetFullPath(args[0]);
            string target = Path.GetFullPath(args[1]);
            string directory = Path.GetDirectoryName(target);
            if (!string.Equals(source, target + ".tmp", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(directory, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return 2;

            try
            {
                // The launcher exits immediately after starting this companion.
                // Do not replace a still-running executable or wait forever.
                try
                {
                    using var launcher = Process.GetProcessById(launcherPid);
                    if (!launcher.WaitForExit(TimeSpan.FromSeconds(60))) return 3;
                }
                catch (ArgumentException) { /* Already exited. */ }

                if (!File.Exists(source)) return 4;
                File.Move(source, target, overwrite: true);
                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = directory,
                });
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(directory, "Patcher-error.log"), ex.ToString()); }
                catch { /* Preserve the exit code if the directory is read-only. */ }
                return 1;
            }
        }
    }
}
