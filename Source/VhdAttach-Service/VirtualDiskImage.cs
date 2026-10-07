using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VhdAttachCommon {

    /// <summary>
    /// Kind of virtual disk as reported by the provider subtype.
    /// </summary>
    internal enum VirtualDiskKind {
        Unknown = 0,
        Fixed = 2,
        Dynamic = 3,
        Differencing = 4,
    }

    /// <summary>
    /// Snapshot of virtual disk properties read through GetVirtualDiskInformation.
    /// Every property is nullable because older formats and providers do not support all queries.
    /// </summary>
    internal sealed class VirtualDiskDetails {
        public string FileName { get; set; }
        public string Format { get; set; }
        public VirtualDiskKind Kind { get; set; }
        public long? VirtualSize { get; set; }
        public long? PhysicalSize { get; set; }
        public int? BlockSize { get; set; }
        public int? LogicalSectorSize { get; set; }
        public int? PhysicalSectorSize { get; set; }
        public Guid? Identifier { get; set; }
        public int? FragmentationPercentage { get; set; }
        public long? SmallestSafeVirtualSize { get; set; }
        public bool? Is4KAligned { get; set; }
        public bool? IsLoaded { get; set; }
        public string AttachedPath { get; set; }
        public bool? ParentResolved { get; set; }
        public IList<string> ParentLocations { get; } = new List<string>();
        public Guid? ParentIdentifier { get; set; }
        public bool NeedsLogReplay { get; set; }
        /// <summary>Why the full chain could not be opened (only set when ParentResolved is false).</summary>
        public string OpenError { get; set; }
    }

    /// <summary>
    /// Progress information for long-running virtual disk operations.
    /// </summary>
    internal readonly struct VirtualDiskProgress {
        public VirtualDiskProgress(long current, long total) {
            this.Current = current;
            this.Total = total;
        }
        public long Current { get; }
        public long Total { get; }
        public int Percentage => (this.Total <= 0) ? 0 : (int)Math.Min(100, this.Current * 100 / this.Total);
    }


    /// <summary>
    /// Maintenance operations on VHD/VHDX files built directly on VirtDisk.dll (no Hyper-V module required).
    /// All operations except GetDetails require administrator rights.
    /// </summary>
    internal static class VirtualDiskImage {

        #region Information

        public static VirtualDiskDetails GetDetails(string fileName) {
            var details = new VirtualDiskDetails { FileName = fileName };
            details.NeedsLogReplay = VhdxHeader.NeedsLogReplay(fileName);

            SafeFileHandle opened;
            try {
                opened = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO | NativeMethods.VIRTUAL_DISK_ACCESS_DETACH, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1);
            } catch (Exception ex) when (!(ex is FileNotFoundException)) {
                try { //differencing disk whose parent is missing or does not match: open the child alone
                    opened = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS, 1);
                    details.ParentResolved = false;
                    details.OpenError = ex.Message;
                } catch (Exception) {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }
            }
            using (var handle = opened) {
                var buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_VIRTUAL_STORAGE_TYPE);
                if (buffer != null) {
                    switch (BitConverter.ToInt32(buffer, 8)) {
                        case NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_ISO: details.Format = "ISO"; break;
                        case NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_VHD: details.Format = "VHD"; break;
                        case NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_VHDX: details.Format = "VHDX"; break;
                        case NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_VHDSET: details.Format = "VHD Set"; break;
                        default: details.Format = "Unknown"; break;
                    }
                }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_SIZE);
                if (buffer != null) {
                    details.VirtualSize = BitConverter.ToInt64(buffer, 8);
                    details.PhysicalSize = BitConverter.ToInt64(buffer, 16);
                    details.BlockSize = BitConverter.ToInt32(buffer, 24);
                    details.LogicalSectorSize = BitConverter.ToInt32(buffer, 28);
                }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_PROVIDER_SUBTYPE);
                if (buffer != null) {
                    var subtype = BitConverter.ToInt32(buffer, 8);
                    details.Kind = Enum.IsDefined(typeof(VirtualDiskKind), subtype) ? (VirtualDiskKind)subtype : VirtualDiskKind.Unknown;
                }
                if ((details.ParentResolved == false) && (details.Kind != VirtualDiskKind.Differencing)) {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new IOException(details.OpenError)).Throw(); //not a chain problem
                }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_IDENTIFIER);
                if (buffer != null) { details.Identifier = ReadGuid(buffer, 8); }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_VHD_PHYSICAL_SECTOR_SIZE);
                if (buffer != null) { details.PhysicalSectorSize = BitConverter.ToInt32(buffer, 8); }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_IS_4K_ALIGNED);
                if (buffer != null) { details.Is4KAligned = BitConverter.ToInt32(buffer, 8) != 0; }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_FRAGMENTATION);
                if (buffer != null) { details.FragmentationPercentage = BitConverter.ToInt32(buffer, 8); }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_SMALLEST_SAFE_VIRTUAL_SIZE);
                if (buffer != null) { details.SmallestSafeVirtualSize = BitConverter.ToInt64(buffer, 8); }

                buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_IS_LOADED);
                if (buffer != null) { details.IsLoaded = BitConverter.ToInt32(buffer, 8) != 0; }

                if (details.Kind == VirtualDiskKind.Differencing) {
                    buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_PARENT_LOCATION);
                    if (buffer != null) {
                        details.ParentResolved = (details.ParentResolved != false) && (BitConverter.ToInt32(buffer, 8) != 0);
                        foreach (var location in ReadMultiString(buffer, 12)) { details.ParentLocations.Add(location); }
                    }
                    buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_PARENT_IDENTIFIER);
                    if (buffer != null) { details.ParentIdentifier = ReadGuid(buffer, 8); }
                }

                details.AttachedPath = GetAttachedPath(handle);
            }

            return details;
        }

        #endregion


        #region Repair

        /// <summary>
        /// Replays a pending VHDX log (left behind by a crash or unclean shutdown) by opening the file read/write once.
        /// Until this is done, Windows refuses read-only opens with "Access denied".
        /// </summary>
        public static void ReplayLog(string fileName) {
            EnsureNotAttached(fileName, "replaying the log");
            using (Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_ALL, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1)) { }
            if (VhdxHeader.NeedsLogReplay(fileName)) { throw new IOException("The log is still pending after replay. The file may be damaged; keep the backup and do not attach it read/write."); }
        }

        /// <summary>
        /// Points a differencing disk at a (moved or renamed) parent. Equivalent to Set-VHD -ParentPath.
        /// </summary>
        /// <remarks>
        /// The new parent is validated before and after the change: for VHD the stored parent identifier must match;
        /// for every format the chain must open afterwards (VirtDisk checks the VHDX parent linkage). On mismatch the
        /// previous parent path is restored, so a wrong selection can never leave the child pointing at foreign data.
        /// </remarks>
        public static void SetParentPath(string fileName, string parentFileName) {
            var child = GetDetails(fileName);
            if (child.Kind != VirtualDiskKind.Differencing) { throw new InvalidOperationException("Only differencing disks have a parent."); }
            if (child.AttachedPath != null) { throw new InvalidOperationException("Detach the virtual disk before changing its parent."); }
            var parent = GetDetails(parentFileName);
            if (parent.AttachedPath != null) { throw new InvalidOperationException("The selected parent is attached. Detach it first."); }
            if (child.Format == "VHD") {
                if ((child.ParentIdentifier == null) || (parent.Identifier == null) || (child.ParentIdentifier != parent.Identifier)) {
                    throw new InvalidOperationException("The selected file is not the parent of this disk (identifier mismatch). Nothing was changed.");
                }
            } else { //VHDX: the child records the parent's DataWriteGuid; it also changes if the parent was modified after the child was created
                var linkage = VhdxHeader.ReadParentLinkage(fileName);
                var parentHeader = VhdxHeader.Read(parentFileName);
                if ((linkage == null) || (linkage.Length == 0) || (parentHeader == null)) {
                    throw new InvalidOperationException("The parent link could not be verified. Nothing was changed.");
                }
                if (Array.IndexOf(linkage, parentHeader.DataWriteGuid) < 0) {
                    throw new InvalidOperationException("The selected file is not the parent of this disk, or it was modified after this disk was created (linkage mismatch). Nothing was changed.");
                }
            }
            var previousParent = child.ParentLocations.FirstOrDefault(Path.IsPathFullyQualified);

            WriteParentPath(fileName, parentFileName);
            try {
                using (Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1)) { } //Windows validates the whole chain
            } catch (Exception validation) {
                var restored = false;
                if (previousParent != null) {
                    try { WriteParentPath(fileName, previousParent); restored = true; } catch (Exception) { }
                }
                throw new InvalidOperationException("The new parent was rejected by Windows (" + validation.Message + "). " + (restored ? "The previous parent path was restored." : "The previous parent path could NOT be restored; restore this file from its backup."), validation);
            }
        }

        private static void WriteParentPath(string fileName, string parentFileName) {
            using (var handle = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_METAOPS, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS, 1)) {
                var info = new NativeMethods.SET_VIRTUAL_DISK_INFO_PARENT_PATH {
                    Version = NativeMethods.SET_VIRTUAL_DISK_INFO_PARENT_PATH_VERSION,
                    ParentFilePath = parentFileName,
                };
                ThrowOnError(NativeMethods.SetVirtualDiskInformation(handle, ref info), fileName);
            }
        }

        /// <summary>
        /// Assigns a new random identifier (fixes duplicate-ID collisions after copying a disk). Breaks existing differencing children.
        /// </summary>
        public static Guid ResetIdentifier(string fileName) {
            EnsureNotAttached(fileName, "changing its identifier");
            var newId = Guid.NewGuid();
            using (var handle = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_METAOPS, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1)) {
                var buffer = Marshal.AllocHGlobal(64);
                try { //union is pointer-aligned, so the GUID sits at offset IntPtr.Size
                    Marshal.Copy(new byte[64], 0, buffer, 64);
                    Marshal.WriteInt32(buffer, 0, NativeMethods.SET_VIRTUAL_DISK_INFO_IDENTIFIER_VERSION);
                    Marshal.Copy(newId.ToByteArray(), 0, buffer + IntPtr.Size, 16);
                    ThrowOnError(NativeMethods.SetVirtualDiskInformation(handle, buffer), fileName);
                } finally {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return newId;
        }

        #endregion


        #region Maintenance

        /// <summary>
        /// Compacts a dynamic or differencing disk. The disk must not be attached.
        /// The file-system-aware pass attaches read-only (no drive letter) so NTFS free space can be released;
        /// the second pass reclaims all-zero blocks (useful for ext4/WSL images after fstrim).
        /// </summary>
        public static void Compact(string fileName, bool fileSystemAware, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            using (var handle = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_METAOPS | NativeMethods.VIRTUAL_DISK_ACCESS_ATTACH_RO | NativeMethods.VIRTUAL_DISK_ACCESS_DETACH | NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1)) {
                if (GetAttachedPath(handle) != null) { throw new InvalidOperationException("Detach the virtual disk before compacting it."); }
                if (VhdxHeader.NeedsLogReplay(fileName)) { throw new InvalidOperationException("Replay the pending log (Repair) before compacting."); }

                if (fileSystemAware) {
                    var attach = new NativeMethods.ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
                    var res = NativeMethods.AttachVirtualDisk(handle, IntPtr.Zero, NativeMethods.ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY | NativeMethods.ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER, 0, ref attach, IntPtr.Zero);
                    ThrowOnError(res, fileName);
                    try {
                        RunCompact(handle, fileName, progress, cancellationToken);
                    } finally {
                        NativeMethods.DetachVirtualDisk(handle, 0, 0);
                    }
                }
                RunCompact(handle, fileName, progress, cancellationToken);
            }
        }

        private static void RunCompact(SafeFileHandle handle, string fileName, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            RunAsync(handle, fileName, progress, cancellationToken, overlapped => {
                var parameters = new NativeMethods.COMPACT_VIRTUAL_DISK_PARAMETERS { Version = 1 };
                return NativeMethods.CompactVirtualDisk(handle, 0, ref parameters, overlapped);
            });
        }

        /// <summary>
        /// Changes the virtual size. VHDX can grow or shrink; VHD can only grow.
        /// Shrinking only succeeds down to the smallest safe size (shrink the partition first).
        /// </summary>
        /// <param name="newSize">New size in bytes, or 0 to shrink to the smallest safe size (VHDX only).</param>
        /// <remarks>Not cancellable: interrupting a resize is not documented as safe.</remarks>
        public static void Resize(string fileName, long newSize, IProgress<VirtualDiskProgress> progress) {
            var cancellationToken = CancellationToken.None;
            var details = GetDetails(fileName);
            if (details.AttachedPath != null) { throw new InvalidOperationException("Detach the virtual disk before resizing it."); }
            if (details.NeedsLogReplay) { throw new InvalidOperationException("Replay the pending log (Repair) before resizing."); }
            if ((newSize != 0) && (details.VirtualSize != null)) {
                if (newSize == details.VirtualSize) { throw new InvalidOperationException("The disk already has this size."); }
                if ((newSize < details.VirtualSize) && (details.SmallestSafeVirtualSize != null) && (newSize < details.SmallestSafeVirtualSize)) {
                    throw new InvalidOperationException("The new size is below the smallest safe size. Shrink the partition first.");
                }
                if ((newSize > details.VirtualSize) && (details.Kind == VirtualDiskKind.Fixed)) {
                    EnsureFreeSpace(fileName, newSize - details.VirtualSize.Value, "grow the fixed-size disk");
                }
            }
            if (IsVhdx(fileName) || (details.Format == "VHDX")) {
                using (var handle = OpenV2(fileName, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, readOnly: false)) {
                    var flags = (newSize == 0) ? NativeMethods.RESIZE_VIRTUAL_DISK_FLAG_RESIZE_TO_SMALLEST_SAFE_VIRTUAL_SIZE : 0;
                    RunAsync(handle, fileName, progress, cancellationToken, overlapped => {
                        var parameters = new NativeMethods.RESIZE_VIRTUAL_DISK_PARAMETERS { Version = 1, NewSize = newSize };
                        return NativeMethods.ResizeVirtualDisk(handle, flags, ref parameters, overlapped);
                    });
                }
            } else {
                if (newSize == 0) { throw new NotSupportedException("Shrinking is only supported for VHDX files. Convert the disk to VHDX first."); }
                if ((details.VirtualSize != null) && (newSize < details.VirtualSize)) { throw new NotSupportedException("VHD files cannot shrink. Convert the disk to VHDX first."); }
                using (var handle = Open(fileName, NativeMethods.VIRTUAL_DISK_ACCESS_METAOPS, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 1)) {
                    RunAsync(handle, fileName, progress, cancellationToken, overlapped => {
                        var parameters = new NativeMethods.EXPAND_VIRTUAL_DISK_PARAMETERS { Version = 1, NewSize = newSize };
                        return NativeMethods.ExpandVirtualDisk(handle, 0, ref parameters, overlapped);
                    });
                }
            }
        }

        /// <summary>
        /// Creates a new disk with the content of the source (VHD↔VHDX and fixed↔dynamic conversion).
        /// The format of the destination follows its extension. Source is not modified.
        /// </summary>
        public static void Convert(string sourceFileName, string destinationFileName, bool fixedSize, int logicalSectorSize, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            if (string.Equals(Path.GetFullPath(sourceFileName), Path.GetFullPath(destinationFileName), StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Destination must be a different file."); }
            var source = GetDetails(sourceFileName);
            if (source.AttachedPath != null) { throw new InvalidOperationException("Detach the virtual disk before converting it, so the copy is consistent."); }
            if (source.NeedsLogReplay) { throw new InvalidOperationException("Replay the pending log (Repair) before converting."); }
            if (source.ParentResolved == false) { throw new InvalidOperationException("The parent of this differencing disk is missing. Fix the parent path first."); }
            if ((logicalSectorSize != 0) && (logicalSectorSize != source.LogicalSectorSize)) { throw new NotSupportedException("Changing the logical sector size would make partitions and file systems unreadable."); }
            if (!IsVhdx(destinationFileName) && (source.LogicalSectorSize != null) && (source.LogicalSectorSize != 512)) {
                throw new NotSupportedException("The source uses " + source.LogicalSectorSize + "-byte sectors; VHD only supports 512. Convert to VHDX instead.");
            }
            var required = fixedSize ? (source.VirtualSize ?? 0) : Math.Min(source.VirtualSize ?? long.MaxValue, GetChainPhysicalSize(sourceFileName));
            EnsureFreeSpace(destinationFileName, required, "create the converted disk");

            CreateV2(destinationFileName, 0, fixedSize, logicalSectorSize, null, sourceFileName, progress, cancellationToken);

            try { //never report success for a copy that does not match the source
                var result = GetDetails(destinationFileName);
                if ((result.VirtualSize == null) || (source.VirtualSize == null) || (result.VirtualSize < source.VirtualSize)) {
                    throw new IOException("Verification failed: the converted disk is smaller than the source.");
                }
                if (fixedSize != (result.Kind == VirtualDiskKind.Fixed)) {
                    throw new IOException("Verification failed: the converted disk has the wrong type.");
                }
                if ((source.LogicalSectorSize != null) && (result.LogicalSectorSize != source.LogicalSectorSize)) {
                    throw new IOException("Verification failed: the converted disk has a different sector size.");
                }
            } catch {
                TryDelete(destinationFileName);
                throw;
            }
        }

        /// <summary>
        /// Creates a differencing (child) disk that records changes against the parent.
        /// </summary>
        /// <summary>
        /// Creates a new empty disk; never replaces an existing file. A cancelled or failed creation removes the partial file.
        /// </summary>
        public static void Create(string fileName, long size, bool fixedSize, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            if (fixedSize) { EnsureFreeSpace(fileName, size, "create the fixed-size disk"); }
            CreateV2(fileName, size, fixedSize, 0, null, null, progress, cancellationToken);
        }

        /// <param name="protectParent">Sets the read-only attribute on the parent; writing to a parent silently corrupts all of its children.</param>
        public static void CreateDifferencing(string parentFileName, string childFileName, bool protectParent) {
            var parent = GetDetails(parentFileName);
            if (parent.AttachedPath != null) { throw new InvalidOperationException("Detach the parent disk first. A parent must not change while it has children."); }
            if (parent.NeedsLogReplay) { throw new InvalidOperationException("Replay the pending log (Repair) of the parent first."); }
            CreateV2(childFileName, 0, false, 0, parentFileName, null, null, CancellationToken.None);
            if (protectParent) {
                File.SetAttributes(parentFileName, File.GetAttributes(parentFileName) | FileAttributes.ReadOnly);
            }
        }

        /// <summary>
        /// Merges a differencing disk into its immediate parent. The child file can be deleted afterwards.
        /// </summary>
        /// <remarks>Not cancellable: an interrupted merge can leave the parent half-updated.</remarks>
        public static void MergeIntoParent(string childFileName, IProgress<VirtualDiskProgress> progress) {
            var cancellationToken = CancellationToken.None;
            var child = GetDetails(childFileName);
            if (child.Kind != VirtualDiskKind.Differencing) { throw new InvalidOperationException("Only differencing disks can be merged."); }
            if (child.AttachedPath != null) { throw new InvalidOperationException("Detach the virtual disk before merging it."); }
            if ((child.ParentResolved == false) || (child.ParentLocations.Count == 0)) { throw new InvalidOperationException("The parent disk cannot be found. Fix the parent path first."); }
            var grandChildren = FindDependents(childFileName);
            if (grandChildren.Count > 0) {
                throw new InvalidOperationException("This disk is itself the parent of " + string.Join(", ", grandChildren.Select(Path.GetFileName)) + ". Merge those first; merging this one would break them.");
            }
            var parentFileName = child.ParentLocations[0];
            var parent = GetDetails(parentFileName);
            if (parent.AttachedPath != null) { throw new InvalidOperationException("The parent disk is attached. Detach it first."); }
            if ((File.GetAttributes(parentFileName) & FileAttributes.ReadOnly) != 0) {
                throw new InvalidOperationException("The parent disk is marked read-only (protected parent). Clear the read-only attribute deliberately before merging; other children of this parent become invalid after the merge.");
            }
            if (parent.Kind != VirtualDiskKind.Fixed) { //dynamic parents grow by up to the child's data; fixed ones are pre-allocated
                EnsureFreeSpace(parentFileName, child.PhysicalSize ?? 0, "merge into the parent");
            }
            using (var handle = Open(childFileName, NativeMethods.VIRTUAL_DISK_ACCESS_METAOPS | NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO | NativeMethods.VIRTUAL_DISK_ACCESS_DETACH, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NONE, 2)) {
                if (GetAttachedPath(handle) != null) { throw new InvalidOperationException("Detach the virtual disk before merging it."); }
                RunAsync(handle, childFileName, progress, cancellationToken, overlapped => {
                    var parameters = new NativeMethods.MERGE_VIRTUAL_DISK_PARAMETERS { Version = 1, MergeDepth = 1 };
                    return NativeMethods.MergeVirtualDisk(handle, 0, ref parameters, overlapped);
                });
            }
        }

        #endregion


        #region Helpers

        /// <summary>
        /// Uses the storage subsystem (not an image open, which a pending log could refuse).
        /// </summary>
        private static void EnsureNotAttached(string fileName, string operation) {
            if (IsAttached(fileName)) { throw new InvalidOperationException("Detach the virtual disk before " + operation + "."); }
        }

        public static bool IsAttached(string fileName) {
            var full = Path.GetFullPath(fileName);
            using (var searcher = new System.Management.ManagementObjectSearcher(@"\\.\root\Microsoft\Windows\Storage", "SELECT Location FROM MSFT_Disk WHERE BusType = 15")) {
                foreach (System.Management.ManagementObject disk in searcher.Get()) {
                    using (disk) {
                        if ((disk["Location"] is string location) && SamePath(location, full)) { return true; }
                    }
                }
            }
            return false;
        }

        private static bool SamePath(string a, string b) {
            return string.Equals(LongPath(a), LongPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string LongPath(string path) {
            var buffer = new StringBuilder(1024);
            var length = NativeMethods.GetLongPathName(path, buffer, buffer.Capacity);
            return ((length > 0) && (length < buffer.Capacity)) ? buffer.ToString() : path;
        }

        /// <summary>
        /// Differencing disks in the same folder whose parent is the given file (they break if it changes).
        /// </summary>
        public static IList<string> FindDependents(string fileName) {
            var full = Path.GetFullPath(fileName);
            var list = new List<string>();
            foreach (var candidate in Directory.EnumerateFiles(Path.GetDirectoryName(full))) {
                var extension = Path.GetExtension(candidate).ToLowerInvariant();
                if (((extension != ".vhd") && (extension != ".vhdx") && (extension != ".avhd") && (extension != ".avhdx")) || SamePath(candidate, full)) { continue; }
                try {
                    using (var handle = Open(candidate, NativeMethods.VIRTUAL_DISK_ACCESS_GET_INFO, NativeMethods.OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS, 1)) {
                        var buffer = GetInfo(handle, NativeMethods.GET_VIRTUAL_DISK_INFO_PARENT_LOCATION);
                        if (buffer == null) { continue; }
                        foreach (var location in ReadMultiString(buffer, 12)) {
                            var resolved = Path.IsPathFullyQualified(location) ? location : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(candidate), location));
                            if (SamePath(resolved, full)) { list.Add(candidate); break; }
                        }
                    }
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) {
                    //not a virtual disk or not readable; cannot depend on us in a way we could check
                }
            }
            return list;
        }

        private static void EnsureFreeSpace(string fileName, long required, string purpose) {
            const long margin = 256L * 1024 * 1024;
            var available = VirtualDiskBackup.GetFreeSpace(fileName);
            if (available < required + margin) {
                throw new IOException(string.Format(System.Globalization.CultureInfo.CurrentCulture, "Not enough free space to {0}: {1:#,##0} MB needed, {2:#,##0} MB available.", purpose, (required + margin) / 1048576, available / 1048576));
            }
        }

        private static long GetChainPhysicalSize(string fileName) {
            long total = 0;
            var current = fileName;
            for (int depth = 0; (current != null) && (depth < 32); depth++) {
                var details = GetDetails(current);
                total += details.PhysicalSize ?? new FileInfo(current).Length;
                current = (details.Kind == VirtualDiskKind.Differencing && details.ParentLocations.Count > 0) ? details.ParentLocations[0] : null;
            }
            return total;
        }

        public static bool IsVhdx(string fileName) {
            return fileName.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase);
        }

        private static void CreateV2(string fileName, long size, bool fixedSize, int logicalSectorSize, string parentPath, string sourcePath, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            var storageType = new NativeMethods.VIRTUAL_STORAGE_TYPE {
                DeviceId = IsVhdx(fileName) ? NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_VHDX : NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_VHD,
                VendorId = NativeMethods.VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT,
            };
            var parameters = new NativeMethods.CREATE_VIRTUAL_DISK_PARAMETERS_V2 {
                Version = 2,
                Version2 = new NativeMethods.CREATE_VIRTUAL_DISK_PARAMETERS_V2_DATA {
                    MaximumSize = size,
                    SectorSizeInBytes = logicalSectorSize,
                    ParentPath = parentPath,
                    SourcePath = sourcePath,
                },
            };
            var flags = fixedSize ? NativeMethods.CREATE_VIRTUAL_DISK_FLAG_FULL_PHYSICAL_ALLOCATION : 0;

            if (File.Exists(fileName)) { throw new IOException(string.Format("\"{0}\" already exists.", Path.GetFileName(fileName))); }
            var overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            var created = false;
            using (var doneEvent = new ManualResetEvent(false)) {
                try {
                    Marshal.StructureToPtr(new NativeOverlapped { EventHandle = doneEvent.SafeWaitHandle.DangerousGetHandle() }, overlappedPtr, false);
                    var res = NativeMethods.CreateVirtualDisk(ref storageType, fileName, 0, IntPtr.Zero, flags, 0, ref parameters, overlappedPtr, out var handle);
                    using (handle) {
                        if ((res != NativeMethods.ERROR_SUCCESS) && (res != NativeMethods.ERROR_IO_PENDING)) { ThrowOnError(res, fileName); }
                        created = true; //from here on the file is ours and may be removed on failure
                        try {
                            WaitForCompletion(handle, fileName, overlappedPtr, doneEvent, progress, cancellationToken);
                        } finally {
                            if (res == NativeMethods.ERROR_IO_PENDING) { doneEvent.WaitOne(); } //kernel still owns the OVERLAPPED until completion
                        }
                    }
                } catch {
                    if (created) { TryDelete(fileName); } //incomplete disk; never touches a file we did not create
                    throw;
                } finally {
                    Marshal.FreeHGlobal(overlappedPtr);
                }
            }
        }

        private static void RunAsync(SafeFileHandle handle, string fileName, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken, Func<IntPtr, int> start) {
            var overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            using (var doneEvent = new ManualResetEvent(false)) {
                try {
                    Marshal.StructureToPtr(new NativeOverlapped { EventHandle = doneEvent.SafeWaitHandle.DangerousGetHandle() }, overlappedPtr, false);
                    var res = start(overlappedPtr);
                    if ((res != NativeMethods.ERROR_SUCCESS) && (res != NativeMethods.ERROR_IO_PENDING)) { ThrowOnError(res, fileName); }
                    try {
                        WaitForCompletion(handle, fileName, overlappedPtr, doneEvent, progress, cancellationToken);
                    } finally {
                        if (res == NativeMethods.ERROR_IO_PENDING) { doneEvent.WaitOne(); } //never free the OVERLAPPED or close the handle mid-operation
                    }
                } finally {
                    Marshal.FreeHGlobal(overlappedPtr);
                }
            }
        }

        private static void WaitForCompletion(SafeFileHandle handle, string fileName, IntPtr overlappedPtr, ManualResetEvent doneEvent, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            var cancelled = false;
            while (true) {
                var done = doneEvent.WaitOne(250);
                var res = NativeMethods.GetVirtualDiskOperationProgress(handle, overlappedPtr, out var state);
                if (res != NativeMethods.ERROR_SUCCESS) { //progress unavailable: wait for the real outcome instead of guessing
                    doneEvent.WaitOne();
                    res = NativeMethods.GetVirtualDiskOperationProgress(handle, overlappedPtr, out state);
                    if (res != NativeMethods.ERROR_SUCCESS) { ThrowOnError(res, fileName); }
                    done = true;
                }
                progress?.Report(new VirtualDiskProgress(state.CurrentValue, state.CompletionValue));

                if (done || (state.OperationStatus != NativeMethods.ERROR_IO_PENDING)) {
                    if (state.OperationStatus == NativeMethods.ERROR_SUCCESS) { return; }
                    if (cancelled || (state.OperationStatus == NativeMethods.ERROR_OPERATION_ABORTED)) { throw new OperationCanceledException(cancellationToken); }
                    ThrowOnError(state.OperationStatus, fileName);
                }

                if (!cancelled && cancellationToken.IsCancellationRequested) {
                    cancelled = true;
                    NativeMethods.CancelIoEx(handle, overlappedPtr);
                }
            }
        }

        private static SafeFileHandle Open(string fileName, int accessMask, int flags, int rwDepth) {
            var storageType = GetStorageType(fileName);
            var parameters = new NativeMethods.OPEN_VIRTUAL_DISK_PARAMETERS_V1 { Version = 1, RWDepth = rwDepth };
            var res = NativeMethods.OpenVirtualDisk(ref storageType, fileName, accessMask, flags, ref parameters, out var handle);
            if (res != NativeMethods.ERROR_SUCCESS) {
                handle.Dispose();
                ThrowOnError(res, fileName);
            }
            return handle;
        }

        private static SafeFileHandle OpenV2(string fileName, int flags, bool readOnly) {
            var storageType = GetStorageType(fileName);
            var parameters = new NativeMethods.OPEN_VIRTUAL_DISK_PARAMETERS_V2 { Version = 2, ReadOnly = readOnly ? 1 : 0 };
            var res = NativeMethods.OpenVirtualDisk(ref storageType, fileName, 0, flags, ref parameters, out var handle);
            if (res != NativeMethods.ERROR_SUCCESS) {
                handle.Dispose();
                ThrowOnError(res, fileName);
            }
            return handle;
        }

        /// <summary>
        /// ISO is detected by extension; everything else is left for VirtDisk to detect from the file content,
        /// which also handles .avhd/.avhdx differencing disks and files with a misleading extension.
        /// </summary>
        internal static NativeMethods.VIRTUAL_STORAGE_TYPE GetStorageType(string fileName) {
            if (fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) {
                return new NativeMethods.VIRTUAL_STORAGE_TYPE { DeviceId = NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_ISO, VendorId = NativeMethods.VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT };
            }
            return new NativeMethods.VIRTUAL_STORAGE_TYPE { DeviceId = NativeMethods.VIRTUAL_STORAGE_TYPE_DEVICE_UNKNOWN, VendorId = Guid.Empty };
        }

        private static byte[] GetInfo(SafeFileHandle handle, int version) {
            var size = 4096;
            var buffer = Marshal.AllocHGlobal(size);
            try {
                for (int i = 0; i < size; i += 4) { Marshal.WriteInt32(buffer, i, 0); }
                Marshal.WriteInt32(buffer, 0, version);
                var sizeUsed = 0;
                var res = NativeMethods.GetVirtualDiskInformation(handle, ref size, buffer, ref sizeUsed);
                if (res != NativeMethods.ERROR_SUCCESS) { return null; } //not supported for this format/OS
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string GetAttachedPath(SafeFileHandle handle) {
            var size = 1024;
            var path = new StringBuilder(size / 2);
            var res = NativeMethods.GetVirtualDiskPhysicalPath(handle, ref size, path);
            LastPhysicalPathError = res;
            return (res == NativeMethods.ERROR_SUCCESS) ? path.ToString() : null;
        }

        /// <summary>
        /// Win32 result of the last GetVirtualDiskPhysicalPath call (diagnostics).
        /// </summary>
        internal static int LastPhysicalPathError;

        private static Guid ReadGuid(byte[] buffer, int offset) {
            var bytes = new byte[16];
            Buffer.BlockCopy(buffer, offset, bytes, 0, 16);
            return new Guid(bytes);
        }

        internal static IList<string> ReadMultiString(byte[] buffer, int offset) {
            var list = new List<string>();
            var text = Encoding.Unicode.GetString(buffer, offset, buffer.Length - offset);
            foreach (var part in text.Split('\0')) {
                if (part.Length == 0) { break; }
                list.Add(part);
            }
            return list;
        }

        private static void TryDelete(string fileName) {
            try { File.Delete(fileName); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static void ThrowOnError(int error, string fileName) {
            if (error == NativeMethods.ERROR_SUCCESS) { return; }
            var name = Path.GetFileName(fileName);
            switch (error) {
                case NativeMethods.ERROR_FILE_NOT_FOUND:
                case NativeMethods.ERROR_PATH_NOT_FOUND:
                    throw new FileNotFoundException(string.Format("File \"{0}\" was not found.", name), fileName);
                case NativeMethods.ERROR_ACCESS_DENIED:
                    if (VhdxHeader.NeedsLogReplay(fileName)) {
                        throw new UnauthorizedAccessException(string.Format("\"{0}\" was not closed cleanly and its log has to be replayed before it can be used. Use Repair → Replay log (requires write access to the file).", name));
                    }
                    throw new UnauthorizedAccessException(string.Format("Access to \"{0}\" was denied. The file may be in use, read-only, or the operation requires administrator rights.", name));
                case NativeMethods.ERROR_FILE_SYSTEM_LIMITATION:
                case NativeMethods.ERROR_VHD_INVALID_FILE_ATTRIBUTES:
                    throw new IOException(string.Format("Windows cannot use \"{0}\" as a virtual disk: virtual disk files must not be compressed, encrypted or sparse. This usually comes from a compressed or encrypted folder. Use Maintenance → Repair → Make usable, or choose another folder.", name));
                case NativeMethods.ERROR_SHARING_VIOLATION:
                    throw new IOException(string.Format("\"{0}\" is in use by another process (for example a running VM, WSL or Docker).", name));
                case NativeMethods.ERROR_FILE_EXISTS:
                    throw new IOException(string.Format("\"{0}\" already exists.", name));
                case NativeMethods.ERROR_NOT_SUPPORTED:
                case NativeMethods.ERROR_INVALID_PARAMETER:
                    throw new NotSupportedException(string.Format("Operation is not supported for \"{0}\" (check disk type, format and size). ({1})", name, new Win32Exception(error).Message));
                case NativeMethods.ERROR_VHD_INVALID_TYPE:
                    throw new NotSupportedException(string.Format("Operation is not supported for this type of disk (\"{0}\").", name));
                case NativeMethods.ERROR_VIRTDISK_NOT_VIRTUAL_DISK:
                case NativeMethods.ERROR_FILE_CORRUPT:
                    throw new InvalidDataException(string.Format("\"{0}\" is not a valid virtual disk or is corrupted.", name));
                case NativeMethods.ERROR_VIRTDISK_PROVIDER_NOT_FOUND:
                    throw new NotSupportedException(string.Format("Format of \"{0}\" is not supported by this version of Windows.", name));
                default:
                    throw new Win32Exception(error, string.Format("{0} ({1})", new Win32Exception(error).Message, name));
            }
        }

        #endregion


        internal static class NativeMethods {

            public const int ERROR_SUCCESS = 0;
            public const int ERROR_FILE_NOT_FOUND = 2;
            public const int ERROR_PATH_NOT_FOUND = 3;
            public const int ERROR_ACCESS_DENIED = 5;
            public const int ERROR_SHARING_VIOLATION = 32;
            public const int ERROR_FILE_SYSTEM_LIMITATION = 665;
            public const int ERROR_VHD_INVALID_FILE_ATTRIBUTES = unchecked((int)0xC03A001A); //compressed, encrypted or sparse
            public const int ERROR_NOT_SUPPORTED = 50;
            public const int ERROR_FILE_EXISTS = 80;
            public const int ERROR_INVALID_PARAMETER = 87;
            public const int ERROR_OPERATION_ABORTED = 995;
            public const int ERROR_IO_PENDING = 997;
            public const int ERROR_FILE_CORRUPT = 1392;
            public const int ERROR_VIRTDISK_PROVIDER_NOT_FOUND = unchecked((int)0xC03A0014);
            public const int ERROR_VIRTDISK_NOT_VIRTUAL_DISK = unchecked((int)0xC03A0015);
            public const int ERROR_VHD_INVALID_TYPE = unchecked((int)0xC03A001B);

            public const int VIRTUAL_STORAGE_TYPE_DEVICE_UNKNOWN = 0;
            public const int VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;
            public const int VIRTUAL_STORAGE_TYPE_DEVICE_VHD = 2;
            public const int VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;
            public const int VIRTUAL_STORAGE_TYPE_DEVICE_VHDSET = 4;
            public static readonly Guid VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B");

            public const int VIRTUAL_DISK_ACCESS_ATTACH_RO = 0x00010000;
            public const int VIRTUAL_DISK_ACCESS_DETACH = 0x00040000;
            public const int VIRTUAL_DISK_ACCESS_GET_INFO = 0x00080000;
            public const int VIRTUAL_DISK_ACCESS_METAOPS = 0x00200000;
            public const int VIRTUAL_DISK_ACCESS_ALL = 0x003f0000;

            public const int OPEN_VIRTUAL_DISK_FLAG_NONE = 0;
            public const int OPEN_VIRTUAL_DISK_FLAG_NO_PARENTS = 1;

            public const int ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x1;
            public const int ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x2;

            public const int CREATE_VIRTUAL_DISK_FLAG_FULL_PHYSICAL_ALLOCATION = 0x1;
            public const int RESIZE_VIRTUAL_DISK_FLAG_RESIZE_TO_SMALLEST_SAFE_VIRTUAL_SIZE = 0x2;

            public const int GET_VIRTUAL_DISK_INFO_SIZE = 1;
            public const int GET_VIRTUAL_DISK_INFO_IDENTIFIER = 2;
            public const int GET_VIRTUAL_DISK_INFO_PARENT_LOCATION = 3;
            public const int GET_VIRTUAL_DISK_INFO_PARENT_IDENTIFIER = 4;
            public const int GET_VIRTUAL_DISK_INFO_VIRTUAL_STORAGE_TYPE = 6;
            public const int GET_VIRTUAL_DISK_INFO_PROVIDER_SUBTYPE = 7;
            public const int GET_VIRTUAL_DISK_INFO_IS_4K_ALIGNED = 8;
            public const int GET_VIRTUAL_DISK_INFO_VHD_PHYSICAL_SECTOR_SIZE = 10;
            public const int GET_VIRTUAL_DISK_INFO_SMALLEST_SAFE_VIRTUAL_SIZE = 11;
            public const int GET_VIRTUAL_DISK_INFO_FRAGMENTATION = 12;
            public const int GET_VIRTUAL_DISK_INFO_IS_LOADED = 13;

            public const int SET_VIRTUAL_DISK_INFO_PARENT_PATH_VERSION = 1;
            public const int SET_VIRTUAL_DISK_INFO_IDENTIFIER_VERSION = 2;


            [StructLayout(LayoutKind.Sequential)]
            public struct VIRTUAL_STORAGE_TYPE {
                public int DeviceId;
                public Guid VendorId;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct OPEN_VIRTUAL_DISK_PARAMETERS_V1 {
                public int Version;
                public int RWDepth;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct OPEN_VIRTUAL_DISK_PARAMETERS_V2 {
                public int Version;
                public int GetInfoOnly;
                public int ReadOnly;
                public Guid ResiliencyGuid;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct ATTACH_VIRTUAL_DISK_PARAMETERS {
                public int Version;
                public long Reserved1;
                public long Reserved2;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct COMPACT_VIRTUAL_DISK_PARAMETERS {
                public int Version;
                public int Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct EXPAND_VIRTUAL_DISK_PARAMETERS {
                public int Version;
                public long NewSize;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct RESIZE_VIRTUAL_DISK_PARAMETERS {
                public int Version;
                public long NewSize;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MERGE_VIRTUAL_DISK_PARAMETERS {
                public int Version;
                public int MergeDepth;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct SET_VIRTUAL_DISK_INFO_PARENT_PATH {
                public int Version;
                [MarshalAs(UnmanagedType.LPWStr)] public string ParentFilePath;
            }


            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct CREATE_VIRTUAL_DISK_PARAMETERS_V2 {
                public int Version;
                public CREATE_VIRTUAL_DISK_PARAMETERS_V2_DATA Version2; //nested so the union lands on its 8-byte boundary
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct CREATE_VIRTUAL_DISK_PARAMETERS_V2_DATA {
                public Guid UniqueId;
                public long MaximumSize;
                public int BlockSizeInBytes;
                public int SectorSizeInBytes;
                public int PhysicalSectorSizeInBytes;
                [MarshalAs(UnmanagedType.LPWStr)] public string ParentPath;
                [MarshalAs(UnmanagedType.LPWStr)] public string SourcePath;
                public int OpenFlags;
                public VIRTUAL_STORAGE_TYPE ParentVirtualStorageType;
                public VIRTUAL_STORAGE_TYPE SourceVirtualStorageType;
                public Guid ResiliencyGuid;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct VIRTUAL_DISK_PROGRESS {
                public int OperationStatus;
                public long CurrentValue;
                public long CompletionValue;
            }


            [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
            public static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE VirtualStorageType, string Path, int VirtualDiskAccessMask, int Flags, ref OPEN_VIRTUAL_DISK_PARAMETERS_V1 Parameters, out SafeFileHandle Handle);

            [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
            public static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE VirtualStorageType, string Path, int VirtualDiskAccessMask, int Flags, ref OPEN_VIRTUAL_DISK_PARAMETERS_V2 Parameters, out SafeFileHandle Handle);

            [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
            public static extern int CreateVirtualDisk(ref VIRTUAL_STORAGE_TYPE VirtualStorageType, string Path, int VirtualDiskAccessMask, IntPtr SecurityDescriptor, int Flags, int ProviderSpecificFlags, ref CREATE_VIRTUAL_DISK_PARAMETERS_V2 Parameters, IntPtr Overlapped, out SafeFileHandle Handle);

            [DllImport("virtdisk.dll")]
            public static extern int AttachVirtualDisk(SafeFileHandle VirtualDiskHandle, IntPtr SecurityDescriptor, int Flags, int ProviderSpecificFlags, ref ATTACH_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

            [DllImport("virtdisk.dll")]
            public static extern int DetachVirtualDisk(SafeFileHandle VirtualDiskHandle, int Flags, int ProviderSpecificFlags);

            [DllImport("virtdisk.dll")]
            public static extern int CompactVirtualDisk(SafeFileHandle VirtualDiskHandle, int Flags, ref COMPACT_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

            [DllImport("virtdisk.dll")]
            public static extern int ExpandVirtualDisk(SafeFileHandle VirtualDiskHandle, int Flags, ref EXPAND_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

            [DllImport("virtdisk.dll")]
            public static extern int ResizeVirtualDisk(SafeFileHandle VirtualDiskHandle, int Flags, ref RESIZE_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

            [DllImport("virtdisk.dll")]
            public static extern int MergeVirtualDisk(SafeFileHandle VirtualDiskHandle, int Flags, ref MERGE_VIRTUAL_DISK_PARAMETERS Parameters, IntPtr Overlapped);

            [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
            public static extern int SetVirtualDiskInformation(SafeFileHandle VirtualDiskHandle, ref SET_VIRTUAL_DISK_INFO_PARENT_PATH VirtualDiskInfo);

            [DllImport("virtdisk.dll")]
            public static extern int SetVirtualDiskInformation(SafeFileHandle VirtualDiskHandle, IntPtr VirtualDiskInfo);

            [DllImport("virtdisk.dll")]
            public static extern int GetVirtualDiskInformation(SafeFileHandle VirtualDiskHandle, ref int VirtualDiskInfoSize, IntPtr VirtualDiskInfo, ref int SizeUsed);

            [DllImport("virtdisk.dll")]
            public static extern int GetVirtualDiskOperationProgress(SafeFileHandle VirtualDiskHandle, IntPtr Overlapped, out VIRTUAL_DISK_PROGRESS Progress);

            [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
            public static extern int GetVirtualDiskPhysicalPath(SafeFileHandle VirtualDiskHandle, ref int DiskPathSizeInBytes, StringBuilder DiskPath);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, int cchBuffer);

        }

    }

}
