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
    public class VsuxCreationTest {
        private const long DiskSize = 64L * 1024 * 1024;
        private const string Password = "VSUX disposable creation password";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsuxCreate-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            foreach (var file in Directory.GetFiles(this.Folder)) { File.Delete(file); }
            Directory.Delete(this.Folder); // Only this test's flat directory, with all native handles disposed.
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Test_CreateVsux_DynamicAndFixedProduceLockedNativeVhdx(bool fixedSize) {
            this.ProbeNativeCreation();
            var target = Path.Combine(this.Folder, "new-disk.vsux");

            VhdAttach.VsuxCreation.Create(target, DiskSize, fixedSize, Password);

            CollectionAssert.AreEquivalent(new[] { target }, Directory.GetFiles(this.Folder), "Creation must leave one locked, self-contained file without a companion or plaintext scratch image.");
            var info = VsuxOperations.GetInfo(target);
            Assert.IsTrue(info.IsLocked);
            Assert.IsFalse(info.IsAttached, "Creating a protected disk must not attach it or leave its signature unlocked.");
            Assert.AreNotEqual(Guid.Empty, info.DiskId);
            var imageHash = ScratchDisk.HashFile(target);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(target, "incorrect password"));
            Assert.AreEqual(imageHash, ScratchDisk.HashFile(target));

            VsuxOperations.Unlock(target, Password);
            // Generic Maintenance excludes VSUX; inspect only this unlocked disposable fixture under its native extension.
            var nativePath = Path.ChangeExtension(target, ".vhdx");
            File.Move(target, nativePath);
            var details = VirtualDiskImage.GetDetails(nativePath);
            Assert.AreEqual("VHDX", details.Format, "Windows must recognize the unlocked result as a real VHDX.");
            Assert.AreEqual(DiskSize, details.VirtualSize);
            Assert.AreEqual(fixedSize ? VirtualDiskKind.Fixed : VirtualDiskKind.Dynamic, details.Kind);
            Assert.IsNull(details.AttachedPath);
            Assert.IsFalse(details.NeedsLogReplay);
            using (var stream = File.OpenRead(nativePath)) {
                var signature = new byte[8];
                stream.ReadExactly(signature);
                CollectionAssert.AreEqual("vhdxfile"u8.ToArray(), signature);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Test_CreateVsux_ExistingTargetIsNeverChanged(bool createSinglePartition) {
            var target = Path.Combine(this.Folder, "existing.vsux");
            var existing = target;
            File.WriteAllBytes(existing, RandomNumberGenerator.GetBytes(4096));
            var originalHash = ScratchDisk.HashFile(existing);

            Assert.ThrowsExactly<IOException>(() => VhdAttach.VsuxCreation.Create(target, DiskSize, false, Password, createSinglePartition));

            Assert.AreEqual(originalHash, ScratchDisk.HashFile(existing), "A name collision changed the existing file.");
            CollectionAssert.AreEquivalent(new[] { existing }, Directory.GetFiles(this.Folder), "A refused creation left an image or plaintext scratch file behind.");
        }

        [TestMethod]
        public void Test_CreateVsux_ExistingUnrelatedJsonIsPreservedAndNotRequired() {
            this.ProbeNativeCreation();
            var target = Path.Combine(this.Folder, "new-disk.vsux");
            var json = target + ".json";
            File.WriteAllText(json, "unrelated user file");
            var originalHash = ScratchDisk.HashFile(json);

            VhdAttach.VsuxCreation.Create(target, DiskSize, false, Password);
            VsuxOperations.Unlock(target, Password);

            Assert.AreEqual(originalHash, ScratchDisk.HashFile(json), "Creating or unlocking an embedded VSUX changed an unrelated JSON file.");
            Assert.IsTrue(VsuxOperations.GetInfo(target).HasEmbeddedPassword);
            CollectionAssert.AreEquivalent(new[] { target, json }, Directory.GetFiles(this.Folder));
        }

        [TestMethod]
        public void Test_CreateVsux_InvalidPasswordCreatesNothing() {
            var target = Path.Combine(this.Folder, "never-created.vsux");
            Assert.ThrowsExactly<ArgumentException>(() => VhdAttach.VsuxCreation.Create(target, DiskSize, false, "short"));
            Assert.AreEqual(0, Directory.GetFiles(this.Folder).Length, "Password validation must finish before creating either the disk or its companion.");
        }

        [TestMethod]
        public void Test_NewDiskForm_OffersVisibleSelectableVsuxFormat() {
            RunSta(() => {
                using (var form = new VhdAttach.NewDiskForm()) {
                    var settingsHandler = typeof(VhdAttach.NewDiskForm).GetMethod("Form_FormClosed", PrivateInstance);
                    form.FormClosed -= (FormClosedEventHandler)settingsHandler.CreateDelegate(typeof(FormClosedEventHandler), form);
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    try {
                        var vsux = (RadioButton)typeof(VhdAttach.NewDiskForm).GetField("radFormatVsux", PrivateInstance).GetValue(form);
                        Assert.IsTrue(vsux.Visible, "New virtual disk must visibly offer the VSUX format.");
                        Assert.IsTrue(vsux.Enabled);
                        StringAssert.Contains(vsux.Text, "VSUX");
                        Assert.IsTrue(vsux.Parent.ClientRectangle.Contains(vsux.Bounds), "The VSUX option is clipped by the format group.");
                        vsux.Checked = true;
                        Assert.IsTrue(vsux.Checked);
                        Assert.IsFalse(((RadioButton)typeof(VhdAttach.NewDiskForm).GetField("radFormatVhdX", PrivateInstance).GetValue(form)).Checked);
                        var help = (Label)typeof(VhdAttach.NewDiskForm).GetField("lblVsuxHelp", PrivateInstance).GetValue(form);
                        Assert.IsTrue(help.Visible, "Selecting VSUX must explain the password prompt and its unencrypted payload.");
                        StringAssert.Contains(help.Text, "unencrypted");
                        Assert.IsTrue(help.Parent.ClientRectangle.Contains(help.Bounds), "The VSUX explanation is clipped by the format group.");
                        var partition = (CheckBox)typeof(VhdAttach.NewDiskForm).GetField("chbCreatePartition", PrivateInstance).GetValue(form);
                        Assert.IsTrue(partition.Visible && partition.Enabled);
                        Assert.IsTrue(partition.Checked && form.CreateSinglePartition, "New disks must default to one ready-to-use NTFS partition.");
                        StringAssert.Contains(partition.Text, "NTFS");
                        Assert.IsTrue(partition.Parent.ClientRectangle.Contains(partition.Bounds), "The partition option is clipped.");
                        var size = (TextBox)typeof(VhdAttach.NewDiskForm).GetField("txtSize", PrivateInstance).GetValue(form);
                        var units = (ComboBox)typeof(VhdAttach.NewDiskForm).GetField("cmbSizeUnit", PrivateInstance).GetValue(form);
                        var create = (Button)typeof(VhdAttach.NewDiskForm).GetField("btnOK", PrivateInstance).GetValue(form);
                        units.SelectedItem = "MB";
                        size.Text = "32";
                        Assert.IsFalse(create.Enabled, "NTFS creation must reject sizes below 64 MiB before opening a save dialog.");
                        partition.Checked = false;
                        Assert.IsFalse(form.CreateSinglePartition);
                        Assert.IsTrue(create.Enabled, "Unchecking the option must preserve small blank-disk creation.");
                        foreach (var name in new[] { "radFormatVhd", "radFormatVhdX", "radFormatVsux" }) {
                            ((RadioButton)typeof(VhdAttach.NewDiskForm).GetField(name, PrivateInstance).GetValue(form)).Checked = true;
                            Assert.IsTrue(partition.Visible && partition.Enabled, "The NTFS option must be available for every disk format.");
                            Assert.IsFalse(form.CreateSinglePartition, "Changing format must not discard the blank-disk choice.");
                        }
                    } finally {
                        form.Hide(); // Dispose without FormClosed: the test must not persist the user's last format or size.
                    }
                }
            });
        }

        [TestMethod]
        public void Test_Maintenance_OffersSoftProtectionForDetachedVhdxAndRefusesVhd() {
            this.ProbeNativeCreation();
            foreach (var extension in new[] { ".vhdx", ".vhd" }) {
                var file = this.CreateNativeImage(extension);
                RunSta(() => this.AssertProtectionPage(file, extension == ".vhdx"));
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_Maintenance_RefusesSoftProtectionForAttachedVhdx() {
            ScratchDisk.RequireElevation();
            this.ProbeNativeCreation();
            var file = this.CreateNativeImage(".vhdx");
            using (var disk = ScratchDisk.Open(file)) {
                disk.Attach(readOnly: true);
                try {
                    RunSta(() => this.AssertProtectionPage(file, false));
                } finally { disk.Detach(); }
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_CreateVsux_PasswordCancelHappensBeforeAnyImageIsCreated() {
            ScratchDisk.RequireElevation();
            var target = Path.Combine(this.Folder, "cancelled.vsux");
            var savedExitCode = Environment.ExitCode;
            try {
                RunSta(() => {
                    using (var form = new VhdAttach.VsuxForm("Create", target, sizeInBytes: DiskSize, createSinglePartition: true))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        var observed = false;
                        var masked = false;
                        var emptyBeforePrompt = false;
                        var onlyPasswordVisible = false;
                        var timedOut = false;
                        var elapsed = Stopwatch.StartNew();
                        timer.Tick += (sender, args) => {
                            foreach (var password in form.OwnedForms.OfType<VhdAttach.VsuxPasswordForm>()) {
                                password.StartPosition = FormStartPosition.Manual;
                                password.Location = new Point(-30000, -30000);
                                observed = true;
                                masked = ((TextBox)password.GetType().GetField("PasswordBox", PrivateInstance).GetValue(password)).UseSystemPasswordChar;
                                emptyBeforePrompt = Directory.GetFiles(this.Folder).Length == 0;
                                onlyPasswordVisible = form.Opacity == 0 && !form.ShowInTaskbar && password.Visible && password.Opacity > 0;
                                password.DialogResult = DialogResult.Cancel;
                            }
                            if (elapsed.Elapsed < TimeSpan.FromSeconds(10)) { return; }
                            timedOut = true;
                            foreach (var dialog in form.OwnedForms) { dialog.DialogResult = DialogResult.Cancel; }
                        };
                        DialogResult result;
                        try {
                            timer.Start();
                            result = form.ShowDialog();
                        } finally { timer.Stop(); }
                        Assert.IsFalse(timedOut, "The creation password prompt did not complete cancellation within ten seconds.");
                        Assert.IsTrue(observed, "VSUX creation did not ask for a password.");
                        Assert.IsTrue(masked);
                        Assert.IsTrue(emptyBeforePrompt, "VSUX creation wrote files before the password was accepted.");
                        Assert.IsTrue(onlyPasswordVisible, "Creation must show its password prompt without an extra progress window behind it.");
                        Assert.AreEqual(DialogResult.Cancel, result);
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            Assert.AreEqual(0, Directory.GetFiles(this.Folder).Length, "Cancelling the password prompt must leave neither a container, companion nor plaintext scratch image.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_CreateVsux_WithNtfsIsReadyOnFirstAttachAndRetainsContent(bool fixedSize) {
            ScratchDisk.RequireElevation();
            var container = Path.Combine(this.Folder, "ready.vsux");
            VhdAttach.VsuxCreation.Create(container, DiskSize, fixedSize, Password, createSinglePartition: true);
            CollectionAssert.AreEquivalent(new[] { container }, Directory.GetFiles(this.Folder));
            var locked = VsuxOperations.GetInfo(container);
            Assert.IsTrue(locked.IsLocked && !locked.IsAttached, "Prepared VSUX creation must still finish locked and detached.");
            Assert.IsTrue(locked.HasEmbeddedPassword);
            try {
                VsuxOperations.Attach(container, Password);
                var volume = WaitForReadyVolume(VsuxOperations.GetInfo(container).AttachedPath, container);
                var content = RandomNumberGenerator.GetBytes(1024 * 1024);
                var expected = Convert.ToHexString(SHA256.HashData(content));
                using (var file = new FileStream(Path.Combine(volume.DriveLetter3, "first-attach.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    file.Write(content);
                }
                VsuxOperations.Lock(container, Password);
                Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
                VsuxOperations.Attach(container, Password, readOnly: true);
                var restored = WaitForReadyVolume(VsuxOperations.GetInfo(container).AttachedPath, container);
                using (var file = File.OpenRead(Path.Combine(restored.VolumeName, "first-attach.bin"))) {
                    Assert.AreEqual(expected, Convert.ToHexString(SHA256.HashData(file)), "Safe detach and password reattach changed the newly written NTFS file.");
                }
            } finally { VsuxOperations.Lock(container, Password); }
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
        }

        [TestMethod]
        [DataRow(".vhd", false)]
        [DataRow(".vhd", true)]
        [DataRow(".vhdx", false)]
        [DataRow(".vhdx", true)]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_CreateNative_WithNtfsIsReadyOnFirstAttach(string extension, bool fixedSize) {
            ScratchDisk.RequireElevation();
            var image = Path.Combine(this.Folder, "ready" + extension);
            VhdAttach.NewDiskCreation.Create(image, DiskSize, fixedSize, createSinglePartition: true);
            Assert.IsNull(VirtualDiskImage.GetDetails(image).AttachedPath, "Prepared creation must leave the disk detached.");
            var content = RandomNumberGenerator.GetBytes(1024 * 1024);
            var expected = Convert.ToHexString(SHA256.HashData(content));
            try {
                var volume = WaitForReadyVolume(AttachNative(image, readOnly: false), image);
                using (var file = new FileStream(Path.Combine(volume.DriveLetter3, "first-attach.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    file.Write(content);
                }
                DetachNative(image);
                var restored = WaitForReadyVolume(AttachNative(image, readOnly: true), image);
                using (var file = File.OpenRead(Path.Combine(restored.VolumeName, "first-attach.bin"))) {
                    Assert.AreEqual(expected, Convert.ToHexString(SHA256.HashData(file)));
                }
            } finally { DetachNative(image); }
        }

        [TestMethod]
        [DataRow(".vhd")]
        [DataRow(".vhdx")]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void Test_CreateNative_WithoutNtfsKeepsSmallDiskBlank(string extension) {
            ScratchDisk.RequireElevation();
            var image = Path.Combine(this.Folder, "blank" + extension);
            VhdAttach.NewDiskCreation.Create(image, 8L * 1024 * 1024, false, createSinglePartition: false);
            try {
                var path = AttachNative(image, readOnly: true);
                var disk = WaitForStorageDisk(path, image);
                Assert.AreEqual(0, StorageManager.GetPartitions(disk.Number).Count, "Unchecking NTFS creation must leave the disk unpartitioned.");
                Assert.AreEqual(0, Volume.GetVolumesOnPhysicalDrive(path).Count);
            } finally { DetachNative(image); }
        }

        [TestMethod]
        [DataRow(".vhd")]
        [DataRow(".vhdx")]
        [DataRow(".vsux")]
        public void Test_CreateWithNtfs_TooSmallCreatesNothing(string extension) {
            var target = Path.Combine(this.Folder, "too-small" + extension);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => {
                if (extension == ".vsux") { VhdAttach.VsuxCreation.Create(target, DiskSize - 4096, false, Password, createSinglePartition: true); }
                else { VhdAttach.NewDiskCreation.Create(target, DiskSize - 4096, false, createSinglePartition: true); }
            });
            Assert.AreEqual(0, Directory.GetFiles(this.Folder).Length, "The minimum NTFS size must be checked before creating an image or scratch file.");
        }

        [TestMethod]
        [DataRow(".vhd", false)]
        [DataRow(".vhd", true)]
        [DataRow(".vhdx", false)]
        [DataRow(".vhdx", true)]
        public void Test_CreateNative_CollisionNeverChangesExistingFile(string extension, bool createSinglePartition) {
            var target = Path.Combine(this.Folder, "existing" + extension);
            File.WriteAllBytes(target, RandomNumberGenerator.GetBytes(4096));
            var originalHash = ScratchDisk.HashFile(target);
            Assert.ThrowsExactly<IOException>(() => VhdAttach.NewDiskCreation.Create(target, DiskSize, false, createSinglePartition));
            Assert.AreEqual(originalHash, ScratchDisk.HashFile(target));
            CollectionAssert.AreEquivalent(new[] { target }, Directory.GetFiles(this.Folder));
        }

        private static string AttachNative(string image, bool readOnly) {
            using (var disk = new Medo.IO.VirtualDisk(image)) {
                disk.Open(readOnly ? Medo.IO.VirtualDiskAccessMask.AttachReadOnly | Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach : Medo.IO.VirtualDiskAccessMask.All);
                disk.Attach(Medo.IO.VirtualDiskAttachOptions.PermanentLifetime | (readOnly ? Medo.IO.VirtualDiskAttachOptions.ReadOnly : Medo.IO.VirtualDiskAttachOptions.None));
                return disk.GetAttachedPath();
            }
        }

        private static void DetachNative(string image) {
            if (VirtualDiskImage.GetDetails(image).AttachedPath != null) {
                VhdAttachService.AttachHelper.Detach(image, false, VhdAttachService.PipeCaller.ForService());
            }
        }

        private static Volume WaitForReadyVolume(string attachedPath, string image) {
            var disk = WaitForStorageDisk(attachedPath, image);
            for (var attempt = 0; attempt < 60; attempt++) {
                var volume = Volume.GetVolumesOnPhysicalDrive(attachedPath).FirstOrDefault(candidate => candidate.DriveLetter3 != null && Directory.Exists(candidate.DriveLetter3));
                if (volume != null) {
                    var partitions = StorageManager.GetPartitions(disk.Number);
                    Assert.AreEqual(1, partitions.Count(partition => partition.FileSystem == "NTFS"), "Creation must prepare exactly one NTFS data volume.");
                    Assert.AreEqual(1, partitions.Count(partition => partition.Type != "Reserved (MSR)"), "Creation made more than one user partition.");
                    return volume;
                }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The newly prepared disk did not expose an NTFS drive letter on first attach.");
        }

        private static DiskInfo WaitForStorageDisk(string attachedPath, string image) {
            var number = int.Parse(attachedPath.Substring(17), System.Globalization.CultureInfo.InvariantCulture);
            for (var attempt = 0; attempt < 40; attempt++) {
                var disk = StorageManager.GetDisks().SingleOrDefault(candidate => candidate.Number == number);
                if (disk != null && disk.IsVirtual && string.Equals(PathGuard.GetLongPath(disk.Location ?? ""), image, StringComparison.OrdinalIgnoreCase)) { return disk; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The storage view does not identify the exact disposable image.");
        }

        private void AssertProtectionPage(string file, bool enabled) {
            using (var form = new VhdAttach.MaintenanceForm(file)) {
                typeof(VhdAttach.MaintenanceForm).GetMethod("ReloadDetails", PrivateInstance).Invoke(form, null);
                var tasks = (ListBox)typeof(VhdAttach.MaintenanceForm).GetField("TaskList", PrivateInstance).GetValue(form);
                Assert.IsTrue(tasks.Items.Contains("Soft protection"), "Maintenance must visibly list soft protection alongside its existing tasks.");
                tasks.SelectedItem = "Soft protection";
                var protect = Descendants(form).OfType<Button>().Single(button => button.Name == "ProtectVsuxButton");
                Assert.AreEqual(enabled, protect.Enabled, "Protection must be offered only for a detached, eligible VHDX.");
                StringAssert.Contains(protect.AccessibleName, "Protect as VSUX");
            }
        }

        private string CreateNativeImage(string extension) {
            var path = Path.Combine(this.Folder, "native-" + Guid.NewGuid().ToString("N") + extension);
            using (var disk = new Medo.IO.VirtualDisk(path)) {
                disk.Create(DiskSize, Medo.IO.VirtualDiskCreateOptions.None, 0, 0,
                    extension == ".vhdx" ? Medo.IO.VirtualDiskType.Vhdx : Medo.IO.VirtualDiskType.Vhd);
            }
            return path;
        }

        private void ProbeNativeCreation() {
            string probe = null;
            try { probe = this.CreateNativeImage(".vhdx"); }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
                Assert.Inconclusive("Cannot create disposable native VHDX files in this environment: " + ex.Message);
            } finally { if (probe != null) { File.Delete(probe); } }
        }

        internal static IEnumerable<Control> Descendants(Control parent) {
            foreach (Control child in parent.Controls) {
                yield return child;
                foreach (var descendant in Descendants(child)) { yield return descendant; }
            }
        }

        internal static void RunSta(Action action) {
            Exception failure = null;
            var thread = new Thread(() => {
                try { action(); }
                catch (Exception ex) { failure = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The VSUX UI check did not complete.");
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }
}
