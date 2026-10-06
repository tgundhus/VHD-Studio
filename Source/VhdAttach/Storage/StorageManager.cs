using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace VhdAttach.Storage {

    internal enum PartitionStyle { Unknown = 0, Mbr = 1, Gpt = 2, Raw = 3 }

    internal sealed class DiskInfo {
        public int Number { get; set; }
        public string FriendlyName { get; set; }
        public string Location { get; set; }
        public long Size { get; set; }
        public long AllocatedSize { get; set; }
        public PartitionStyle PartitionStyle { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsSystem { get; set; }
        public bool IsBoot { get; set; }
        public bool IsOffline { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsClustered { get; set; }
        public string SerialNumber { get; set; }
        public string ObjectPath { get; set; }

        /// <summary>
        /// Why the disk as a whole must not be changed (system/boot/cluster disk, or a partition on it is protected); null if it may.
        /// </summary>
        public string ProtectedReason { get; set; }
        public bool IsProtected => this.ProtectedReason != null;

        /// <summary>
        /// Hard protection that also blocks changes to individual partitions.
        /// </summary>
        public bool IsSystemDisk => this.IsSystem || this.IsBoot || this.IsClustered;
        public long FreeSize => Math.Max(0, this.Size - this.AllocatedSize);
        public override string ToString() => string.Format(CultureInfo.CurrentCulture, "Disk {0}", this.Number);
    }

    internal sealed class PartitionInfo {
        public int DiskNumber { get; set; }
        public int PartitionNumber { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public char? DriveLetter { get; set; }
        public IList<string> AccessPaths { get; set; } = new List<string>();
        public string Type { get; set; }
        public bool IsActive { get; set; }
        public bool IsHidden { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsOffline { get; set; }
        public bool IsSystem { get; set; }
        public bool IsBoot { get; set; }
        public string ObjectPath { get; set; }

        public string FileSystem { get; set; }
        public string Label { get; set; }
        public long? VolumeSize { get; set; }
        public long? VolumeFree { get; set; }
        public string HealthStatus { get; set; }
        public string VolumeObjectPath { get; set; }

        /// <summary>
        /// Why the partition must not be changed (Windows, page file, images of attached virtual disks); null if it may.
        /// </summary>
        public string ProtectedReason { get; set; }
        public bool IsProtected => this.ProtectedReason != null;
        public string VolumeGuidPath => this.AccessPaths.FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
        public string DisplayName => this.DriveLetter.HasValue ? this.DriveLetter.Value + ":" : (this.MountFolders.FirstOrDefault() ?? ("partition " + this.PartitionNumber.ToString(CultureInfo.CurrentCulture)));

        public IEnumerable<string> MountFolders => this.AccessPaths.Where(p => !p.StartsWith(@"\\?\", StringComparison.Ordinal) && !((p.Length == 3) && (p[1] == ':')));
        public override string ToString() => string.Format(CultureInfo.CurrentCulture, "Partition {0}", this.PartitionNumber);
    }


    /// <summary>
    /// DiskPart-like operations through the Windows Storage Management API (root\Microsoft\Windows\Storage).
    /// Reading works for every user; changes require administrator rights.
    /// </summary>
    internal static class StorageManager {

        private const string Namespace = @"\\.\root\Microsoft\Windows\Storage";
        private const int BusTypeFileBackedVirtual = 15;

        private static ManagementScope GetScope() {
            var scope = new ManagementScope(Namespace);
            scope.Connect();
            return scope;
        }

        #region Query

        public static IList<DiskInfo> GetDisks() {
            var list = new List<DiskInfo>();
            using (var searcher = new ManagementObjectSearcher(GetScope(), new ObjectQuery("SELECT * FROM MSFT_Disk"))) {
                foreach (ManagementObject mo in searcher.Get()) {
                    using (mo) {
                        list.Add(new DiskInfo {
                            Number = Convert.ToInt32(mo["Number"], CultureInfo.InvariantCulture),
                            FriendlyName = (mo["FriendlyName"] as string)?.Trim(),
                            Location = mo["Location"] as string,
                            Size = ToLong(mo["Size"]),
                            AllocatedSize = ToLong(mo["AllocatedSize"]),
                            PartitionStyle = (PartitionStyle)Convert.ToInt32(mo["PartitionStyle"] ?? 0, CultureInfo.InvariantCulture),
                            IsVirtual = Convert.ToInt32(mo["BusType"] ?? 0, CultureInfo.InvariantCulture) == BusTypeFileBackedVirtual,
                            IsSystem = (bool)(mo["IsSystem"] ?? false),
                            IsBoot = (bool)(mo["IsBoot"] ?? false),
                            IsOffline = (bool)(mo["IsOffline"] ?? false),
                            IsReadOnly = (bool)(mo["IsReadOnly"] ?? false),
                            IsClustered = (bool)(mo["IsClustered"] ?? false),
                            SerialNumber = (mo["SerialNumber"] as string)?.Trim(),
                            ObjectPath = mo.Path.Path,
                        });
                    }
                }
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            foreach (var disk in list) {
                if (disk.IsSystem) { disk.ProtectedReason = "it holds the Windows system partition"; }
                else if (disk.IsBoot) { disk.ProtectedReason = "it holds the running Windows installation"; }
                else if (disk.IsClustered) { disk.ProtectedReason = "it is a cluster disk"; }
                else if (disk.PartitionStyle != PartitionStyle.Raw) {
                    try {
                        var partition = GetPartitions(disk.Number, list).FirstOrDefault(p => p.IsProtected);
                        if (partition != null) { disk.ProtectedReason = partition.DisplayName + " is protected: " + partition.ProtectedReason; }
                    } catch (ManagementException) {
                        disk.ProtectedReason = "its partitions could not be read";
                    }
                }
            }
            return list;
        }

        public static IList<PartitionInfo> GetPartitions(int diskNumber) {
            return GetPartitions(diskNumber, null);
        }

        private static IList<PartitionInfo> GetPartitions(int diskNumber, IList<DiskInfo> knownDisks) {
            var list = new List<PartitionInfo>();
            var scope = GetScope();
            var query = new ObjectQuery("SELECT * FROM MSFT_Partition WHERE DiskNumber = " + diskNumber.ToString(CultureInfo.InvariantCulture));
            using (var searcher = new ManagementObjectSearcher(scope, query)) {
                foreach (ManagementObject mo in searcher.Get()) {
                    using (mo) {
                        var letter = Convert.ToChar(mo["DriveLetter"] ?? '\0', CultureInfo.InvariantCulture);
                        var partition = new PartitionInfo {
                            DiskNumber = diskNumber,
                            PartitionNumber = Convert.ToInt32(mo["PartitionNumber"], CultureInfo.InvariantCulture),
                            Offset = ToLong(mo["Offset"]),
                            Size = ToLong(mo["Size"]),
                            DriveLetter = (letter == '\0') ? (char?)null : letter,
                            AccessPaths = ((mo["AccessPaths"] as string[]) ?? new string[0]).ToList(),
                            Type = GetPartitionType(mo),
                            IsActive = (bool)(mo["IsActive"] ?? false),
                            IsHidden = (bool)(mo["IsHidden"] ?? false),
                            IsReadOnly = (bool)(mo["IsReadOnly"] ?? false),
                            IsOffline = (bool)(mo["IsOffline"] ?? false),
                            IsSystem = (bool)(mo["IsSystem"] ?? false),
                            IsBoot = (bool)(mo["IsBoot"] ?? false),
                            ObjectPath = mo.Path.Path,
                        };
                        using (var volumes = new ManagementObjectSearcher(scope, new ObjectQuery("ASSOCIATORS OF {" + mo.Path.RelativePath + "} WHERE AssocClass = MSFT_PartitionToVolume"))) {
                            foreach (ManagementObject volume in volumes.Get()) {
                                using (volume) {
                                    partition.FileSystem = volume["FileSystem"] as string;
                                    partition.Label = volume["FileSystemLabel"] as string;
                                    partition.VolumeSize = ToLong(volume["Size"]);
                                    partition.VolumeFree = ToLong(volume["SizeRemaining"]);
                                    partition.HealthStatus = HealthToText(volume["HealthStatus"]);
                                    partition.VolumeObjectPath = volume.Path.Path;
                                }
                                break;
                            }
                        }
                        list.Add(partition);
                    }
                }
            }
            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            //protect partitions Windows depends on, and those holding image files of attached virtual disks
            var pageFiles = GetPageFiles();
            var images = GetAttachedImageFiles(knownDisks);
            foreach (var p in list) {
                var roots = p.AccessPaths.Select(a => a.EndsWith("\\", StringComparison.Ordinal) ? a : a + "\\").ToList();
                if (p.IsSystem) { p.ProtectedReason = "Windows system (EFI) partition"; }
                else if (p.IsBoot) { p.ProtectedReason = "running Windows installation"; }
                else if (p.Type.StartsWith("LDM", StringComparison.Ordinal) || (p.Type == "Storage Spaces") || (p.Type == "MBR 0x42")) { p.ProtectedReason = "member of a dynamic disk or storage pool (its volume can span other disks); use Disk Management or Storage Spaces"; }
                else if (pageFiles.Any(f => roots.Any(r => f.StartsWith(r, StringComparison.OrdinalIgnoreCase)))) { p.ProtectedReason = "holds a page file"; }
                else {
                    var image = images.FirstOrDefault(i => roots.Any(r => i.Value.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
                    if (image.Value != null) { p.ProtectedReason = string.Format(CultureInfo.CurrentCulture, "holds the image file of attached virtual disk {0}", image.Key); }
                }
            }
            return list;
        }

        private static IList<string> GetPageFiles() {
            var list = new List<string>();
            try {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name FROM Win32_PageFileUsage")) {
                    foreach (ManagementObject mo in searcher.Get()) {
                        using (mo) { if (mo["Name"] is string name) { list.Add(name); } }
                    }
                }
            } catch (ManagementException) { }
            return list;
        }

        private static IList<KeyValuePair<int, string>> GetAttachedImageFiles(IList<DiskInfo> knownDisks) {
            var disks = knownDisks;
            if (disks == null) {
                disks = new List<DiskInfo>();
                using (var searcher = new ManagementObjectSearcher(GetScope(), new ObjectQuery("SELECT Number, BusType, Location FROM MSFT_Disk"))) {
                    foreach (ManagementObject mo in searcher.Get()) {
                        using (mo) {
                            disks.Add(new DiskInfo { Number = Convert.ToInt32(mo["Number"], CultureInfo.InvariantCulture), IsVirtual = Convert.ToInt32(mo["BusType"] ?? 0, CultureInfo.InvariantCulture) == BusTypeFileBackedVirtual, Location = mo["Location"] as string });
                        }
                    }
                }
            }
            return disks.Where(d => d.IsVirtual && !string.IsNullOrEmpty(d.Location)).Select(d => new KeyValuePair<int, string>(d.Number, d.Location)).ToList();
        }

        /// <summary>
        /// Re-reads the disk right before a change and verifies it is still the one the user confirmed.
        /// </summary>
        public static DiskInfo Revalidate(DiskInfo confirmed) {
            var fresh = GetDisks().FirstOrDefault(d => d.ObjectPath == confirmed.ObjectPath);
            if ((fresh == null) || (fresh.Number != confirmed.Number) || (fresh.Size != confirmed.Size) || (fresh.IsVirtual != confirmed.IsVirtual)
                || !string.Equals(fresh.Location, confirmed.Location, StringComparison.OrdinalIgnoreCase) || !string.Equals(fresh.SerialNumber, confirmed.SerialNumber, StringComparison.Ordinal)) {
                throw new InvalidOperationException("The disk changed since the list was loaded (it may have been detached, replaced or renumbered). Nothing was changed. Refresh and try again.");
            }
            return fresh;
        }

        public static PartitionInfo Revalidate(DiskInfo disk, PartitionInfo confirmed) {
            var fresh = GetPartitions(disk.Number).FirstOrDefault(p => p.ObjectPath == confirmed.ObjectPath);
            if ((fresh == null) || (fresh.PartitionNumber != confirmed.PartitionNumber) || (fresh.Offset != confirmed.Offset) || (fresh.Size != confirmed.Size)) {
                throw new InvalidOperationException("The partition changed since the list was loaded. Nothing was changed. Refresh and try again.");
            }
            return fresh;
        }

        /// <summary>
        /// Refuses when files are open on any of the volumes (they would be cut off and lose unsaved data).
        /// </summary>
        public static void EnsureNotInUse(IEnumerable<PartitionInfo> partitions) {
            var withVolumes = partitions.Where(p => p.VolumeGuidPath != null).ToList();
            var inUse = VhdAttachCommon.VolumeLock.FindInUse(withVolumes.Select(p => p.VolumeGuidPath).ToList(), withVolumes.Select(p => p.DisplayName).ToList());
            if (inUse.Count > 0) { throw new VhdAttachCommon.VolumeInUseException(inUse); }
        }

        /// <summary>
        /// Returns minimum and maximum size the partition can be resized to.
        /// </summary>
        public static (long Min, long Max) GetSupportedSize(PartitionInfo partition) {
            var result = Invoke(partition.ObjectPath, "GetSupportedSize", null);
            return (ToLong(result["SizeMin"]), ToLong(result["SizeMax"]));
        }

        #endregion


        #region Disk operations

        public static void InitializeDisk(DiskInfo disk, PartitionStyle style) {
            disk = Revalidate(disk);
            EnsureModifiable(disk);
            if (disk.PartitionStyle != PartitionStyle.Raw) { throw new InvalidOperationException("The disk is already initialized."); }
            Invoke(disk.ObjectPath, "Initialize", p => p["PartitionStyle"] = (ushort)style);
        }

        public static void ConvertStyle(DiskInfo disk, PartitionStyle style) {
            disk = Revalidate(disk);
            EnsureModifiable(disk);
            if (GetPartitions(disk.Number).Count > 0) { throw new InvalidOperationException("Partition style can only be converted on an empty disk."); }
            Invoke(disk.ObjectPath, "ConvertStyle", p => p["PartitionStyle"] = (ushort)style);
        }

        /// <summary>
        /// Removes all partitions and data (diskpart "clean").
        /// </summary>
        public static void CleanDisk(DiskInfo disk) {
            disk = Revalidate(disk);
            EnsureModifiable(disk);
            EnsureNotInUse(GetPartitions(disk.Number));
            Invoke(disk.ObjectPath, "Clear", p => { p["RemoveData"] = true; p["RemoveOEM"] = true; p["ZeroOutEntireDisk"] = false; });
        }

        public static void SetDiskOnline(DiskInfo disk, bool online) {
            disk = Revalidate(disk);
            EnsureModifiable(disk);
            if (!online) { EnsureNotInUse(GetPartitions(disk.Number)); }
            Invoke(disk.ObjectPath, online ? "Online" : "Offline", null);
        }

        public static void SetDiskReadOnly(DiskInfo disk, bool readOnly) {
            disk = Revalidate(disk);
            EnsureModifiable(disk);
            if (readOnly) { EnsureNotInUse(GetPartitions(disk.Number)); }
            Invoke(disk.ObjectPath, "SetAttributes", p => p["IsReadOnly"] = readOnly);
        }

        /// <summary>
        /// Creates a partition and, optionally, formats it.
        /// </summary>
        /// <param name="size">Size in bytes or 0 for all available space.</param>
        public static void CreatePartition(DiskInfo disk, long size, bool assignDriveLetter, string fileSystem, string label) {
            disk = Revalidate(disk);
            EnsureModifiable(disk, null, creatingPartition: true);
            if ((size > 0) && (size > disk.FreeSize)) { throw new InvalidOperationException("There is not enough unallocated space for a partition of this size."); }
            var result = Invoke(disk.ObjectPath, "CreatePartition", p => {
                if (size <= 0) { p["UseMaximumSize"] = true; } else { p["Size"] = (ulong)size; }
                p["AssignDriveLetter"] = assignDriveLetter;
            });
            if (!string.IsNullOrEmpty(fileSystem) && (result["CreatedPartition"] is ManagementBaseObject created)) {
                var number = Convert.ToInt32(created["PartitionNumber"], CultureInfo.InvariantCulture);
                var partition = GetPartitions(disk.Number).FirstOrDefault(x => x.PartitionNumber == number);
                if (partition != null) { FormatPartition(disk, partition, fileSystem, label, quick: true); }
            }
        }

        #endregion


        #region Partition operations

        public static void DeletePartition(DiskInfo disk, PartitionInfo partition) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            EnsureNotInUse(new[] { partition });
            Invoke(partition.ObjectPath, "DeleteObject", null);
        }

        public static void ResizePartition(DiskInfo disk, PartitionInfo partition, long newSize) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            var (min, max) = GetSupportedSize(partition);
            if ((newSize < min) || (newSize > max)) { throw new InvalidOperationException("The requested size is outside the supported range; nothing was changed."); }
            Invoke(partition.ObjectPath, "Resize", p => p["Size"] = (ulong)newSize);
        }

        public static void FormatPartition(DiskInfo disk, PartitionInfo partition, string fileSystem, string label, bool quick) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            EnsureNotInUse(new[] { partition });
            var volumePath = partition.VolumeObjectPath ?? GetVolumePath(partition);
            if (volumePath == null) { throw new InvalidOperationException("Partition has no volume that can be formatted."); }
            Invoke(volumePath, "Format", p => {
                p["FileSystem"] = fileSystem;
                p["FileSystemLabel"] = label ?? "";
                p["Full"] = !quick;
                p["Force"] = false; //never dismount a volume with open files
            });
        }

        public static void AddAccessPath(DiskInfo disk, PartitionInfo partition, string accessPath) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            Invoke(partition.ObjectPath, "AddAccessPath", p => {
                if (string.IsNullOrEmpty(accessPath)) { p["AssignDriveLetter"] = true; } else { p["AccessPath"] = accessPath; }
            });
        }

        public static void RemoveAccessPath(DiskInfo disk, PartitionInfo partition, string accessPath) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            Invoke(partition.ObjectPath, "RemoveAccessPath", p => p["AccessPath"] = accessPath);
        }

        public static void SetPartitionAttributes(DiskInfo disk, PartitionInfo partition, bool? isActive = null, bool? isHidden = null, bool? isReadOnly = null, bool? noDefaultDriveLetter = null) {
            disk = Revalidate(disk);
            partition = Revalidate(disk, partition);
            EnsureModifiable(disk, partition);
            Invoke(partition.ObjectPath, "SetAttributes", p => {
                if (isActive.HasValue) { p["IsActive"] = isActive.Value; }
                if (isHidden.HasValue) { p["IsHidden"] = isHidden.Value; }
                if (isReadOnly.HasValue) { p["IsReadOnly"] = isReadOnly.Value; }
                if (noDefaultDriveLetter.HasValue) { p["NoDefaultDriveLetter"] = noDefaultDriveLetter.Value; }
            });
        }

        /// <summary>
        /// Sends TRIM/unmap for free space so a following compact can release it (Optimize-Volume -ReTrim).
        /// </summary>
        public static void RetrimVolume(PartitionInfo partition) {
            var volumePath = partition.VolumeObjectPath ?? GetVolumePath(partition);
            if (volumePath == null) { throw new InvalidOperationException("Partition has no volume."); }
            Invoke(volumePath, "Optimize", p => p["ReTrim"] = true);
        }

        /// <summary>
        /// Scans the file system for errors (Repair-Volume -Scan) or fixes them online (-SpotFix).
        /// </summary>
        public static string RepairVolume(PartitionInfo partition, bool fix) {
            var volumePath = partition.VolumeObjectPath ?? GetVolumePath(partition);
            if (volumePath == null) { throw new InvalidOperationException("Partition has no volume."); }
            var result = Invoke(volumePath, "Repair", p => { if (fix) { p["SpotFix"] = true; } else { p["Scan"] = true; } }, allowNonZero: true);
            var code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            switch (code) {
                case 0: return "No errors found.";
                case 1: return "Errors were found and fixed.";
                case 2: return "Errors were found; run Fix (spot fix) or an offline scan.";
                default: throw new InvalidOperationException(GetErrorText(code, result));
            }
        }

        #endregion


        #region Equivalent commands

        /// <summary>
        /// PowerShell equivalent shown before every change so users can see (and learn) exactly what will run.
        /// </summary>
        public static string Describe(string operation, DiskInfo disk, PartitionInfo partition = null, string argument = null) {
            var d = disk.Number.ToString(CultureInfo.InvariantCulture);
            var p = partition?.PartitionNumber.ToString(CultureInfo.InvariantCulture);
            switch (operation) {
                case "InitializeGpt": return "Initialize-Disk -Number " + d + " -PartitionStyle GPT";
                case "InitializeMbr": return "Initialize-Disk -Number " + d + " -PartitionStyle MBR";
                case "Clean": return "Clear-Disk -Number " + d + " -RemoveData -RemoveOEM";
                case "ConvertStyle": return "Set-Disk -Number " + d + " -PartitionStyle " + argument;
                case "Online": return "Set-Disk -Number " + d + " -IsOffline $false";
                case "Offline": return "Set-Disk -Number " + d + " -IsOffline $true";
                case "ReadOnly": return "Set-Disk -Number " + d + " -IsReadOnly $" + (argument ?? "true");
                case "CreatePartition": return "New-Partition -DiskNumber " + d + " " + (argument ?? "-UseMaximumSize -AssignDriveLetter");
                case "Delete": return "Remove-Partition -DiskNumber " + d + " -PartitionNumber " + p;
                case "Resize": return "Resize-Partition -DiskNumber " + d + " -PartitionNumber " + p + " -Size " + argument;
                case "Format": return "Get-Partition -DiskNumber " + d + " -PartitionNumber " + p + " | Format-Volume " + argument;
                case "AddLetter": return "Add-PartitionAccessPath -DiskNumber " + d + " -PartitionNumber " + p + " -AssignDriveLetter";
                case "AddPath": return "Add-PartitionAccessPath -DiskNumber " + d + " -PartitionNumber " + p + " -AccessPath '" + argument + "'";
                case "RemovePath": return "Remove-PartitionAccessPath -DiskNumber " + d + " -PartitionNumber " + p + " -AccessPath '" + argument + "'";
                case "Active": return "Set-Partition -DiskNumber " + d + " -PartitionNumber " + p + " -IsActive $" + (argument ?? "true");
                case "Retrim": return "Get-Partition -DiskNumber " + d + " -PartitionNumber " + p + " | Get-Volume | Optimize-Volume -ReTrim";
                case "Scan": return "Get-Partition -DiskNumber " + d + " -PartitionNumber " + p + " | Get-Volume | Repair-Volume -Scan";
                case "SpotFix": return "Get-Partition -DiskNumber " + d + " -PartitionNumber " + p + " | Get-Volume | Repair-Volume -SpotFix";
                default: return operation;
            }
        }

        #endregion


        #region Helpers

        /// <summary>
        /// Disk-wide changes need an unprotected disk; partition changes need an unprotected partition on a non-system disk.
        /// </summary>
        private static void EnsureModifiable(DiskInfo disk, PartitionInfo partition = null, bool creatingPartition = false) {
            if (disk.IsSystemDisk || ((partition == null) && !creatingPartition && disk.IsProtected)) {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "Disk {0} cannot be changed here because {1}.", disk.Number, disk.ProtectedReason));
            }
            if ((partition != null) && partition.IsProtected) {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "{0} cannot be changed here: {1}.", partition.DisplayName, partition.ProtectedReason));
            }
        }

        private static string GetVolumePath(PartitionInfo partition) {
            return GetPartitions(partition.DiskNumber).FirstOrDefault(x => x.PartitionNumber == partition.PartitionNumber)?.VolumeObjectPath;
        }

        private static ManagementBaseObject Invoke(string objectPath, string method, Action<ManagementBaseObject> fill, bool allowNonZero = false) {
            using (var mo = new ManagementObject(GetScope(), new ManagementPath(objectPath), null)) {
                var input = mo.GetMethodParameters(method);
                fill?.Invoke(input);
                ManagementBaseObject result;
                try {
                    result = mo.InvokeMethod(method, input, null);
                } catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied) {
                    throw new UnauthorizedAccessException("Administrator rights are required for this operation.", ex);
                }
                var code = Convert.ToUInt32(result["ReturnValue"] ?? 0u, CultureInfo.InvariantCulture);
                if ((code != 0) && !allowNonZero) { throw new InvalidOperationException(GetErrorText(code, result)); }
                return result;
            }
        }

        private static string GetErrorText(uint code, ManagementBaseObject result) {
            string extended = null;
            try {
                if (result["ExtendedStatus"] is ManagementBaseObject status) { extended = status["Message"] as string; }
            } catch (ManagementException) { }
            if (!string.IsNullOrEmpty(extended)) { return extended; }
            switch (code) {
                case 1: return "Operation is not supported.";
                case 2: return "Unspecified error.";
                case 3: return "Operation timed out.";
                case 4: return "Operation failed.";
                case 5: return "Invalid parameter.";
                case 40001: return "Access denied. Administrator rights are required.";
                case 40002: return "There are not enough resources to complete the operation.";
                case 41000: return "The disk has not been initialized.";
                case 41001: return "The disk is already initialized.";
                case 41006: return "The disk is read-only.";
                case 41008: return "The disk is offline.";
                case 42002: return "The requested size is not supported. Check the minimum and maximum size.";
                case 42004: return "There is not enough free space on the disk.";
                case 42008: return "The partition is in use (system, boot or page file) and cannot be changed.";
                case 42012: return "The drive letter or access path is already in use.";
                case 43000: return "The file system is not supported for this volume size.";
                default: return string.Format(CultureInfo.CurrentCulture, "Storage operation failed (code {0}).", code);
            }
        }

        private static string GetPartitionType(ManagementObject mo) {
            var gpt = mo["GptType"] as string;
            if (!string.IsNullOrEmpty(gpt)) {
                switch (gpt.ToLowerInvariant()) {
                    case "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}": return "EFI System";
                    case "{e3c9e316-0b5c-4db8-817d-f92df00215ae}": return "Reserved (MSR)";
                    case "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}": return "Basic data";
                    case "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}": return "Recovery";
                    case "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}": return "LDM metadata";
                    case "{af9b60a0-1431-4f62-bc68-3311714a69ad}": return "LDM data";
                    case "{e75caf8f-f680-4cee-afa3-b001e56efc2d}": return "Storage Spaces";
                    case "{0fc63daf-8483-4772-8e79-3d69d8477de4}": return "Linux filesystem";
                    default: return "GPT " + gpt;
                }
            }
            var mbr = Convert.ToInt32(mo["MbrType"] ?? 0, CultureInfo.InvariantCulture);
            switch (mbr) {
                case 0: return "Unknown";
                case 1: return "FAT12";
                case 4: return "FAT16";
                case 5: return "Extended";
                case 6: return "Huge";
                case 7: return "IFS (NTFS/exFAT)";
                case 12: return "FAT32";
                case 0x27: return "Recovery";
                case 0x83: return "Linux";
                default: return string.Format(CultureInfo.InvariantCulture, "MBR 0x{0:X2}", mbr);
            }
        }

        private static string HealthToText(object value) {
            switch (Convert.ToInt32(value ?? -1, CultureInfo.InvariantCulture)) {
                case 0: return "Healthy";
                case 1: return "Warning";
                case 2: return "Unhealthy";
                default: return null;
            }
        }

        private static long ToLong(object value) {
            return (value == null) ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        #endregion

    }
}
