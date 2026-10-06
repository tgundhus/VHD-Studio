using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    [TestClass()]
    public class VirtualDiskImageTest {

        [TestMethod()]
        public void Test_Crc32C_KnownVector() {
            Assert.AreEqual(0xE3069283u, VhdxHeader.Crc32C(Encoding.ASCII.GetBytes("123456789")));
        }

        [TestMethod()]
        public void Test_VhdxHeader_CleanFile() {
            var buffer = CreateVhdxPrefix();
            WriteHeader(buffer, 64 * 1024, 5, Guid.Empty);
            WriteHeader(buffer, 128 * 1024, 4, Guid.NewGuid()); //older header with log must be ignored
            var header = VhdxHeader.Parse(buffer);
            Assert.IsNotNull(header);
            Assert.AreEqual(5, header.SequenceNumber);
            Assert.IsFalse(header.HasPendingLog);
        }

        [TestMethod()]
        public void Test_VhdxHeader_PendingLog() {
            var logGuid = Guid.NewGuid();
            var buffer = CreateVhdxPrefix();
            WriteHeader(buffer, 64 * 1024, 336, Guid.Empty);
            WriteHeader(buffer, 128 * 1024, 337, logGuid);
            var header = VhdxHeader.Parse(buffer);
            Assert.AreEqual(337, header.SequenceNumber);
            Assert.AreEqual(logGuid, header.LogGuid);
            Assert.IsTrue(header.HasPendingLog);
        }

        [TestMethod()]
        public void Test_VhdxHeader_CorruptChecksumIgnored() {
            var buffer = CreateVhdxPrefix();
            WriteHeader(buffer, 64 * 1024, 10, Guid.Empty);
            WriteHeader(buffer, 128 * 1024, 11, Guid.NewGuid());
            buffer[128 * 1024 + 100] ^= 0xFF; //torn write in newer header
            var header = VhdxHeader.Parse(buffer);
            Assert.AreEqual(10, header.SequenceNumber);
            Assert.IsFalse(header.HasPendingLog);
        }

        [TestMethod()]
        public void Test_VhdxHeader_NotVhdx() {
            Assert.IsNull(VhdxHeader.Parse(new byte[192 * 1024]));
            Assert.IsNull(VhdxHeader.Parse(new byte[10]));
        }

        [TestMethod()]
        public void Test_ReadMultiString() {
            var bytes = new byte[12].Concat(Encoding.Unicode.GetBytes("C:\\a.vhdx\0\\\\?\\D:\\b.vhdx\0\0"));
            var list = VirtualDiskImage.ReadMultiString(bytes, 12);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("C:\\a.vhdx", list[0]);
            Assert.AreEqual("\\\\?\\D:\\b.vhdx", list[1]);
        }

        [TestMethod()]
        public void Test_StorageType_AutoDetectsDifferencing() { //.avhdx used to be opened as VHD
            Assert.AreEqual(0, VirtualDiskImage.GetStorageType(@"C:\VMs\disk_1A2B.avhdx").DeviceId);
            Assert.AreEqual(1, VirtualDiskImage.GetStorageType(@"C:\Images\setup.ISO").DeviceId);
        }

        [TestMethod()]
        public void Test_GetDetails_RealVhdx() {
            var fileName = Path.Combine(Path.GetTempPath(), "VhdStudioTest_" + Guid.NewGuid().ToString("N") + ".vhdx");
            try {
                try {
                    using (var disk = new Medo.IO.VirtualDisk(fileName)) {
                        disk.Create(64 * 1024 * 1024, Medo.IO.VirtualDiskCreateOptions.None, 0, 0, Medo.IO.VirtualDiskType.Vhdx);
                    }
                } catch (Exception ex) {
                    Assert.Inconclusive("Cannot create virtual disk in this environment: " + ex.Message);
                }

                var details = VirtualDiskImage.GetDetails(fileName);
                Assert.AreEqual("VHDX", details.Format);
                Assert.AreEqual(VirtualDiskKind.Dynamic, details.Kind);
                Assert.AreEqual(64L * 1024 * 1024, details.VirtualSize);
                Assert.IsNotNull(details.Identifier);
                Assert.IsNull(details.AttachedPath);
                Assert.IsFalse(details.NeedsLogReplay);
            } finally {
                File.Delete(fileName);
            }
        }


        private static byte[] CreateVhdxPrefix() {
            var buffer = new byte[192 * 1024];
            Encoding.ASCII.GetBytes("vhdxfile").CopyTo(buffer, 0);
            return buffer;
        }

        private static void WriteHeader(byte[] buffer, int offset, long sequence, Guid logGuid) {
            var header = new byte[4096];
            Encoding.ASCII.GetBytes("head").CopyTo(header, 0);
            BitConverter.GetBytes(sequence).CopyTo(header, 8);
            Guid.NewGuid().ToByteArray().CopyTo(header, 16);
            Guid.NewGuid().ToByteArray().CopyTo(header, 32);
            logGuid.ToByteArray().CopyTo(header, 48);
            BitConverter.GetBytes((ushort)1).CopyTo(header, 66);
            BitConverter.GetBytes(1024 * 1024).CopyTo(header, 68);
            BitConverter.GetBytes(1024L * 1024).CopyTo(header, 72);
            BitConverter.GetBytes(VhdxHeader.Crc32C(header)).CopyTo(header, 4);
            header.CopyTo(buffer, offset);
        }

    }

    internal static class ByteArrayExtensions {
        public static byte[] Concat(this byte[] first, byte[] second) {
            var result = new byte[first.Length + second.Length];
            first.CopyTo(result, 0);
            second.CopyTo(result, first.Length);
            return result;
        }
    }

}
