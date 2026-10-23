namespace VsuPrototype;

internal sealed class PasswordForm : Form {
    private readonly TextBox passwordBox = new() { UseSystemPasswordChar = true, MaxLength = 1024, Dock = DockStyle.Fill, AccessibleName = "Password" };
    private readonly TextBox confirmationBox = new() { UseSystemPasswordChar = true, MaxLength = 1024, Dock = DockStyle.Fill, AccessibleName = "Confirm password" };
    private readonly Label errorLabel = new() { AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = SystemColors.ControlText, AccessibleRole = AccessibleRole.Alert };
    private readonly bool creating;

    public string Password => passwordBox.Text;

    public PasswordForm(string path, bool create, string action = "Unlock") {
        creating = create;
        Text = create ? "Set VSUX password" : "Unlock VSUX drive";
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(500, 0);

        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new Label {
            Text = create ? "Choose a password" : "Enter the password to continue",
            Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8)
        };
        DpiChanged += (_, e) => heading.Font = new Font(Font.FontFamily, 10F * e.DeviceDpiNew / 96F, FontStyle.Bold);
        Shown += (_, _) => heading.Font = new Font(Font.FontFamily, 10F * DeviceDpi / 96F, FontStyle.Bold);
        layout.Controls.Add(heading);
        layout.Controls.Add(new Label {
            Text = Path.GetFileName(path), AutoSize = true, MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 16), UseMnemonic = false
        });
        layout.Controls.Add(new Label { Text = "&Password", AutoSize = true });
        passwordBox.Margin = new Padding(0, 4, 0, 12);
        layout.Controls.Add(passwordBox);
        if (create) {
            layout.Controls.Add(new Label { Text = "&Confirm password", AutoSize = true });
            confirmationBox.Margin = new Padding(0, 4, 0, 12);
            layout.Controls.Add(confirmationBox);
        }
        var showPassword = new CheckBox { Text = "&Show password", AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        showPassword.CheckedChanged += (_, _) => {
            passwordBox.UseSystemPasswordChar = !showPassword.Checked;
            confirmationBox.UseSystemPasswordChar = !showPassword.Checked;
        };
        layout.Controls.Add(showPassword);
        layout.Controls.Add(new Label {
            Text = create
                ? "Use at least 8 characters. This password gates this app; the disk data stays unencrypted."
                : "This prototype uses a mount lock. The disk data stays unencrypted.",
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 12)
        });
        errorLabel.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(errorLabel);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(95, 32) };
        var accept = new Button { Text = create ? "Create lock" : action, AutoSize = true, MinimumSize = new Size(110, 32) };
        accept.Click += (_, _) => {
            if (creating && passwordBox.Text.Length < 8) {
                errorLabel.Text = "Enter a password with at least 8 characters.";
                passwordBox.Focus();
                return;
            }
            if (!creating && passwordBox.Text.Length == 0) {
                errorLabel.Text = "Enter the drive password.";
                passwordBox.Focus();
                return;
            }
            if (creating && passwordBox.Text != confirmationBox.Text) {
                errorLabel.Text = "The passwords do not match. Enter the confirmation again.";
                confirmationBox.SelectAll();
                confirmationBox.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = cancel;
        Shown += (_, _) => passwordBox.Focus();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            passwordBox.Clear();
            confirmationBox.Clear();
        }
        base.Dispose(disposing);
    }
}
