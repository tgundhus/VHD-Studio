using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VhdAttachCommon;

namespace VhdAttach {

    /// <summary>
    /// Image maintenance: compact, resize, convert, differencing disks, merge and repair.
    /// Everything runs on VirtDisk.dll, so it works on Windows Home without the Hyper-V module.
    /// </summary>
    internal sealed class MaintenanceForm : Form {

        public static readonly string[] TaskNames = { "Details", "Compact", "Resize", "Convert", "Differencing", "Merge", "Repair" };

        private readonly string FileName;
        private VirtualDiskDetails Details;
        private readonly ListBox TaskList;
        private readonly Panel Page;
        private readonly ProgressBar Progress;
        private readonly Label ProgressText;
        private readonly Button CancelTaskButton;
        private CancellationTokenSource Cancellation;
        private bool IsBusy;

        public MaintenanceForm(string fileName, string initialTask = null) {
            this.FileName = Path.GetFullPath(fileName);
            this.Text = "Maintenance - " + Path.GetFileName(fileName) + " - " + Branding.ProductName;
            this.Font = SystemFonts.MessageBoxFont;
            this.Icon = Ui.AppIcon;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(this.Font.Height * 52, this.Font.Height * 34);
            this.MinimumSize = new Size(this.Font.Height * 44, this.Font.Height * 28);

            var banner = new Ui.Banner("Maintenance", this.FileName) { Font = this.Font };

            this.TaskList = new ListBox { Dock = DockStyle.Left, Width = this.Font.Height * 11, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = (int)(this.Font.Height * 2.2), BorderStyle = BorderStyle.None, BackColor = Ui.AccentSoft };
            this.TaskList.Items.AddRange(TaskNames.Cast<object>().ToArray());
            this.TaskList.DrawItem += this.TaskList_DrawItem;
            this.TaskList.SelectedIndexChanged += (s, e) => this.ShowTask(this.TaskList.SelectedItem as string);

            this.Page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(this.Font.Height), AutoScroll = true };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 3, AutoSize = true, Padding = new Padding(this.Font.Height / 2) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            this.Progress = new ProgressBar { Dock = DockStyle.Fill, Visible = false };
            this.ProgressText = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Muted };
            this.CancelTaskButton = new Button { Text = "Cancel", AutoSize = true, Visible = false };
            this.CancelTaskButton.Click += (s, e) => { this.Cancellation?.Cancel(); this.CancelTaskButton.Enabled = false; };
            bottom.Controls.Add(this.Progress, 0, 0);
            bottom.Controls.Add(this.ProgressText, 1, 0);
            bottom.Controls.Add(this.CancelTaskButton, 2, 0);

            this.Controls.Add(this.Page);
            this.Controls.Add(this.TaskList);
            this.Controls.Add(bottom);
            this.Controls.Add(banner);

