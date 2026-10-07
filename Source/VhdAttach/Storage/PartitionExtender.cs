using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using VhdAttachCommon;

namespace VhdAttach.Storage {

    /// <summary>
    /// After a virtual disk grows, extends its last partition into the new space, so the extra capacity is usable
    /// (growing the disk alone only adds unallocated space). Requires administrator rights.
    /// </summary>
    internal static class PartitionExtender {

        private const long MinimumGain = 1024 * 1024; //ignore rounding leftovers

        /// <summary>
        /// Attaches the (detached) disk without a drive letter, extends the last NTFS/ReFS partition to the maximum,
        /// flushes and detaches again. Returns a sentence describing what happened; never throws for "nothing to do".
        /// </summary>
        public static string ExtendLastPartition(string fileName) {
            if (VirtualDiskImage.IsAttached(fileName)) { throw new InvalidOperationException("Detach the virtual disk first."); }

            using (var disk = new Medo.IO.VirtualDisk(fileName)) {
                disk.Open(Medo.IO.VirtualDiskAccessMask.All);
                disk.Attach(Medo.IO.VirtualDiskAttachOptions.PermanentLifetime | Medo.IO.VirtualDiskAttachOptions.NoDriveLetter);
                try {
                    var storageDisk = WaitForDisk(disk.GetAttachedPath(), fileName);
                    if (storageDisk.IsOffline) { StorageManager.SetDiskOnline(storageDisk, true); storageDisk = StorageManager.Revalidate(storageDisk); }
                    WaitForPartitions(storageDisk.Number);
                    return ExtendOnline(storageDisk);
                } finally {
                    DetachSafely(disk, fileName);
                }
            }
        }

        /// <summary>
        /// For a disk that is attached right now: how much space after its last partition could be used, and the disk itself.
        /// Returns null if the disk cannot be found or nothing can be extended.
        /// </summary>
        public static (DiskInfo Disk, PartitionInfo Partition, long Gain)? FindExtendable(string fileName) {
            var expected = PathGuard.GetLongPath(Path.GetFullPath(fileName));
            var disk = StorageManager.GetDisks().FirstOrDefault(d => d.IsVirtual && string.Equals(PathGuard.GetLongPath(d.Location ?? ""), expected, StringComparison.OrdinalIgnoreCase));
            if ((disk == null) || (disk.PartitionStyle == PartitionStyle.Raw)) { return null; }
            var partition = StorageManager.GetPartitions(disk.Number).OrderBy(p => p.Offset).LastOrDefault();
            if ((partition == null) || partition.IsProtected || ((partition.FileSystem != "NTFS") && (partition.FileSystem != "ReFS"))) { return null; }
            var (_, max) = StorageManager.GetSupportedSize(partition);
            return (max >= partition.Size + MinimumGain) ? (disk, partition, max - partition.Size) : ((DiskInfo, PartitionInfo, long)?)null;
        }

        /// <summary>
        /// Extends the last NTFS/ReFS partition of an attached disk to the maximum (safe while in use).
        /// </summary>
        public static string ExtendOnline(DiskInfo storageDisk) {
            if (storageDisk.PartitionStyle == PartitionStyle.Raw) { return "The disk has no partitions yet; create one in Disk Manager."; }
            var partition = StorageManager.GetPartitions(storageDisk.Number).OrderBy(p => p.Offset).LastOrDefault();
            if (partition == null) { return "The disk has no partitions yet; create one in Disk Manager."; }
            if (partition.IsProtected) { return "The last partition is protected (" + partition.ProtectedReason + ") and was not changed."; }
            if ((partition.FileSystem != "NTFS") && (partition.FileSystem != "ReFS")) {
                return string.Format(CultureInfo.CurrentCulture, "The last partition ({0}) cannot be extended automatically; use Disk Manager.", string.IsNullOrEmpty(partition.FileSystem) ? "no file system" : partition.FileSystem);
            }
            var (_, max) = StorageManager.GetSupportedSize(partition);
            if (max < partition.Size + MinimumGain) { return "The partition already uses all available space."; }
            var before = partition.Size;
            StorageManager.ResizePartition(storageDisk, partition, max);
            return string.Format(CultureInfo.CurrentCulture, "Partition {0} extended from {1} to {2}.", partition.DisplayName + (string.IsNullOrEmpty(partition.Label) ? "" : " (" + partition.Label + ")"), Ui.FormatSize(before), Ui.FormatSize(max));
        }

        private static DiskInfo WaitForDisk(string physicalPath, string fileName) {
            var number = int.Parse(physicalPath.Substring(physicalPath.IndexOf("PhysicalDrive", StringComparison.OrdinalIgnoreCase) + 13), CultureInfo.InvariantCulture);
            var expected = PathGuard.GetLongPath(Path.GetFullPath(fileName));
            for (int i = 0; i < 40; i++) {
                var found = StorageManager.GetDisks().FirstOrDefault(d => d.Number == number);
                if ((found != null) && found.IsVirtual && string.Equals(PathGuard.GetLongPath(found.Location ?? ""), expected, StringComparison.OrdinalIgnoreCase)) { return found; }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("The attached disk could not be identified; nothing was changed.");
        }

        private static System.Collections.Generic.IList<PartitionInfo> WaitForPartitions(int diskNumber) {
            for (int i = 0; i < 40; i++) {
                var partitions = StorageManager.GetPartitions(diskNumber);
                if (partitions.Any(p => !string.IsNullOrEmpty(p.FileSystem))) { return partitions; } //volumes are mounted
                Thread.Sleep(250);
            }
            return StorageManager.GetPartitions(diskNumber);
        }

        private static void DetachSafely(Medo.IO.VirtualDisk disk, string fileName) {
            string physicalPath = null;
            try { physicalPath = disk.GetAttachedPath(); } catch (IOException) { } catch (System.ComponentModel.Win32Exception) { }
            VolumeLock locks = null;
            if (physicalPath != null) {
                var volumes = Volume.GetVolumesOnPhysicalDrive(physicalPath);
                locks = VolumeLock.Acquire(volumes.Select(v => v.VolumeName).ToList(), volumes.Select(v => v.DriveLetter2 ?? v.VolumeName).ToList(), force: false);
            }
            using (locks) { disk.Detach(); } //flushed and locked first, like every detach in VHD Studio
        }

    }
}
