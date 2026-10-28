using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;
using VhdAttachService;

namespace VhdAttachTest {
    [TestClass]
    public class CreationBrokerTest {
        [TestMethod]
        [DataRow("disk.vhdx", 64L * 1024 * 1024)]
        [DataRow("C:\\disk.vhd", 64L * 1024 * 1024)]
        [DataRow("C:\\disk.vsux", 64L * 1024 * 1024)]
        [DataRow("\\\\server\\share\\disk.vhdx", 64L * 1024 * 1024)]
        [DataRow("C:\\disk.vhdx", 64L * 1024 * 1024 - 4096)]
        [DataRow("C:\\disk.vhdx", 64L * 1024 * 1024 + 1)]
        public void Test_CreateImage_RejectsUnsupportedPathOrSize(string path, long size) {
            Assert.Throws<ArgumentException>(() => ImageCreation.Validate(path, size));
        }

        [TestMethod]
        public void Test_CreateImage_RefusesExistingDestinationWithoutChangingIt() {
            var path = ScratchDisk.NewPath();
            var data = new byte[] { 11, 29, 37, 53 };
            File.WriteAllBytes(path, data);
            try {
                using (var caller = PipeCaller.ForService()) {
                    Assert.ThrowsExactly<InvalidOperationException>(() => ImageCreation.Create(path, 64L * 1024 * 1024, false, true, caller));
                }
                CollectionAssert.AreEqual(data, File.ReadAllBytes(path));
            } finally { File.Delete(path); }
        }

        [TestMethod]
        public void Test_CreateImage_AccessBoundaryGuardsValidationAndNativeCreation() {
            var path = ScratchDisk.NewPath();
            var calls = 0;
            Assert.ThrowsExactly<IOException>(() => VhdAttach.NewDiskCreation.Create(path, 64L * 1024 * 1024, false, true, action => {
                calls++;
                if (calls == 1) { action(); }
                else { throw new UnauthorizedAccessException("Caller cannot create this image."); }
            }));
            Assert.AreEqual(2, calls, "Native creation must run inside the caller's access boundary.");
            Assert.IsFalse(File.Exists(path), "A denied caller must not leave a file created as SYSTEM.");
        }

        [TestMethod]
        public void Test_CreateImage_BlankImageUsesCallersFileAccess() {
            var path = ScratchDisk.NewPath();
            try {
                using (var caller = PipeCaller.ForService()) {
                    ImageCreation.Create(path, 64L * 1024 * 1024, false, false, caller);
                }
                var details = VirtualDiskImage.GetDetails(path);
                Assert.AreEqual(64L * 1024 * 1024, details.VirtualSize);
                Assert.IsNull(details.AttachedPath, "Blank creation must not attach the image.");
            } catch (Exception error) when (ProviderUnavailable(error)) {
                Assert.Inconclusive("The native virtual disk provider is unavailable in this environment.");
            } finally { if (File.Exists(path)) { File.Delete(path); } }
        }

        private static bool ProviderUnavailable(Exception error) {
            for (var current = error; current != null; current = current.InnerException) {
                if (current is System.ComponentModel.Win32Exception native && native.NativeErrorCode == unchecked((int)0xC03A0014)) { return true; }
            }
            return false;
        }

        [TestMethod]
        [DataRow("attach")]
        [DataRow("detach")]
        public void Test_ExpectedImageIdIsCheckedOnOpenedHandleBeforeActing(string operation) {
            var path = ScratchDisk.NewPath();
            try {
                using (var caller = PipeCaller.ForService()) {
                    ImageCreation.Create(path, 64L * 1024 * 1024, false, false, caller);
                    var before = ScratchDisk.HashFile(path);
                    var expected = Guid.NewGuid();
                    Assert.AreNotEqual(VirtualDiskImage.GetDetails(path).Identifier, expected);
                    if (operation == "attach") {
                        Assert.ThrowsExactly<ImageChangedException>(() => AttachHelper.Attach(new FileWithOptions(path) { ReadOnly = true }, caller,
                            refuseLogReplay: true, expectedImageId: expected));
                    } else {
                        Assert.ThrowsExactly<ImageChangedException>(() => AttachHelper.Detach(path, false, caller, expectedImageId: expected));
                    }
                    Assert.IsNull(VirtualDiskImage.GetDetails(path).AttachedPath, "A mismatched identifier must be refused before attaching the image.");
                    Assert.AreEqual(before, ScratchDisk.HashFile(path), "A refused identifier must leave the image unchanged.");
                }
            } catch (Exception error) when (ProviderUnavailable(error)) {
                Assert.Inconclusive("The native virtual disk provider is unavailable in this environment.");
            } finally { if (File.Exists(path)) { File.Delete(path); } }
        }

        [TestMethod]
        public void Test_Attach_RefuseLogReplayStopsBeforeNativeMutation() {
            var path = ScratchDisk.NewPath();
            var bytes = new byte[192 * 1024];
            "vhdxfile"u8.CopyTo(bytes);
            var header = new byte[4096];
            "head"u8.CopyTo(header);
            BitConverter.GetBytes(1L).CopyTo(header, 8);
            Guid.NewGuid().ToByteArray().CopyTo(header, 48);
            BitConverter.GetBytes((ushort)1).CopyTo(header, 66);
            BitConverter.GetBytes(1024 * 1024).CopyTo(header, 68);
            BitConverter.GetBytes(1024L * 1024).CopyTo(header, 72);
            BitConverter.GetBytes(VhdxHeader.Crc32C(header)).CopyTo(header, 4);
            header.CopyTo(bytes, 64 * 1024);
            File.WriteAllBytes(path, bytes);
            try {
                Assert.IsTrue(VhdxHeader.NeedsLogReplay(path), "Fixture must contain a valid pending-log header.");
                using (var caller = PipeCaller.ForService()) {
                    Assert.ThrowsExactly<LogReplayRequiredException>(() => AttachHelper.Attach(new FileWithOptions(path) { ReadOnly = false }, caller, refuseLogReplay: true));
                }
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "A refused automation attach must leave every fixture byte unchanged.");
            } finally { File.Delete(path); }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_CreateImage_PreparedWorkspaceRoundTrip() {
            ScratchDisk.RequireElevation();
            using (var disk = ScratchDisk.Open(ScratchDisk.NewPath())) {
                using (var caller = PipeCaller.ForService()) {
                    ImageCreation.Create(disk.FileName, 64L * 1024 * 1024, false, true, caller);
                }
                Assert.IsNull(VirtualDiskImage.GetDetails(disk.FileName).AttachedPath, "Creation must finish detached.");
                disk.Attach(readOnly: false);
                var expected = disk.WriteFiles(2, 4096);
                disk.Detach();
                disk.Verify(expected);
            }
        }
    }
}
