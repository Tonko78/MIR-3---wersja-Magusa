using Launcher.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PatchManager
{
    public partial class PMain : DevExpress.XtraEditors.XtraForm
    {
        public const string PListFileName = "PList.Bin";

        public const string ClientFileName = "Zircon.exe";

        public const string TempDownloadDirectory = "In";

        public const string ReportDirectory = "Report";

        public long TotalUpload, TotalProgress, TotalProgressPercent;
        public long Speed;
        public bool Error;

        public PMain()
        {
            InitializeComponent();
        }

        #region Event Handlers
        private void PMain_Load(object sender, EventArgs e)
        {
            CleanClientButtonEdit.EditValue = Config.CleanClient;
            PublishDirectoryButtonEdit.EditValue = Config.PublishDirectory;
        }

        private void CleanClientButtonEdit_EditValueChanged(object sender, EventArgs e)
        {
            Config.CleanClient = (string)CleanClientButtonEdit.EditValue;
        }

        private void PublishDirectoryButtonEdit_EditValueChanged(object sender, EventArgs e)
        {
            Config.PublishDirectory = (string)PublishDirectoryButtonEdit.EditValue;
        }

        private void UploadPatchButton_Click(object sender, EventArgs e)
        {
            CreatePatch();
        }
        #endregion

        private void InterfaceLock(bool enabled)
        {
            CleanClientButtonEdit.Enabled = enabled;
            PublishDirectoryButtonEdit.Enabled = enabled;
            UploadPatchButton.Enabled = enabled;
        }


        private async void CreatePatch()
        {
            InterfaceLock(false);

            Progress<string> progress = new Progress<string>(s => StatusLabel.Text = s);

            try
            {
                List<PatchInformation> currentVersion = await Task.Run(() => CreateVersion(progress));
                List<PatchInformation> liveVersion = await Task.Run(() => GetPatchInformation(progress));

                List<PatchInformation> patch = await Task.Run(() => CalculatePatch(currentVersion, liveVersion, progress));

                Task publishTask = Task.Run(() => PublishFiles(patch, currentVersion, progress));

                while (!publishTask.IsCompleted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    CreateSizeLabel();
                }

                await publishTask;

                if (!Error)
                    CreateReport(patch);

                StatusLabel.Text = "Complete.";
                UploadSizeLabel.Text = "Complete.";
                UploadSpeedLabel.Text = "Complete.";
            }
            catch (Exception ex)
            {
                Error = true;
                StatusLabel.Text = ex.Message;
            }
            finally
            {
                if (Directory.Exists(".\\Patch\\"))
                    Directory.Delete(".\\Patch\\", true);

                if (Directory.Exists(TempDownloadDirectory))
                    Directory.Delete(TempDownloadDirectory, true);

                InterfaceLock(true);
            }
        }

        private void CreateSizeLabel()
        {
            const decimal KB = 1024;
            const decimal MB = KB * 1024;
            const decimal GB = MB * 1024;

            long progress = TotalProgress;

            StringBuilder text = new StringBuilder();

            if (progress > GB)
                text.Append($"{progress / GB:#,##0.0}GB");
            else if (progress > MB)
                text.Append($"{progress / MB:#,##0.0}MB");
            else if (progress > KB)
                text.Append($"{progress / KB:#,##0}KB");
            else
                text.Append($"{progress:#,##0}B");

            if (TotalUpload > GB)
                text.Append($" / {TotalUpload / GB:#,##0.0}GB");
            else if (TotalUpload > MB)
                text.Append($" / {TotalUpload / MB:#,##0.0}MB");
            else if (TotalUpload > KB)
                text.Append($" / {TotalUpload / KB:#,##0}KB");
            else
                text.Append($" / {TotalUpload:#,##0}B");

            UploadSizeLabel.Text = text.ToString();

            if (TotalUpload > 0)
                TotalProgressBar.EditValue = Math.Max(0, Math.Min(100, TotalProgressPercent));

            long speed = Speed;

            if (speed > GB)
                UploadSpeedLabel.Text = $"{speed / GB:#,##0.0}GBps";
            else if (speed > MB)
                UploadSpeedLabel.Text = $"{speed / MB:#,##0.0}MBps";
            else if (speed > KB)
                UploadSpeedLabel.Text = $"{speed / KB:#,##0}KBps";
            else
                UploadSpeedLabel.Text = $"{speed:#,##0}Bps";
        }


        private string GetPublishDirectory()
        {
            string path = Path.GetFullPath(Config.PublishDirectory);

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }

        private List<PatchInformation> CreateVersion(IProgress<string> progress)
        {
            try
            {
                string[] files = Directory.GetFiles(Config.CleanClient, "*.*", SearchOption.AllDirectories);

                PatchInformation[] list = new PatchInformation[files.Length];
                ParallelOptions po = new ParallelOptions { MaxDegreeOfParallelism = 8 };
                int count = 0;
                Parallel.For(0, files.Length, po, i =>
                {
                    list[i] = new PatchInformation(files[i]);
                    progress.Report($"Version Created: File {Interlocked.Increment(ref count)} of {files.Length}");
                });

                return list.ToList();
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
                Error = true;
            }

            return null;
        }

        private List<PatchInformation> GetPatchInformation(IProgress<string> progress)
        {
            try
            {
                string publishDirectory = GetPublishDirectory();
                string manifestPath = Path.Combine(publishDirectory, PListFileName);

                if (!File.Exists(manifestPath))
                {
                    progress.Report("Patch Information Not Found, first publish.");
                    return null;
                }

                progress.Report("Reading Patch Information");

                using BinaryReader reader = new BinaryReader(File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read));

                return PatchManifest.Read(reader)
                    .Select(entry => new PatchInformation
                    {
                        FileName = entry.FileName,
                        CompressedLength = entry.CompressedLength,
                        CheckSum = entry.CheckSum,
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
                Error = true;
            }

            return null;
        }

        private List<PatchInformation> CalculatePatch(List<PatchInformation> current, List<PatchInformation> live, IProgress<string> progress)
        {
            List<PatchInformation> patch = new List<PatchInformation>();

            if (current == null) return patch;

            int count = 0;

            Parallel.For(0, current.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                PatchInformation file = current[i];
                PatchInformation lFile = live?.FirstOrDefault(x => x.FileName == file.FileName);

                if (lFile != null && PatchManifest.IsMatch(lFile.CheckSum, file.CheckSum))
                {
                    file.CompressedLength = lFile.CompressedLength;
                    return;
                }

                if (!Directory.Exists(".\\Patch\\"))
                    Directory.CreateDirectory(".\\Patch\\");

                string webFileName = PatchPaths.ToWebFileName(file.FileName);

                file.UploadFileName = ".\\Patch\\" + webFileName;
                file.PatchFileName = Path.Combine(Directory.GetCurrentDirectory(), $"Patch\\{webFileName}");

                file.CompressedLength = Compress(Config.CleanClient + file.FileName, file.PatchFileName);
                Interlocked.Add(ref TotalUpload, file.CompressedLength);

                lock (patch)
                    patch.Add(file);

                progress.Report($"File Created: {Interlocked.Increment(ref count)} of {current.Count}");

            });

            return patch;
        }

        private void PublishFiles(List<PatchInformation> patch, List<PatchInformation> currentVersion, IProgress<string> progress)
        {
            string publishDirectory = GetPublishDirectory();
            string patchDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Patch");

            long publishedBytes = 0;
            int current = 0;

            // Payloads first; the manifest is switched last so a launcher can
            // never observe a manifest that points at half-copied payloads.
            foreach (PatchInformation file in patch)
            {
                string destination = Path.Combine(publishDirectory, Path.GetFileName(file.PatchFileName));
                File.Copy(file.PatchFileName, destination, overwrite: true);
                publishedBytes += file.CompressedLength;
                TotalProgress = publishedBytes;
                TotalProgressPercent = TotalUpload > 0 ? publishedBytes * 100 / TotalUpload : 100;
                Speed = 0;
                progress.Report($"Files Published: {Interlocked.Increment(ref current)} of {patch.Count}");
            }

            SaveVersion(currentVersion, publishDirectory, progress);
        }

        private void SaveVersion(List<PatchInformation> current, string publishDirectory, IProgress<string> progress)
        {
            byte[] bytes;

            using (MemoryStream mStream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(mStream, Encoding.UTF8, leaveOpen: true))
                    PatchManifest.Write(writer, current);

                bytes = mStream.ToArray();
            }

            string manifestPath = Path.Combine(publishDirectory, PListFileName);
            string temporaryPath = manifestPath + ".tmp";

            // Switch the manifest atomically so launchers never read a
            // partially written PList.Bin.
            File.WriteAllBytes(temporaryPath, bytes);

            if (File.Exists(manifestPath))
                File.Replace(temporaryPath, manifestPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, manifestPath);
        }

        private static void CreateReport(IEnumerable<PatchInformation> patch)
        {
            DateTime created = DateTime.Now;

            Directory.CreateDirectory(ReportDirectory);

            List<string> updatedFiles = patch
                .Select(info => info.FileName)
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> report = new List<string>
            {
                $"Patch update report - {created:yyyy-MM-dd HH:mm:ss}",
                $"Files updated: {updatedFiles.Count}",
                string.Empty
            };

            report.AddRange(updatedFiles);

            string reportPath = Path.Combine(ReportDirectory, $"PatchReport_{created:yyyy-MM-dd_HH-mm-ss}.log");
            File.WriteAllLines(reportPath, report, Encoding.UTF8);
        }


        #region Helpers
        private static long Compress(string sourceFile, string destFile)
        {
            var dir = Path.GetDirectoryName(destFile);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (FileStream tofile = File.Create(destFile))
            using (FileStream fromfile = File.OpenRead(sourceFile))
            {
                using (GZipStream gStream = new GZipStream(tofile, CompressionMode.Compress, true))
                    fromfile.CopyTo(gStream);

                return tofile.Length;
            }
        }
        #endregion
    }
}
