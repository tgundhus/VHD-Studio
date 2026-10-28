using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Automation;
using VhdAttachCommon;

namespace VhdAttachTest {

    [TestClass]
    [DoNotParallelize]
    public class AutomationTest {
        private const long MiB = 1024L * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private string Folder;
        private bool PreserveFolder;

        [TestInitialize]
        public void Init() {
            this.Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioAutomation-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.PreserveFolder || this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            var expectedParent = Path.TrimEndingDirectorySeparator(PathGuard.GetLongPath(Path.GetTempPath()));
            var target = Path.GetFullPath(this.Folder);
            Assert.AreEqual(expectedParent, Path.GetDirectoryName(target), true, "Only this test's temporary directory may be removed.");
            StringAssert.StartsWith(Path.GetFileName(target), "VhdStudioAutomation-");
            Assert.IsFalse((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0, "Do not clean a substituted root.");
            Directory.Delete(target, recursive: true);
        }

        [TestMethod]
        public void Root_MustBeAnExistingFullyQualifiedDirectory() {
            Assert.ThrowsExactly<ArgumentException>(() => new AutomationOperations("relative-folder", 128 * MiB, false));
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => new AutomationOperations(Path.Combine(this.Folder, "missing"), 128 * MiB, false));
            Assert.ThrowsExactly<ArgumentException>(() => new AutomationOperations(@"\\server\share", 128 * MiB, false));
        }

