using System.ComponentModel;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Automation;
using VhdAttachCommon;
using VhdAttachService;

namespace VhdAttachTest {
    [TestClass]
    public class AttachmentStateTest {
        [TestMethod]
        public void SafeDetach_PropagatesNativeQueryFailuresBeforeSkippingVolumeLocks() {
            var error = Assert.ThrowsExactly<Win32Exception>(() => AttachHelper.GetPathForSafeDetach(() => throw new Win32Exception(5)));
            Assert.AreEqual(5, error.NativeErrorCode);
        }

        [TestMethod]
        public void SafeDetach_AcceptsOnlyTheWrappersExplicitDetachedResult() {
            Assert.IsNull(AttachHelper.GetPathForSafeDetach(() => throw new IOException("Device not attached")));
            Assert.AreEqual(@"\\.\PhysicalDrive42", AttachHelper.GetPathForSafeDetach(() => @"\\.\PhysicalDrive42"));
        }

        [TestMethod]
        [DataRow(5)]
        [DataRow(32)]
        [DataRow(122)]
        public void UnknownNativeAttachmentResults_AreRefused(int errorCode) {
            var details = new VirtualDiskDetails { AttachedPathError = errorCode };
            var error = Assert.ThrowsExactly<Win32Exception>(() => AutomationOperations.EnsureKnownAttachmentState(details));
            Assert.AreEqual(errorCode, error.NativeErrorCode);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void SuccessWithoutADevicePath_IsRefused(string path) {
            var details = new VirtualDiskDetails { AttachedPathError = 0, AttachedPath = path };
            Assert.ThrowsExactly<IOException>(() => AutomationOperations.EnsureKnownAttachmentState(details));
        }

        [TestMethod]
        public void ExplicitDeviceAbsentResult_IsAcceptedAsDetached() {
            AutomationOperations.EnsureKnownAttachmentState(new VirtualDiskDetails { AttachedPathError = 55 });
        }

        [TestMethod]
        public void SuccessWithADevicePath_IsAcceptedAsAttached() {
            AutomationOperations.EnsureKnownAttachmentState(new VirtualDiskDetails { AttachedPathError = 0, AttachedPath = @"\\.\PhysicalDrive42" });
        }
    }
}
