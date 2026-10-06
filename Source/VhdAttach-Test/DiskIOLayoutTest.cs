using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VhdAttachTest {
    [TestClass()]
    public class DiskIOLayoutTest {
        [TestMethod()]
        public void Test_PartitionInformationMbr_MatchesWinIoCtl() { //review finding L4
            var type = typeof(VhdAttachService.DiskIO).GetNestedType("NativeMethods", System.Reflection.BindingFlags.NonPublic).GetNestedType("PARTITION_INFORMATION_MBR", System.Reflection.BindingFlags.Public);
            Assert.AreEqual(1, (int)Marshal.OffsetOf(type, "BootIndicator"));
            Assert.AreEqual(2, (int)Marshal.OffsetOf(type, "RecognizedPartition"));
            Assert.AreEqual(4, (int)Marshal.OffsetOf(type, "HiddenSectors"));
            var ex = typeof(VhdAttachService.DiskIO).GetNestedType("NativeMethods", System.Reflection.BindingFlags.NonPublic).GetNestedType("PARTITION_INFORMATION_EX", System.Reflection.BindingFlags.Public);
            Assert.AreEqual(28, (int)Marshal.OffsetOf(ex, "RewritePartition"));
            Assert.AreEqual(32, (int)Marshal.OffsetOf(ex, "Mbr"));
        }
    }
}
