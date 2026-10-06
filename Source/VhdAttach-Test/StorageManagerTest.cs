using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Storage;

namespace VhdAttachTest {

    [TestClass()]
    public class StorageManagerTest {

        [TestMethod()]
        public void Test_GetDisks_ReadOnlyEnumeration() {
            var disks = StorageManager.GetDisks();
            Assert.IsTrue(disks.Count > 0, "Expected at least one disk.");
            Assert.IsTrue(disks.Any(d => d.IsProtected), "The system/boot disk must be flagged as protected.");
            foreach (var disk in disks.Where(d => d.PartitionStyle != PartitionStyle.Raw)) {
                var partitions = StorageManager.GetPartitions(disk.Number);
                foreach (var partition in partitions) {
                    Assert.AreEqual(disk.Number, partition.DiskNumber);
                    Assert.IsTrue(partition.Size > 0);
                }
            }
            var withLetter = disks.SelectMany(d => StorageManager.GetPartitions(d.Number)).Where(p => p.DriveLetter.HasValue).ToList();
            Assert.IsTrue(withLetter.Any(p => !string.IsNullOrEmpty(p.FileSystem)), "Volumes should be resolved through MSFT_PartitionToVolume.");
        }

        [TestMethod()]
        public void Test_ProtectedDiskIsRefused() {
            var system = StorageManager.GetDisks().First(d => d.IsProtected);
            Assert.ThrowsExactly<System.InvalidOperationException>(() => StorageManager.CleanDisk(system));
        }

        [TestMethod()]
        public void Test_Describe() {
            var disk = new DiskInfo { Number = 3 };
            var part = new PartitionInfo { DiskNumber = 3, PartitionNumber = 2 };
            Assert.AreEqual("Clear-Disk -Number 3 -RemoveData -RemoveOEM", StorageManager.Describe("Clean", disk));
            Assert.AreEqual("Remove-Partition -DiskNumber 3 -PartitionNumber 2", StorageManager.Describe("Delete", disk, part));
        }

    }
}
