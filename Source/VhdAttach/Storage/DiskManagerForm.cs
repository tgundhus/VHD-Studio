using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VhdAttach.Storage {

    /// <summary>
    /// DiskPart-style graphical tool: disks, partitions and volumes with safe, confirmed operations.
    /// </summary>
    internal sealed class DiskManagerForm : Form {

        private readonly ListView DiskList;
        private readonly ListView PartitionList;
        private readonly DiskMap Map;
        private readonly ToolStrip Tools;
        private readonly ToolStripDropDownButton DiskMenu;
        private readonly ToolStripDropDownButton PartitionMenu;
        private readonly ToolStripButton ShowPhysicalButton;
        private readonly ToolStripButton UnlockPhysicalButton;
        private readonly ToolStripButton ElevateButton;
        private readonly StatusStrip Status;
        private readonly ToolStripStatusLabel StatusText;

        private IList<DiskInfo> Disks = new List<DiskInfo>();
        private bool IsBusy;
        private IList<PartitionInfo> Partitions = new List<PartitionInfo>();
        private readonly int? InitialDiskNumber;

        public DiskManagerForm(int? selectDiskNumber = null) {
            this.InitialDiskNumber = selectDiskNumber;
            this.Text = "Disk Manager - " + VhdAttachCommon.Branding.ProductName;
            this.Font = SystemFonts.MessageBoxFont;
            this.Icon = Ui.AppIcon;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(this.Font.Height * 62, this.Font.Height * 40);
            this.MinimumSize = new Size(this.Font.Height * 40, this.Font.Height * 26);

            var banner = new Ui.Banner("Disk Manager", "DiskPart-style partition and volume tools. Virtual disks are editable; system and boot disks are always protected.") { Font = this.Font };

            this.Tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(this.Font.Height / 2, 2, 0, 2), RenderMode = ToolStripRenderMode.System };
            var iconSize = Ui.ScaledIconSize(this);
            this.Tools.ImageScalingSize = new Size(iconSize, iconSize);
            var refresh = new ToolStripButton("Refresh", Ui.GetGlyph(Ui.Glyph.Refresh, iconSize, Ui.Accent), (s, e) => this.RefreshAll()) { ToolTipText = "Refresh (F5)" };
            this.DiskMenu = new ToolStripDropDownButton("Disk", Ui.GetGlyph(Ui.Glyph.Disk, iconSize, Ui.Accent));
            this.PartitionMenu = new ToolStripDropDownButton("Partition", Ui.GetGlyph(Ui.Glyph.Format, iconSize, Ui.Accent));
            this.ShowPhysicalButton = new ToolStripButton("Show physical disks") { CheckOnClick = true, Checked = true };
            this.ShowPhysicalButton.CheckedChanged += (s, e) => this.RefreshAll();
            this.UnlockPhysicalButton = new ToolStripButton("Allow changes to physical disks", Ui.GetGlyph(Ui.Glyph.Warning, iconSize, Ui.Danger)) { CheckOnClick = true };
            this.UnlockPhysicalButton.CheckedChanged += this.UnlockPhysical_CheckedChanged;
            this.ElevateButton = new ToolStripButton("Restart as administrator", Ui.GetGlyph(Ui.Glyph.Shield, iconSize, Ui.Accent), this.Elevate_Click) { Alignment = ToolStripItemAlignment.Right, Visible = !Ui.IsElevated };
            this.Tools.Items.AddRange(new ToolStripItem[] { refresh, new ToolStripSeparator(), this.DiskMenu, this.PartitionMenu, new ToolStripSeparator(), this.ShowPhysicalButton, this.UnlockPhysicalButton, this.ElevateButton });
            this.DiskMenu.DropDownOpening += (s, e) => this.BuildDiskMenu(this.DiskMenu.DropDownItems);
            this.PartitionMenu.DropDownOpening += (s, e) => this.BuildPartitionMenu(this.PartitionMenu.DropDownItems);


            this.DiskList = CreateList("Disk", "Type", "Name / backing file", "Size", "Unallocated", "Style", "Status");
            this.DiskList.SelectedIndexChanged += (s, e) => this.LoadPartitions();
            this.DiskList.ContextMenuStrip = new ContextMenuStrip();
            this.DiskList.ContextMenuStrip.Opening += (s, e) => { this.BuildDiskMenu(this.DiskList.ContextMenuStrip.Items); e.Cancel = this.SelectedDisk == null; };

            this.PartitionList = CreateList("#", "Drive / mount", "Label", "File system", "Size", "Free", "Type", "Status");
            this.PartitionList.SelectedIndexChanged += (s, e) => { this.Map.SelectedPartition = this.SelectedPartition; this.Map.Invalidate(); };
            this.PartitionList.ContextMenuStrip = new ContextMenuStrip();
            this.PartitionList.ContextMenuStrip.Opening += (s, e) => { this.BuildPartitionMenu(this.PartitionList.ContextMenuStrip.Items); e.Cancel = this.SelectedPartition == null; };

            this.BuildDiskMenu(this.DiskMenu.DropDownItems); //lists must exist first
            this.BuildPartitionMenu(this.PartitionMenu.DropDownItems);

            this.Map = new DiskMap { Dock = DockStyle.Top, Height = this.Font.Height * 4 };
            this.Map.PartitionClicked += (s, partition) => {
                foreach (ListViewItem item in this.PartitionList.Items) { item.Selected = (item.Tag == partition); }
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            split.Panel1.Controls.Add(this.DiskList);
            split.Panel2.Controls.Add(this.PartitionList);
            split.Panel2.Controls.Add(this.Map);
            split.Panel2.Padding = new Padding(0, 4, 0, 0);

            this.StatusText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            this.Status = new StatusStrip { SizingGrip = true };
            this.Status.Items.Add(this.StatusText);

            this.Controls.Add(split);
            this.Controls.Add(this.Tools);
            this.Controls.Add(banner);
            this.Controls.Add(this.Status);

            this.Load += (s, e) => { split.SplitterDistance = this.ClientSize.Height / 3; this.RefreshAll(); };
            this.FormClosing += (s, e) => {
                if (this.IsBusy) {
                    e.Cancel = true;
                    Medo.MessageBox.ShowWarning(this, "A disk operation is still running. Wait for it to finish before closing.");
                }
            };
            this.KeyPreview = true;
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) { this.RefreshAll(); e.Handled = true; } };
        }

        private static ListView CreateList(params string[] columns) {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            foreach (var column in columns) { list.Columns.Add(column); }
            return list;
        }

        private DiskInfo SelectedDisk => (this.DiskList.SelectedItems.Count > 0) ? this.DiskList.SelectedItems[0].Tag as DiskInfo : null;
        private PartitionInfo SelectedPartition => (this.PartitionList.SelectedItems.Count > 0) ? this.PartitionList.SelectedItems[0].Tag as PartitionInfo : null;


        #region Loading

        private void RefreshAll() {
            var selectedNumber = this.SelectedDisk?.Number ?? this.InitialDiskNumber;
            try {
                this.Cursor = Cursors.WaitCursor;
                this.Disks = StorageManager.GetDisks();
            } catch (Exception ex) {
                this.SetStatus("Cannot read disks: " + ex.Message, true);
                return;
            } finally {
                this.Cursor = Cursors.Default;
            }

            this.DiskList.BeginUpdate();
            this.DiskList.Items.Clear();
            foreach (var disk in this.Disks) {
                if (!disk.IsVirtual && !this.ShowPhysicalButton.Checked) { continue; }
                var status = new List<string>();
                if (disk.IsSystem) { status.Add("System"); }
                if (disk.IsBoot) { status.Add("Boot"); }
                status.Add(disk.IsOffline ? "Offline" : "Online");
                if (disk.IsProtected) { status.Add("Protected: " + disk.ProtectedReason); }
                if (disk.IsReadOnly) { status.Add("Read-only"); }
                var name = (disk.IsVirtual && !string.IsNullOrEmpty(disk.Location)) ? disk.Location : disk.FriendlyName;
                var item = new ListViewItem(new[] {
                    disk.Number.ToString(CultureInfo.CurrentCulture),
                    disk.IsVirtual ? "Virtual" : "Physical",
                    name,
                    Ui.FormatSize(disk.Size),
                    (disk.PartitionStyle == PartitionStyle.Raw) ? "" : Ui.FormatSize(disk.FreeSize),
                    (disk.PartitionStyle == PartitionStyle.Raw) ? "Not initialized" : disk.PartitionStyle.ToString().ToUpperInvariant(),
                    string.Join(", ", status),
                }) { Tag = disk };
                if (disk.IsProtected) { item.ForeColor = Ui.Muted; }
                if (disk.IsVirtual) { item.ForeColor = Ui.Accent; }
                this.DiskList.Items.Add(item);
                if (disk.Number == selectedNumber) { item.Selected = true; }
            }
            AutoSizeColumns(this.DiskList);
            this.DiskList.EndUpdate();
            if ((this.DiskList.SelectedItems.Count == 0) && (this.DiskList.Items.Count > 0)) {
                var firstVirtual = this.DiskList.Items.Cast<ListViewItem>().FirstOrDefault(i => ((DiskInfo)i.Tag).IsVirtual);
                (firstVirtual ?? this.DiskList.Items[0]).Selected = true;
            }
            this.LoadPartitions();
            this.SetStatus(string.Format(CultureInfo.CurrentCulture, "{0} disk(s), {1} virtual.", this.Disks.Count, this.Disks.Count(d => d.IsVirtual)), false);
        }

        private void LoadPartitions() {
            var disk = this.SelectedDisk;
            this.PartitionList.BeginUpdate();
            this.PartitionList.Items.Clear();
            this.Partitions = new List<PartitionInfo>();
            try {
                if ((disk != null) && (disk.PartitionStyle != PartitionStyle.Raw)) {
                    this.Partitions = StorageManager.GetPartitions(disk.Number);
                    foreach (var p in this.Partitions) {
                        var paths = new List<string>();
                        if (p.DriveLetter.HasValue) { paths.Add(p.DriveLetter.Value + ":"); }
                        paths.AddRange(p.MountFolders);
                        var status = new List<string>();
                        if (p.IsSystem) { status.Add("System"); }
                        if (p.IsBoot) { status.Add("Boot"); }
                        if (p.IsActive) { status.Add("Active"); }
                        if (p.IsHidden) { status.Add("Hidden"); }
                        if (p.IsReadOnly) { status.Add("Read-only"); }
                        if (p.IsOffline) { status.Add("Offline"); }
                        if (!string.IsNullOrEmpty(p.HealthStatus)) { status.Add(p.HealthStatus); }
                        if (p.IsProtected) { status.Add("Protected: " + p.ProtectedReason); }
                        this.PartitionList.Items.Add(new ListViewItem(new[] {
                            p.PartitionNumber.ToString(CultureInfo.CurrentCulture),
                            string.Join("  ", paths),
                            p.Label ?? "",
                            p.FileSystem ?? "",
                            Ui.FormatSize(p.Size),
                            Ui.FormatSize(p.VolumeFree),
                            p.Type,
                            string.Join(", ", status),
                        }) { Tag = p });
                    }
                }
            } catch (Exception ex) {
                this.SetStatus("Cannot read partitions: " + ex.Message, true);
            }
            AutoSizeColumns(this.PartitionList);
            this.PartitionList.EndUpdate();
            this.Map.Disk = disk;
            this.Map.Partitions = this.Partitions;
            this.Map.SelectedPartition = null;
            this.Map.Invalidate();
        }

        private static void AutoSizeColumns(ListView list) {
            foreach (ColumnHeader column in list.Columns) {
                column.Width = -2;
                if (column.Width > list.Font.Height * 30) { column.Width = list.Font.Height * 30; }
            }
        }

        private void SetStatus(string text, bool isError) {
            this.StatusText.Text = text;
            this.StatusText.ForeColor = isError ? Ui.Danger : SystemColors.ControlText;
        }

        #endregion


        #region Menus

        private void BuildDiskMenu(ToolStripItemCollection items) {
            items.Clear();
            var disk = this.SelectedDisk;
            var editable = this.CanModify(disk, out var reason);
            ToolStripMenuItem Add(string text, EventHandler handler, bool enabled = true) {
                var item = new ToolStripMenuItem(text, null, handler) { Enabled = editable && enabled };
                if (!editable && (reason != null)) { item.ToolTipText = reason; }
                items.Add(item);
                return item;
            }
            var raw = (disk != null) && (disk.PartitionStyle == PartitionStyle.Raw);
            Add("Initialize as GPT…", (s, e) => this.Initialize(PartitionStyle.Gpt), raw);
            Add("Initialize as MBR…", (s, e) => this.Initialize(PartitionStyle.Mbr), raw);
            var create = new ToolStripMenuItem("Create partition…", null, (s, e) => this.CreatePartition()) { Enabled = this.CanModifyPartitions(disk) && !raw && (disk?.FreeSize > 1024 * 1024) };
            items.Add(create);
            items.Add(new ToolStripSeparator());
            Add(disk?.IsOffline == true ? "Bring online" : "Take offline", (s, e) => this.Run("Online/offline", d => StorageManager.SetDiskOnline(d, d.IsOffline), disk.IsOffline ? "Online" : "Offline"));
            Add(disk?.IsReadOnly == true ? "Clear read-only" : "Set read-only", (s, e) => this.Run("Read-only", d => StorageManager.SetDiskReadOnly(d, !d.IsReadOnly), "ReadOnly", (!disk.IsReadOnly).ToString().ToLowerInvariant()));
            Add("Convert to GPT…", (s, e) => this.ConvertStyle(PartitionStyle.Gpt), !raw && disk?.PartitionStyle == PartitionStyle.Mbr);
            Add("Convert to MBR…", (s, e) => this.ConvertStyle(PartitionStyle.Mbr), !raw && disk?.PartitionStyle == PartitionStyle.Gpt);
            items.Add(new ToolStripSeparator());
            var clean = Add("Clean (erase all partitions)…", (s, e) => this.Clean(), !raw);
            clean.ForeColor = Ui.Danger;
        }

        private void BuildPartitionMenu(ToolStripItemCollection items) {
            items.Clear();
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            var editable = this.CanModifyPartitions(disk) && (partition != null) && !partition.IsProtected;
            var hasVolume = !string.IsNullOrEmpty(partition?.VolumeObjectPath);
            ToolStripMenuItem Add(string text, EventHandler handler, bool enabled = true, bool needsEdit = true) {
                var item = new ToolStripMenuItem(text, null, handler) { Enabled = (partition != null) && enabled && (!needsEdit || editable) };
                items.Add(item);
                return item;
            }
            Add("Open in Explorer", (s, e) => this.OpenPartition(), partition?.DriveLetter != null || (partition?.MountFolders.Any() ?? false), needsEdit: false);
            items.Add(new ToolStripSeparator());
            Add("Assign drive letter", (s, e) => this.RunPartition("Assign letter", (d, p) => StorageManager.AddAccessPath(d, p, null), "AddLetter"), partition?.DriveLetter == null);
            Add("Mount in empty folder…", (s, e) => this.AddMountFolder());
            var remove = new ToolStripMenuItem("Remove drive letter / mount point") { Enabled = editable };
            if (partition != null) {
                foreach (var path in partition.AccessPaths.Where(x => !x.StartsWith(@"\\?\", StringComparison.Ordinal))) {
                    var accessPath = path;
                    remove.DropDownItems.Add(path, null, (s, e) => this.RunPartition("Remove access path", (d, p) => StorageManager.RemoveAccessPath(d, p, accessPath), "RemovePath", accessPath));
                }
            }
            remove.Enabled = editable && remove.DropDownItems.Count > 0;
            items.Add(remove);
            items.Add(new ToolStripSeparator());
            Add("Extend / shrink…", (s, e) => this.ResizePartition());
            Add("Format…", (s, e) => this.Format());
            Add(partition?.IsActive == true ? "Mark inactive" : "Mark active (MBR boot)", (s, e) => this.RunPartition("Active flag", (d, p) => StorageManager.SetPartitionAttributes(d, p, isActive: !p.IsActive), "Active", (!partition.IsActive).ToString().ToLowerInvariant()), disk?.PartitionStyle == PartitionStyle.Mbr);
            items.Add(new ToolStripSeparator());
            Add("Retrim free space (before compact)", (s, e) => this.RunPartition("Retrim", (d, p) => StorageManager.RetrimVolume(p), "Retrim"), hasVolume, needsEdit: false);
            Add("Check file system (scan)", (s, e) => this.RunPartition("Scan", (d, p) => this.SetStatusFromThread(StorageManager.RepairVolume(p, false)), "Scan"), hasVolume, needsEdit: false);
            Add("Repair file system (spot fix)", (s, e) => this.RunPartition("Spot fix", (d, p) => this.SetStatusFromThread(StorageManager.RepairVolume(p, true)), "SpotFix"), hasVolume);
            items.Add(new ToolStripSeparator());
            var delete = Add("Delete partition…", (s, e) => this.DeletePartition());
            delete.ForeColor = Ui.Danger;
        }

        private bool CanModify(DiskInfo disk, out string reason) {
            reason = null;
            if (this.IsBusy) { reason = "Another operation is running."; return false; }
            if (disk == null) { reason = "Select a disk."; return false; }
            if (disk.IsProtected) { reason = "Protected: " + disk.ProtectedReason + "."; return false; }
            if (!disk.IsVirtual && !this.UnlockPhysicalButton.Checked) { reason = "Enable \"Allow changes to physical disks\" to modify this disk."; return false; }
            return true;
        }

        /// <summary>
        /// Partitions may be changed on disks that are only protected because of another partition (e.g. one holding image files).
        /// </summary>
        private bool CanModifyPartitions(DiskInfo disk) {
            if (this.IsBusy || (disk == null) || disk.IsSystemDisk) { return false; }
            return disk.IsVirtual || this.UnlockPhysicalButton.Checked;
        }

        #endregion


        #region Operations

        private void Initialize(PartitionStyle style) {
            var disk = this.SelectedDisk;
            var op = (style == PartitionStyle.Gpt) ? "InitializeGpt" : "InitializeMbr";
            this.Run("Initialize", d => StorageManager.InitializeDisk(d, style), op);
        }

        private void ConvertStyle(PartitionStyle style) {
            var disk = this.SelectedDisk;
            if (this.Partitions.Count > 0) {
                Medo.MessageBox.ShowWarning(this, "Partition style can only be converted on an empty disk. Delete all partitions (or Clean the disk) first.");
                return;
            }
            this.Run("Convert partition style", d => StorageManager.ConvertStyle(d, style), "ConvertStyle", style.ToString().ToUpperInvariant());
        }

        private void Clean() {
            var disk = this.SelectedDisk;
            var label = disk.ToString();
            using (var frm = new PromptForm("Clean disk", string.Format(CultureInfo.CurrentCulture, "All partitions and data on {0} ({1}, {2}) will be removed. This cannot be undone.", label, disk.IsVirtual ? disk.Location : disk.FriendlyName, Ui.FormatSize(disk.Size)), StorageManager.Describe("Clean", disk), destructive: true).RequireTyping(label)) {
                if (frm.ShowDialog(this) != DialogResult.OK) { return; }
            }
            this.Execute("Clean", () => StorageManager.CleanDisk(disk));
        }

        private void CreatePartition() {
            var disk = this.SelectedDisk;
            using (var frm = new PromptForm("Create partition", string.Format(CultureInfo.CurrentCulture, "Create a partition on {0}. Unallocated: {1}. Leave size empty to use all available space.", disk, Ui.FormatSize(disk.FreeSize)))
                .AddText("size", "Size (e.g. 20 GB):")
                .AddChoice("fs", "File system:", new[] { "NTFS", "ReFS", "exFAT", "FAT32", "(none)" }, "NTFS")
                .AddText("label", "Volume label:", "New Volume")
                .AddCheck("letter", "Assign drive letter", true)) {
                if (frm.ShowDialog(this) != DialogResult.OK) { return; }
                long size = 0;
                if (!string.IsNullOrEmpty(frm.GetText("size")) && !Ui.TryParseSize(frm.GetText("size"), out size)) {
                    Medo.MessageBox.ShowWarning(this, "Size is not valid.");
                    return;
                }
                var fs = frm.GetText("fs");
                if (fs == "(none)") { fs = null; }
                var label = frm.GetText("label");
                var letter = frm.GetCheck("letter");
                var args = ((size > 0) ? "-Size " + size.ToString(CultureInfo.InvariantCulture) : "-UseMaximumSize") + (letter ? " -AssignDriveLetter" : "") + ((fs != null) ? " | Format-Volume -FileSystem " + fs + " -NewFileSystemLabel '" + label + "'" : "");
                if (!this.Confirm("Create partition", "Create the partition now?", StorageManager.Describe("CreatePartition", disk, null, args))) { return; }
                this.Execute("Create partition", () => StorageManager.CreatePartition(disk, size, letter, fs, label));
            }
        }

        private void DeletePartition() {
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            var label = "Partition " + partition.PartitionNumber.ToString(CultureInfo.InvariantCulture);
            var used = (partition.VolumeSize.HasValue && partition.VolumeFree.HasValue) ? Ui.FormatSize(partition.VolumeSize.Value - partition.VolumeFree.Value) + " of data" : "all of its data";
            using (var frm = new PromptForm("Delete partition", string.Format(CultureInfo.CurrentCulture, "{0} ({1}) on {2} ({3}, {4}) and {5} will be deleted. This cannot be undone.", label, partition.DisplayName, disk, partition.Label ?? partition.Type, Ui.FormatSize(partition.Size), used), StorageManager.Describe("Delete", disk, partition), destructive: true).RequireTyping(label)) {
                if (frm.ShowDialog(this) != DialogResult.OK) { return; }
            }
            this.Execute("Delete partition", () => StorageManager.DeletePartition(disk, partition));
        }

        private void Format() {
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            using (var frm = new PromptForm("Format", string.Format(CultureInfo.CurrentCulture, "Format partition {0} on {1}. All data on it will be erased.", partition.PartitionNumber, disk), null, destructive: true)
                .AddChoice("fs", "File system:", new[] { "NTFS", "ReFS", "exFAT", "FAT32" }, partition.FileSystem ?? "NTFS")
                .AddText("label", "Volume label:", partition.Label ?? "")
                .AddCheck("quick", "Quick format", true)
                .RequireTyping("Format")) {
                if (frm.ShowDialog(this) != DialogResult.OK) { return; }
                var fs = frm.GetText("fs");
                var label = frm.GetText("label");
                var quick = frm.GetCheck("quick");
                this.SetStatus(StorageManager.Describe("Format", disk, partition, "-FileSystem " + fs + " -NewFileSystemLabel '" + label + "'" + (quick ? "" : " -Full")), false);
                this.Execute("Format", () => StorageManager.FormatPartition(disk, partition, fs, label, quick));
            }
        }

        private void ResizePartition() {
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            (long Min, long Max) range;
            try {
                range = StorageManager.GetSupportedSize(partition);
            } catch (Exception ex) {
                this.ShowError("Resize", ex);
                return;
            }
            using (var frm = new PromptForm("Extend / shrink", string.Format(CultureInfo.CurrentCulture, "Current size: {0}\nMinimum: {1}\nMaximum: {2}\n\nTo shrink a virtual disk file afterwards, use Maintenance → Resize → Shrink to minimum.", Ui.FormatSize(partition.Size), Ui.FormatSize(range.Min), Ui.FormatSize(range.Max)))
                .AddChoice("preset", "Size:", new[] { "Maximum (extend)", "Minimum (shrink)", "Custom" }, "Maximum (extend)")
                .AddText("size", "Custom size:")) {
                if (frm.ShowDialog(this) != DialogResult.OK) { return; }
                long size;
                switch (frm.GetText("preset")) {
                    case "Minimum (shrink)": size = range.Min; break;
                    case "Custom":
                        if (!Ui.TryParseSize(frm.GetText("size"), out size)) { Medo.MessageBox.ShowWarning(this, "Size is not valid."); return; }
                        break;
                    default: size = range.Max; break;
                }
                if ((size < range.Min) || (size > range.Max)) {
                    Medo.MessageBox.ShowWarning(this, string.Format(CultureInfo.CurrentCulture, "Size must be between {0} and {1}.", Ui.FormatSize(range.Min), Ui.FormatSize(range.Max)));
                    return;
                }
                if (!this.Confirm("Resize partition", string.Format(CultureInfo.CurrentCulture, "Resize partition {0} to {1}?", partition.PartitionNumber, Ui.FormatSize(size)), StorageManager.Describe("Resize", disk, partition, size.ToString(CultureInfo.InvariantCulture)))) { return; }
                this.Execute("Resize", () => StorageManager.ResizePartition(disk, partition, size));
            }
        }

        private void AddMountFolder() {
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            using (var dialog = new FolderBrowserDialog { Description = "Select an empty folder on an NTFS volume", UseDescriptionForTitle = true, ShowNewFolderButton = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                var path = dialog.SelectedPath.TrimEnd('\\') + "\\";
                if (System.IO.Directory.GetFileSystemEntries(path).Length > 0) {
                    Medo.MessageBox.ShowWarning(this, "The folder must be empty.");
                    return;
                }
                this.RunPartition("Mount in folder", (d, p) => StorageManager.AddAccessPath(d, p, path), "AddPath", path);
            }
        }

        private void OpenPartition() {
            var p = this.SelectedPartition;
            var path = p.DriveLetter.HasValue ? p.DriveLetter.Value + @":\" : p.MountFolders.FirstOrDefault();
            if (path != null) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        }

        private void Run(string title, Action<DiskInfo> action, string operation, string argument = null) {
            var disk = this.SelectedDisk;
            if (disk == null) { return; }
            if (!this.Confirm(title, string.Format(CultureInfo.CurrentCulture, "{0}: {1}?", title, disk), StorageManager.Describe(operation, disk, null, argument))) { return; }
            this.Execute(title, () => action(disk));
        }

        private void RunPartition(string title, Action<DiskInfo, PartitionInfo> action, string operation, string argument = null) {
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            if ((disk == null) || (partition == null)) { return; }
            if (!this.Confirm(title, string.Format(CultureInfo.CurrentCulture, "{0}: partition {1} on {2}?", title, partition.PartitionNumber, disk), StorageManager.Describe(operation, disk, partition, argument))) { return; }
            this.Execute(title, () => action(disk, partition));
        }

        private bool Confirm(string title, string message, string command) {
            var disk = this.SelectedDisk;
            var physical = (disk != null) && !disk.IsVirtual;
            if (physical) { message = "⚠ PHYSICAL DISK: " + (disk.FriendlyName ?? "") + " " + (disk.SerialNumber ?? "") + "\n\n" + message; }
            using (var frm = new PromptForm(title, message, command, destructive: physical)) {
                if (physical) { frm.RequireTyping(disk.ToString()); }
                return frm.ShowDialog(this) == DialogResult.OK;
            }
        }

        private async void Execute(string title, Action action) {
            if (this.IsBusy) { return; }
            if (!Ui.IsElevated) {
                if (Medo.MessageBox.ShowQuestion(this, "Changing disks requires administrator rights.\n\nRestart Disk Manager as administrator?", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    this.Elevate_Click(null, null);
                }
                return;
            }
            var disk = this.SelectedDisk;
            var partition = this.SelectedPartition;
            var target = (disk == null) ? "" : string.Format(CultureInfo.InvariantCulture, "Disk {0} [{1}]{2}", disk.Number, disk.IsVirtual ? disk.Location : (disk.FriendlyName + " " + disk.SerialNumber), (partition != null) ? " partition " + partition.PartitionNumber.ToString(CultureInfo.InvariantCulture) : "");
            this.IsBusy = true;
            this.UseWaitCursor = true;
            this.Tools.Enabled = false;
            this.DiskList.Enabled = false;
            this.PartitionList.Enabled = false;
            this.SetStatus(title + "…", false);
            try {
                VhdAttachCommon.AuditLog.Started("Disk Manager: " + title, target);
                await Task.Run(action);
                VhdAttachCommon.AuditLog.Succeeded("Disk Manager: " + title, target);
                if (this.StatusText.Text == title + "…") { this.SetStatus(title + " completed.", false); } //keep messages set by the action (e.g. scan results)
            } catch (Exception ex) {
                VhdAttachCommon.AuditLog.Failed("Disk Manager: " + title, target, ex);
                this.ShowError(title, ex);
            } finally {
                this.IsBusy = false;
                this.UseWaitCursor = false;
                this.Tools.Enabled = true;
                this.DiskList.Enabled = true;
                this.PartitionList.Enabled = true;
                this.RefreshAll();
            }
        }

        private void SetStatusFromThread(string text) {
            this.BeginInvoke((Action)(() => this.SetStatus(text, false)));
        }

        private void ShowError(string title, Exception ex) {
            this.SetStatus(title + " failed: " + ex.Message, true);
            Medo.MessageBox.ShowError(this, title + " failed.\n\n" + ex.Message);
        }

        private void UnlockPhysical_CheckedChanged(object sender, EventArgs e) {
            if (this.UnlockPhysicalButton.Checked) {
                using (var frm = new PromptForm("Allow changes to physical disks", "Physical disks can hold your data and other operating systems. Mistakes here can cause permanent data loss. System and boot disks stay protected.", null, destructive: true).RequireTyping("I understand")) {
                    if (frm.ShowDialog(this) != DialogResult.OK) { this.UnlockPhysicalButton.Checked = false; }
                }
            }
        }

        private void Elevate_Click(object sender, EventArgs e) {
            var args = "/DiskManager" + ((this.SelectedDisk != null) ? " /Disk=" + this.SelectedDisk.Number.ToString(CultureInfo.InvariantCulture) : "");
            if (Ui.RunElevated(args, wait: false)) { this.Close(); }
        }

        #endregion


        /// <summary>
        /// Proportional bar showing partitions and unallocated space, like Disk Management.
        /// </summary>
        private sealed class DiskMap : Control {

            public DiskMap() {
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            public DiskInfo Disk { get; set; }
            public IList<PartitionInfo> Partitions { get; set; } = new List<PartitionInfo>();
            public PartitionInfo SelectedPartition { get; set; }
            public event EventHandler<PartitionInfo> PartitionClicked;
            private readonly List<(Rectangle Bounds, PartitionInfo Partition)> Hits = new List<(Rectangle, PartitionInfo)>();

            protected override void OnMouseClick(MouseEventArgs e) {
                base.OnMouseClick(e);
                foreach (var hit in this.Hits) {
                    if (hit.Bounds.Contains(e.Location)) { this.PartitionClicked?.Invoke(this, hit.Partition); return; }
                }
            }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(this.BackColor);
                this.Hits.Clear();
                var bounds = new Rectangle(2, 2, this.Width - 5, this.Height - 9);
                if ((this.Disk == null) || (this.Disk.Size <= 0)) { return; }
                if (this.Disk.PartitionStyle == PartitionStyle.Raw) {
                    using (var brush = new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.BackwardDiagonal, Color.Silver, Color.White)) { g.FillRectangle(brush, bounds); }
                    TextRenderer.DrawText(g, "Not initialized - use Disk → Initialize", this.Font, bounds, Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    g.DrawRectangle(Pens.Gray, bounds);
                    return;
                }
                using (var brush = new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.BackwardDiagonal, Color.Gainsboro, Color.White)) { g.FillRectangle(brush, bounds); }
                //proportional layout, but every partition stays wide enough to see and click (MSR/recovery are tiny)
                var minWidth = Math.Min(this.Font.Height * 4, bounds.Width / Math.Max(1, this.Partitions.Count));
                var lefts = new int[this.Partitions.Count];
                var widths = new int[this.Partitions.Count];
                var cursor = bounds.Left;
                for (int i = 0; i < this.Partitions.Count; i++) {
                    var p = this.Partitions[i];
                    lefts[i] = Math.Max(cursor, bounds.Left + (int)(bounds.Width * (double)p.Offset / this.Disk.Size));
                    widths[i] = Math.Max(minWidth, (int)(bounds.Width * (double)p.Size / this.Disk.Size));
                    cursor = lefts[i] + widths[i];
                }
                var limit = bounds.Right;
                for (int i = this.Partitions.Count - 1; i >= 0; i--) { //pull back anything pushed past the end
                    if (lefts[i] + widths[i] > limit) {
                        var shrinkable = Math.Max(minWidth, limit - lefts[i]);
                        widths[i] = Math.Min(widths[i], shrinkable);
                        lefts[i] = limit - widths[i];
                    }
                    limit = lefts[i];
                }
                for (int i = 0; i < this.Partitions.Count; i++) {
                    var p = this.Partitions[i];
                    var rect = new Rectangle(lefts[i], bounds.Top, widths[i], bounds.Height);
                    var selected = (p == this.SelectedPartition);
                    var color = (p.IsSystem || p.IsBoot) ? Color.FromArgb(0x9A, 0xA4, 0xB2) : (string.IsNullOrEmpty(p.FileSystem) ? Ui.Teal : Ui.Accent);
                    using (var fill = new SolidBrush(selected ? Ui.AccentSoft : Color.White))
                    using (var header = new SolidBrush(color)) {
                        g.FillRectangle(fill, rect);
                        g.FillRectangle(header, rect.X, rect.Y, rect.Width, Math.Max(4, this.Font.Height / 3));
                    }
                    g.DrawRectangle(selected ? new Pen(Ui.Accent, 2) : Pens.Silver, rect);
                    var text = (p.DriveLetter.HasValue ? p.DriveLetter.Value + ": " : "") + (p.Label ?? p.Type) + "\n" + Ui.FormatSize(p.Size) + " " + (p.FileSystem ?? "");
                    var textRect = Rectangle.Inflate(rect, -3, -3);
                    textRect.Y += this.Font.Height / 3;
                    TextRenderer.DrawText(g, text, this.Font, textRect, SystemColors.ControlText, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
                    this.Hits.Add((rect, p));
                }
            }
        }

    }
}
