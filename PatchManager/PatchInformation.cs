using Launcher.Core;
using System.IO;
using System.Security.Cryptography;

namespace PatchManager
{
    public sealed class PatchInformation : PatchManifestEntry
    {
        public string UploadFileName { get; set; }
        public string PatchFileName { get; set; }

        public PatchInformation()//FileInfo
        {
        }

        public PatchInformation(string fileName)
        {
            FileName = fileName.Remove(0, Config.CleanClient.Length);

            using (MD5 md5 = MD5.Create())
            {
                using (FileStream stream = File.OpenRead(fileName))
                    CheckSum = md5.ComputeHash(stream);
            }
        }
    }
}
