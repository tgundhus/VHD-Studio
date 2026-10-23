using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttach {
    /// <summary>A narrow elevated operation window. Passwords never leave this process.</summary>
    internal sealed class VsuxForm : Form {
        private readonly string Operation;
        private readonly string FilePath;
        private readonly bool ReadOnly;
        private readonly string Destination;
        private readonly long SizeInBytes;
        private readonly bool FixedSize;
        private readonly Label StatusLabel;
        private bool IsBusy;

        public VsuxForm(string operation, string path, bool readOnly = false, string destination = null, long sizeInBytes = 0, bool fixedSize = false) {
            if (!string.Equals(operation, "Attach", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(operation, "Lock", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(operation, "Protect", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(operation, "Create", StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentException("Unknown VSUX operation.", nameof(operation));
            }
            this.Operation = operation.ToLowerInvariant();
            this.FilePath = Path.GetFullPath(path);
            this.ReadOnly = readOnly;
            this.Destination = destination == null ? null : Path.GetFullPath(destination);
            this.SizeInBytes = sizeInBytes;
            this.FixedSize = fixedSize;
            this.Font = SystemFonts.MessageBoxFont;
            this.Icon = Ui.AppIcon;
            this.Text = this.ActionTitle + " - " + Branding.ProductName;
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(this.Font.Height * 35, this.Font.Height * 9);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(this.Font.Height), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = Path.GetFileName(this.FilePath), AutoSize = true, MaximumSize = new Size(this.ClientSize.Width - this.Font.Height * 3, 0), UseMnemonic = false });
            this.StatusLabel = new Label { Text = "Waiting for the drive password…", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleRole = AccessibleRole.StatusBar };
            layout.Controls.Add(this.StatusLabel);
            layout.Controls.Add(new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Height = 8, AccessibleName = "Operation in progress" });
            this.Controls.Add(layout);
            this.Shown += async (s, e) => await this.ExecuteAsync();
            this.FormClosing += (s, e) => { if (this.IsBusy) { e.Cancel = true; } };
        }

        private string ActionTitle => this.Operation == "create" ? "Create VSUX" : this.Operation == "protect" ? "Protect as VSUX" : this.Operation == "lock" ? "Detach & lock" : this.ReadOnly ? "Attach read-only" : "Attach";

        public static DialogResult RunOperation(IWin32Window owner, string operation, string path, bool readOnly = false, string destination = null, long sizeInBytes = 0, bool fixedSize = false) {
            using (var form = new VsuxForm(operation, path, readOnly, destination, sizeInBytes, fixedSize)) { return form.ShowDialog(owner); }
        }

        private async Task ExecuteAsync() {
            this.IsBusy = true;
            var result = DialogResult.Cancel;
            try {
                if (!Ui.IsElevated) {
                    this.StatusLabel.Text = "Waiting for administrator approval and the VSUX operation…";
                    var arguments = "/Vsux" + (this.Operation == "create" ? "Create" : this.Operation == "lock" ? "Lock" : this.Operation == "protect" ? "Protect" : "Attach") + " " + Ui.Quote(this.FilePath);
                    if (this.Destination != null) { arguments += " " + Ui.Quote(this.Destination); }
                    if (this.ReadOnly) { arguments += " /readonly"; }
                    if (this.Operation == "create") {
                        arguments += " /size=" + this.SizeInBytes.ToString(CultureInfo.InvariantCulture);
                        if (this.FixedSize) { arguments += " /fixed"; }
                    }
                    var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, Branding.ApplicationExe), arguments) { UseShellExecute = true, Verb = "runas" };
                    using (var process = Process.Start(startInfo)) {
                        if (process == null) { throw new InvalidOperationException("The VSUX operation could not start."); }
                        await process.WaitForExitAsync();
                        result = process.ExitCode == 0 ? DialogResult.OK : process.ExitCode == 2 ? DialogResult.Cancel : DialogResult.Abort;
                    }
                } else {
                    using (var password = new VsuxPasswordForm(this.Destination ?? this.FilePath, this.Operation == "protect" || this.Operation == "create", this.ActionTitle)) {
                        if (password.ShowDialog(this) == DialogResult.OK) {
                            var secret = password.Password;
                            this.StatusLabel.Text = this.Operation == "lock" ? "Safely detaching, then locking… Close files on the drive if it is busy."
                                : this.Operation == "create" ? "Creating the blank disk and applying its mount lock…"
                                : this.Operation == "protect" ? "Checking the detached image and creating its mount lock…"
                                : "Checking the password and attaching the drive…";
                            try {
                                await Task.Run(() => {
                                    var auditOperation = "VSUX " + this.ActionTitle;
                                    AuditLog.Started(auditOperation, this.FilePath, this.Destination);
                                    try {
                                        switch (this.Operation) {
                                            case "create": VsuxCreation.Create(this.FilePath, this.SizeInBytes, this.FixedSize, secret); break;
                                            case "protect": VsuxOperations.Protect(this.FilePath, this.Destination, secret); break;
                                            case "lock": VsuxOperations.Lock(this.FilePath, secret); break;
                                            default: VsuxOperations.Attach(this.FilePath, secret, this.ReadOnly); break;
                                        }
                                        AuditLog.Succeeded(auditOperation, this.FilePath, this.Destination);
                                    } catch (Exception error) {
                                        AuditLog.Failed(auditOperation, this.FilePath, error);
                                        throw;
                                    }
                                });
                                result = DialogResult.OK;
                            } finally { secret = null; }
                        }
                    }
                }
            } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
                result = DialogResult.Cancel;
            } catch (Exception ex) {
                var message = ex.Message;
                var statusPath = this.Destination ?? this.FilePath;
                if (File.Exists(statusPath)) {
                    try { message += Environment.NewLine + Environment.NewLine + await Task.Run(() => VsuxOperations.Status(statusPath)); } catch { }
                }
                Medo.MessageBox.ShowError(this, this.ActionTitle + " failed.\n\n" + message);
                result = DialogResult.Abort;
            } finally {
                this.IsBusy = false;
                Environment.ExitCode = result == DialogResult.OK ? 0 : result == DialogResult.Cancel ? 2 : 1;
                this.DialogResult = result;
                this.Close();
            }
        }
    }
}
