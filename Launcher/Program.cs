using Library;
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Launcher
{
    static class Program
    {
        public const string PatcherFileName = @".\Patcher.exe";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            ConfigReader.Load(Assembly.GetAssembly(typeof(Config)));

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetCompatibleTextRenderingDefault(false);

            Application.Run(new LMain());

            ConfigReader.Save(typeof(Config).Assembly);
        }
    }
}
