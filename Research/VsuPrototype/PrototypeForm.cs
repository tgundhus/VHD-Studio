using PrototypeOperations = VhdStudio.Vsux.VsuxOperations;

namespace VsuPrototype;

internal sealed class PrototypeForm : Form {
    private readonly TextBox pathBox = new() { ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "VSUX file path" };
    private readonly TextBox statusBox = new() {
        ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
        AccessibleName = "Drive status and operation result"
    };
    private readonly Label progressLabel = new() { Text = "Ready", AutoSize = true, Dock = DockStyle.Fill };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Dock = DockStyle.Fill, Height = 5 };
    private readonly CheckBox readOnly = new() { Text = "Attach &read-only", AutoSize = true };
    private readonly FlowLayoutPanel commands = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private readonly Button browse = MakeButton("&Browse…");
    private readonly Button protect = MakeButton("&Protect disposable VHDX…");
    private readonly Button attach = MakeButton("&Attach…");
    private readonly Button detach = MakeButton("&Detach and lock…");
    private readonly Button unlock = MakeButton("&Unlock only…");
    private readonly Button refresh = MakeButton("Refresh &status");
    private bool busy;

    public PrototypeForm(string? initialPath = null) {
        Text = "VSUX mount lock — disposable-image prototype";
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 530);
        MinimumSize = new Size(650, 560);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(row == 7 ? SizeType.Percent : SizeType.AutoSize, row == 7 ? 100 : 0));
        var heading = new Label {
            Text = "VSUX mount lock", AutoSize = true, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8)
        };
        DpiChanged += (_, e) => heading.Font = new Font(Font.FontFamily, 16F * e.DeviceDpiNew / 96F, FontStyle.Bold);
        Shown += (_, _) => heading.Font = new Font(Font.FontFamily, 16F * DeviceDpi / 96F, FontStyle.Bold);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(new Label {
            Text = "Use disposable VHDX images only. New VSUX stores password information inside VHDX metadata and needs no JSON companion. Protection updates metadata; raw lock/unlock changes only eight signature bytes. Disk data stays unencrypted.",
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(1100, 0), Margin = new Padding(0, 0, 0, 16)
        }, 0, 1);
        layout.Controls.Add(new Label { Text = "VSUX &file", AutoSize = true, Margin = Padding.Empty }, 0, 2);
        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 12) };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.Controls.Add(pathBox, 0, 0);
        fileRow.Controls.Add(browse, 1, 0);
        browse.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(fileRow, 0, 3);
        commands.Controls.AddRange([attach, detach, unlock, refresh, protect]);
        layout.Controls.Add(commands, 0, 4);
        readOnly.Margin = new Padding(0, 8, 0, 12);
        layout.Controls.Add(readOnly, 0, 5);
        layout.Controls.Add(new Label { Text = "Drive status", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 0, 6);
        statusBox.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(statusBox, 0, 7);
        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 12) };
        progressPanel.Controls.Add(progressLabel);
        progressPanel.Controls.Add(progress);
        layout.Controls.Add(progressPanel, 0, 8);
        layout.Controls.Add(new Label {
            Text = "Windows requests administrator approval when this prototype starts. Closing this window does not detach or lock a drive. Standalone recovery is available from the command line.",
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(1100, 0), Margin = Padding.Empty
        }, 0, 9);
        Controls.Add(layout);
        AcceptButton = attach;
        browse.Click += async (_, _) => await BrowseAsync();
        protect.Click += async (_, _) => await ProtectAsync();
        attach.Click += async (_, _) => {
            bool attachReadOnly = readOnly.Checked;
            await PasswordOperationAsync("Attach", (path, password) => PrototypeOperations.Attach(path, password, attachReadOnly));
        };
        detach.Click += async (_, _) => await PasswordOperationAsync("Detach and lock", PrototypeOperations.Lock);
        unlock.Click += async (_, _) => await PasswordOperationAsync("Unlock without mounting", PrototypeOperations.Unlock);
        refresh.Click += async (_, _) => await RunOperationAsync("Reading drive status…", () => { });
        FormClosing += (_, e) => {
            if (busy) {
                e.Cancel = true;
                progressLabel.Text = "Wait for the current operation to finish before closing.";
            }
        };
        pathBox.Text = initialPath ?? "";
        Shown += async (_, _) => {
            SetEnabled();
            if (pathBox.Text.Length > 0) await RunOperationAsync("Reading drive status…", () => { });
            else statusBox.Text = "Select a .vsux file, or protect a disposable .vhdx image to begin.";
        };
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 34), Margin = new Padding(0, 0, 8, 8) };

    private void SetEnabled() {
        bool selected = pathBox.Text.Length > 0;
        commands.Enabled = !busy;
        browse.Enabled = !busy;
        readOnly.Enabled = !busy;
        attach.Enabled = selected;
        detach.Enabled = selected;
        unlock.Enabled = selected;
        refresh.Enabled = selected;
        UseWaitCursor = busy;
        progress.Visible = busy;
    }

    private async Task BrowseAsync() {
        using var dialog = new OpenFileDialog { Title = "Select a VSUX file", Filter = "VSUX files (*.vsux)|*.vsux", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        pathBox.Text = dialog.FileName;
        await RunOperationAsync("Reading drive status…", () => { });
    }

    private async Task ProtectAsync() {
        using var source = new OpenFileDialog { Title = "Choose a disposable VHDX image", Filter = "VHDX images (*.vhdx)|*.vhdx", CheckFileExists = true };
        if (source.ShowDialog(this) != DialogResult.OK) return;
        using var target = new SaveFileDialog {
            Title = "Save VSUX in the same folder", Filter = "VSUX files (*.vsux)|*.vsux", DefaultExt = "vsux", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(source.FileName) + ".vsux", InitialDirectory = Path.GetDirectoryName(source.FileName), OverwritePrompt = false
        };
        if (target.ShowDialog(this) != DialogResult.OK) return;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(source.FileName)), Path.GetDirectoryName(Path.GetFullPath(target.FileName)), StringComparison.OrdinalIgnoreCase)) {
            MessageBox.Show(this, "Save the VSUX file in the same folder as its source VHDX image.", "Choose the same folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (File.Exists(target.FileName) || Directory.Exists(target.FileName)) {
            MessageBox.Show(this, "The destination already exists. Choose a new name.", "File already exists", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var password = new PasswordForm(target.FileName, true);
        if (password.ShowDialog(this) != DialogResult.OK) return;
        string secret = password.Password;
        await RunOperationAsync("Validating detached image and creating VSUX lock…",
            () => PrototypeOperations.Protect(source.FileName, target.FileName, secret),
            () => pathBox.Text = target.FileName);
    }

    private async Task PasswordOperationAsync(string action, Action<string, string> operation) {
        string path = pathBox.Text;
        if (path.Length == 0) return;
        using var password = new PasswordForm(path, false, action);
        if (password.ShowDialog(this) != DialogResult.OK) return;
        string secret = password.Password;
        await RunOperationAsync(action + "…", () => operation(path, secret));
    }

    private async Task RunOperationAsync(string description, Action operation, Action? completed = null) {
        if (busy) return;
        busy = true;
        progressLabel.Text = description;
        SetEnabled();
        try {
            await Task.Run(operation);
            completed?.Invoke();
            string path = pathBox.Text;
            statusBox.Text = path.Length == 0 ? "Ready" : await Task.Run(() => PrototypeOperations.Status(path));
            progressLabel.Text = "Operation completed.";
        } catch (Exception ex) {
            statusBox.Text = ex.Message;
            string path = pathBox.Text;
            if (path.Length > 0) {
                try {
                    statusBox.AppendText(Environment.NewLine + Environment.NewLine + await Task.Run(() => PrototypeOperations.Status(path)));
                } catch (Exception statusError) {
                    statusBox.AppendText(Environment.NewLine + "Unable to read drive status: " + statusError.Message);
                }
            }
            progressLabel.Text = "Operation failed. Review the details above.";
            MessageBox.Show(this, ex.Message, "VSUX operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        } finally {
            busy = false;
            SetEnabled();
        }
    }
}
