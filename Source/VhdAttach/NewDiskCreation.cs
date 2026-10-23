using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Medo.IO;
using VhdAttach.Storage;
using VhdAttachCommon;

namespace VhdAttach {
    /// <summary>Creates new image files, optionally ready to use as one NTFS data volume.</summary>
    internal static class NewDiskCreation {
        internal static void Create(string fileName, long size, bool fixedSize, bool createSinglePartition = false) {
            var target = Validate(fileName, size, createSinglePartition);
            using (PathGuard.Open(Path.GetDirectoryName(target), directory: true, rejectHardLinks: false)) {
                try {
                    // Keep the create-new native handle until preparation and safe detach finish.
                    // Reopening the path after creation would permit a substituted file to be initialized.
                    using (var image = new VirtualDisk(target)) {
                        var format = Path.GetExtension(target).Equals(".vhdx", StringComparison.OrdinalIgnoreCase) ? VirtualDiskType.Vhdx : VirtualDiskType.Vhd;
                        image.Create(size, fixedSize ? VirtualDiskCreateOptions.FullPhysicalAllocation : VirtualDiskCreateOptions.None, 0, 0, format);
                        if (createSinglePartition) { PrepareNewDisk(image, target); }
                    }
                } catch (Exception error) {
                    throw new IOException("New disk creation did not finish. Keep any remaining image for inspection; it may still be attached."
                        + Environment.NewLine + "Image: " + target + Environment.NewLine + error.Message, error);
                }
            }
        }

        internal static string Validate(string fileName, long size, bool createSinglePartition = false) {
            ValidateSize(size, createSinglePartition);
            if (string.IsNullOrWhiteSpace(fileName) || !Path.IsPathFullyQualified(fileName)) {
                throw new ArgumentException("Choose a full local path for the new disk image.", nameof(fileName));
            }
            var target = Path.GetFullPath(fileName);
            if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.AsSpan(2).Contains(':')) {
                throw new ArgumentException("Use a local file, not a network path, device path or alternate data stream.", nameof(fileName));
            }
            var extension = Path.GetExtension(target);
            if (!extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentException("Choose a .vhd or .vhdx image.", nameof(fileName));
            }
            foreach (var part in target.Substring(Path.GetPathRoot(target).Length).Split(Path.DirectorySeparatorChar)) {
                if (part.EndsWith(' ') || part.EndsWith('.')) { throw new IOException("Trailing spaces or dots in a path are not supported."); }
            }
            using (PathGuard.Open(Path.GetDirectoryName(target), directory: true, rejectHardLinks: false)) { }
            if (File.Exists(target) || Directory.Exists(target)) { throw new IOException("The destination already exists. Nothing will be overwritten."); }
            return target;
        }

        internal static void ValidateSize(long size, bool createSinglePartition) {
            var minimum = (createSinglePartition ? 64L : 8L) * 1024 * 1024;
            if (size < minimum || size % 4096 != 0) {
                throw new ArgumentOutOfRangeException(nameof(size), createSinglePartition
                    ? "Use at least 64 MiB for one NTFS partition, aligned to 4096 bytes."
                    : "Use a disk size of at least 8 MiB, aligned to 4096 bytes.");
            }
        }

