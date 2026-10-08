using Launcher.Core;
using Library;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Launcher
{
    public partial class LMain : Form
    {
        public const string PListFileName = "PList.Bin";
        public const string ClientPath = ".\\";
        public const string ClientFileName = "Zircon.exe";

        public DateTime LastSpeedCheck;
        public long TotalDownload, TotalProgress, CurrentProgress, LastDownloadProcess;
        public bool NeedUpdate;

        public static bool HasError;

        private static readonly HttpClient Client = CreateHttpClient();

        public LMain()
        {
            InitializeComponent();

        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // Reject redirects rather than following an HTTPS-to-HTTP downgrade.
                AllowAutoRedirect = false,
                // The payloads are already .gz compressed; asking the server for
                // gzip transfer encoding would double-compress them because this
                // client stores the raw response bytes for GZipStream.
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(100),
            };
        }

        private void LMain_Load(object sender, EventArgs e)
        {
            CheckPatch(false);
        }

        private void PatchNotesHyperlinkControl_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://github.com/Tonko78/MIR-3---wersja-Magusa") { UseShellExecute = true });
        }

        private void RepairButton_Click(object sender, EventArgs e)
        {
            CheckPatch(true);
        }

        private async void CheckPatch(bool repair)
        {
            try
            {
                await CheckPatchCore(repair);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Update failed: " + ex.Message;
                RepairButton.Enabled = true;
                StartGameButton.Enabled = false;
            }
        }

        private async Task CheckPatchCore(bool repair)
        {
            HasError = false;
            RepairButton.Enabled = false;
            StartGameButton.Enabled = false;
            TotalDownload = 0;
            TotalProgress = 0;
            CurrentProgress = 0;
            TotalProgressBar.Value = 0;
            LastSpeedCheck = Time.Now;
            NeedUpdate = false;

            Progress<string> progress = new Progress<string>(s => StatusLabel.Text = s);

            List<PatchManifestEntry> liveVersion = await GetPatchInformation(progress);

            if (liveVersion == null)
            {
                DownloadSizeLabel.Text = "Patch information unavailable. Retry later.";
                RepairButton.Enabled = true;
                StartGameButton.Enabled = false;
                return;
            }

            List<PatchManifestEntry> currentVersion = repair ? null : await LoadVersion(progress);
            List<PatchManifestEntry> patch = await CalculatePatch(liveVersion, currentVersion, progress);

            StatusLabel.Text = "Downloading";
            CreateSizeLabel();

            Task task = DownloadPatch(patch, progress, new Progress<int>(percent =>
            {
                // Update progress bar or label with the percentage downloaded
                CreateSizeLabel();
            }));

            await task;

            CreateSizeLabel();

            SaveVersion(liveVersion);

            bool incomplete = liveVersion.Any(entry => entry.CheckSum.Length != 16);
            StatusLabel.Text = incomplete ? "Update incomplete. Retry or repair." : "Complete";
            DownloadSizeLabel.Text = incomplete ? "Some files could not be updated." : "Complete.";
            DownloadSpeedLabel.Text = incomplete ? string.Empty : "Complete.";

            if (Directory.Exists(ClientPath + "Patch\\"))
                Directory.Delete(ClientPath + "Patch\\", true);

            if (NeedUpdate)
            {
                if (!File.Exists(Program.PatcherFileName))
                    throw new FileNotFoundException("Patcher.exe is missing from the game directory.");
                Process.Start(Program.PatcherFileName,
                    $"\"{Application.ExecutablePath}.tmp\" \"{Application.ExecutablePath}\" {Environment.ProcessId}");
                Environment.Exit(0);
            }

            RepairButton.Enabled = true;
            StartGameButton.Enabled = !incomplete;
        }
        private void CreateSizeLabel()
        {
            const decimal KB = 1024;
            const decimal MB = KB * 1024;
            const decimal GB = MB * 1024;

            long progress = TotalProgress + CurrentProgress;

            StringBuilder text = new StringBuilder();

            if (progress > GB)
                text.Append($"{progress / GB:#,##0.0}GB");
            else if (progress > MB)
                text.Append($"{progress / MB:#,##0.0}MB");
            else if (progress > KB)
                text.Append($"{progress / KB:#,##0}KB");
            else
                text.Append($"{progress:#,##0}B");

            if (TotalDownload > GB)
                text.Append($" / {TotalDownload / GB:#,##0.0}GB");
            else if (TotalDownload > MB)
                text.Append($" / {TotalDownload / MB:#,##0.0}MB");
            else if (TotalDownload > KB)
                text.Append($" / {TotalDownload / KB:#,##0}KB");
            else
                text.Append($" / {TotalDownload:#,##0}B");

            DownloadSizeLabel.Text = text.ToString();

            if (TotalDownload > 0)
                TotalProgressBar.Value = Math.Max(0, Math.Min(100, (int)(progress * 100 / TotalDownload)));

            long elapsedTicks = Time.Now.Ticks - LastSpeedCheck.Ticks;
            long speed = elapsedTicks > 0
                ? (progress - LastDownloadProcess) * TimeSpan.TicksPerSecond / elapsedTicks
                : 0;
            LastDownloadProcess = progress;

            if (speed > GB)
                DownloadSpeedLabel.Text = $"{speed / GB:#,##0.0}GBps";
            else if (speed > MB)
                DownloadSpeedLabel.Text = $"{speed / MB:#,##0.0}MBps";
            else if (speed > KB)
                DownloadSpeedLabel.Text = $"{speed / KB:#,##0}KBps";
            else
                DownloadSpeedLabel.Text = $"{speed:#,##0}Bps";

            LastSpeedCheck = Time.Now;
        }

        private async Task<List<PatchManifestEntry>> LoadVersion(IProgress<string> progress)
        {
            List<PatchManifestEntry> list = null;

            try
            {
                if (File.Exists(ClientPath + "Version.bin"))
                {
                    using (MemoryStream mStream = new MemoryStream(await File.ReadAllBytesAsync(ClientPath + "Version.bin")))
                    using (BinaryReader reader = new BinaryReader(mStream))
                        list = PatchManifest.Read(reader);

                    progress.Report("Calculating Patch.");
                    return list;
                }

                progress.Report("Version Info is missing, Running Repairing");
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }

            return null;
        }
        private async Task<List<PatchManifestEntry>> GetPatchInformation(IProgress<string> progress)
        {
            try
            {
                progress.Report("Downloading Patch Information");

                PatchOrigin origin = PatchOrigin.Parse(Config.Host);

                // The cache buster keeps proxies from serving a stale manifest;
                // it is added as a query on the manifest request only, never on
                // the configured origin itself.
                Uri manifestUri = new Uri(origin.ResolveManifestUri().AbsoluteUri + "?nocache=" + Guid.NewGuid().ToString("N"));

                using (HttpResponseMessage response = await Client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                    using (BinaryReader reader = new BinaryReader(contentStream))
                    {
                        var entries = PatchManifest.Read(reader);
                        if (entries.Any(entry => entry.CheckSum.Length != 16))
                            throw new InvalidDataException("Patch manifest contains a non-MD5 checksum.");
                        return entries;
                    }
                }
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }

            return null;
        }
        private async Task<List<PatchManifestEntry>> CalculatePatch(IReadOnlyList<PatchManifestEntry> list, List<PatchManifestEntry> current, IProgress<string> progress)
        {
            List<PatchManifestEntry> patch = new List<PatchManifestEntry>();

            if (list == null) return patch;

            for (int i = 0; i < list.Count; i++)
            {
                progress.Report($"Files Checked: {i + 1} of {list.Count}");

                PatchManifestEntry file = list[i];
                string existingPath = PatchPaths.ResolveClientPath(ClientPath, file.FileName);
                // Existing personal settings and local user data belong to the player.
                // Download the reference copy only when installing into an empty folder.
                if (PatchPaths.IsLocalState(file.FileName) && File.Exists(existingPath)) continue;
                if (File.Exists(existingPath) && current != null && current.Any(x => x.FileName == file.FileName && PatchManifest.IsMatch(x.CheckSum, file.CheckSum))) continue;

                if (File.Exists(existingPath))
                {
                    byte[] CheckSum;
                    using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                    {
                        using (FileStream stream = File.OpenRead(existingPath))
                            CheckSum = await md5.ComputeHashAsync(stream);
                    }

                    if (PatchManifest.IsMatch(CheckSum, file.CheckSum))
                        continue;
                }

                patch.Add(file);
                TotalDownload += file.CompressedLength;
            }

            return patch;
        }

        private void SaveVersion(List<PatchManifestEntry> version)
        {
            string versionPath = ClientPath + "Version.bin";
            string temporaryPath = versionPath + ".tmp";

            // Write beside the target first so an interrupted save can never
            // leave a half-written Version.bin behind.
            using (FileStream fStream = File.Create(temporaryPath))
            using (BinaryWriter writer = new BinaryWriter(fStream))
                PatchManifest.Write(writer, version);

            if (File.Exists(versionPath))
                File.Replace(temporaryPath, versionPath, null);
            else
                File.Move(temporaryPath, versionPath);
        }

        private async Task DownloadPatch(List<PatchManifestEntry> patch, IProgress<string> progress, IProgress<int> downloadProgress)
        {
            List<Task> tasks = new List<Task>();

            foreach (PatchManifestEntry file in patch)
            {
                if (!await Download(file, downloadProgress)) continue;

                tasks.Add(Extract(file));
            }

            if (tasks.Count == 0) return;

            progress.Report("Downloaded, Extracting.");

            await Task.WhenAll(tasks);
        }


        private void StartGameButton_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(ClientPath + ClientFileName);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private async Task<bool> Download(PatchManifestEntry file, IProgress<int> progress)
        {
            string webFileName = PatchPaths.ToWebFileName(file.FileName);

            try
            {
                PatchOrigin origin = PatchOrigin.Parse(Config.Host);

                Uri fileUri = origin.ResolveFileUri(file.FileName);

                using (HttpResponseMessage response = await Client.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long declaredLength &&
                        declaredLength != file.CompressedLength)
                        throw new InvalidDataException("Patch payload length does not match the manifest.");

                    if (!Directory.Exists(ClientPath + "Patch\\"))
                        Directory.CreateDirectory(ClientPath + "Patch\\");

                    string patchFile = Path.Combine(ClientPath + "Patch\\", webFileName);

                    using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                    using (FileStream fileStream = new FileStream(patchFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        long totalBytes = response.Content.Headers.ContentLength ?? -1;
                        long totalDownloadedBytes = 0;
                        byte[] buffer = new byte[8192];
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            totalDownloadedBytes += bytesRead;
                            if (totalDownloadedBytes > file.CompressedLength)
                                throw new InvalidDataException("Patch payload exceeds the manifest length.");
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            if (totalBytes > 0)
                                progress.Report((int)(totalDownloadedBytes * 100 / totalBytes));
                        }
                        if (totalDownloadedBytes != file.CompressedLength)
                            throw new InvalidDataException("Patch payload is incomplete.");
                    }
                }

                CurrentProgress = 0;
                TotalProgress += file.CompressedLength;

                return true;
            }
            catch (Exception)
            {
                // Zeroing the check sum marks the entry as incomplete: the
                // mutated entry is persisted in Version.bin below, so the next
                // run re-downloads only the failed files.
                file.CheckSum = new byte[8];
            }

            return false;
        }

        private async Task Extract(PatchManifestEntry file)
        {
            string webFileName = PatchPaths.ToWebFileName(file.FileName);

            try
            {
                string toPath = PatchPaths.ResolveClientPath(ClientPath, file.FileName);

                bool selfUpdate = string.Equals(Path.GetFullPath(Application.ExecutablePath),
                    toPath, StringComparison.OrdinalIgnoreCase);
                if (selfUpdate) toPath += ".tmp";

                using (FileStream source = File.OpenRead($"{ClientPath}Patch\\{webFileName}"))
                    await PatchPayload.ExtractVerifiedAsync(source, toPath, file.CheckSum);

                if (selfUpdate) NeedUpdate = true;
            }
            catch (UnauthorizedAccessException ex)
            {
                file.CheckSum = new byte[8];

                if (HasError) return;
                HasError = true;
                MessageBox.Show(ex.Message + "\n\nFile might be in use, please make sure the game is closed.", "File Error", MessageBoxButtons.OK);
            }
            catch (Exception)
            {
                file.CheckSum = new byte[8];
            }
        }
    }
}
