using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Storage;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttachTest {

    [TestClass]
    public class VsuxIntegrationTest {
        private const string Password = "VSUX disposable integration password";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private string Folder;

        [TestInitialize]
        public void Init() {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsux-" + Guid.NewGuid().ToString("N")));
            this.Folder = PathGuard.GetLongPath(directory.FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            foreach (var file in Directory.GetFiles(this.Folder)) { File.Delete(file); }
            Directory.Delete(this.Folder); // Only this run's flat directory; no recursive deletion or mounted disks.
        }

        [TestMethod]
        public void Test_ServiceRoutes_RefuseVsuxBeforeOpeningOrCheckingCaller() {
            var container = Path.Combine(this.Folder, "missing.VSUX");
            Assert.IsTrue(VsuxGuard.IsVsux(container));
            Assert.IsFalse(VsuxGuard.IsVsux(container + ".json"));
            Assert.IsFalse(VsuxGuard.IsVsux(Path.ChangeExtension(container, ".vhdx")));

            // A null caller proves the password guard runs before caller access or elevation checks.
            var attachError = Assert.ThrowsExactly<InvalidOperationException>(() =>
                VhdAttachService.AttachHelper.Attach(new FileWithOptions(container), null));
            StringAssert.Contains(attachError.Message, "password");
            Assert.ThrowsExactly<InvalidOperationException>(() => VhdAttachService.AttachHelper.Detach(container, false, null));
            Assert.ThrowsExactly<InvalidOperationException>(() => VhdAttachService.AttachHelper.Detach(container, true, null));
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                VhdAttachService.AttachHelper.Attach(new FileWithOptions(Path.ChangeExtension(container, ".vhdx")), null));
        }

        [TestMethod]
        public void Test_PublicCore_EmbeddedUnlockChangesOnlySignature() {
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");

            VsuxOperations.Protect(image, container, Password);
            Assert.IsFalse(File.Exists(image));
            Assert.IsFalse(File.Exists(container + ".json"), "New VSUX protection must be self-contained.");
            var locked = File.ReadAllBytes(container);
            Assert.IsTrue(locked.AsSpan(0, 8).SequenceEqual("vsuxlock"u8));
            var info = VsuxOperations.GetInfo(container);
            Assert.IsTrue(info.IsLocked);
            Assert.IsFalse(info.IsAttached);
            Assert.IsNull(info.AttachedPath);
            Assert.AreNotEqual(Guid.Empty, info.DiskId);
            Assert.AreEqual((long)locked.Length, info.Length);

            var lockedHash = Hash(locked);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(container, "incorrect password"));
            Assert.AreEqual(lockedHash, Hash(File.ReadAllBytes(container)), "A wrong password changed the image.");

            VsuxOperations.Unlock(container, Password);
            var restored = File.ReadAllBytes(container);
            Assert.IsTrue(restored.AsSpan(0, 8).SequenceEqual("vhdxfile"u8));
            Assert.IsTrue(locked.AsSpan(8).SequenceEqual(restored.AsSpan(8)), "Unlock changed bytes outside the eight-byte signature.");
            var unlocked = VsuxOperations.GetInfo(container);
            Assert.IsFalse(unlocked.IsLocked);
            Assert.IsFalse(unlocked.IsAttached);
            Assert.IsNull(unlocked.AttachedPath);
            Assert.AreEqual(info.DiskId, unlocked.DiskId);
        }

        [TestMethod]
        public void Test_MainForm_VsuxCommandsRemainPasswordGated() {
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);

            RunSta(() => {
                // Do not show/load/close the form: those paths persist window and recent-file settings.
                using (var form = new VhdAttach.MainForm()) {
                    typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                    RefreshCommands(form, container);
                    AssertCommands(form, canLock: false);

                    VsuxOperations.Unlock(container, Password);
                    RefreshCommands(form, container);
                    AssertCommands(form, canLock: true);
                }
            });
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_MainForm_AttachPromptsForPasswordAndCancelLeavesLockedImageUnchanged() {
            ScratchDisk.RequireElevation();
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);
            var lockedHash = Hash(File.ReadAllBytes(container));
            var savedExitCode = Environment.ExitCode;
            try {
                RunSta(() => {
                    using (var form = new VhdAttach.MainForm())
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                        RefreshCommands(form, container);
                        var observedPassword = false;
                        var maskedPassword = false;
                        var onlyPasswordVisible = false;
                        var timedOut = false;
                        var elapsed = Stopwatch.StartNew();
                        timer.Tick += (sender, args) => {
                            var dialogs = OwnedDialogs(form);
                            foreach (var dialog in dialogs) {
                                dialog.StartPosition = FormStartPosition.Manual;
                                dialog.Location = new Point(-30000, -30000);
                                if (dialog is VhdAttach.VsuxPasswordForm) {
                                    observedPassword = true;
                                    var box = (TextBox)dialog.GetType().GetField("PasswordBox", PrivateInstance).GetValue(dialog);
                                    maskedPassword = box.UseSystemPasswordChar;
                                    onlyPasswordVisible = dialog.Owner is VhdAttach.VsuxForm operation && operation.Opacity == 0 && !operation.ShowInTaskbar && form.Opacity == 1;
                                    dialog.DialogResult = DialogResult.Cancel;
                                }
                            }
                            if (elapsed.Elapsed < TimeSpan.FromSeconds(10)) { return; }
                            timedOut = true;
                            // Close deepest owned dialogs first. Never close the unshown MainForm.
                            for (var i = dialogs.Count - 1; i >= 0; i--) {
                                dialogs[i].DialogResult = DialogResult.Cancel;
                                dialogs[i].Close();
                            }
                        };
                        try {
                            timer.Start();
                            ((ToolStripSplitButton)Command(form, "mnuAttach")).PerformButtonClick();
                        } finally {
                            timer.Stop();
                            var remaining = OwnedDialogs(form);
                            for (var i = remaining.Count - 1; i >= 0; i--) {
                                remaining[i].DialogResult = DialogResult.Cancel;
                                remaining[i].Close();
                            }
                        }
                        Assert.IsFalse(timedOut, "The Attach button did not finish the password cancellation within ten seconds.");
                        Assert.IsTrue(observedPassword, "The Attach button did not open the VSUX password prompt.");
                        Assert.IsTrue(maskedPassword, "The VSUX prompt must mask the password by default.");
                        Assert.IsTrue(onlyPasswordVisible, "Attach must leave the main window available without showing a progress window behind the password prompt.");
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            Assert.AreEqual(lockedHash, Hash(File.ReadAllBytes(container)), "Cancelling Attach changed the locked image.");
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked, "Cancelling Attach restored the VHDX signature.");
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_AttachedVsux_ExposesItsVolumeAndPreservesReadWriteContent() {
            ScratchDisk.RequireElevation();
            using (var original = ScratchDisk.Create()) {
                original.Attach(readOnly: false);
                var expected = original.WriteFiles(2, 1024 * 1024, "vsux-attach-");
                original.Detach();
                var container = Path.ChangeExtension(original.FileName, ".vsux");
                try {
                    VsuxOperations.Protect(original.FileName, container, Password);
                    VsuxOperations.Attach(container, Password);
                    var attached = VsuxOperations.GetInfo(container);
                    Assert.IsTrue(attached.IsAttached);
                    Assert.IsFalse(attached.IsLocked);
                    var volume = WaitForVolume(attached.AttachedPath);
                    AssertPayload(volume.VolumeName, expected);
                    if (volume.DriveLetter2 == null) {
                        var letter = VhdAttachService.DriveLetterMemory.FindFreeLetter(new HashSet<char>());
                        Assert.IsNotNull(letter, "The disposable test volume needs one free drive letter.");
                        volume.ChangeLetter(letter.Value + ":");
                    }
                    var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
                    using (var file = new FileStream(Path.Combine(volume.VolumeName, "written-after-vsux-attach.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                        file.Write(bytes);
                    }
                    expected.Add("written-after-vsux-attach.bin", Hash(bytes));

                    RunSta(() => {
                        using (var form = new VhdAttach.MainForm()) {
                            typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                            RefreshCommands(form, container);
                            AssertSelectedDisk(form, attached.AttachedPath);
                            var drive = (ToolStripSplitButton)Command(form, "mnuDrive");
                            Assert.IsTrue(drive.Enabled, "An attached VSUX volume must be accessible through Drive.");
                            var open = drive.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text.StartsWith("Open ", StringComparison.Ordinal)
                                && item.Tag is Volume target && target.VolumeName == volume.VolumeName);
                            var target = (Volume)open.Tag;
                            Assert.AreEqual(volume.PhysicalDriveNumber, target.PhysicalDriveNumber);
                            Assert.AreEqual(volume.DriveLetter3, target.DriveLetter3, "Open must target this VSUX volume's drive letter.");
                            AssertPayload(target.DriveLetter3, expected);
                            Assert.IsFalse(Command(form, "mnuAttach").Enabled);
                            Assert.IsTrue(Command(form, "mnuDetach").Enabled);
                        }
                    });

                    VsuxOperations.Lock(container, Password);
                    Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
                    VsuxOperations.Attach(container, Password, readOnly: true);
                    AssertPayload(WaitForVolume(VsuxOperations.GetInfo(container).AttachedPath).VolumeName, expected);
                } finally {
                    if (File.Exists(container)) {
                        VsuxOperations.Lock(container, Password);
                        File.Delete(container); // Only the uniquely named disk created by this test, after safe detach.
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_AttachedBlankVsux_OffersDiskManagerForTheCorrectDiskWithoutInitializingIt() {
            ScratchDisk.RequireElevation();
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);
            try {
                VsuxOperations.Attach(container, Password);
                var attached = VsuxOperations.GetInfo(container);
                Assert.IsTrue(attached.IsAttached, "A blank disk is attached even though it has no Explorer drive yet.");
                var number = int.Parse(attached.AttachedPath.Substring(17), System.Globalization.CultureInfo.InvariantCulture);
                var disk = WaitForStorageDisk(number, container);
                Assert.IsTrue(disk.PartitionStyle is PartitionStyle.Raw or PartitionStyle.Mbr);
                Assert.AreEqual(0, StorageManager.GetPartitions(number).Count);
                Assert.AreEqual(0, Volume.GetVolumesOnPhysicalDrive(attached.AttachedPath).Count);

                RunSta(() => {
                    using (var form = new VhdAttach.MainForm()) {
                        typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                        RefreshCommands(form, container);
                        AssertSelectedDisk(form, attached.AttachedPath);
                        var drive = (ToolStripSplitButton)Command(form, "mnuDrive");
                        Assert.IsTrue(drive.Enabled, "A blank attached VSUX disk needs an available path to Disk Manager.");
                        Assert.IsTrue(drive.DropDownItems.OfType<ToolStripMenuItem>().Any(item => item.Text.Contains("Disk Manager", StringComparison.Ordinal)));
                        var list = (ListView)typeof(VhdAttach.MainForm).GetField("list", PrivateInstance).GetValue(form);
                        var guidance = list.Items.Cast<ListViewItem>().Single(item => item.Text == "Drive access").SubItems[1].Text;
                        StringAssert.Contains(guidance, "Disk Manager");
                        Assert.IsFalse(Command(form, "mnuAttach").Enabled);
                        Assert.IsTrue(Command(form, "mnuDetach").Enabled);
                    }
                });

                Assert.AreEqual(disk.PartitionStyle, WaitForStorageDisk(number, container).PartitionStyle, "Displaying guidance initialized the blank disk.");
                Assert.AreEqual(0, StorageManager.GetPartitions(number).Count, "Displaying guidance created a partition.");
                Assert.AreEqual(0, Volume.GetVolumesOnPhysicalDrive(attached.AttachedPath).Count, "Displaying guidance formatted the blank disk.");
            } finally { VsuxOperations.Lock(container, Password); }
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
        }

        private static void AssertSelectedDisk(VhdAttach.MainForm form, string attachedPath) {
            Assert.AreEqual(attachedPath, (string)typeof(VhdAttach.MainForm).GetMethod("GetSelectedAttachedPath", PrivateInstance).Invoke(form, null));
            Assert.AreEqual(int.Parse(attachedPath.Substring(17), System.Globalization.CultureInfo.InvariantCulture),
                (int?)typeof(VhdAttach.MainForm).GetMethod("GetSelectedDiskNumber", PrivateInstance).Invoke(form, null),
                "Disk Manager must preselect this VSUX image's physical disk.");
        }

        private static Volume WaitForVolume(string attachedPath) {
            for (var attempt = 0; attempt < 60; attempt++) {
                var volume = Volume.GetVolumesOnPhysicalDrive(attachedPath).FirstOrDefault(candidate => Directory.Exists(candidate.VolumeName));
                if (volume != null) { return volume; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The attached disposable VSUX volume did not appear.");
        }

        private static DiskInfo WaitForStorageDisk(int number, string container) {
            for (var attempt = 0; attempt < 40; attempt++) {
                var disk = StorageManager.GetDisks().SingleOrDefault(candidate => candidate.Number == number);
                if (disk != null && disk.IsVirtual && string.Equals(PathGuard.GetLongPath(disk.Location ?? ""), container, StringComparison.OrdinalIgnoreCase)) { return disk; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The storage view does not identify the exact disposable VSUX file.");
        }

        private static void AssertPayload(string volumePath, IDictionary<string, string> expected) {
            foreach (var pair in expected) {
                using (var file = File.OpenRead(Path.Combine(volumePath, pair.Key))) {
                    Assert.AreEqual(pair.Value, Convert.ToHexString(SHA256.HashData(file)), "VSUX attach changed " + pair.Key);
                }
            }
        }

        private string CreateDisposableImage() {
            var path = Path.Combine(this.Folder, "disposable.vhdx");
            try {
                using (var disk = new Medo.IO.VirtualDisk(path)) {
                    disk.Create(64L * 1024 * 1024, Medo.IO.VirtualDiskCreateOptions.None, 0, 0, Medo.IO.VirtualDiskType.Vhdx);
                }
            } catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
                Assert.Inconclusive("Cannot create disposable VHDX files in this environment: " + ex.Message);
            }
            return path;
        }

        private static void RefreshCommands(VhdAttach.MainForm form, string path) {
            typeof(VhdAttach.MainForm).GetMethod("UpdateData", PrivateInstance).Invoke(form, new object[] { path });
            typeof(VhdAttach.MainForm).GetMethod("tmrUpdateMenu_Tick", PrivateInstance).Invoke(form, new object[] { null, EventArgs.Empty });
        }

        private static void AssertCommands(VhdAttach.MainForm form, bool canLock) {
            Assert.IsTrue(Command(form, "mnuAttach").Enabled, "Attach must remain available for the password prompt.");
            Assert.AreEqual(canLock, Command(form, "mnuDetach").Enabled, "Only an unlocked VSUX image can be detached and locked.");
            Assert.AreEqual("Detach && lock", Command(form, "mnuDetach").Text);
            Assert.IsTrue(Command(form, "mnuMaintenance").Enabled, "VSUX needs its dedicated soft-protection tools.");
            Assert.AreEqual("Soft protection", Command(form, "mnuMaintenance").Text);
            Assert.IsTrue(Command(form, "mnuVsuxConvert").Enabled);
            Assert.IsTrue(Command(form, "mnuVsuxBackup").Enabled);
            Assert.IsFalse(Command(form, "mnuVsuxPortable").Enabled, "An embedded VSUX file needs no companion migration.");
            foreach (var name in new[] { "mnuAutomount", "mnuDrive", "mnuProtectVsux" }) {
                Assert.IsFalse(Command(form, name).Enabled, name + " must remain unavailable for VSUX images.");
            }
        }

        private static ToolStripItem Command(VhdAttach.MainForm form, string name) =>
            (ToolStripItem)typeof(VhdAttach.MainForm).GetField(name, PrivateInstance).GetValue(form);

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static List<Form> OwnedDialogs(Form owner) {
            var dialogs = new List<Form>();
            foreach (Form dialog in Application.OpenForms) {
                if (dialog == owner || dialog.InvokeRequired) { continue; }
                for (var ancestor = dialog.Owner; ancestor != null; ancestor = ancestor.Owner) {
                    if (ancestor == owner) { dialogs.Add(dialog); break; }
                }
            }
            return dialogs;
        }

        private static void RunSta(Action action) {
            Exception failure = null;
            var thread = new Thread(() => {
                try { action(); }
                catch (Exception ex) { failure = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The VSUX UI state check did not complete.");
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }
}