        [TestMethod]
        [DataRow("image_create")]
        [DataRow("image_attach")]
        [DataRow("image_detach")]
        public void Mutations_RequireExplicitWriteDelegation(string operation) {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, false)) {
                var result = Execute(automation, operation, ImageArguments(operation, "new.vhdx"));
                AssertFailure(result, "approval_required");
                Assert.IsFalse(File.Exists(Path.Combine(this.Folder, "new.vhdx")));
            }
        }

        [TestMethod]
        [DataRow(-1L)]
        [DataRow(0L)]
        [DataRow(32L * MiB)]
        [DataRow(64L * MiB + 1)]
        [DataRow(129L * MiB)]
        [DataRow(long.MaxValue)]
        public void Create_RejectsInvalidOrOverBudgetCapacityBeforeCreatingAnything(long sizeBytes) {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                AssertFailure(Execute(automation, "image_create", new { path = "new.vhdx", sizeBytes, requestId = Guid.NewGuid().ToString() }), "invalid_argument");
                Assert.IsFalse(File.Exists(Path.Combine(this.Folder, "new.vhdx")));
            }
        }

        [TestMethod]
        public void Create_RefusesAnExistingDestinationWithoutChangingItsBytes() {
            var path = Path.Combine(this.Folder, "existing.vhdx");
            var bytes = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(path, bytes);
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                AssertFailure(Execute(automation, "image_create", ImageArguments("image_create", path)), "already_exists");
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            }
        }

        [TestMethod]
        [DataRow("image_create")]
        [DataRow("image_inspect")]
        [DataRow("image_attach")]
        [DataRow("image_detach")]
        public void Paths_CannotEscapeByTraversalOrSiblingPrefix(string operation) {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                foreach (var path in new[] { @"..\outside.vhdx", this.Folder + "-sibling\\outside.vhdx" }) {
                    AssertFailure(Execute(automation, operation, ImageArguments(operation, path)), "outside_root");
                }
            }
        }

        [TestMethod]
        [DataRow(@"\\.\PhysicalDrive0")]
        [DataRow(@"\\?\C:\disk.vhdx")]
        [DataRow(@"\\server\share\disk.vhdx")]
        [DataRow("disk.vhdx:stream")]
        public void Paths_RejectDevicesNetworksAndAlternateStreams(string path) {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                var result = Execute(automation, "image_create", ImageArguments("image_create", path));
                Assert.IsFalse(result.Success, "A path that bypasses the local image namespace was accepted.");
            }
        }

        [TestMethod]
        [DataRow("image_create")]
        [DataRow("image_inspect")]
        [DataRow("image_attach")]
        [DataRow("image_detach")]
        public void Vsux_RemainsOnItsDedicatedInteractivePath(string operation) {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                AssertFailure(Execute(automation, operation, ImageArguments(operation, "protected.vsux")), "unsupported_format");
                Assert.IsFalse(File.Exists(Path.Combine(this.Folder, "protected.vsux")));
            }
        }

        [TestMethod]
        public void Inspect_RefusesHardLinksAndPreservesTheFile() {
            var original = Path.Combine(this.Folder, "original.vhdx");
            var linked = Path.Combine(this.Folder, "linked.vhdx");
            var bytes = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(original, bytes);
            Assert.IsTrue(CreateHardLink(linked, original, IntPtr.Zero), "Create the disposable link fixture.");
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, false)) {
                AssertFailure(Execute(automation, "image_inspect", new { path = linked }), "access_denied");
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(original));
        }

        [TestMethod]
        public void RootAndImagePaths_RefuseJunctions() {
            var real = Directory.CreateDirectory(Path.Combine(this.Folder, "real")).FullName;
            var junction = Path.Combine(this.Folder, "junction");
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + junction + "\" \"" + real + "\"") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })) {
                Assert.IsTrue(process.WaitForExit(10000), "Create the disposable junction fixture.");
                Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
            }
            try {
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => new AutomationOperations(junction, 128 * MiB, false));
                using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                    AssertFailure(Execute(automation, "image_create", ImageArguments("image_create", Path.Combine(junction, "new.vhdx"))), "access_denied");
                    Assert.IsFalse(File.Exists(Path.Combine(real, "new.vhdx")));
                }
            } finally {
                Directory.Delete(junction); // Remove only the junction; do not traverse it during cleanup.
            }
        }

        [TestMethod]
        public void UnknownOperationsAndArguments_AreRejectedWithoutDiskCalls() {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                AssertFailure(Execute(automation, "format_physical_disk", new { diskNumber = 0 }), "invalid_argument");
                AssertFailure(Execute(automation, "image_list", new { unexpected = true }), "invalid_argument");
                AssertFailure(Execute(automation, "image_create", new { path = "new.vhdx", sizeBytes = 64 * MiB, force = true }), "invalid_argument");
            }
        }

        [TestMethod]
        public void OperationStatus_RejectsMissingAndMalformedRequestIdentifiers() {
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, false)) {
                AssertFailure(Execute(automation, "operation_status", new { }), "invalid_argument");
                AssertFailure(Execute(automation, "operation_status", new { requestId = "invalid" }), "invalid_argument");
            }
        }

        [TestMethod]
        [DataRow("{\"path\":\"a.vhdx\",\"path\":\"b.vhdx\",\"sizeBytes\":67108864}")]
        [DataRow("{\"path\":\"a.vhdx\",\"sizeBytes\":67108864,\"fixedSize\":\"true\"}")]
        [DataRow("{\"path\":\"a.vhdx\",\"sizeBytes\":67108864,\"prepare\":null}")]
        [DataRow("{\"path\":\"a.vhdx\",\"sizeBytes\":67108864.5}")]
        public void Create_RejectsDuplicateAndIncorrectlyTypedArguments(string json) {
            using var automation = new AutomationOperations(this.Folder, 128 * MiB, true);
            using var document = JsonDocument.Parse(json);
            AssertFailure(automation.Execute("image_create", document.RootElement), "invalid_argument");
            Assert.IsFalse(File.Exists(Path.Combine(this.Folder, "a.vhdx")));
            Assert.IsFalse(File.Exists(Path.Combine(this.Folder, "b.vhdx")));
        }

        [TestMethod]
        public void Attach_RejectsStringBooleanWithoutOpeningAnImage() {
            using var automation = new AutomationOperations(this.Folder, 128 * MiB, true);
            AssertFailure(Execute(automation, "image_attach", new { path = "missing.vhdx", readOnly = "false" }), "invalid_argument");
        }

        [TestMethod]
        public void InterruptedRequest_StatusReportsUnknownAndPreventsAutomaticRetry() {
            var requestId = Guid.NewGuid().ToString();
            var path = Path.Combine(this.Folder, "interrupted.vhdx");
            var journal = Directory.CreateDirectory(Path.Combine(this.Folder, ".vhdstudio-operations")).FullName;
            var recordPath = Path.Combine(journal, requestId + ".json");
            var record = JsonSerializer.Serialize(new {
                requestId, operation = "image_create", path, fingerprint = new string('A', 64),
                state = "running", startedUtc = DateTimeOffset.UtcNow,
            });
            File.WriteAllText(recordPath, record);
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                var status = Execute(automation, "operation_status", new { requestId });
                Assert.IsTrue(status.Success, status.Error?.Message);
                var data = JsonSerializer.SerializeToElement(status.Data, JsonOptions);
                Assert.AreEqual("unknown", data.GetProperty("state").GetString());
                Assert.AreEqual("outcome_unknown", data.GetProperty("result").GetProperty("error").GetProperty("code").GetString());
                AssertFailure(Execute(automation, "image_create", ImageArguments("image_create", path)), "outcome_unknown");
                Assert.IsFalse(File.Exists(path), "Neither status nor a retry may create an image after an interrupted request.");
                Assert.AreEqual(record, File.ReadAllText(recordPath), "Reading status must preserve the existing request record.");
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [TestCategory("AutomationNative")]
        public void NativeWorkflow_PreservesPayloadAcrossSafeDetachAndReadOnlyReattach() {
            if (Environment.GetEnvironmentVariable("VHDSTUDIO_AUTOMATION_NATIVE_TEST") != "1") {
                Assert.Inconclusive("Set VHDSTUDIO_AUTOMATION_NATIVE_TEST=1 with the matching trusted VHD Studio service running to exercise the complete automation workflow.");
            }
            ScratchDisk.RequireElevation();
            using (var automation = new AutomationOperations(this.Folder, 128 * MiB, true)) {
                const string relativePath = "workspace.vhdx";
                try {
                    var createRequestId = Guid.NewGuid().ToString();
                    var createArguments = new { path = relativePath, sizeBytes = 64 * MiB, prepare = true, requestId = createRequestId };
                    var created = RequireImage(Execute(automation, "image_create", createArguments));
                    Assert.IsFalse(created.Attached, "Prepared creation must finish safely detached.");
                    Assert.AreEqual(64 * MiB, created.VirtualSizeBytes);
                    Assert.IsTrue(Guid.TryParse(created.ImageId, out var id) && id != Guid.Empty);
                    var replayed = RequireImage(Execute(automation, "image_create", createArguments));
                    Assert.AreEqual(created.ImageId, replayed.ImageId, "Retrying an identical request must return its prior result, without recreating the destination.");
                    AssertFailure(Execute(automation, "image_create", new {
                        path = relativePath, sizeBytes = 128 * MiB, prepare = true, requestId = createRequestId,
                    }), "request_conflict");
                    var status = Execute(automation, "operation_status", new { requestId = createRequestId });
                    Assert.IsTrue(status.Success, status.Error?.Message);
                    var state = JsonSerializer.SerializeToElement(status.Data, JsonOptions);
                    Assert.AreEqual("succeeded", state.GetProperty("state").GetString());
                    Assert.IsTrue(state.GetProperty("current").GetProperty("success").GetBoolean());
                    Assert.IsFalse(state.GetProperty("current").GetProperty("data").GetProperty("attached").GetBoolean());

                    RequireImage(Execute(automation, "image_attach", new {
                        path = relativePath, readOnly = false, expectedImageId = created.ImageId, requestId = Guid.NewGuid().ToString(),
                    }));
                    var volumePath = WaitForVolume(automation, relativePath);
                    var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
                    var payloadPath = Path.Combine(volumePath, "automation-payload.bin");
                    using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                        stream.Write(payload);
                        stream.Flush(flushToDisk: true);
                        AssertFailure(Execute(automation, "image_detach", new {
                            path = relativePath, expectedImageId = created.ImageId, requestId = Guid.NewGuid().ToString(),
                        }), "volume_busy");
                    }
                    Assert.IsFalse(RequireImage(Execute(automation, "image_detach", ImageArguments("image_detach", relativePath))).Attached);

                    RequireImage(Execute(automation, "image_attach", new {
                        path = relativePath, readOnly = true, expectedImageId = created.ImageId, requestId = Guid.NewGuid().ToString(),
                    }));
                    volumePath = WaitForVolume(automation, relativePath);
                    using (var stream = File.OpenRead(Path.Combine(volumePath, "automation-payload.bin"))) {
                        CollectionAssert.AreEqual(SHA256.HashData(payload), SHA256.HashData(stream), "The detached image must retain the exact payload.");
                    }
                    Assert.IsFalse(RequireImage(Execute(automation, "image_detach", ImageArguments("image_detach", relativePath))).Attached);
                } finally {
                    if (File.Exists(Path.Combine(this.Folder, relativePath))) {
                        var state = Execute(automation, "image_inspect", new { path = relativePath });
                        if (!state.Success || ((AutomationImage)state.Data).Attached) {
                            var detach = Execute(automation, "image_detach", ImageArguments("image_detach", relativePath));
                            if (!detach.Success || ((AutomationImage)detach.Data).Attached) {
                                this.PreserveFolder = true;
                                Assert.Fail("Safe cleanup could not confirm detachment. Disposable image retained at " + this.Folder);
                            }
                        }
                    }
                }
            }
        }

        private static string WaitForVolume(AutomationOperations automation, string path) {
            for (int attempt = 0; attempt < 40; attempt++) {
                var image = RequireImage(Execute(automation, "image_inspect", new { path }));
                foreach (var volume in image.Volumes) {
                    if (!string.IsNullOrEmpty(volume.VolumePath) && Directory.Exists(volume.VolumePath)) { return volume.VolumePath; }
                }
                Thread.Sleep(250);
            }
            Assert.Fail("Windows did not publish the prepared scratch image's volume.");
            return null;
        }

        private static AutomationImage RequireImage(AutomationResult result) {
            Assert.IsTrue(result.Success, result.Error?.Code + ": " + result.Error?.Message);
            return result.Data is AutomationImage image ? image : ((JsonElement)result.Data).Deserialize<AutomationImage>(JsonOptions);
        }

        private static object ImageArguments(string operation, string path) => operation switch {
            "image_create" => new { path, sizeBytes = 64 * MiB, requestId = Guid.NewGuid().ToString() },
            "image_attach" or "image_detach" => new { path, requestId = Guid.NewGuid().ToString() },
            _ => new { path },
        };

        private static AutomationResult Execute(AutomationOperations automation, string operation, object arguments) => automation.Execute(operation, JsonSerializer.SerializeToElement(arguments));

        private static void AssertFailure(AutomationResult result, string code) {
            Assert.IsFalse(result.Success);
            Assert.IsNotNull(result.Error);
            Assert.AreEqual(code, result.Error.Code, result.Error.Message);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
