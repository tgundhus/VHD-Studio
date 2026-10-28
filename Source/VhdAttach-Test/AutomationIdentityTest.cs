using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;
using VhdAttachService;

namespace VhdAttachTest {
    [TestClass]
    [DoNotParallelize]
    public class AutomationIdentityTest {
        [TestMethod]
        [TestCategory("Elevated")]
        public void VhdxStableIdentifierSurvivesWritableAttachmentAndPayloadWrites() {
            ScratchDisk.RequireElevation();
            using (var disk = ScratchDisk.Create(64L * 1024 * 1024)) {
                var before = VirtualDiskImage.GetDetails(disk.FileName);
                Assert.IsTrue(before.VirtualDiskId.HasValue && before.VirtualDiskId != Guid.Empty);
                Assert.AreEqual(VhdxHeader.Read(disk.FileName).DataWriteGuid, before.Identifier);
                disk.Attach(readOnly: false);
                var payload = disk.WriteFiles(1, 1024 * 1024, "identity");
                var written = VirtualDiskImage.GetDetails(disk.FileName);
                Assert.AreEqual(VhdxHeader.Read(disk.FileName).DataWriteGuid, written.Identifier);
                Console.WriteLine("Before: Identifier={0}; VirtualDiskId={1}", before.Identifier, before.VirtualDiskId);
                Console.WriteLine("Written: Identifier={0}; VirtualDiskId={1}", written.Identifier, written.VirtualDiskId);
                Assert.AreNotEqual(before.Identifier, written.Identifier, "VHDX linkage identifiers change after a writable attachment modifies disk content.");
                Assert.AreEqual(before.VirtualDiskId, written.VirtualDiskId, "Automation identity must survive normal payload writes.");
                using (var native = new Medo.IO.VirtualDisk(disk.FileName)) {
                    native.Open(Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach);
                    Assert.AreEqual(before.VirtualDiskId.Value, native.GetVirtualDiskId(), "The service handle must query the same stable identity as inspection.");
                }
                using (var caller = PipeCaller.ForService()) {
                    AttachHelper.Detach(disk.FileName, false, caller, expectedImageId: before.VirtualDiskId);
                    Assert.IsNull(VirtualDiskImage.GetDetails(disk.FileName).AttachedPath);
                    AttachHelper.Attach(new FileWithOptions(disk.FileName) { ReadOnly = true }, caller,
                        refuseLogReplay: true, expectedImageId: before.VirtualDiskId);
                }
                disk.Detach();
                disk.Verify(payload);
                Assert.AreEqual(before.VirtualDiskId, VirtualDiskImage.GetDetails(disk.FileName).VirtualDiskId);
            }
        }
    }
}
