using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using VhdStudio.Vsux;

namespace VhdAttach {
    internal sealed class VsuxToolsForm : Form {
        private readonly string FilePath;
        private readonly FlowLayoutPanel Page;
        private readonly Label Status;
        private readonly Button ConvertButton;
        private readonly Button PortableButton;
        private readonly Button BackupButton;
        private bool IsBusy;

        public string ResultFileName { get; private set; }

        public VsuxToolsForm(string containerPath, string initialAction = null) {
            this.FilePath = Path.GetFullPath(containerPath);
            this.Font = SystemFonts.MessageBoxFont;
            this.Icon = Ui.AppIcon;
            this.Text = "Soft protection - " + Path.GetFileName(this.FilePath) + " - VHD Studio";
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(this.Font.Height * 44, this.Font.Height * 35);
            this.Page = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(this.Font.Height), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            this.Status = this.AddText("Reading the drive's protection information…", Ui.AccentDark);
            this.AddText("VSUX leaves disk data unencrypted. Specialists and standalone recovery can bypass its password.", Ui.Muted);
            this.ConvertButton = this.AddButton("ConvertVsuxButton", "&Convert back to VHDX…", () => this.Run("Convert"));
            this.AddText("Remove soft protection and rename this detached image to .vhdx. Its files and partitions stay in place.");
            this.PortableButton = this.AddButton("MakeSelfContainedButton", "&Make self-contained…", () => this.Run("Portable"));
            this.AddText("Upgrade an older VSUX so it works without its JSON companion. The old JSON file is kept.");
            this.BackupButton = this.AddButton("ExportRecoveryButton", "&Export recovery information…", () => this.Run("Backup"));
            this.AddText("Save optional JSON diagnostics. This is not a full backup, and standalone recovery works without it.");
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(this.Font.Height / 2) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
            footer.Controls.Add(close);
            this.Controls.Add(this.Page);
            this.Controls.Add(footer);
            this.Controls.Add(new Ui.Banner("Soft protection", this.FilePath) { Font = this.Font });
            this.CancelButton = close;
            this.Load += (s, e) => this.RefreshState();
            this.Activated += (s, e) => { if (!this.IsBusy) { this.RefreshState(); } };
            this.Shown += (s, e) => {
                if (initialAction == "Convert") { this.ConvertButton.PerformClick(); }
                else if (initialAction == "Portable") { this.PortableButton.PerformClick(); }
                else if (initialAction == "Backup") { this.BackupButton.PerformClick(); }
            };
            this.FormClosing += (s, e) => { if (this.IsBusy) { e.Cancel = true; } };
        }

        private Label AddText(string text, Color? color = null) {
            var label = new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(this.Font.Height * 40, 0), ForeColor = color ?? SystemColors.ControlText, Margin = new Padding(0, 0, 0, this.Font.Height / 2) };
            this.Page.Controls.Add(label);
            return label;
        }

        private Button AddButton(string name, string text, Action action) {
            var button = new Button { Name = name, Text = text, AccessibleName = text.Replace("&", ""), AutoSize = true, Padding = new Padding(this.Font.Height / 2, 3, this.Font.Height / 2, 3), Margin = new Padding(0, this.Font.Height / 2, 0, this.Font.Height / 2) };
            button.Click += (s, e) => action();
            this.Page.Controls.Add(button);
            return button;
        }

        private void RefreshState() {
            this.ConvertButton.Enabled = this.PortableButton.Enabled = this.BackupButton.Enabled = false;
            try {
                var info = VsuxOperations.GetInfo(this.FilePath);
                var hasPassword = info.HasEmbeddedPassword || File.Exists(this.FilePath + ".json");
                this.Status.ForeColor = Ui.AccentDark;
                this.Status.Text = info.HasEmbeddedPassword ? "Self-contained VSUX — this file works without a JSON companion." : "Legacy VSUX — keep its .vsux.json companion until you make it self-contained.";
                if (info.IsAttached) { this.Status.Text += " Use Detach & lock before these actions."; }
                this.ConvertButton.Enabled = !info.IsAttached && hasPassword;
                this.PortableButton.Enabled = !info.IsAttached && !info.HasEmbeddedPassword && hasPassword;
                this.BackupButton.Enabled = !info.IsAttached;
            } catch (Exception error) {
                this.Status.ForeColor = Ui.Danger;
                this.Status.Text = error.Message + " Standalone VsuRecover.exe can restore a detached image without password information.";
            }
        }

        private void Run(string operation) {
            if (this.IsBusy) { return; }
            this.RefreshState();
            if ((operation == "Convert" && !this.ConvertButton.Enabled) || (operation == "Portable" && !this.PortableButton.Enabled) || (operation == "Backup" && !this.BackupButton.Enabled)) { return; }
            string target = null;
            if (operation != "Portable") {
                using (var save = new SaveFileDialog {
                    Title = operation == "Convert" ? "Convert back to VHDX in the same folder" : "Export optional recovery information",
                    Filter = operation == "Convert" ? "VHDX images (*.vhdx)|*.vhdx" : "Recovery information (*.json)|*.json",
                    DefaultExt = operation == "Convert" ? "vhdx" : "json", AddExtension = true, OverwritePrompt = false,
                    InitialDirectory = Path.GetDirectoryName(this.FilePath),
                    FileName = operation == "Convert" ? Path.GetFileNameWithoutExtension(this.FilePath) + ".vhdx" : Path.GetFileName(this.FilePath) + ".recovery.json"
                }) {
                    if (save.ShowDialog(this) != DialogResult.OK) { return; }
                    target = save.FileName;
                }
                if (File.Exists(target) || Directory.Exists(target)) { Medo.MessageBox.ShowWarning(this, "The destination already exists. Choose a new name; existing files are never replaced."); return; }
                if (operation == "Convert" && !string.Equals(Path.GetDirectoryName(this.FilePath), Path.GetDirectoryName(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase)) {
                    Medo.MessageBox.ShowWarning(this, "Choose a VHDX filename in the same folder as the VSUX image."); return;
                }
            }
            if (operation != "Backup") {
                var question = operation == "Convert"
                    ? "This removes soft protection, removes its embedded password record and renames the detached image to .vhdx. Ordinary tools can then mount it without a password. Disk files and partitions stay in place; any old JSON file is kept.\n\nThis writes container metadata. Keep a full backup. Continue?"
                    : "This stores password information inside the detached image so it can travel without its JSON companion. The existing JSON file is kept.\n\nThis writes container metadata. Keep a full backup. Continue?";
                if (Medo.MessageBox.ShowQuestion(this, question, MessageBoxButtons.YesNo) != DialogResult.Yes) { return; }
            }
            this.IsBusy = true;
            this.Page.Enabled = false;
            try {
                if (VsuxForm.RunOperation(this, operation, this.FilePath, destination: target) != DialogResult.OK) { return; }
                if (operation == "Convert") {
                    this.ResultFileName = target;
                    this.IsBusy = false;
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                } else if (operation == "Backup") {
                    Medo.MessageBox.ShowInformation(this, "Recovery information exported. Keep a full backup separately; this JSON file is optional for normal use and standalone recovery.");
                }
            } finally {
                this.IsBusy = false;
                if (this.ResultFileName == null && !this.IsDisposed) { this.Page.Enabled = true; this.RefreshState(); }
            }
        }
    }
}
