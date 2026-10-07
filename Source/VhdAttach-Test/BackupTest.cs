using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Verified backup copies (no administrator rights needed).
    /// </summary>
    [TestClass()]
    public class BackupTest {

        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = Path.Combine(Path.GetTempPath(), "VhdStudioBackup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(this.Folder);
        }

        [TestCleanup]
        public void Cleanup() => Directory.Delete(this.Folder, true);

        private string NewFile(long size) {
            var path = Path.Combine(this.Folder, "source-" + size + ".bin");
            var data = new byte[size];
            RandomNumberGenerator.Fill(data);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        [DataTestMethod()]
        [DataRow(0L)]                              //empty file
        [DataRow(1L)]                              //smaller than a sector
        [DataRow(4096L)]                           //exactly one sector
        [DataRow(8L * 1024 * 1024)]                //exactly one block
        [DataRow(8L * 1024 * 1024 + 1)]            //one byte into the next block
        [DataRow(37L * 1024 * 1024 + 12345)]       //several blocks with an unaligned tail
        public void Test_Backup_IsExactCopy(long size) {
            var source = NewFile(size);
            var backup = Path.Combine(this.Folder, "backup.bin");
            long lastProgress = -1, lastTotal = 0;
            VirtualDiskBackup.Create(source, backup, new SyncProgress(p => { Assert.IsTrue(p.Current >= lastProgress); lastProgress = p.Current; lastTotal = p.Total; }), CancellationToken.None);
            Assert.AreEqual(new FileInfo(source).Length, new FileInfo(backup).Length);
            Assert.AreEqual(Hash(source), Hash(backup));
            if (size > 0) { Assert.AreEqual(lastTotal, lastProgress, "Progress must reach 100 %."); }
        }

        [TestMethod()]
        public void Test_Backup_NeverOverwrites() {
            var source = NewFile(1024 * 1024);
            var existing = Path.Combine(this.Folder, "existing.bin");
            File.WriteAllText(existing, "precious");
            Assert.ThrowsExactly<IOException>(() => VirtualDiskBackup.Create(source, existing, null, CancellationToken.None));
            Assert.AreEqual("precious", File.ReadAllText(existing));
        }

        [TestMethod()]
        public void Test_Backup_CancelledMidway_LeavesNothing() {
            var source = NewFile(64L * 1024 * 1024);
            var backup = Path.Combine(this.Folder, "backup.bin");
            using (var cancellation = new CancellationTokenSource()) {
                var progress = new SyncProgress(p => { if (p.Current > 16L * 1024 * 1024) { cancellation.Cancel(); } });
                Assert.ThrowsExactly<OperationCanceledException>(() => VirtualDiskBackup.Create(source, backup, progress, cancellation.Token));
            }
            Assert.IsFalse(File.Exists(backup), "A cancelled backup must not leave a partial copy.");
        }

        [TestMethod()]
        public void Test_Backup_RefusesWhileSourceIsBeingWritten() {
            var source = NewFile(1024 * 1024);
            var backup = Path.Combine(this.Folder, "backup.bin");
            using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { //a writer (e.g. a running VM) has the file open
                Assert.ThrowsExactly<IOException>(() => VirtualDiskBackup.Create(source, backup, null, CancellationToken.None));
            }
            Assert.IsFalse(File.Exists(backup));
        }

        private sealed class SyncProgress : IProgress<VirtualDiskProgress> {
            private readonly Action<VirtualDiskProgress> Handler;
            public SyncProgress(Action<VirtualDiskProgress> handler) { this.Handler = handler; }
            public void Report(VirtualDiskProgress value) => this.Handler(value);
        }

    }
}
