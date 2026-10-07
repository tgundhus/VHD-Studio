using System;
using System.Collections.Generic;
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
            this.TaskList.SelectedIndexChanged += (s, e) => { this.ReloadDetails(); this.ShowTask(this.TaskList.SelectedItem as string); };

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
            this.Activated += (s, e) => { //the disk may have been attached, detached or changed in another window meanwhile
                if (this.IsBusy || (this.TaskList.SelectedItem == null)) { return; }
                var before = this.Details;
                this.ReloadDetails();
                if (!SameState(before, this.Details)) { this.ShowTask(this.TaskList.SelectedItem as string); }
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
            var backup = this.AddBackupOption(flow, this.FileName);
            var run = this.AddButton(flow, "Compact now", Ui.Glyph.Compact);
            run.Enabled = (this.Details == null) || (this.Details.Kind != VirtualDiskKind.Fixed);
            run.Click += (s, e) => {
                var before = this.Details?.PhysicalSize;
                var fsAware = aware.Checked;
                var shutdownWsl = wslShutdown.Checked;
                this.RunOperation(new Operation {
                    Title = "Compact", Verb = "Compacting", Cancellable = true, BackupOf = this.FileName, Backup = backup,
                    Summary = "Unused blocks inside the disk will be released and the file will shrink. Data inside the disk is not changed." + (shutdownWsl ? " WSL (and Docker Desktop) will be shut down first." : ""),
                    Action = (progress, token) => {
                        if (shutdownWsl) { ShutdownWsl(); }
                        VirtualDiskImage.Compact(this.FileName, fsAware, progress, token);
                    },
                    OnSuccess = () => {
                        var after = new FileInfo(this.FileName).Length;
                        return (before.HasValue) ? string.Format(CultureInfo.CurrentCulture, "Compacted from {0} to {1} (saved {2}).", Ui.FormatSize(before.Value), Ui.FormatSize(after), Ui.FormatSize(Math.Max(0, before.Value - after))) : "Compact completed.";
                    },
                });
            };
        }

        private void BuildResize(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Resize");
            var isVhdx = VirtualDiskImage.IsVhdx(this.FileName) || (this.Details?.Format == "VHDX");
            this.AddParagraph(flow, isVhdx
                ? "Grow or shrink the virtual disk. A disk and its partition are separate: growing the disk adds unused space, which the partition can then be extended into. To shrink, first shrink the partition in Disk Manager, then shrink the file to the smallest safe size."
                : "VHD files can only grow. Convert to VHDX to be able to shrink. A disk and its partition are separate: growing the disk adds unused space, which the partition can then be extended into.");
            if (this.Details != null) {
                this.AddParagraph(flow, string.Format(CultureInfo.CurrentCulture, "Current size: {0}. Smallest safe size: {1}.", Ui.FormatSize(this.Details.VirtualSize), Ui.FormatSize(this.Details.SmallestSafeVirtualSize)), Ui.Muted);
            }
            if (this.Details?.AttachedPath != null) { //attached: the partition can be extended right now, safely while in use
                (Storage.DiskInfo Disk, Storage.PartitionInfo Partition, long Gain)? extendable = null;
                try { extendable = Storage.PartitionExtender.FindExtendable(this.FileName); } catch (Exception ex) when (ex is System.Management.ManagementException || ex is InvalidOperationException || ex is UnauthorizedAccessException) { }
                if (extendable != null) {
                    var found = extendable.Value;
                    this.AddParagraph(flow, string.Format(CultureInfo.CurrentCulture, "⚠ {0} at the end of this disk is not used by any partition. {1} is {2}.", Ui.FormatSize(found.Gain), found.Partition.DisplayName, Ui.FormatSize(found.Partition.Size)), Ui.Danger);
                    var extendNow = this.AddButton(flow, "Extend " + found.Partition.DisplayName + " by " + Ui.FormatSize(found.Gain), Ui.Glyph.Resize);
                    extendNow.Click += (s, e) => this.RunOperation(new Operation {
                        Title = "Extend partition", Verb = "Extending", Cancellable = false,
                        Summary = found.Partition.DisplayName + " will grow by " + Ui.FormatSize(found.Gain) + " into the unused space at the end of the disk. Its data stays where it is, and it can stay in use.",
                        Action = (p, t) => extendResult = Storage.PartitionExtender.ExtendOnline(Storage.StorageManager.Revalidate(found.Disk)),
                        OnSuccess = () => extendResult,
                    });
                }
            }
            var newSize = new RadioButton { Text = "New size:", AutoSize = true, Checked = true };
            var sizeBox = new TextBox { Width = this.Font.Height * 10, Text = (this.Details?.VirtualSize != null) ? Ui.FormatSize(this.Details.VirtualSize.Value * 2) : "" };
            var shrink = new RadioButton { Text = "Shrink to smallest safe size", AutoSize = true, Enabled = isVhdx };
            flow.Controls.AddRange(new Control[] { newSize, sizeBox, shrink });
            var extend = new CheckBox { Text = "After growing, extend the last partition to use the new space (recommended)", AutoSize = true, Checked = true, Margin = new Padding(0, this.Font.Height / 2, 0, 0) };
            flow.Controls.Add(extend);
            shrink.CheckedChanged += (s, e) => extend.Enabled = !shrink.Checked;
            var backup = this.AddBackupOption(flow, this.FileName);
            var run = this.AddButton(flow, "Resize now", Ui.Glyph.Resize);
            run.Click += (s, e) => {
                long size = 0;
                if (newSize.Checked && !Ui.TryParseSize(sizeBox.Text, out size)) { Medo.MessageBox.ShowWarning(this, "Size is not valid (e.g. 64 GB)."); return; }
                if (newSize.Checked && (this.Details?.VirtualSize != null) && (size < this.Details.VirtualSize) && (this.Details.SmallestSafeVirtualSize != null) && (size < this.Details.SmallestSafeVirtualSize)) {
                    Medo.MessageBox.ShowWarning(this, "The new size is below the smallest safe size. Shrink the partition first (Disk Manager), or choose 'Shrink to smallest safe size'.");
                    return;
                }
                var summary = (size == 0)
                    ? "The virtual disk will shrink to its smallest safe size (" + Ui.FormatSize(this.Details?.SmallestSafeVirtualSize) + "). Partitions are not changed; space after the last partition is removed."
                    : "The virtual disk will change from " + Ui.FormatSize(this.Details?.VirtualSize) + " to " + Ui.FormatSize(size) + ".";
                var grows = (size > 0) && ((this.Details?.VirtualSize == null) || (size > this.Details.VirtualSize));
                var extendAfter = grows && extend.Checked && extend.Enabled;
                if (size > 0) { summary += extendAfter ? " Afterwards the last partition is extended into the new space (the disk is attached briefly without a drive letter)." : " Partitions are not changed; extend the partition yourself to use the new space."; }
                var result = "";
                this.RunOperation(new Operation {
                    Title = "Resize", Verb = "Resizing", Cancellable = false, BackupOf = this.FileName, Backup = backup, Summary = summary,
                    Action = (progress, token) => {
                        VirtualDiskImage.Resize(this.FileName, size, progress);
                        if (extendAfter) {
                            try {
                                result = Storage.PartitionExtender.ExtendLastPartition(this.FileName);
                            } catch (Exception ex) when (!(ex is OutOfMemoryException)) { //the resize itself succeeded; report, do not fail it
                                result = "The partition could not be extended automatically (" + ex.Message + "). Extend it in Disk Manager.";
                            }
                        }
                    },
                    OnSuccess = () => "Disk resized to " + Ui.FormatSize(size == 0 ? this.Details?.SmallestSafeVirtualSize : size) + ". " + (extendAfter ? result : (size == 0 ? "" : "The partition was not changed; extend it to use the new space.")),
                });
            };
        }

        private string extendResult = "";

        private static bool SameState(VirtualDiskDetails a, VirtualDiskDetails b) {
            if ((a == null) || (b == null)) { return a == b; }
            return (a.AttachedPath == b.AttachedPath) && (a.VirtualSize == b.VirtualSize) && (a.PhysicalSize == b.PhysicalSize) && (a.NeedsLogReplay == b.NeedsLogReplay) && (a.ParentResolved == b.ParentResolved);
        }

        private void BuildConvert(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Convert");
            this.AddParagraph(flow, "Creates a new virtual disk with the same content. The original file is not changed. Choose the format with the file extension (.vhd or .vhdx).");
            var target = new TextBox { Width = this.Font.Height * 30, Text = SuggestConvertName(this.FileName) };
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) => {
                using (var dialog = new SaveFileDialog { Filter = "Virtual disk (*.vhdx)|*.vhdx|Legacy virtual disk (*.vhd)|*.vhd", FileName = target.Text, OverwritePrompt = false }) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { target.Text = dialog.FileName; }
                }
            };
            var dynamic = new RadioButton { Text = "Dynamically expanding", AutoSize = true, Checked = this.Details?.Kind != VirtualDiskKind.Fixed };
            var fixedSize = new RadioButton { Text = "Fixed size (pre-allocated)", AutoSize = true, Checked = this.Details?.Kind == VirtualDiskKind.Fixed };
            var typePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            typePanel.Controls.AddRange(new Control[] { dynamic, fixedSize });

            flow.Controls.Add(new Label { Text = "Destination:", AutoSize = true });
            flow.Controls.Add(target);
            flow.Controls.Add(browse);
            flow.Controls.Add(typePanel);
            this.AddParagraph(flow, "The logical sector size is kept, so partitions and file systems stay readable.", Ui.Muted);
            var run = this.AddButton(flow, "Convert now", Ui.Glyph.Convert);
            run.Click += (s, e) => {
                var destination = target.Text.Trim();
                if (string.IsNullOrEmpty(destination) || string.Equals(Path.GetFullPath(destination), this.FileName, StringComparison.OrdinalIgnoreCase)) { Medo.MessageBox.ShowWarning(this, "Choose a different destination file."); return; }
                if (File.Exists(destination)) { Medo.MessageBox.ShowWarning(this, "Destination file already exists."); return; }
                var sectorSize = 0; //always the source's sector size
                var isFixed = fixedSize.Checked;
                this.RunOperation(new Operation {
                    Title = "Convert", Verb = "Converting", Cancellable = true,
                    Summary = "A new " + (isFixed ? "fixed-size" : "dynamic") + " disk will be created at " + destination + ". The original file is only read, never changed.",
                    Action = (progress, token) => VirtualDiskImage.Convert(this.FileName, destination, isFixed, sectorSize, progress, token),
                    OnSuccess = () => "Created and verified " + destination + ".",
                });
            };
        }

        private void BuildDifferencing(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Create differencing disk");
            this.AddParagraph(flow, "Creates a child disk that records only changes. Attach the child to experiment safely; the parent stays untouched (do not modify or move the parent while children exist). Merge the child later to keep the changes, or delete it to discard them.");
            var target = new TextBox { Width = this.Font.Height * 30, Text = SuggestChildName(this.FileName) };
            flow.Controls.Add(new Label { Text = "Child file:", AutoSize = true });
            flow.Controls.Add(target);
            var protect = new CheckBox { Text = "Mark the parent read-only so it cannot be changed by accident (recommended)", AutoSize = true, Checked = true };
            var attach = new CheckBox { Text = "Open the child disk when done", AutoSize = true, Checked = true };
            flow.Controls.Add(protect);
            flow.Controls.Add(attach);
            var run = this.AddButton(flow, "Create child disk", Ui.Glyph.Branch);
            run.Click += (s, e) => {
                var child = target.Text.Trim();
                if (File.Exists(child)) { Medo.MessageBox.ShowWarning(this, "Child file already exists."); return; }
                var protectParent = protect.Checked;
                this.RunOperation(new Operation {
                    Title = "Create differencing disk", Verb = "Creating", Cancellable = false,
                    Summary = "A new child disk will be created at " + child + "." + (protectParent ? " The parent file will be marked read-only." : " Warning: any later change to the parent will corrupt the child."),
                    Action = (progress, token) => VirtualDiskImage.CreateDifferencing(this.FileName, child, protectParent),
                    OnSuccess = () => {
                        if (attach.Checked) { Process.Start(new ProcessStartInfo(Environment.ProcessPath, Ui.Quote(child)) { UseShellExecute = false }); }
                        return "Created " + child + ".";
                    },
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
            var parentFile = this.Details.ParentLocations.FirstOrDefault();
            var siblings = new List<string>();
            try { if (parentFile != null) { siblings.AddRange(VirtualDiskImage.FindDependents(parentFile).Where(f => !string.Equals(Path.GetFullPath(f), this.FileName, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName)); } } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (siblings.Count > 0) { this.AddParagraph(flow, "⚠ These disks are also based on the parent and will stop working after the merge: " + string.Join(", ", siblings), Ui.Danger); }
            var deleteChild = new CheckBox { Text = "Move this child file to the Recycle Bin after a successful merge", AutoSize = true };
            flow.Controls.Add(deleteChild);
            var backup = (parentFile != null) ? this.AddBackupOption(flow, parentFile, "parent") : null;
            var run = this.AddButton(flow, "Merge now", Ui.Glyph.Merge);
            run.Enabled = (parentFile != null) && (this.Details.ParentResolved != false);
            run.Click += (s, e) => {
                var delete = deleteChild.Checked;
                this.RunOperation(new Operation {
                    Title = "Merge", Verb = "Merging", Cancellable = false, BackupOf = parentFile, Backup = backup, TypedConfirmation = "Merge",
                    Summary = "All changes in this child disk will be written permanently into the parent " + parentFile + "." + ((siblings.Count > 0) ? " These disks based on the same parent will stop working: " + string.Join(", ", siblings) + "." : " Any other differencing disks based on the same parent will stop working."),
                    Action = (progress, token) => VirtualDiskImage.MergeIntoParent(this.FileName, progress),
                    OnSuccess = () => {
                        if (!delete) { return "Merged into parent. The child disk is no longer needed."; }
                        try {
                            RecycleFile(this.FileName);
                            AuditLog.Succeeded("Recycle merged child", this.FileName);
                            return "Merged. The child disk was moved to the Recycle Bin.";
                        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException) {
                            return "Merged into parent. The child file was kept: " + ex.Message;
                        }
                    },
                });
            };
        }

        private void BuildRepair(FlowLayoutPanel flow) {
            this.AddHeading(flow, "Repair");

            this.AddSubheading(flow, "Replay log");
            this.AddParagraph(flow, "A VHDX that was not closed cleanly (crash, power loss) keeps a log that must be replayed before Windows will open it, often reported as \"Access denied\". Replaying opens the file read/write once.");
            if (this.Details != null) { this.AddParagraph(flow, this.Details.NeedsLogReplay ? "Status: log replay is required." : "Status: no pending log.", this.Details.NeedsLogReplay ? Ui.Danger : Ui.Teal); }
            var replayBackup = this.AddBackupOption(flow, this.FileName);
            var replay = this.AddButton(flow, "Replay log", Ui.Glyph.Repair);
            replay.Enabled = (this.Details == null) || this.Details.NeedsLogReplay;
            replay.Click += (s, e) => this.RunOperation(new Operation {
                Title = "Replay log", Verb = "Replaying", BackupOf = this.FileName, Backup = replayBackup,
                Summary = "Pending writes recorded in the VHDX log will be applied to the file, exactly as Windows does on a normal read/write open.",
                Action = (p, t) => VirtualDiskImage.ReplayLog(this.FileName),
                OnSuccess = () => "Log replayed. The disk can be attached again.",
            });

            this.AddSubheading(flow, "Fix parent path");
            this.AddParagraph(flow, "Re-links a differencing disk whose parent was moved or renamed (like Set-VHD -ParentPath).");
            this.AddParagraph(flow, "The selected file is verified to be the real parent; on a mismatch nothing is changed.", Ui.Muted);
            var parentBackup = this.AddBackupOption(flow, this.FileName);
            parentBackup.Enabled = false; //mandatory for this operation
            var fixParent = this.AddButton(flow, "Choose new parent…", Ui.Glyph.Branch);
            fixParent.Enabled = ((this.Details == null) || (this.Details.Kind == VirtualDiskKind.Differencing)) && parentBackup.Checked;
            if (!parentBackup.Checked) { this.AddParagraph(flow, "A backup is required for this repair; free up space first.", Ui.Danger); }
            fixParent.Click += (s, e) => {
                using (var dialog = new OpenFileDialog { Filter = "Virtual disks (*.vhd; *.vhdx; *.avhd; *.avhdx)|*.vhd;*.vhdx;*.avhd;*.avhdx|All files (*.*)|*.*" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                    var parent = dialog.FileName;
                    this.RunOperation(new Operation {
                        Title = "Fix parent path", Verb = "Updating", BackupOf = this.FileName, Backup = parentBackup,
                        Summary = "This disk will be linked to " + parent + ". Only the parent reference inside this file changes.",
                        Action = (p, t) => VirtualDiskImage.SetParentPath(this.FileName, parent),
                        OnSuccess = () => "Parent set to " + parent + " and the chain verified.",
                    });
                }
            };

            this.AddSubheading(flow, "Reset disk identifier");
            this.AddParagraph(flow, "Gives the disk a new unique ID. Use this when two copies of the same disk cannot be attached at the same time. Differencing children of this disk will stop working.");
            var resetBackup = this.AddBackupOption(flow, this.FileName);
            var reset = this.AddButton(flow, "Reset identifier…", Ui.Glyph.Refresh);
            reset.Click += (s, e) => {
                Guid newId = Guid.Empty;
                this.RunOperation(new Operation {
                    Title = "Reset identifier", Verb = "Updating", BackupOf = this.FileName, Backup = resetBackup, TypedConfirmation = "Reset",
                    Summary = "This disk gets a new unique identifier. Existing differencing disks based on it will no longer open.",
                    Action = (p, t) => newId = VirtualDiskImage.ResetIdentifier(this.FileName),
                    OnSuccess = () => "New identifier: " + newId + ".",
                });
            };

            this.AddSubheading(flow, "File system");
            this.AddParagraph(flow, "To check or repair the file system inside the disk, attach it and use Disk Manager → Partition → Check / Repair file system.");
        }

        #endregion


        #region Running

        /// <summary>
        /// Everything that changes a disk goes through one path: preconditions, explicit confirmation,
        /// optional verified backup, the operation, and an audit log entry.
        /// </summary>
        private sealed class Operation {
            public string Title;
            public string Verb;
            public string Summary;
            public string BackupOf;              //file changed in place; null if nothing existing is modified
            public CheckBox Backup;
            public bool Cancellable;             //only operations that are safe to interrupt
            public string TypedConfirmation;     //high-impact operations require typing this word
            public Action<IProgress<VirtualDiskProgress>, CancellationToken> Action;
            public Func<string> OnSuccess;
        }

        private async void RunOperation(Operation op) {
            if (!Ui.IsElevated) {
                if (Medo.MessageBox.ShowQuestion(this, op.Title + " requires administrator rights.\n\nRestart maintenance as administrator?", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    this.Elevate(this.TaskList.SelectedItem as string);
                }
                return;
            }
            if (this.IsBusy) { return; }

            var makeBackup = (op.BackupOf != null) && (op.Backup?.Checked == true);
            var backupPath = makeBackup ? VirtualDiskBackup.GetDefaultBackupPath(op.BackupOf) : null;
            var message = op.Summary + "\n\n";
            if (op.BackupOf != null) {
                message += makeBackup ? "A verified backup will be created first:\n" + backupPath : "⚠ No backup will be made. If something goes wrong, the original cannot be recovered.";
                message += "\n\n";
            }
            if (!op.Cancellable && (op.BackupOf != null)) { message += "This operation cannot be interrupted once started. Do not shut down or put the computer to sleep.\n\n"; }
            message += "Continue?";
            using (var confirm = new Storage.PromptForm(op.Title, message, null, destructive: (op.BackupOf != null) && !makeBackup)) {
                if (op.TypedConfirmation != null) { confirm.RequireTyping(op.TypedConfirmation); }
                if (confirm.ShowDialog(this) != DialogResult.OK) { return; }
            }

            this.IsBusy = true;
            this.Cancellation = new CancellationTokenSource();
            this.Page.Enabled = false;
            this.TaskList.Enabled = false;
            this.Progress.Visible = true;
            this.Progress.Style = ProgressBarStyle.Marquee;
            this.ProgressText.ForeColor = Ui.Muted;
            Medo.Windows.Forms.TaskbarProgress.DefaultOwner = this;
            Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.Indeterminate);
            var verb = op.Verb;
            var progress = new Progress<VirtualDiskProgress>(p => {
                if (p.Total <= 0) { return; }
                this.Progress.Style = ProgressBarStyle.Continuous;
                this.Progress.Value = p.Percentage;
                var label = ((verb == "Backing up") && (p.Current > p.Total / 2)) ? "Verifying backup" : verb; //second half re-reads the copy from disk
                this.ProgressText.Text = label + " " + p.Percentage.ToString(CultureInfo.CurrentCulture) + " %";
                Medo.Windows.Forms.TaskbarProgress.SetPercentage(p.Percentage);
            });
            var token = this.Cancellation.Token;
            var auditTarget = this.FileName;
            var backupDone = false;
            try {
                if (makeBackup) {
                    verb = "Backing up";
                    this.ShowCancel(true);
                    this.ProgressText.Text = verb + "…";
                    AuditLog.Started("Backup", op.BackupOf, backupPath);
                    await Task.Run(() => VirtualDiskBackup.Create(op.BackupOf, backupPath, progress, token));
                    AuditLog.Succeeded("Backup", op.BackupOf, backupPath);
                    backupDone = true;
                }

                verb = op.Verb;
                this.ShowCancel(op.Cancellable);
                this.Progress.Style = ProgressBarStyle.Marquee;
                this.ProgressText.Text = verb + "…";
                AuditLog.Started(op.Title, auditTarget, op.Summary);
                await Task.Run(() => op.Action(progress, op.Cancellable ? token : CancellationToken.None));
                AuditLog.Succeeded(op.Title, auditTarget, backupDone ? "backup: " + backupPath : null);

                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.NoProgress);
                this.ProgressText.Text = op.OnSuccess() + (backupDone ? " Backup: " + Path.GetFileName(backupPath) : "");
                this.ProgressText.ForeColor = Ui.Teal;
            } catch (OperationCanceledException) {
                AuditLog.Failed(op.Title, auditTarget, new OperationCanceledException("Cancelled by user."));
                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.NoProgress);
                this.ProgressText.Text = op.Title + " was cancelled. Nothing was changed" + (backupDone ? " except the backup copy." : ".");
            } catch (Exception ex) {
                AuditLog.Failed(op.Title, auditTarget, ex);
                Medo.Windows.Forms.TaskbarProgress.SetState(Medo.Windows.Forms.TaskbarProgressState.Error);
                this.ProgressText.Text = op.Title + " failed.";
                this.ProgressText.ForeColor = Ui.Danger;
                var text = op.Title + " failed.\n\n" + ex.Message;
                if (backupDone) { text += "\n\nYour verified backup is at:\n" + backupPath + "\n\nKeep it until you have checked the disk."; }
                Medo.MessageBox.ShowError(this, text);
            } finally {
                this.IsBusy = false;
                this.Progress.Visible = false;
                this.ShowCancel(false);
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

        private void ShowCancel(bool visible) {
            this.CancelTaskButton.Visible = visible;
            this.CancelTaskButton.Enabled = visible;
        }

        /// <summary>
        /// Offers a verified backup of a file that an operation changes in place. Checked whenever there is room for it.
        /// </summary>
        private CheckBox AddBackupOption(FlowLayoutPanel flow, string fileToBackUp, string what = null) {
            var check = new CheckBox { AutoSize = true, MaximumSize = new Size(this.Font.Height * 36, 0), Margin = new Padding(0, this.Font.Height / 2, 0, 0) };
            try {
                var size = new FileInfo(fileToBackUp).Length;
                var hasRoom = VirtualDiskBackup.HasRoomFor(fileToBackUp, fileToBackUp, out _, out var available);
                check.Text = string.Format(CultureInfo.CurrentCulture, "Create a verified backup of the {0} first (recommended, {1}; {2} free)", what ?? "file", Ui.FormatSize(size), Ui.FormatSize(available));
                check.Checked = hasRoom;
                check.Enabled = hasRoom;
                if (!hasRoom) { check.Text += " - not enough free space; copy the file elsewhere manually before continuing."; check.ForeColor = Ui.Danger; }
            } catch (IOException) {
                check.Text = "Create a verified backup first (file not accessible)";
                check.Enabled = false;
            }
            flow.Controls.Add(check);
            return check;
        }

        /// <summary>
        /// Moves a file to the Recycle Bin. Refuses for locations without a Recycle Bin instead of deleting permanently.
        /// </summary>
        private static void RecycleFile(string fileName) {
            var root = Path.GetPathRoot(Path.GetFullPath(fileName));
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || (new DriveInfo(root).DriveType != DriveType.Fixed)) {
                throw new IOException("its location has no Recycle Bin. Delete it manually once you have verified the parent.");
            }
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(fileName, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
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
