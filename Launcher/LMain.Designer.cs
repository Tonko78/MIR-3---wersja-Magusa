namespace Launcher
{
    partial class LMain
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Button StartGameButton;
        private System.Windows.Forms.Button RepairButton;
        private System.Windows.Forms.ProgressBar TotalProgressBar;
        private System.Windows.Forms.Label StatusLabel;
        private System.Windows.Forms.Label DownloadSizeLabel;
        private System.Windows.Forms.Label DownloadSpeedLabel;
        private System.Windows.Forms.LinkLabel PatchNotesHyperlinkControl;
        private System.Windows.Forms.PictureBox pictureBox1;

        protected override void Dispose(bool disposing)
        {
            if (disposing) components?.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            var resources = new System.ComponentModel.ComponentResourceManager(typeof(LMain));
            components = new System.ComponentModel.Container();
            StartGameButton = new System.Windows.Forms.Button();
            RepairButton = new System.Windows.Forms.Button();
            TotalProgressBar = new System.Windows.Forms.ProgressBar();
            StatusLabel = new System.Windows.Forms.Label();
            DownloadSizeLabel = new System.Windows.Forms.Label();
            DownloadSpeedLabel = new System.Windows.Forms.Label();
            PatchNotesHyperlinkControl = new System.Windows.Forms.LinkLabel();
            pictureBox1 = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            SuspendLayout();

            pictureBox1.Image = global::Launcher.Properties.Resources.PatchHeader;
            pictureBox1.Location = new System.Drawing.Point(12, 12);
            pictureBox1.Size = new System.Drawing.Size(700, 306);
            pictureBox1.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            pictureBox1.TabStop = false;

            StatusLabel.Location = new System.Drawing.Point(12, 328);
            StatusLabel.Size = new System.Drawing.Size(490, 20);
            StatusLabel.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            StatusLabel.Text = "Status: Checking for updates";
            DownloadSizeLabel.Location = new System.Drawing.Point(12, 352);
            DownloadSizeLabel.Size = new System.Drawing.Size(360, 20);
            DownloadSizeLabel.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left;
            DownloadSpeedLabel.Location = new System.Drawing.Point(390, 352);
            DownloadSpeedLabel.Size = new System.Drawing.Size(210, 20);
            DownloadSpeedLabel.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;

            PatchNotesHyperlinkControl.Location = new System.Drawing.Point(505, 328);
            PatchNotesHyperlinkControl.Size = new System.Drawing.Size(105, 20);
            PatchNotesHyperlinkControl.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            PatchNotesHyperlinkControl.Text = "Portal Mir3";
            PatchNotesHyperlinkControl.LinkClicked += PatchNotesHyperlinkControl_LinkClicked;

            RepairButton.Location = new System.Drawing.Point(612, 325);
            RepairButton.Size = new System.Drawing.Size(100, 24);
            RepairButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            RepairButton.Text = "Repair";
            RepairButton.Click += RepairButton_Click;
            StartGameButton.Location = new System.Drawing.Point(612, 353);
            StartGameButton.Size = new System.Drawing.Size(100, 48);
            StartGameButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            StartGameButton.Text = "Start Game";
            StartGameButton.Enabled = false;
            StartGameButton.Click += StartGameButton_Click;

            TotalProgressBar.Location = new System.Drawing.Point(12, 382);
            TotalProgressBar.Size = new System.Drawing.Size(594, 18);
            TotalProgressBar.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;

            ClientSize = new System.Drawing.Size(724, 412);
            Controls.Add(pictureBox1);
            Controls.Add(StatusLabel);
            Controls.Add(DownloadSizeLabel);
            Controls.Add(DownloadSpeedLabel);
            Controls.Add(PatchNotesHyperlinkControl);
            Controls.Add(RepairButton);
            Controls.Add(StartGameButton);
            Controls.Add(TotalProgressBar);
            Icon = (System.Drawing.Icon)resources.GetObject("$this.Icon");
            MinimumSize = new System.Drawing.Size(600, 420);
            Name = "LMain";
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            Text = "Mir3 Zircon Launcher";
            Load += LMain_Load;
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ResumeLayout(false);
        }
    }
}
