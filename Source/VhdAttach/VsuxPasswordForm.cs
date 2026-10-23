using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VhdAttach {
    internal sealed class VsuxPasswordForm : Form {
        private readonly TextBox PasswordBox = new TextBox { UseSystemPasswordChar = true, MaxLength = 1024, Dock = DockStyle.Fill, AccessibleName = "Password" };
        private readonly TextBox ConfirmationBox = new TextBox { UseSystemPasswordChar = true, MaxLength = 1024, Dock = DockStyle.Fill, AccessibleName = "Confirm password" };
        private readonly Label ErrorLabel = new Label { AutoSize = true, ForeColor = Ui.Danger, AccessibleRole = AccessibleRole.Alert };

        public string Password => this.PasswordBox.Text;

        public VsuxPasswordForm(string path, bool create, string action) {
            this.Text = create ? "Set VSUX password" : action + " - VSUX drive";
            this.Font = SystemFonts.MessageBoxFont;
            this.Icon = Ui.AppIcon;
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.AutoSize = true;
            this.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.MinimumSize = new Size(this.Font.Height * 33, 0);

            var width = this.Font.Height * 31;
            this.ErrorLabel.MaximumSize = new Size(width, 0);
            var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(this.Font.Height), ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = create ? "Choose a password" : "Enter the drive password", Font = new Font(this.Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
            layout.Controls.Add(new Label { Text = Path.GetFileName(path), UseMnemonic = false, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 16) });
            layout.Controls.Add(new Label { Text = "&Password", AutoSize = true });
            this.PasswordBox.Margin = new Padding(0, 4, 0, 12);
            layout.Controls.Add(this.PasswordBox);
            if (create) {
                layout.Controls.Add(new Label { Text = "&Confirm password", AutoSize = true });
                this.ConfirmationBox.Margin = new Padding(0, 4, 0, 12);
                layout.Controls.Add(this.ConfirmationBox);
            }
            var showPassword = new CheckBox { Text = "&Show password", AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            showPassword.CheckedChanged += (s, e) => {
                this.PasswordBox.UseSystemPasswordChar = !showPassword.Checked;
                this.ConfirmationBox.UseSystemPasswordChar = !showPassword.Checked;
            };
            layout.Controls.Add(showPassword);
            layout.Controls.Add(new Label {
                Text = create
                    ? "Use at least 8 characters. The password information stays inside the drive; no JSON companion is required. This mount lock leaves the data unencrypted."
                    : "VSUX uses a mount lock. The disk data remains unencrypted.",
                AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 12)
            });
            layout.Controls.Add(this.ErrorLabel);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
            var accept = new Button { Text = create ? "Create &lock" : "&" + action.Replace("&", "&&"), AutoSize = true, MinimumSize = new Size(110, 30) };
            accept.Click += (s, e) => {
                if (this.PasswordBox.Text.Length < (create ? 8 : 1)) {
                    this.ErrorLabel.Text = create ? "Enter a password with at least 8 characters." : "Enter the drive password.";
                    this.PasswordBox.Focus();
                    return;
                }
                if (create && this.PasswordBox.Text != this.ConfirmationBox.Text) {
                    this.ErrorLabel.Text = "The passwords do not match. Enter the confirmation again.";
                    this.ConfirmationBox.SelectAll();
                    this.ConfirmationBox.Focus();
                    return;
                }
                this.DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(accept);
            layout.Controls.Add(buttons);
            this.Controls.Add(layout);
            this.AcceptButton = accept;
            this.CancelButton = cancel;
            this.Shown += (s, e) => this.PasswordBox.Focus();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                this.PasswordBox.Clear();
                this.ConfirmationBox.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