        // Only accepts the still-open handle from our own create-new operation, never a selected existing disk.
        private static void PrepareNewDisk(VirtualDisk image, string fileName) {
            using (PathGuard.Open(fileName, directory: false, rejectHardLinks: true)) {
                image.GetSize(out var size, out _, out _, out _);
                ValidateSize(size, createSinglePartition: true);
                image.Attach(VirtualDiskAttachOptions.NoDriveLetter | VirtualDiskAttachOptions.PermanentLifetime);
                Exception failure = null;
                try {
                    var disk = WaitForDisk(image, fileName, size);
                    disk = Revalidate(image, disk, fileName);
                    if (disk.PartitionStyle != PartitionStyle.Raw || StorageManager.GetPartitions(disk.Number).Count != 0) {
                        throw new InvalidOperationException("The new disk already contains a partition table. It was not initialized.");
                    }
                    EnsureBlank(image.GetAttachedPath());
                    if (disk.IsReadOnly) { throw new InvalidOperationException("The new disk is read-only."); }
                    if (disk.IsOffline) { StorageManager.SetDiskOnline(Revalidate(image, disk, fileName), true); }
                    StorageManager.InitializeDisk(Revalidate(image, disk, fileName), PartitionStyle.Gpt);
                    disk = Revalidate(image, disk, fileName);
                    if (StorageManager.GetPartitions(disk.Number).Any(p => p.Type != "Reserved (MSR)")) {
                        throw new InvalidOperationException("The new disk changed before its data partition could be created.");
                    }
                    StorageManager.CreatePartition(disk, 0, assignDriveLetter: false, fileSystem: null, label: null);
                    var partition = WaitForPartition(image, disk, fileName, formatted: false);
                    StorageManager.FormatPartition(Revalidate(image, disk, fileName), partition, "NTFS", "", quick: true);
                    partition = WaitForPartition(image, disk, fileName, formatted: true);
                    // Assign only after formatting, so Explorer never sees an unformatted volume.
                    StorageManager.AddAccessPath(Revalidate(image, disk, fileName), partition, null);
                } catch (Exception error) {
                    failure = error;
                }
                try {
                    // Permanent lifetime preserves the disk if safe detach is refused; never force it.
                    var volumes = Volume.GetVolumesOnPhysicalDrive(image.GetAttachedPath()).ToList();
                    using (VolumeLock.Acquire(volumes.Select(v => v.VolumeName).ToList(), volumes.Select(v => v.DriveLetter2 ?? v.VolumeName).ToList(), force: false)) {
                        image.Detach();
                    }
                    if (VirtualDiskImage.GetDetails(fileName).AttachedPath != null) { throw new IOException("The new disk is still attached."); }
                } catch (Exception detachError) {
                    throw new IOException("The new disk could not be safely detached. Keep the image and close any programs using it before detaching; it has not been soft protected."
                        + Environment.NewLine + (failure == null ? "" : failure.Message + Environment.NewLine) + detachError.Message, detachError);
                }
                if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
            }
        }

        private static DiskInfo WaitForDisk(VirtualDisk image, string fileName, long size) {
            var number = DiskNumber(image.GetAttachedPath());
            for (int attempt = 0; attempt < 40; attempt++) {
                var disk = StorageManager.GetDisks().FirstOrDefault(d => d.Number == number);
                if (disk != null && disk.Size == size && Matches(disk, fileName)) { return disk; }
                Thread.Sleep(250);
            }
            throw new IOException("Windows did not report the new virtual disk with the expected backing file and size.");
        }

        private static DiskInfo Revalidate(VirtualDisk image, DiskInfo disk, string fileName) {
            var fresh = StorageManager.Revalidate(disk);
            if (fresh.Number != DiskNumber(image.GetAttachedPath()) || !Matches(fresh, fileName) || fresh.IsSystemDisk || fresh.IsProtected) {
                throw new InvalidOperationException("The new virtual disk could not be positively identified. Nothing further was changed.");
            }
            return fresh;
        }

        private static bool Matches(DiskInfo disk, string fileName) => disk.IsVirtual && !string.IsNullOrEmpty(disk.Location)
            && string.Equals(PathGuard.GetLongPath(disk.Location), PathGuard.GetLongPath(fileName), StringComparison.OrdinalIgnoreCase);

        private static int DiskNumber(string physicalPath) {
            const string prefix = @"\\.\PhysicalDrive";
            if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(physicalPath.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
                throw new IOException("Windows returned an unexpected virtual disk device path.");
            }
            return number;
        }

        private static PartitionInfo WaitForPartition(VirtualDisk image, DiskInfo disk, string fileName, bool formatted) {
            for (int attempt = 0; attempt < 40; attempt++) {
                disk = Revalidate(image, disk, fileName);
                var partitions = StorageManager.GetPartitions(disk.Number).Where(p => p.Type != "Reserved (MSR)").ToList();
                if (partitions.Count > 1 || partitions.Any(p => p.Type != "Basic data" || p.IsProtected)) {
                    throw new InvalidOperationException("The new disk no longer contains just its expected data partition.");
                }
                if (partitions.Count == 1 && partitions[0].VolumeObjectPath != null
                    && (!formatted || string.Equals(partitions[0].FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))) { return partitions[0]; }
                Thread.Sleep(250);
            }
            throw new IOException("Windows did not report the new " + (formatted ? "NTFS volume." : "data partition and volume."));
        }

        private static void EnsureBlank(string physicalPath) {
            var probe = new byte[1024 * 1024];
            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { stream.ReadExactly(probe); }
            if (Array.Exists(probe, b => b != 0)) { throw new InvalidOperationException("The new disk already contains data. It was not initialized."); }
        }
    }
}
