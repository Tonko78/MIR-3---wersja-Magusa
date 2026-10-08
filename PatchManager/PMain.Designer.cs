namespace PatchManager
{
    partial class PMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.labelControl1 = new DevExpress.XtraEditors.LabelControl();
            this.CleanClientButtonEdit = new DevExpress.XtraEditors.ButtonEdit();
            this.DLookAndFeel = new DevExpress.LookAndFeel.DefaultLookAndFeel(this.components);
            this.labelControl2 = new DevExpress.XtraEditors.LabelControl();
            this.PublishDirectoryButtonEdit = new DevExpress.XtraEditors.ButtonEdit();
            this.UploadPatchButton = new DevExpress.XtraEditors.SimpleButton();
            this.labelControl6 = new DevExpress.XtraEditors.LabelControl();
            this.StatusLabel = new DevExpress.XtraEditors.LabelControl();
            this.labelControl8 = new DevExpress.XtraEditors.LabelControl();
            this.UploadSizeLabel = new DevExpress.XtraEditors.LabelControl();
            this.TotalProgressBar = new DevExpress.XtraEditors.ProgressBarControl();
            this.UploadSpeedLabel = new DevExpress.XtraEditors.LabelControl();
            this.labelControl10 = new DevExpress.XtraEditors.LabelControl();
            this.FolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            ((System.ComponentModel.ISupportInitialize)(this.CleanClientButtonEdit.Properties)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.PublishDirectoryButtonEdit.Properties)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.TotalProgressBar.Properties)).BeginInit();
            this.SuspendLayout();
            //
            // labelControl1
            //
            this.labelControl1.Location = new System.Drawing.Point(12, 15);
            this.labelControl1.Name = "labelControl1";
            this.labelControl1.Size = new System.Drawing.Size(61, 13);
            this.labelControl1.TabIndex = 0;
            this.labelControl1.Text = "Clean Client:";
            //
            // CleanClientButtonEdit
            //
            this.CleanClientButtonEdit.Location = new System.Drawing.Point(110, 12);
            this.CleanClientButtonEdit.Name = "CleanClientButtonEdit";
            this.CleanClientButtonEdit.Properties.Buttons.AddRange(new DevExpress.XtraEditors.Controls.EditorButton[] {
            new DevExpress.XtraEditors.Controls.EditorButton()});
            this.CleanClientButtonEdit.Size = new System.Drawing.Size(202, 20);
            this.CleanClientButtonEdit.TabIndex = 1;
            this.CleanClientButtonEdit.EditValueChanged += new System.EventHandler(this.CleanClientButtonEdit_EditValueChanged);
            //
            // DLookAndFeel
            //
            this.DLookAndFeel.LookAndFeel.SkinName = "Blue";
            //
            // labelControl2
            //
            this.labelControl2.Location = new System.Drawing.Point(12, 41);
            this.labelControl2.Name = "labelControl2";
            this.labelControl2.Size = new System.Drawing.Size(88, 13);
            this.labelControl2.TabIndex = 2;
            this.labelControl2.Text = "Publish Directory:";
            //
            // PublishDirectoryButtonEdit
            //
            this.PublishDirectoryButtonEdit.Location = new System.Drawing.Point(110, 38);
            this.PublishDirectoryButtonEdit.Name = "PublishDirectoryButtonEdit";
            this.PublishDirectoryButtonEdit.Properties.Buttons.AddRange(new DevExpress.XtraEditors.Controls.EditorButton[] {
            new DevExpress.XtraEditors.Controls.EditorButton()});
            this.PublishDirectoryButtonEdit.Size = new System.Drawing.Size(202, 20);
            this.PublishDirectoryButtonEdit.TabIndex = 8;
            this.PublishDirectoryButtonEdit.EditValueChanged += new System.EventHandler(this.PublishDirectoryButtonEdit_EditValueChanged);
            //
            // UploadPatchButton
            //
            this.UploadPatchButton.Location = new System.Drawing.Point(110, 64);
            this.UploadPatchButton.Name = "UploadPatchButton";
            this.UploadPatchButton.Size = new System.Drawing.Size(202, 23);
            this.UploadPatchButton.TabIndex = 10;
            this.UploadPatchButton.Text = "Create Patch";
            this.UploadPatchButton.Click += new System.EventHandler(this.UploadPatchButton_Click);
            //
            // labelControl6
            //
            this.labelControl6.Location = new System.Drawing.Point(14, 105);
            this.labelControl6.Name = "labelControl6";
            this.labelControl6.Size = new System.Drawing.Size(35, 13);
            this.labelControl6.TabIndex = 11;
            this.labelControl6.Text = "Status:";
            //
            // StatusLabel
            //
            this.StatusLabel.Location = new System.Drawing.Point(55, 105);
            this.StatusLabel.Name = "StatusLabel";
            this.StatusLabel.Size = new System.Drawing.Size(41, 13);
            this.StatusLabel.TabIndex = 12;
            this.StatusLabel.Text = "<None>";
            //
            // labelControl8
            //
            this.labelControl8.Location = new System.Drawing.Point(12, 124);
            this.labelControl8.Name = "labelControl8";
            this.labelControl8.Size = new System.Drawing.Size(37, 13);
            this.labelControl8.TabIndex = 13;
            this.labelControl8.Text = "Upload:";
            //
            // UploadSizeLabel
            //
            this.UploadSizeLabel.Location = new System.Drawing.Point(55, 124);
            this.UploadSizeLabel.Name = "UploadSizeLabel";
            this.UploadSizeLabel.Size = new System.Drawing.Size(41, 13);
            this.UploadSizeLabel.TabIndex = 14;
            this.UploadSizeLabel.Text = "<None>";
            //
            // TotalProgressBar
            //
            this.TotalProgressBar.Location = new System.Drawing.Point(12, 143);
            this.TotalProgressBar.Name = "TotalProgressBar";
            this.TotalProgressBar.Size = new System.Drawing.Size(300, 18);
            this.TotalProgressBar.TabIndex = 15;
            //
            // UploadSpeedLabel
            //
            this.UploadSpeedLabel.Location = new System.Drawing.Point(262, 124);
            this.UploadSpeedLabel.Name = "UploadSpeedLabel";
            this.UploadSpeedLabel.Size = new System.Drawing.Size(41, 13);
            this.UploadSpeedLabel.TabIndex = 17;
            this.UploadSpeedLabel.Text = "<None>";
            //
            // labelControl10
            //
            this.labelControl10.Location = new System.Drawing.Point(222, 124);
            this.labelControl10.Name = "labelControl10";
            this.labelControl10.Size = new System.Drawing.Size(34, 13);
            this.labelControl10.TabIndex = 16;
            this.labelControl10.Text = "Speed:";
            //
            // PMain
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(325, 171);
            this.Controls.Add(this.UploadSpeedLabel);
            this.Controls.Add(this.labelControl10);
            this.Controls.Add(this.TotalProgressBar);
            this.Controls.Add(this.UploadSizeLabel);
            this.Controls.Add(this.labelControl8);
            this.Controls.Add(this.StatusLabel);
            this.Controls.Add(this.labelControl6);
            this.Controls.Add(this.UploadPatchButton);
            this.Controls.Add(this.PublishDirectoryButtonEdit);
            this.Controls.Add(this.labelControl2);
            this.Controls.Add(this.CleanClientButtonEdit);
            this.Controls.Add(this.labelControl1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Name = "PMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Patch Manager";
            this.TopMost = true;
            this.Load += new System.EventHandler(this.PMain_Load);
            ((System.ComponentModel.ISupportInitialize)(this.CleanClientButtonEdit.Properties)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.PublishDirectoryButtonEdit.Properties)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.TotalProgressBar.Properties)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private DevExpress.XtraEditors.LabelControl labelControl1;
        private DevExpress.XtraEditors.ButtonEdit CleanClientButtonEdit;
        private DevExpress.LookAndFeel.DefaultLookAndFeel DLookAndFeel;
        private DevExpress.XtraEditors.LabelControl labelControl2;
        private DevExpress.XtraEditors.ButtonEdit PublishDirectoryButtonEdit;
        private DevExpress.XtraEditors.SimpleButton UploadPatchButton;
        private DevExpress.XtraEditors.LabelControl labelControl6;
        private DevExpress.XtraEditors.LabelControl StatusLabel;
        private DevExpress.XtraEditors.LabelControl labelControl8;
        private DevExpress.XtraEditors.LabelControl UploadSizeLabel;
        private DevExpress.XtraEditors.ProgressBarControl TotalProgressBar;
        private DevExpress.XtraEditors.LabelControl UploadSpeedLabel;
        private DevExpress.XtraEditors.LabelControl labelControl10;
        private System.Windows.Forms.FolderBrowserDialog FolderDialog;
    }
}
