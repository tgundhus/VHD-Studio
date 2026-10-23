using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttachTest {

    [TestClass]
    public class VsuxMetadataTest {
        private const long DiskSize = 64L * 1024 * 1024;
        private const string Password = "VSUX disposable embedded metadata password";
        private const int SnapshotSize = 1024 * 1024;
        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsuxMetadata-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            foreach (var file in Directory.GetFiles(this.Folder)) { File.Delete(file); }
            Directory.Delete(this.Folder); // Only this test's flat directory; mounted fixtures use ScratchDisk disposal.
        }

        [TestMethod]
        public void EmbeddedPassword_TravelsWithCopiedAndRenamedFile() {
            var original = this.CreateProtected();
            var moved = Path.Combine(this.Folder, "renamed-copy.vsux");
            File.Copy(original, moved, overwrite: false);
            Assert.IsFalse(File.Exists(original + ".json"));
            var originalHash = ScratchDisk.HashFile(original);
            var info = VsuxOperations.GetInfo(moved);
            Assert.IsTrue(info.HasEmbeddedPassword);
            Assert.IsFalse(info.RequiresCompanion);
            Assert.IsTrue(info.IsLocked);
            Assert.AreEqual(VsuxOperations.GetInfo(original).DiskId, info.DiskId);

            VsuxOperations.Unlock(moved, Password);

            Assert.IsFalse(VsuxOperations.GetInfo(moved).IsLocked);
            Assert.AreEqual(originalHash, ScratchDisk.HashFile(original), "Unlocking the copy changed the original disk.");
            CollectionAssert.AreEquivalent(new[] { original, moved }, Directory.GetFiles(this.Folder), "Password unlock must need no sidecar or application-local state.");
        }

        [TestMethod]
        public void EmbeddedPassword_IgnoresUnrelatedJsonWithoutChangingIt() {
            var container = this.CreateProtected();
            File.WriteAllText(container + ".json", "This is not a VSUX descriptor.");
            var jsonHash = ScratchDisk.HashFile(container + ".json");
            var lockedHash = ScratchDisk.HashFile(container);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(container, "incorrect password"));
            Assert.AreEqual(lockedHash, ScratchDisk.HashFile(container));

            VsuxOperations.Unlock(container, Password);

            Assert.AreEqual(jsonHash, ScratchDisk.HashFile(container + ".json"));
            Assert.IsTrue(VsuxOperations.GetInfo(container).HasEmbeddedPassword);
        }

        [TestMethod]
        public void EmbeddedPassword_UsesDifferentSaltForEachDisk() {
            var first = File.ReadAllBytes(this.CreateProtected());
            var second = File.ReadAllBytes(this.CreateProtected());
            var firstSalt = first.AsSpan(FindRecord(first) + 32, 32);
            var secondSalt = second.AsSpan(FindRecord(second) + 32, 32);
            Assert.IsFalse(firstSalt.SequenceEqual(secondSalt), "The password salt must be generated for each disk, not shared by the application.");
        }

        [TestMethod]
        public void EmbeddedPassword_CorruptionIsRefusedWithoutFallingBackToJson() {
            var container = this.CreateProtected();
            this.WriteLegacyCompanion(container); // A valid old descriptor must not hide damaged embedded credentials.
            var bytes = File.ReadAllBytes(container);
            var offset = FindRecord(bytes);
            bytes[offset + "VSUXPWD2"u8.Length] ^= 1;
            File.WriteAllBytes(container, bytes); // Corrupt only this test-created custom record, leaving its table intact.
            var imageHash = ScratchDisk.HashFile(container);
            var jsonHash = ScratchDisk.HashFile(container + ".json");

            Assert.ThrowsExactly<InvalidDataException>(() => VsuxOperations.Unlock(container, Password));

            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container), "Rejecting corrupt metadata changed the image.");
            Assert.AreEqual(jsonHash, ScratchDisk.HashFile(container + ".json"));
        }

        [TestMethod]
        public void EmbeddedPassword_RecordFromAnotherDiskIsRefused() {
            var source = File.ReadAllBytes(this.CreateProtected());
            var target = this.CreateProtected();
            var bytes = File.ReadAllBytes(target);
            source.AsSpan(FindRecord(source), 128).CopyTo(bytes.AsSpan(FindRecord(bytes), 128));
            File.WriteAllBytes(target, bytes); // This fixture has a valid record checksum but the wrong disk identifier.
            var imageHash = ScratchDisk.HashFile(target);

            Assert.ThrowsExactly<InvalidDataException>(() => VsuxOperations.Unlock(target, Password));

            Assert.AreEqual(imageHash, ScratchDisk.HashFile(target));
        }

        [TestMethod]
        public void LegacyPassword_StillUnlocksWithoutImplicitMigration() {
            var container = this.CreateLegacyProtected();
            var locked = File.ReadAllBytes(container);
            var jsonHash = ScratchDisk.HashFile(container + ".json");
            var info = VsuxOperations.GetInfo(container);
            Assert.IsFalse(info.HasEmbeddedPassword);
            Assert.IsTrue(info.RequiresCompanion);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(container, "incorrect password"));
            CollectionAssert.AreEqual(locked, File.ReadAllBytes(container));

            VsuxOperations.Unlock(container, Password);

            var unlocked = File.ReadAllBytes(container);
            Assert.IsTrue(unlocked.AsSpan(0, 8).SequenceEqual("vhdxfile"u8));
            Assert.IsTrue(locked.AsSpan(8).SequenceEqual(unlocked.AsSpan(8)), "A legacy unlock must not migrate or rewrite metadata.");
            Assert.AreEqual(jsonHash, ScratchDisk.HashFile(container + ".json"));
            Assert.IsTrue(VsuxOperations.GetInfo(container).RequiresCompanion);
        }

        [TestMethod]
        public void LegacyMigration_RequiresPasswordAndKeepsExistingRecoveryFile() {
            var container = this.CreateLegacyProtected();
            var imageHash = ScratchDisk.HashFile(container);
            var json = container + ".json";
            var jsonHash = ScratchDisk.HashFile(json);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.MakeSelfContained(container, "incorrect password"));
            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container));
            Assert.AreEqual(jsonHash, ScratchDisk.HashFile(json));

            VsuxOperations.MakeSelfContained(container, Password);

            var info = VsuxOperations.GetInfo(container);
            Assert.IsTrue(info.HasEmbeddedPassword);
            Assert.IsFalse(info.RequiresCompanion);
            Assert.IsTrue(info.IsLocked, "Migration must finish locked.");
            Assert.IsFalse(info.IsAttached);
            Assert.AreEqual(jsonHash, ScratchDisk.HashFile(json), "Migration must not delete or rewrite a pre-existing companion.");
            File.Move(json, Path.Combine(this.Folder, "retained-legacy-backup.json"));
            VsuxOperations.Unlock(container, Password);
            Assert.IsFalse(VsuxOperations.GetInfo(container).IsLocked, "Migrated password operations still depend on the old filename.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ConvertBack_EmbeddedAndLegacyProduceOrdinaryDetachedVhdx(bool legacy) {
            var container = legacy ? this.CreateLegacyProtected() : this.CreateProtected();
            var diskId = VsuxOperations.GetInfo(container).DiskId;
            var destination = Path.ChangeExtension(container, ".vhdx");
            var companionHash = legacy ? ScratchDisk.HashFile(container + ".json") : null;

            VsuxOperations.ConvertToVhdx(container, destination, Password);

            Assert.IsFalse(File.Exists(container), "Conversion should rename the detached file without leaving an unlocked VSUX source.");
            this.AssertOrdinaryVhdx(destination, diskId);
            if (legacy) { Assert.AreEqual(companionHash, ScratchDisk.HashFile(container + ".json"), "Conversion changed a pre-existing recovery file."); }
        }

        [TestMethod]
        public void ConvertBack_WrongPasswordLeavesLockedFileUnchanged() {
            var container = this.CreateProtected();
            var destination = Path.ChangeExtension(container, ".vhdx");
            var imageHash = ScratchDisk.HashFile(container);

            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.ConvertToVhdx(container, destination, "incorrect password"));

            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container));
            Assert.IsFalse(File.Exists(destination));
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
        }

        [TestMethod]
        public void ConvertBack_ExistingDestinationIsNeverOverwritten() {
            var container = this.CreateProtected();
            var destination = Path.ChangeExtension(container, ".vhdx");
            File.WriteAllBytes(destination, RandomNumberGenerator.GetBytes(4096));
            var sourceHash = ScratchDisk.HashFile(container);
            var destinationHash = ScratchDisk.HashFile(destination);

            Assert.ThrowsExactly<IOException>(() => VsuxOperations.ConvertToVhdx(container, destination, Password));

            Assert.AreEqual(sourceHash, ScratchDisk.HashFile(container));
            Assert.AreEqual(destinationHash, ScratchDisk.HashFile(destination));
            CollectionAssert.AreEquivalent(new[] { container, destination }, Directory.GetFiles(this.Folder));
        }

        [TestMethod]
        public void ConvertBack_OpenFileIsRefusedBeforeChangingIt() {
            var container = this.CreateProtected();
            var destination = Path.ChangeExtension(container, ".vhdx");
            var imageHash = ScratchDisk.HashFile(container);
            using (var held = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                AssertInUseRefusal(() => VsuxOperations.ConvertToVhdx(container, destination, Password));
            }
            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container));
            Assert.IsFalse(File.Exists(destination));
        }

        [TestMethod]
        public void RecoveryExport_IsOptionalAndNeverOverwritesItsDestination() {
            var container = this.CreateProtected();
            var backup = Path.Combine(this.Folder, "optional-recovery.json");
            var imageHash = ScratchDisk.HashFile(container);

            VsuxOperations.ExportRecoveryBackup(container, backup);

            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container), "Exporting diagnostics changed the disk.");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(backup))) {
                Assert.AreEqual(JsonValueKind.Object, json.RootElement.ValueKind);
                Assert.IsFalse(json.RootElement.TryGetProperty("passwordHash", out _), "A recovery-only export should not become required password storage.");
                Assert.IsFalse(json.RootElement.TryGetProperty("salt", out _));
            }
            var backupHash = ScratchDisk.HashFile(backup);
            Assert.ThrowsExactly<IOException>(() => VsuxOperations.ExportRecoveryBackup(container, backup));
            Assert.AreEqual(imageHash, ScratchDisk.HashFile(container));
            Assert.AreEqual(backupHash, ScratchDisk.HashFile(backup));
            File.Delete(backup);
            VsuxOperations.Unlock(container, Password);
            Assert.IsFalse(VsuxOperations.GetInfo(container).IsLocked, "Removing an optional backup broke password unlock.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SoftProtectionPanel_VisiblyOffersConversionAndRecoveryTools(bool legacy) {
            var container = legacy ? this.CreateLegacyProtected() : this.CreateProtected();
            VsuxCreationTest.RunSta(() => {
                using (var form = new VhdAttach.VsuxToolsForm(container)) {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    try {
                        var buttons = VsuxCreationTest.Descendants(form).OfType<Button>().ToDictionary(button => button.Name);
                        var convert = buttons["ConvertVsuxButton"];
                        var portable = buttons["MakeSelfContainedButton"];
                        var backup = buttons["ExportRecoveryButton"];
                        Assert.IsTrue(convert.Visible && portable.Visible && backup.Visible, "The soft-protection panel must visibly expose all three tools.");
                        Assert.IsTrue(convert.Enabled);
                        Assert.IsTrue(backup.Enabled);
                        Assert.AreEqual(legacy, portable.Enabled, "Only a legacy image needs companion migration.");
                        StringAssert.Contains(convert.AccessibleName, "Convert back to VHDX");
                        StringAssert.Contains(backup.AccessibleName, "Export recovery");
                        var text = string.Join("\n", VsuxCreationTest.Descendants(form).Select(control => control.Text));
                        Assert.IsFalse(text.Contains("JSON", StringComparison.OrdinalIgnoreCase), "Normal soft-protection tools must describe optional recovery files without JSON terminology.");
                        Assert.IsFalse(text.Contains("companion", StringComparison.OrdinalIgnoreCase), "The normal workflow must not describe a companion-file requirement.");
                    } finally { form.Hide(); }
                }
            });
        }

        [TestMethod]
        [DataRow("Convert", false)]
        [DataRow("Convert", true)]
        [DataRow("Protect", false)]
        [DataRow("Protect", true)]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void ProtectionActions_AskForPasswordDirectlyWithOneVisibleDialog(string operation, bool accept) {
            ScratchDisk.RequireElevation();
            var source = operation == "Convert" ? this.CreateProtected() : this.CreateNative();
            var target = Path.ChangeExtension(source, operation == "Convert" ? ".vhdx" : ".vsux");
            var originalHash = ScratchDisk.HashFile(source);
            var savedExitCode = Environment.ExitCode;
            try {
                VsuxCreationTest.RunSta(() => {
                    using (Form owner = operation == "Convert" ? new VhdAttach.VsuxToolsForm(source) : new VhdAttach.MaintenanceForm(source, "Soft protection")) {
                        owner.StartPosition = FormStartPosition.Manual;
                        owner.Location = new Point(-30000, -30000);
                        owner.Opacity = 0.85;
                        owner.ShowInTaskbar = true;
                        owner.Show();
                        var opacity = owner.Opacity;
                        var prompts = 0;
                        var oneVisibleDialog = true;
                        var imageUnchangedAtPrompt = false;
                        var correctButton = false;
                        var timedOut = false;
                        var elapsed = Stopwatch.StartNew();
                        using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                            timer.Tick += (sender, args) => {
                                var dialogs = OwnedDialogs(owner);
                                var password = dialogs.OfType<VhdAttach.VsuxPasswordForm>().FirstOrDefault(form => form.Visible);
                                if (password != null && prompts == 0) {
                                    prompts++;
                                    oneVisibleDialog = owner.Opacity == 0 && !owner.ShowInTaskbar
                                        && password.Owner is VhdAttach.VsuxForm operationForm && operationForm.Opacity == 0 && !operationForm.ShowInTaskbar
                                        && dialogs.Count(dialog => dialog.Visible && dialog.Opacity > 0) == 1;
                                    imageUnchangedAtPrompt = File.Exists(source) && ScratchDisk.HashFile(source) == originalHash && !File.Exists(target);
                                    correctButton = operation != "Convert" || ((Button)password.AcceptButton).Text.Replace("&", "") == "OK";
                                    password.StartPosition = FormStartPosition.Manual;
                                    password.Location = new Point(-30000, -30000);
                                    if (accept) {
                                        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                                        ((TextBox)typeof(VhdAttach.VsuxPasswordForm).GetField("PasswordBox", fields).GetValue(password)).Text = Password;
                                        if (operation == "Protect") { ((TextBox)typeof(VhdAttach.VsuxPasswordForm).GetField("ConfirmationBox", fields).GetValue(password)).Text = Password; }
                                        password.AcceptButton.PerformClick();
                                    } else { password.DialogResult = DialogResult.Cancel; }
                                }
                                if (elapsed.Elapsed < TimeSpan.FromSeconds(20)) { return; }
                                timedOut = true;
                                for (var index = dialogs.Count - 1; index >= 0; index--) { dialogs[index].DialogResult = DialogResult.Cancel; }
                            };
                            try {
                                timer.Start();
                                var buttonName = operation == "Convert" ? "ConvertVsuxButton" : "ProtectVsuxButton";
                                VsuxCreationTest.Descendants(owner).OfType<Button>().Single(button => button.Name == buttonName).PerformClick();
                            } finally { timer.Stop(); }
                        }
                        Assert.IsFalse(timedOut, "The direct password workflow did not finish within twenty seconds.");
                        Assert.AreEqual(1, prompts, "The action must go directly to a password prompt without a destination or confirmation dialog.");
                        Assert.IsTrue(oneVisibleDialog, "Only the password prompt should be visible while the operation and its owning tools wait.");
                        Assert.IsTrue(imageUnchangedAtPrompt, "The image changed before the password was accepted.");
                        Assert.IsTrue(correctButton, "Convert back to VHDX must require only the password and OK.");
                        if (!accept) {
                            Assert.AreEqual(opacity, owner.Opacity, "Cancellation must restore the original tools window opacity.");
                            Assert.IsTrue(owner.ShowInTaskbar && owner.Visible && owner.Enabled, "Cancellation must restore the usable tools window.");
                        }
                        if (!owner.IsDisposed) { owner.Hide(); }
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            if (accept) {
                Assert.IsFalse(File.Exists(source));
                CollectionAssert.AreEquivalent(new[] { target }, Directory.GetFiles(this.Folder), "A successful action must rename the source using the same base name without extra output files.");
                if (operation == "Convert") { Assert.AreEqual("VHDX", VirtualDiskImage.GetDetails(target).Format); }
                else { Assert.IsTrue(VsuxOperations.GetInfo(target).IsLocked); }
            } else {
                Assert.AreEqual(originalHash, ScratchDisk.HashFile(source), "Cancelling the password changed the image.");
                CollectionAssert.AreEquivalent(new[] { source }, Directory.GetFiles(this.Folder), "Cancellation must leave no output or temporary images.");
            }
        }

        private static List<Form> OwnedDialogs(Form owner) {
            var dialogs = new List<Form>();
            foreach (var dialog in owner.OwnedForms) {
                dialogs.Add(dialog);
                dialogs.AddRange(OwnedDialogs(dialog));
            }
            return dialogs;
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void EmbeddedLockAndUnlock_OnlyChangeSignatureAfterInitialProtection() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            var locked = File.ReadAllBytes(container);
            VsuxOperations.Unlock(container, Password);
            var unlocked = File.ReadAllBytes(container);
            Assert.IsTrue(unlocked.AsSpan(0, 8).SequenceEqual("vhdxfile"u8));
            Assert.IsTrue(locked.AsSpan(8).SequenceEqual(unlocked.AsSpan(8)));

            VsuxOperations.Lock(container, Password);

            CollectionAssert.AreEqual(locked, File.ReadAllBytes(container), "Locking a detached, clean image changed bytes beyond its signature.");
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void ConvertBack_AttachedImageIsRefusedWithoutRenamingOrDetachingIt() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            var destination = Path.ChangeExtension(container, ".vhdx");
            VsuxOperations.Attach(container, Password, readOnly: true);
            try {
                var imageHash = ScratchDisk.HashFile(container);
                AssertInUseRefusal(() => VsuxOperations.ConvertToVhdx(container, destination, Password));
                Assert.AreEqual(imageHash, ScratchDisk.HashFile(container));
                Assert.IsTrue(File.Exists(container));
                Assert.IsFalse(File.Exists(destination));
                Assert.IsTrue(VsuxOperations.GetInfo(container).IsAttached, "Conversion must not silently detach an attached image.");
            } finally { VsuxOperations.Lock(container, Password); }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DoNotParallelize]
        public void ProtectUnlockAndConvertBack_PreserveEveryPayloadFile() {
            ScratchDisk.RequireElevation();
            using (var original = ScratchDisk.Create()) {
                original.Attach(readOnly: false);
                var expected = original.WriteFiles(3, 2 * 1024 * 1024, "vsux-payload-");
                original.Detach();
                var container = Path.ChangeExtension(original.FileName, ".vsux");
                var destination = Path.Combine(Path.GetDirectoryName(original.FileName), "restored-" + Guid.NewGuid().ToString("N") + ".vhdx");
                var inspection = Path.Combine(Path.GetDirectoryName(original.FileName), "inspect-" + Guid.NewGuid().ToString("N") + ".vhdx");
                try {
                    VsuxOperations.Protect(original.FileName, container, Password);
                    Assert.IsFalse(File.Exists(container + ".json"));
                    VsuxOperations.Unlock(container, Password);
                    // ScratchDisk's service detach intentionally refuses .vsux. Use the normal extension
                    // only for this fixture's read-only content inspection, then restore its original path.
                    File.Move(container, inspection, overwrite: false);
                    using (var unlocked = ScratchDisk.Open(inspection)) {
                        unlocked.Verify(expected);
                        File.Move(inspection, container, overwrite: false);
                    }
                    VsuxOperations.Lock(container, Password);
                    VsuxOperations.ConvertToVhdx(container, destination, Password);
                    using (var restored = ScratchDisk.Open(destination)) { restored.Verify(expected); }
                    Assert.IsFalse(File.Exists(container));
                } finally {
                    if (File.Exists(container)) { File.Delete(container); }
                    if (File.Exists(destination)) { File.Delete(destination); }
                    if (File.Exists(inspection)) { File.Delete(inspection); }
                }
            }
        }

        private string CreateNative() {
            var image = Path.Combine(this.Folder, "disposable-" + Guid.NewGuid().ToString("N") + ".vhdx");
            try {
                using (var disk = new Medo.IO.VirtualDisk(image)) {
                    disk.Create(DiskSize, Medo.IO.VirtualDiskCreateOptions.None, 0, 0, Medo.IO.VirtualDiskType.Vhdx);
                }
            } catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
                Assert.Inconclusive("Cannot create disposable VHDX files in this environment: " + ex.Message);
            }
            return image;
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(4097)]
        public void IndependentRecovery_IgnoresInvalidPrivateCredentialEntry(int credentialLength) {
            var container = this.CreateProtected();
            var bytes = File.ReadAllBytes(container);
            var location = FindPasswordEntry(bytes);
            var offset = credentialLength == 0 ? 0 : location.RegionLength - 8192;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(location.Entry + 16), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(location.Entry + 20), (uint)credentialLength);
            if (credentialLength > 0) { bytes.AsSpan(location.Region + offset, credentialLength).Fill(0x5A); }
            File.WriteAllBytes(container, bytes); // Only this test's fresh image, with a structurally valid user item.
            Assert.ThrowsExactly<InvalidDataException>(() => VsuxOperations.Unlock(container, Password));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(container), "Invalid password metadata caused an image write.");

            var signatureType = typeof(VsuxOperations).Assembly.GetType("VhdStudio.Vsux.SignatureFile", throwOnError: true);
            try { signatureType.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { container }); }
            catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }

            var restored = File.ReadAllBytes(container);
            Assert.IsTrue(restored.AsSpan(0, 8).SequenceEqual("vhdxfile"u8));
            Assert.IsTrue(bytes.AsSpan(8).SequenceEqual(restored.AsSpan(8)), "Recovery changed bytes outside the signature.");
            var native = Path.ChangeExtension(container, ".vhdx");
            File.Move(container, native, overwrite: false);
            Assert.AreEqual("VHDX", VirtualDiskImage.GetDetails(native).Format);
        }

        private string CreateProtected() {
            var image = this.CreateNative();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);
            return container;
        }

        private string CreateLegacyProtected() {
            var image = this.CreateNative();
            var container = Path.ChangeExtension(image, ".vsux");
            File.Move(image, container, overwrite: false);
            this.WriteLegacyCompanion(container);
            using (var stream = new FileStream(container, FileMode.Open, FileAccess.Write, FileShare.None)) {
                stream.Write("vsuxlock"u8); // Build the old format only inside a newly created, uniquely named fixture.
                stream.Flush(flushToDisk: true);
            }
            return container;
        }

        private void WriteLegacyCompanion(string container) {
            Assert.AreEqual(this.Folder, Path.GetDirectoryName(container));
            var info = VsuxOperations.GetInfo(container);
            var snapshot = new byte[SnapshotSize];
            using (var file = File.OpenRead(container)) { file.ReadExactly(snapshot); }
            var salt = RandomNumberGenerator.GetBytes(32);
            var passwordHash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, 600_000, HashAlgorithmName.SHA256, 32);
            var json = JsonSerializer.SerializeToUtf8Bytes(new {
                format = "VSUX-prototype-1", diskId = info.DiskId, iterations = 600_000,
                salt, passwordHash, headerSnapshot = snapshot, snapshotSha256 = SHA256.HashData(snapshot)
            });
            using (var file = new FileStream(container + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(json); }
        }

        private void AssertOrdinaryVhdx(string file, Guid diskId) {
            var details = VirtualDiskImage.GetDetails(file);
            Assert.AreEqual("VHDX", details.Format);
            Assert.AreEqual(DiskSize, details.VirtualSize);
            Assert.IsNull(details.AttachedPath);
            Assert.IsFalse(details.NeedsLogReplay);
            using (var stream = File.OpenRead(file)) {
                var signature = new byte[8];
                stream.ReadExactly(signature);
                Assert.IsTrue(signature.AsSpan().SequenceEqual("vhdxfile"u8));
            }
            var type = typeof(VsuxOperations).Assembly.GetType("VhdStudio.Vsux.SignatureFile", throwOnError: true);
            object signatureFile;
            var open = type.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
            var arguments = open.GetParameters().Select((parameter, index) => index == 0 ? (object)file : parameter.DefaultValue).ToArray();
            try { signatureFile = open.Invoke(null, arguments); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            using ((IDisposable)signatureFile) {
                Assert.AreEqual(diskId, (Guid)type.GetProperty("DiskId").GetValue(signatureFile));
                Assert.IsNull(type.GetProperty("EmbeddedPasswordRecord", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(signatureFile), "Converting back left the VSUX password record in the native metadata table.");
            }
        }

        private static int FindRecord(byte[] bytes) {
            var location = FindPasswordEntry(bytes);
            Assert.AreEqual(128u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(location.Entry + 20)));
            Assert.IsTrue(bytes.AsSpan(location.Data, 8).SequenceEqual("VSUXPWD2"u8));
            return location.Data;
        }

        private static (int Data, int Entry, int Region, int RegionLength) FindPasswordEntry(byte[] bytes) {
            // Locate the active custom item, not a stale copy that Windows may leave in its VHDX log.
            var regions = bytes.AsSpan(192 * 1024, 64 * 1024);
            Assert.IsTrue(regions.Slice(0, 4).SequenceEqual("regi"u8));
            var regionCount = BinaryPrimitives.ReadUInt32LittleEndian(regions.Slice(8));
            for (var index = 0; index < regionCount; index++) {
                var region = regions.Slice(16 + index * 32, 32);
                if (new Guid(region.Slice(0, 16)) != new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E")) { continue; }
                var metadataOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(region.Slice(16)));
                var table = bytes.AsSpan(metadataOffset, 64 * 1024);
                Assert.IsTrue(table.Slice(0, 8).SequenceEqual("metadata"u8));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(table.Slice(10));
                for (var item = 0; item < count; item++) {
                    var entry = table.Slice(32 + item * 32, 32);
                    if (new Guid(entry.Slice(0, 16)) != new Guid("F75CC1A7-70E1-43EF-B910-65F43DA2B132")) { continue; }
                    var offset = checked(metadataOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16)));
                    return (offset, metadataOffset + 32 + item * 32, metadataOffset, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(region.Slice(24))));
                }
            }
            Assert.Fail("The embedded test image contains no active VSUX password metadata item.");
            return default;
        }

        private static void AssertInUseRefusal(Action action) {
            try {
                action();
                Assert.Fail("An in-use image was accepted for conversion.");
            } catch (IOException) { // Managed exclusive file opens report sharing failures as IOException.
            } catch (Win32Exception error) {
                Assert.IsTrue(error.NativeErrorCode is 5 or 32 or 33, "The native open failed for an unexpected reason: " + error.Message);
            }
        }
    }
}
