using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Storage;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// End-to-end data-safety tests on real virtual disks. Every test writes random files, runs an operation,
    /// and verifies all data byte-for-byte afterwards. Requires an elevated run:
    ///   dotnet test --filter TestCategory=Elevated
    /// Only scratch disks created by the tests themselves are touched (see ScratchDisk).
    /// </summary>
    [TestClass()]
    [TestCategory("Elevated")]
    [DoNotParallelize]
    public class DataSafetyTest {

        private const int MB = 1024 * 1024;

        [TestInitialize]
        public void Init() => ScratchDisk.RequireElevation();


        [TestMethod()]
        public void Compact_KeepsAllData_AndShrinksFile() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var keep = disk.WriteFiles(6, 8 * MB, "keep");
                var drop = disk.WriteFiles(12, 8 * MB, "drop");
                disk.DeleteFiles(drop.Keys);
                StorageManager.RetrimVolume(disk.DataPartition());
                disk.Detach();
                var before = new FileInfo(disk.FileName).Length;

                VirtualDiskImage.Compact(disk.FileName, true, null, CancellationToken.None);

                Assert.IsTrue(new FileInfo(disk.FileName).Length <= before, "Compact must never grow the file.");
                disk.Verify(keep);
            }
        }

        [TestMethod()]
        public void Compact_Cancelled_KeepsAllData() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var keep = disk.WriteFiles(4, 8 * MB);
                disk.Detach();
                using (var cts = new CancellationTokenSource()) {
                    cts.Cancel();
                    try { VirtualDiskImage.Compact(disk.FileName, true, null, cts.Token); } catch (OperationCanceledException) { }
                }
                disk.Verify(keep);
            }
        }

        [TestMethod()]
        public void Resize_Grow_KeepsAllData() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(4, 4 * MB);
                disk.Detach();

                VirtualDiskImage.Resize(disk.FileName, 2L << 30, null);

                Assert.AreEqual(2L << 30, VirtualDiskImage.GetDetails(disk.FileName).VirtualSize);
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void Resize_GrowAndExtendPartition_KeepsAllData() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(4, 4 * MB);
                var before = disk.DataPartition().Size;
                disk.Detach();

                VirtualDiskImage.Resize(disk.FileName, 2L << 30, null);
                var message = PartitionExtender.ExtendLastPartition(disk.FileName);
                StringAssert.Contains(message, "extended");
                Assert.IsFalse(VirtualDiskImage.IsAttached(disk.FileName), "Extender must detach the disk again.");

                disk.Attach(false);
                var after = disk.DataPartition().Size;
                disk.Detach();
                Assert.IsTrue(after > before + (900L * MB), string.Format("Partition should use the new space ({0} -> {1}).", before, after));
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void ExtendOnline_AttachedDiskWithUnusedSpace_KeepsAllData() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(4, 4 * MB);
                disk.Detach();
                VirtualDiskImage.Resize(disk.FileName, 2L << 30, null); //grown, partition not extended (the reported situation)

                disk.Attach(false);
                var found = PartitionExtender.FindExtendable(disk.FileName);
                Assert.IsNotNull(found, "Unused space at the end must be detected.");
                Assert.IsTrue(found.Value.Gain > 900L * MB);
                using (new FileStream(Path.Combine(disk.WaitForVolume(), "open-while-extending.txt"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    StringAssert.Contains(PartitionExtender.ExtendOnline(StorageManager.Revalidate(found.Value.Disk)), "extended"); //safe while in use
                }
                Assert.IsNull(PartitionExtender.FindExtendable(disk.FileName), "Nothing left to extend.");
                disk.Detach();
                disk.Verify(data);
            }
        }
        [TestMethod()]
        public void Resize_ShrinkToSmallestSafe_KeepsAllData() {
            using (var disk = ScratchDisk.Create(2L << 30)) {
                disk.Attach(false);
                var data = disk.WriteFiles(4, 4 * MB);
                var partition = disk.DataPartition();
                var (min, _) = StorageManager.GetSupportedSize(partition);
                StorageManager.ResizePartition(disk.StorageDisk(), partition, Math.Max(min, 512L * MB));
                disk.Detach();
                var before = VirtualDiskImage.GetDetails(disk.FileName).VirtualSize;

                VirtualDiskImage.Resize(disk.FileName, 0, null);

                Assert.IsTrue(VirtualDiskImage.GetDetails(disk.FileName).VirtualSize < before, "Disk should be smaller.");
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void Resize_BelowSmallestSafe_IsRefused_AndChangesNothing() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                disk.Detach();
                var hash = ScratchDisk.HashFile(disk.FileName);

                Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.Resize(disk.FileName, 64L * MB, null));

                Assert.AreEqual(hash, ScratchDisk.HashFile(disk.FileName), "Refused resize must not touch the file.");
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void Operations_OnAttachedDisk_AreRefused() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.Resize(disk.FileName, 2L << 30, null));
                Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.Compact(disk.FileName, true, null, CancellationToken.None));
                Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.ResetIdentifier(disk.FileName));
                Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.Convert(disk.FileName, ScratchDisk.NewPath(), false, 0, null, CancellationToken.None));
                disk.Detach();
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void Convert_ToVhdAndFixed_CopiesAllData_SourceUntouched() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(4, 8 * MB);
                disk.Detach();
                var sourceHash = ScratchDisk.HashFile(disk.FileName);

                foreach (var (extension, isFixed) in new[] { (".vhd", false), (".vhdx", true) }) {
                    using (var converted = ScratchDisk.Open(ScratchDisk.NewPath(extension))) {
                        VirtualDiskImage.Convert(disk.FileName, converted.FileName, isFixed, 0, null, CancellationToken.None);
                        Assert.AreEqual(isFixed ? VirtualDiskKind.Fixed : VirtualDiskKind.Dynamic, VirtualDiskImage.GetDetails(converted.FileName).Kind);
                        converted.Verify(data);
                    }
                }
                Assert.AreEqual(sourceHash, ScratchDisk.HashFile(disk.FileName), "Convert must never modify the source.");
            }
        }

        [TestMethod()]
        public void Convert_Cancelled_LeavesNoPartialFile() {
            using (var disk = ScratchDisk.Create()) {
                var destination = ScratchDisk.NewPath();
                using (var cts = new CancellationTokenSource()) {
                    var progress = new SyncProgress(_ => cts.Cancel());
                    try {
                        VirtualDiskImage.Convert(disk.FileName, destination, true, 0, progress, cts.Token);
                        File.Delete(destination); //finished before cancellation took effect: complete, verified copy
                    } catch (OperationCanceledException) {
                        Assert.IsFalse(File.Exists(destination), "Cancelled conversion must not leave a partial disk.");
                    }
                }
            }
        }

        [TestMethod()]
        public void Convert_NeverOverwritesExistingFile() {
            using (var disk = ScratchDisk.Create()) {
                var existing = ScratchDisk.NewPath();
                File.WriteAllText(existing, "precious");
                try {
                    Assert.ThrowsExactly<IOException>(() => VirtualDiskImage.Convert(disk.FileName, existing, false, 0, null, CancellationToken.None));
                    Assert.AreEqual("precious", File.ReadAllText(existing));
                } finally {
                    File.Delete(existing);
                }
            }
        }

        [TestMethod()]
        public void Differencing_ProtectsParent_AndMergeKeepsAllData() {
            using (var parent = ScratchDisk.Create()) {
                parent.Attach(false);
                var baseData = parent.WriteFiles(3, 4 * MB, "base");
                parent.Detach();
                var parentHash = ScratchDisk.HashFile(parent.FileName);

                using (var child = ScratchDisk.Open(ScratchDisk.NewPath(".avhdx"))) {
                    VirtualDiskImage.CreateDifferencing(parent.FileName, child.FileName, protectParent: true);
                    Assert.IsTrue((File.GetAttributes(parent.FileName) & FileAttributes.ReadOnly) != 0, "Parent must be protected.");

                    child.Attach(false);
                    var childData = child.WriteFiles(3, 4 * MB, "child");
                    child.Detach();
                    Assert.AreEqual(parentHash, ScratchDisk.HashFile(parent.FileName), "Writes to the child must never reach the parent.");

                    var all = baseData.Concat(childData).ToDictionary(p => p.Key, p => p.Value);
                    child.Verify(all);

                    Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.MergeIntoParent(child.FileName, null), "Merging into a protected parent requires a deliberate unprotect.");
                    Assert.AreEqual(parentHash, ScratchDisk.HashFile(parent.FileName));

                    File.SetAttributes(parent.FileName, FileAttributes.Normal);
                    VirtualDiskImage.MergeIntoParent(child.FileName, null);
                    parent.Verify(all);
                }
            }
        }

        [TestMethod()]
        public void SetParentPath_WrongParent_IsRefused_AndChainStillWorks() {
            using (var parent = ScratchDisk.Create())
            using (var stranger = ScratchDisk.Create()) {
                parent.Attach(false);
                var data = parent.WriteFiles(2, 4 * MB);
                parent.Detach();
                using (var child = ScratchDisk.Open(ScratchDisk.NewPath(".avhdx"))) {
                    VirtualDiskImage.CreateDifferencing(parent.FileName, child.FileName, protectParent: false);

                    Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.SetParentPath(child.FileName, stranger.FileName));

                    var details = VirtualDiskImage.GetDetails(child.FileName);
                    Assert.AreNotEqual(false, details.ParentResolved, "Previous parent must be restored.");
                    child.Verify(data);

                    VirtualDiskImage.SetParentPath(child.FileName, parent.FileName); //the real parent is accepted
                    child.Verify(data);
                }
            }
        }

        [TestMethod()]
        public void SafeDetach_WithOpenFile_IsRefused_ThenSucceeds() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                var open = new FileStream(Path.Combine(disk.WaitForVolume(), "open.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
                try {
                    open.Write(new byte[] { 1, 2, 3 }, 0, 3);
                    var ex = Assert.ThrowsExactly<VolumeInUseException>(() => VhdAttachService.AttachHelper.Detach(disk.FileName, false, VhdAttachService.PipeCaller.ForService()));
                    Assert.IsNotNull(disk.PhysicalPath, "Disk must stay attached when files are open.");
                } finally {
                    open.Dispose();
                }
                disk.Detach();
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void InitializeDisk_RefusesDiskWithData() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                Assert.ThrowsExactly<InvalidOperationException>(() => VhdAttachService.DiskIO.InitializeDisk(disk.PhysicalPath));
                disk.Detach();
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void DiskManager_DeleteAndFormat_RefusedWhileInUse() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                using (new FileStream(Path.Combine(disk.WaitForVolume(), "open.txt"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    Assert.ThrowsExactly<VolumeInUseException>(() => StorageManager.DeletePartition(disk.StorageDisk(), disk.DataPartition()));
                    Assert.ThrowsExactly<VolumeInUseException>(() => StorageManager.FormatPartition(disk.StorageDisk(), disk.DataPartition(), "NTFS", "x", true));
                    Assert.ThrowsExactly<VolumeInUseException>(() => StorageManager.CleanDisk(disk.StorageDisk()));
                }
                disk.Detach();
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void DiskManager_StaleSelection_IsRefused() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(1, 4 * MB);
                var info = disk.StorageDisk();
                var partition = disk.DataPartition();
                var stale = new PartitionInfo { DiskNumber = partition.DiskNumber, PartitionNumber = partition.PartitionNumber, ObjectPath = partition.ObjectPath, Offset = partition.Offset, Size = partition.Size + 1 };
                Assert.ThrowsExactly<InvalidOperationException>(() => StorageManager.DeletePartition(info, stale));
                var otherDisk = new DiskInfo { Number = info.Number, ObjectPath = info.ObjectPath, Size = info.Size, IsVirtual = true, Location = @"C:\other.vhdx", SerialNumber = info.SerialNumber };
                Assert.ThrowsExactly<InvalidOperationException>(() => StorageManager.CleanDisk(otherDisk));
                disk.Detach();
                disk.Verify(data);
            }
        }

        [TestMethod()]
        public void Backup_IsVerifiedCopy() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                disk.WriteFiles(2, 4 * MB);
                disk.Detach();
                var backup = VirtualDiskBackup.GetDefaultBackupPath(disk.FileName);
                try {
                    VirtualDiskBackup.Create(disk.FileName, backup, null, CancellationToken.None);
                    Assert.AreEqual(ScratchDisk.HashFile(disk.FileName), ScratchDisk.HashFile(backup));
                    Assert.ThrowsExactly<IOException>(() => VirtualDiskBackup.Create(disk.FileName, backup, null, CancellationToken.None), "Never overwrite an existing backup.");
                } finally {
                    File.Delete(backup);
                }
                using (var cts = new CancellationTokenSource()) {
                    cts.Cancel();
                    Assert.ThrowsExactly<OperationCanceledException>(() => VirtualDiskBackup.Create(disk.FileName, backup, null, cts.Token));
                    Assert.IsFalse(File.Exists(backup), "Cancelled backup must not leave a partial copy.");
                }
            }
        }

        [TestMethod()]
        public void ReadOnlyAttach_NeverModifiesFile() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(false);
                var data = disk.WriteFiles(2, 4 * MB);
                disk.Detach();
                var hash = ScratchDisk.HashFile(disk.FileName);
                disk.Verify(data); //attaches read-only
                Assert.AreEqual(hash, ScratchDisk.HashFile(disk.FileName));
            }
        }


        private sealed class SyncProgress : IProgress<VirtualDiskProgress> {
            private readonly Action<VirtualDiskProgress> Handler;
            public SyncProgress(Action<VirtualDiskProgress> handler) { this.Handler = handler; }
            public void Report(VirtualDiskProgress value) => this.Handler(value);
        }

    }
}