            this.Load += (s, e) => {
                this.ReloadDetails();
                var index = Array.FindIndex(TaskNames, t => string.Equals(t, initialTask, StringComparison.OrdinalIgnoreCase));
                this.TaskList.SelectedIndex = Math.Max(0, index);
            };
            this.FormClosing += (s, e) => {
                if (this.IsBusy) {
                    e.Cancel = true;
                    Medo.MessageBox.ShowWarning(this, "An operation is still running. Cancel it first.");
                }
            };
        }


        #region Pages

        private void ShowTask(string task) {
            this.Page.SuspendLayout();
            this.Page.Controls.Clear();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, MaximumSize = new Size(this.Font.Height * 38, 0) };
            this.Page.Controls.Add(flow);

            if (this.Details == null) {
                this.AddParagraph(flow, "Virtual disk details could not be read. The file may be in use, damaged, or not a virtual disk.", Ui.Danger);
            } else if (this.Details.NeedsLogReplay && (task != "Repair")) {
                this.AddParagraph(flow, "⚠ This VHDX was not closed cleanly (pending log). Run Repair → Replay log before other operations.", Ui.Danger);
            }

            switch (task) {
                case "Details": this.BuildDetails(flow); break;
                case "Compact": this.BuildCompact(flow); break;
                case "Resize": this.BuildResize(flow); break;
                case "Convert": this.BuildConvert(flow); break;
                case "Differencing": this.BuildDifferencing(flow); break;
                case "Merge": this.BuildMerge(flow); break;
                case "Repair": this.BuildRepair(flow); break;
            }
            if (!Ui.IsElevated && (task != "Details")) {
                var elevate = this.AddButton(flow, "Restart as administrator", Ui.Glyph.Shield);
                elevate.Click += (s, e) => this.Elevate(task);
                this.AddParagraph(flow, "Maintenance operations require administrator rights.", Ui.Muted);
            }
            this.Page.ResumeLayout();
        }

        private void BuildDetails(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Details");
            var d = this.Details;
            if (d == null) { return; }
            var rows = new (string, string)[] {
                ("Format", d.Format),
                ("Type", d.Kind.ToString()),
                ("Virtual size", Ui.FormatSize(d.VirtualSize)),
                ("Size on disk", Ui.FormatSize(d.PhysicalSize)),
                ("Smallest safe size", Ui.FormatSize(d.SmallestSafeVirtualSize)),
                ("Fragmentation", d.FragmentationPercentage.HasValue ? d.FragmentationPercentage + " %" : ""),
                ("Block size", Ui.FormatSize(d.BlockSize)),
                ("Sector size (logical / physical)", string.Format(CultureInfo.CurrentCulture, "{0} / {1} bytes", d.LogicalSectorSize, d.PhysicalSectorSize)),
                ("4K aligned", d.Is4KAligned?.ToString() ?? ""),
                ("Identifier", d.Identifier?.ToString() ?? ""),
                ("Attached as", d.AttachedPath ?? "Not attached"),
                ("Parent", string.Join(Environment.NewLine, d.ParentLocations) + ((d.ParentResolved == false) ? " (NOT FOUND)" : "")),
                ("Pending log", d.NeedsLogReplay ? "Yes - replay required" : "No"),
            };
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, this.Font.Height) };
            foreach (var (key, value) in rows.Where(r => !string.IsNullOrEmpty(r.Item2))) {
                table.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 2, this.Font.Height, 2) });
                table.Controls.Add(new TextBox { Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, Width = this.Font.Height * 22, BackColor = this.BackColor, Multiline = value.Contains(Environment.NewLine), Height = this.Font.Height * (value.Count(c => c == '\n') + 1) });
            }
            flow.Controls.Add(table);
            var refresh = this.AddButton(flow, "Refresh", Ui.Glyph.Refresh);
            refresh.Click += (s, e) => { this.ReloadDetails(); this.ShowTask("Details"); };
        }

        private void BuildCompact(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Compact");
            this.AddParagraph(flow, "Returns unused space inside a dynamic or differencing disk to the host drive. Fixed disks cannot be compacted. The disk must be detached.");
            var aware = new RadioButton { Text = "Full compact (Windows file systems + zeroed blocks) - recommended", AutoSize = true, Checked = true };
            var zeroOnly = new RadioButton { Text = "Zeroed blocks only (Linux / WSL / Docker ext4 images; run 'fstrim' inside first)", AutoSize = true };
            var isWsl = this.LooksLikeWslImage();
            if (isWsl) { zeroOnly.Checked = true; }
            var wslShutdown = new CheckBox { Text = "Run 'wsl --shutdown' first (releases WSL and Docker Desktop images)", AutoSize = true, Checked = isWsl };
            flow.Controls.AddRange(new Control[] { aware, zeroOnly, wslShutdown });
            if (this.Details != null) {
                this.AddParagraph(flow, string.Format(CultureInfo.CurrentCulture, "Size on disk: {0} of {1} virtual.", Ui.FormatSize(this.Details.PhysicalSize), Ui.FormatSize(this.Details.VirtualSize)), Ui.Muted);
            }
            var run = this.AddButton(flow, "Compact now", Ui.Glyph.Compact);
            run.Enabled = (this.Details == null) || (this.Details.Kind != VirtualDiskKind.Fixed);
            run.Click += (s, e) => {
                var before = this.Details?.PhysicalSize;
                var fsAware = aware.Checked;
                var shutdownWsl = wslShutdown.Checked;
                this.RunOperation("Compact", "Compacting", (progress, token) => {
                    if (shutdownWsl) { ShutdownWsl(); }
                    VirtualDiskImage.Compact(this.FileName, fsAware, progress, token);
                }, () => {
                    var after = new FileInfo(this.FileName).Length;
                    return (before.HasValue) ? string.Format(CultureInfo.CurrentCulture, "Compacted from {0} to {1} (saved {2}).", Ui.FormatSize(before.Value), Ui.FormatSize(after), Ui.FormatSize(Math.Max(0, before.Value - after))) : "Compact completed.";
                });
            };
        }

        private void BuildResize(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Resize");
            var isVhdx = VirtualDiskImage.IsVhdx(this.FileName) || (this.Details?.Format == "VHDX");
            this.AddParagraph(flow, isVhdx
                ? "Grow or shrink the virtual disk. After growing, extend the partition in Disk Manager. To shrink, first shrink the partition in Disk Manager, then shrink the file to the smallest safe size."
                : "VHD files can only grow. Convert to VHDX to be able to shrink. After growing, extend the partition in Disk Manager.");
            if (this.Details != null) {
                this.AddParagraph(flow, string.Format(CultureInfo.CurrentCulture, "Current size: {0}. Smallest safe size: {1}.", Ui.FormatSize(this.Details.VirtualSize), Ui.FormatSize(this.Details.SmallestSafeVirtualSize)), Ui.Muted);
            }
            var newSize = new RadioButton { Text = "New size:", AutoSize = true, Checked = true };
            var sizeBox = new TextBox { Width = this.Font.Height * 10, Text = (this.Details?.VirtualSize != null) ? Ui.FormatSize(this.Details.VirtualSize.Value * 2) : "" };
            var shrink = new RadioButton { Text = "Shrink to smallest safe size", AutoSize = true, Enabled = isVhdx };
            flow.Controls.AddRange(new Control[] { newSize, sizeBox, shrink });
            var run = this.AddButton(flow, "Resize now", Ui.Glyph.Resize);
            run.Click += (s, e) => {
                long size = 0;
                if (newSize.Checked && !Ui.TryParseSize(sizeBox.Text, out size)) { Medo.MessageBox.ShowWarning(this, "Size is not valid (e.g. 64 GB)."); return; }
                if (newSize.Checked && (this.Details?.VirtualSize != null) && (size < this.Details.VirtualSize) && (this.Details.SmallestSafeVirtualSize != null) && (size < this.Details.SmallestSafeVirtualSize)) {
                    Medo.MessageBox.ShowWarning(this, "The new size is below the smallest safe size. Shrink the partition first (Disk Manager), or choose 'Shrink to smallest safe size'.");
                    return;
                }
                this.RunOperation("Resize", "Resizing", (progress, token) => VirtualDiskImage.Resize(this.FileName, size, progress, token), () => "Resize completed. Extend or shrink the partition in Disk Manager if needed.");
            };
        }

        private void BuildConvert(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Convert");
            this.AddParagraph(flow, "Creates a new virtual disk with the same content. The original file is not changed. Choose the format with the file extension (.vhd or .vhdx).");
            var target = new TextBox { Width = this.Font.Height * 30, Text = SuggestConvertName(this.FileName) };
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) => {
                using (var dialog = new SaveFileDialog { Filter = "Virtual disk (*.vhdx)|*.vhdx|Legacy virtual disk (*.vhd)|*.vhd", FileName = target.Text, OverwritePrompt = true }) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { target.Text = dialog.FileName; }
                }
            };
            var dynamic = new RadioButton { Text = "Dynamically expanding", AutoSize = true, Checked = this.Details?.Kind != VirtualDiskKind.Fixed };
            var fixedSize = new RadioButton { Text = "Fixed size (pre-allocated)", AutoSize = true, Checked = this.Details?.Kind == VirtualDiskKind.Fixed };
            var typePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            typePanel.Controls.AddRange(new Control[] { dynamic, fixedSize });
            var sector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = this.Font.Height * 14 };
            sector.Items.AddRange(new object[] { "Same as source", "512 bytes", "4096 bytes (4K native)" });
            sector.SelectedIndex = 0;
            flow.Controls.Add(new Label { Text = "Destination:", AutoSize = true });
            flow.Controls.Add(target);
            flow.Controls.Add(browse);
            flow.Controls.Add(typePanel);
            flow.Controls.Add(new Label { Text = "Logical sector size (VHDX only):", AutoSize = true });
            flow.Controls.Add(sector);
            var run = this.AddButton(flow, "Convert now", Ui.Glyph.Convert);
            run.Click += (s, e) => {
                var destination = target.Text.Trim();
                if (string.IsNullOrEmpty(destination) || string.Equals(Path.GetFullPath(destination), this.FileName, StringComparison.OrdinalIgnoreCase)) { Medo.MessageBox.ShowWarning(this, "Choose a different destination file."); return; }
                if (File.Exists(destination)) { Medo.MessageBox.ShowWarning(this, "Destination file already exists."); return; }
                var sectorSize = (sector.SelectedIndex == 1) ? 512 : (sector.SelectedIndex == 2) ? 4096 : 0;
                if ((sectorSize == 4096) && !VirtualDiskImage.IsVhdx(destination)) { Medo.MessageBox.ShowWarning(this, "4K logical sectors are only supported by VHDX."); return; }
                var isFixed = fixedSize.Checked;
                this.RunOperation("Convert", "Converting", (progress, token) => VirtualDiskImage.Convert(this.FileName, destination, isFixed, sectorSize, progress, token), () => "Created " + destination + ".");
            };
        }

        private void BuildDifferencing(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Create differencing disk");
            this.AddParagraph(flow, "Creates a child disk that records only changes. Attach the child to experiment safely; the parent stays untouched (do not modify or move the parent while children exist). Merge the child later to keep the changes, or delete it to discard them.");
            var target = new TextBox { Width = this.Font.Height * 30, Text = SuggestChildName(this.FileName) };
            flow.Controls.Add(new Label { Text = "Child file:", AutoSize = true });
            flow.Controls.Add(target);
            var attach = new CheckBox { Text = "Open the child disk when done", AutoSize = true, Checked = true };
            flow.Controls.Add(attach);
            var run = this.AddButton(flow, "Create child disk", Ui.Glyph.Branch);
            run.Click += (s, e) => {
                var child = target.Text.Trim();
                if (File.Exists(child)) { Medo.MessageBox.ShowWarning(this, "Child file already exists."); return; }
                this.RunOperation("Create differencing disk", "Creating", (progress, token) => VirtualDiskImage.CreateDifferencing(this.FileName, child), () => {
                    if (attach.Checked) { Process.Start(new ProcessStartInfo(Environment.ProcessPath, Ui.Quote(child)) { UseShellExecute = false }); }
                    return "Created " + child + ".";
                });
            };
        }

        private void BuildMerge(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Merge into parent");
            if (this.Details?.Kind != VirtualDiskKind.Differencing) {
                this.AddParagraph(flow, "This is not a differencing disk, so there is nothing to merge.", Ui.Muted);
                return;
            }
            this.AddParagraph(flow, "Writes all changes from this child disk into its parent. Afterwards the child is no longer needed and can be deleted. Other children of the same parent become invalid.");
            this.AddParagraph(flow, "Parent: " + string.Join(", ", this.Details.ParentLocations), Ui.Muted);
            var deleteChild = new CheckBox { Text = "Delete this child file after a successful merge", AutoSize = true };
            flow.Controls.Add(deleteChild);
            var run = this.AddButton(flow, "Merge now", Ui.Glyph.Merge);
            run.Click += (s, e) => {
                if (Medo.MessageBox.ShowWarning(this, "Merging changes the parent disk permanently. Continue?", MessageBoxButtons.YesNo) != DialogResult.Yes) { return; }
                var delete = deleteChild.Checked;
                this.RunOperation("Merge", "Merging", (progress, token) => {
                    VirtualDiskImage.MergeIntoParent(this.FileName, progress, token);
                    if (delete) { File.Delete(this.FileName); }
                }, () => delete ? "Merged and deleted the child disk." : "Merged into parent.");
            };
        }

        private void BuildRepair(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Repair");

            this.AddSubheading(flow, "Replay log");
            this.AddParagraph(flow, "A VHDX that was not closed cleanly (crash, power loss) keeps a log that must be replayed before Windows will open it, often reported as \"Access denied\". Replaying opens the file read/write once.");
            if (this.Details != null) { this.AddParagraph(flow, this.Details.NeedsLogReplay ? "Status: log replay is required." : "Status: no pending log.", this.Details.NeedsLogReplay ? Ui.Danger : Ui.Teal); }
            var replay = this.AddButton(flow, "Replay log", Ui.Glyph.Repair);
            replay.Click += (s, e) => this.RunOperation("Replay log", "Replaying", (p, t) => VirtualDiskImage.ReplayLog(this.FileName), () => "Log replayed. The disk can be attached again.");

            this.AddSubheading(flow, "Fix parent path");
            this.AddParagraph(flow, "Re-links a differencing disk whose parent was moved or renamed (like Set-VHD -ParentPath).");
            var fixParent = this.AddButton(flow, "Choose new parent…", Ui.Glyph.Branch);
            fixParent.Enabled = (this.Details == null) || (this.Details.Kind == VirtualDiskKind.Differencing);
            fixParent.Click += (s, e) => {
                using (var dialog = new OpenFileDialog { Filter = "Virtual disks (*.vhd; *.vhdx; *.avhd; *.avhdx)|*.vhd;*.vhdx;*.avhd;*.avhdx|All files (*.*)|*.*" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                    var parent = dialog.FileName;
                    this.RunOperation("Fix parent path", "Updating", (p, t) => VirtualDiskImage.SetParentPath(this.FileName, parent), () => "Parent set to " + parent + ".");
                }
            };

            this.AddSubheading(flow, "Reset disk identifier");
            this.AddParagraph(flow, "Gives the disk a new unique ID. Use this when two copies of the same disk cannot be attached at the same time. Differencing children of this disk will stop working.");
            var reset = this.AddButton(flow, "Reset identifier…", Ui.Glyph.Refresh);
            reset.Click += (s, e) => {
                if (Medo.MessageBox.ShowWarning(this, "Assign a new identifier to this disk? Existing differencing children will no longer match it.", MessageBoxButtons.YesNo) != DialogResult.Yes) { return; }
                Guid newId = Guid.Empty;
                this.RunOperation("Reset identifier", "Updating", (p, t) => newId = VirtualDiskImage.ResetIdentifier(this.FileName), () => "New identifier: " + newId + ".");
            };

            this.AddSubheading(flow, "File system");
            this.AddParagraph(flow, "To check or repair the file system inside the disk, attach it and use Disk Manager → Partition → Check / Repair file system.");
        }

        #endregion


        #region Running

        private async void RunOperation(string title, string verb, Action<IProgress<VirtualDiskProgress>, CancellationToken> action, Func<string> onSuccess) {
            if (!Ui.IsElevated) {
                if (Medo.MessageBox.ShowQuestion(this, title + " requires administrator rights.\n\nRestart maintenance as administrator?", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    this.Elevate(this.TaskList.SelectedItem as string);
                }
                return;
            }
            if (this.IsBusy) { return; }
            this.IsBusy = true;
            this.Cancellation = new CancellationTokenSource();
            this.Page.Enabled = false;
            this.TaskList.Enabled = false;
            this.Progress.Visible = true;
            this.Progress.Style = ProgressBarStyle.Marquee;
            this.CancelTaskButton.Visible = true;
            this.CancelTaskButton.Enabled = true;
            this.ProgressText.Text = verb + "…";
            this.ProgressText.ForeColor = Ui.Muted;
            Medo.Windows.Forms.TaskbarProgress.DefaultOwner = this;
            Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.Indeterminate);
            var progress = new Progress<VirtualDiskProgress>(p => {
                if (p.Total <= 0) { return; }
                this.Progress.Style = ProgressBarStyle.Continuous;
                this.Progress.Value = p.Percentage;
                this.ProgressText.Text = verb + " " + p.Percentage.ToString(CultureInfo.CurrentCulture) + " %";
                Medo.Windows.Forms.TaskbarProgress.SetPercentage(p.Percentage);
            });
            var token = this.Cancellation.Token;
            try {
                await Task.Run(() => action(progress, token));
                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.NoProgress);
                this.ProgressText.Text = onSuccess();
                this.ProgressText.ForeColor = Ui.Teal;
            } catch (OperationCanceledException) {
                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.NoProgress);
                this.ProgressText.Text = title + " was cancelled.";
            } catch (Exception ex) {
                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.Error);
                this.ProgressText.Text = title + " failed.";
                this.ProgressText.ForeColor = Ui.Danger;
                Medo.MessageBox.ShowError(this, title + " failed.\n\n" + ex.Message);
            } finally {
                this.IsBusy = false;
                this.Progress.Visible = false;
                this.CancelTaskButton.Visible = false;
                this.Page.Enabled = true;
                this.TaskList.Enabled = true;
                this.ReloadDetails();
                var current = this.TaskList.SelectedItem as string;
                var status = this.ProgressText.Text;
                var color = this.ProgressText.ForeColor;
                this.ShowTask(current);
                this.ProgressText.Text = status;
                this.ProgressText.ForeColor = color;
            }
        }

        private void ReloadDetails() {
            try {
                this.Details = VirtualDiskImage.GetDetails(this.FileName);
            } catch (Exception ex) {
                this.Details = null;
                this.ProgressText.Text = ex.Message;
                this.ProgressText.ForeColor = Ui.Danger;
            }
        }

        private void Elevate(string task) {
            if (Ui.RunElevated("/Maintain " + Ui.Quote(this.FileName) + " /Task=" + task, wait: false)) {
                this.DialogResult = DialogResult.Retry;
                this.Close();
            }
        }

        private static void ShutdownWsl() {
            try {
                using (var process = Process.Start(new ProcessStartInfo("wsl.exe", "--shutdown") { CreateNoWindow = true, UseShellExecute = false })) {
                    process?.WaitForExit(60000);
                }
            } catch (System.ComponentModel.Win32Exception) { } //WSL not installed
        }

        private bool LooksLikeWslImage() {
            var name = this.FileName.ToLowerInvariant();
            return name.EndsWith("ext4.vhdx", StringComparison.Ordinal) || name.Contains(@"\docker\") || name.Contains("docker_data") || name.Contains(@"\packages\canonical") || name.Contains(@"\wsl\");
        }

        private static string SuggestConvertName(string fileName) {
            var directory = Path.GetDirectoryName(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = VirtualDiskImage.IsVhdx(fileName) ? ".vhd" : ".vhdx";
            return Path.Combine(directory, name + extension);
        }

        private static string SuggestChildName(string fileName) {
            var directory = Path.GetDirectoryName(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = VirtualDiskImage.IsVhdx(fileName) ? ".avhdx" : ".avhd";
            return Path.Combine(directory, name + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + extension);
        }

        #endregion


        #region Layout helpers

        private void AddHeading(FlowLayoutPanel flow, string text) {
            flow.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(this.Font.FontFamily, this.Font.Size * 1.35f, FontStyle.Bold), ForeColor = Ui.AccentDark, Margin = new Padding(0, 0, 0, this.Font.Height / 2) });
        }

        private void AddSubheading(FlowLayoutPanel flow, string text) {
            flow.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(0, this.Font.Height, 0, 2) });
        }

        private void AddParagraph(FlowLayoutPanel flow, string text, Color? color = null) {
            flow.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(this.Font.Height * 36, 0), ForeColor = color ?? SystemColors.ControlText, Margin = new Padding(0, 0, 0, this.Font.Height / 2) });
        }

        private Button AddButton(FlowLayoutPanel flow, string text, char glyph) {
            var size = Ui.ScaledIconSize(this);
            var button = new Button {
                Text = "  " + text, AutoSize = true, Image = Ui.GetGlyph(glyph, size, Ui.Accent), ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText, Padding = new Padding(this.Font.Height / 2, 2, this.Font.Height / 2, 2), Margin = new Padding(0, this.Font.Height / 2, 0, this.Font.Height / 2),
            };
            flow.Controls.Add(button);
            return button;
        }

        private void TaskList_DrawItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) { return; }
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Ui.Accent : Ui.AccentSoft)) { e.Graphics.FillRectangle(back, e.Bounds); }
            var task = (string)this.TaskList.Items[e.Index];
            var glyph = task switch {
                "Details" => Ui.Glyph.Info, "Compact" => Ui.Glyph.Compact, "Resize" => Ui.Glyph.Resize, "Convert" => Ui.Glyph.Convert,
                "Differencing" => Ui.Glyph.Branch, "Merge" => Ui.Glyph.Merge, _ => Ui.Glyph.Repair,
            };
            var size = Ui.ScaledIconSize(this);
            var color = selected ? Color.White : Ui.AccentDark;
            using (var icon = Ui.GetGlyph(glyph, size, color)) {
                e.Graphics.DrawImage(icon, e.Bounds.Left + this.Font.Height / 2, e.Bounds.Top + (e.Bounds.Height - size) / 2);
            }
            var textBounds = new Rectangle(e.Bounds.Left + this.Font.Height + size, e.Bounds.Top, e.Bounds.Width - size - this.Font.Height, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, task == "Differencing" ? "Differencing disk" : task, this.Font, textBounds, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        #endregion

    }
}
