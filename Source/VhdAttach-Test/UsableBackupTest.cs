using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Backups must be usable as virtual disks even when the folder compresses new files,
    /// and files that Windows refuses (compressed, sparse) can be repaired without changing their content.
    /// </summary>
    [TestClass()]
    public class UsableBackupTest {

        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = Path.Combine(Path.GetTempPath(), "VhdStudioUsable-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(this.Folder);
        }

        [TestCleanup]
        public void Cleanup() => Directory.Delete(this.Folder, true);

        private string NewDisk(string folder, string name) {
            var path = Path.Combine(folder, name);
            try {
                VirtualDiskImage.Create(path, 64L * 1024 * 1024, false, null, CancellationToken.None);
            } catch (Exception ex) {
                Assert.Inconclusive("Cannot create virtual disks here: " + ex.Message);
            }
            return path;
        }

        private static void Compact(string arguments) {
            using (var p = Process.Start(new ProcessStartInfo("compact.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })) {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
            }
        }

        private static string Hash(string path) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { return Convert.ToHexString(SHA256.HashData(stream)); }
        }

        [TestMethod()]
        public void Test_BackupIntoCompressedFolder_IsUsableVirtualDisk() {
            var source = NewDisk(this.Folder, "source.vhdx");
            var compressed = Directory.CreateDirectory(Path.Combine(this.Folder, "compressed")).FullName;
            Compact("/c /q \"" + compressed + "\""); //new files in this folder are compressed by default
            Assert.IsTrue((File.GetAttributes(compressed) & FileAttributes.Compressed) != 0, "test setup: folder must compress new files");

            var backup = Path.Combine(compressed, "source.backup.vhdx");
            VirtualDiskBackup.Create(source, backup, null, CancellationToken.None);

            Assert.IsNull(PlainFile.GetProblem(backup), "Backup must be stored plainly.");
            Assert.AreEqual("VHDX", VirtualDiskImage.GetDetails(backup).Format, "Windows must be able to open the backup as a virtual disk.");
            Assert.AreEqual(Hash(source), Hash(backup));
        }

        [TestMethod()]
        public void Test_MakePlain_CompressedDisk_BecomesUsable_ContentUnchanged() {
            var disk = NewDisk(this.Folder, "disk.vhdx");
            var before = Hash(disk);
            Compact("/c /q \"" + disk + "\"");
            Assert.IsNotNull(PlainFile.GetProblem(disk), "test setup: file must be compressed");
            var refused = false;
            try { VirtualDiskImage.GetDetails(disk); } catch (IOException) { refused = true; }
            Assert.IsTrue(refused, "Windows refuses compressed virtual disk files.");

            PlainFile.MakePlain(disk);

            Assert.IsNull(PlainFile.GetProblem(disk));
            Assert.AreEqual(before, Hash(disk), "Content must not change.");
            Assert.AreEqual("VHDX", VirtualDiskImage.GetDetails(disk).Format);
        }

        [TestMethod()]
        public void Test_MakePlain_SparseFile_BecomesPlain_ContentUnchanged() {
            var disk = NewDisk(this.Folder, "sparse.vhdx");
            var before = Hash(disk);
            using (var handle = File.OpenHandle(disk, FileMode.Open, FileAccess.ReadWrite)) {
                byte setSparse = 1;
                int returned = 0;
                Assert.IsTrue(DeviceIoControl(handle, 0x000900C4, ref setSparse, 1, IntPtr.Zero, 0, ref returned, IntPtr.Zero), "test setup: set sparse");
            }
            Assert.IsNotNull(PlainFile.GetProblem(disk));

            PlainFile.MakePlain(disk);

            Assert.IsNull(PlainFile.GetProblem(disk));
            Assert.AreEqual(before, Hash(disk));
            Assert.AreEqual("VHDX", VirtualDiskImage.GetDetails(disk).Format);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint code, ref byte inBuffer, int inSize, IntPtr outBuffer, int outSize, ref int returned, IntPtr overlapped);

    }
}
