using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Differencing-chain and convert safeguards that can be verified without administrator rights.
    /// </summary>
    [TestClass()]
    public class ChainSafetyTest {

        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = Path.Combine(Path.GetTempPath(), "VhdStudioChain-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(this.Folder);
        }

        [TestCleanup]
        public void Cleanup() {
            foreach (var file in Directory.GetFiles(this.Folder)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(this.Folder, true);
        }

        private string NewDisk(string name) {
            var path = Path.Combine(this.Folder, name);
            try {
                VirtualDiskImage.Create(path, 64L * 1024 * 1024, false, null, CancellationToken.None);
            } catch (Exception ex) {
                Assert.Inconclusive("Cannot create virtual disks here: " + ex.Message);
            }
            return path;
        }

        [TestMethod()]
        public void Test_ParentLinkage_MatchesParentDataWriteGuid() { //review finding M4
            var parent = NewDisk("parent.vhdx");
            var child = Path.Combine(this.Folder, "child.avhdx");
            VirtualDiskImage.CreateDifferencing(parent, child, protectParent: true);

            var linkage = VhdxHeader.ReadParentLinkage(child);
            Assert.IsNotNull(linkage);
            CollectionAssert.Contains(linkage, VhdxHeader.Read(parent).DataWriteGuid);
            Assert.IsNull(VhdxHeader.ReadParentLinkage(parent), "A base disk has no parent locator.");
            Assert.IsTrue((File.GetAttributes(parent) & FileAttributes.ReadOnly) != 0, "Parent is protected.");
        }

        [TestMethod()]
        public void Test_SetParentPath_WrongParent_RefusedBeforeWriting() { //review finding M4
            var parent = NewDisk("parent.vhdx");
            var stranger = NewDisk("stranger.vhdx");
            var child = Path.Combine(this.Folder, "child.avhdx");
            VirtualDiskImage.CreateDifferencing(parent, child, protectParent: false);
            var before = File.ReadAllBytes(child);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => VirtualDiskImage.SetParentPath(child, stranger));
            StringAssert.Contains(ex.Message, "Nothing was changed");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(child), "Refused re-link must not touch the child.");
        }

        [TestMethod()]
        public void Test_FindDependents_FindsChildrenOfParent() { //review finding M2
            var parent = NewDisk("parent.vhdx");
            var child1 = Path.Combine(this.Folder, "a.avhdx");
            var child2 = Path.Combine(this.Folder, "b.avhdx");
            VirtualDiskImage.CreateDifferencing(parent, child1, protectParent: false);
            VirtualDiskImage.CreateDifferencing(parent, child2, protectParent: false);
            NewDisk("unrelated.vhdx");

            var dependents = VirtualDiskImage.FindDependents(parent);
            Assert.AreEqual(2, dependents.Count);
            Assert.AreEqual(0, VirtualDiskImage.FindDependents(child1).Count);
        }

        [TestMethod()]
        public void Test_Convert_RefusesSectorSizeChange() { //review finding M3
            var source = NewDisk("source.vhdx");
            var destination = Path.Combine(this.Folder, "dest.vhdx");
            Assert.ThrowsExactly<NotSupportedException>(() => VirtualDiskImage.Convert(source, destination, false, 4096, null, CancellationToken.None));
            Assert.IsFalse(File.Exists(destination));
        }

        [TestMethod()]
        public void Test_Create_NeverReplacesExistingFile() {
            var existing = Path.Combine(this.Folder, "existing.vhdx");
            File.WriteAllText(existing, "precious");
            Assert.ThrowsExactly<IOException>(() => VirtualDiskImage.Create(existing, 64L * 1024 * 1024, false, null, CancellationToken.None));
            Assert.AreEqual("precious", File.ReadAllText(existing));
        }

        [TestMethod()]
        public void Test_NeedsLogReplay_FalseWhileFileIsOpenForWriting() { //review finding L1
            var disk = NewDisk("busy.vhdx");
            using (new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
                Assert.IsFalse(VhdxHeader.NeedsLogReplay(disk), "An in-use file is not 'unclean'.");
            }
            Assert.IsFalse(VhdxHeader.NeedsLogReplay(disk));
        }

    }
}
