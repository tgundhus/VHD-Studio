using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Automation;
using VhdAttachCommon;

namespace VhdAttachTest {
    [TestClass]
    [DoNotParallelize]
    public class AutomationJournalTest {
        private const long ImageSize = 64L * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private string Folder;

        [TestInitialize]
        public void Init() {
            Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioJournal-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (Folder == null || !Directory.Exists(Folder)) { return; }
            var target = Path.GetFullPath(Folder);
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(PathGuard.GetLongPath(Path.GetTempPath())), Path.GetDirectoryName(target), true);
            StringAssert.StartsWith(Path.GetFileName(target), "VhdStudioJournal-");
            Assert.IsFalse((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(target, recursive: true);
        }

        [TestMethod]
        public void StatusReader_AllowsAtomicCompletionAndKeepsItsOriginalSnapshot() {
            var id = Guid.NewGuid().ToString("D");
            var image = Path.Combine(Folder, "workspace.vhdx");
            var recordPath = JournalPath(id);
            var running = Record(id, image, "running");
            var completed = Record(id, image, "succeeded", Success(id, image));
            File.WriteAllText(recordPath, running);
            var replacement = Path.Combine(Path.GetDirectoryName(recordPath), "completion.tmp");
            File.WriteAllText(replacement, completed);

            using var reader = AutomationOperations.OpenJournalReader(recordPath);
            AutomationOperations.CommitJournal(replacement, recordPath);
            using (var text = new StreamReader(reader, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true)) {
                Assert.AreEqual(running, text.ReadToEnd(), "An open reader must retain a complete old snapshot during replacement.");
            }
            using var automation = new AutomationOperations(Folder, 2 * ImageSize, false);
            var status = Execute(automation, "operation_status", new { requestId = id });
            Assert.IsTrue(status.Success, status.Error?.Message);
            var data = JsonSerializer.SerializeToElement(status.Data, JsonOptions);
            Assert.AreEqual("succeeded", data.GetProperty("state").GetString());
            Assert.IsTrue(data.GetProperty("result").GetProperty("success").GetBoolean());
            Assert.AreEqual(completed, File.ReadAllText(recordPath));
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void UnknownRequest_DirectoryAliasesCannotBypassTheMutationBlock(bool storedAlias, bool imageExists) {
            var (longPath, aliasPath) = AliasImagePaths();
            var stored = storedAlias ? aliasPath : longPath;
            var requested = storedAlias ? longPath : aliasPath;
            var id = Guid.NewGuid().ToString("D");
            var recordPath = JournalPath(id);
            var original = Record(id, stored, "running");
            File.WriteAllText(recordPath, original);
            if (imageExists) { File.WriteAllText(longPath, "Disposable placeholder; not a disk image."); }

            using var automation = new AutomationOperations(Folder, 2 * ImageSize, true);
            // Both regression paths stop before contacting the service, including on an unfixed build:
            // create sees an existing placeholder; detach sees a missing file.
            var result = imageExists
                ? Execute(automation, "image_create", new { path = requested, sizeBytes = ImageSize })
                : Execute(automation, "image_detach", new { path = requested });
            Assert.IsFalse(result.Success);
            Assert.AreEqual("outcome_unknown", result.Error?.Code, result.Error?.Message);
            Assert.AreEqual(original, File.ReadAllText(recordPath), "Checking an alias must preserve the unresolved request.");
            Assert.AreEqual(imageExists, File.Exists(longPath));
        }

        [TestMethod]
        public void CompletedRequest_ReplaysThroughEitherDirectorySpelling() {
            var (longPath, aliasPath) = AliasImagePaths();
            var id = Guid.NewGuid().ToString("D");
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                operation = "image_create", path = longPath.ToUpperInvariant(), size = ImageSize,
                fixedSize = false, prepare = true, readOnly = true, expectedId = (string)null,
            }, JsonOptions)));
            var recordPath = JournalPath(id);
            var original = Record(id, longPath, "succeeded", Success(id, longPath), fingerprint);
            File.WriteAllText(recordPath, original);

            using var automation = new AutomationOperations(Folder, 2 * ImageSize, true);
            foreach (var requested in new[] { longPath, aliasPath }) {
                var result = Execute(automation, "image_create", new { path = requested, sizeBytes = ImageSize, requestId = id });
                Assert.IsTrue(result.Success, result.Error?.Code + ": " + result.Error?.Message);
                Assert.AreEqual(id, result.RequestId);
                Assert.AreEqual(longPath, JsonSerializer.SerializeToElement(result.Data, JsonOptions).GetProperty("path").GetString());
            }
            Assert.IsFalse(File.Exists(longPath), "Replay must return history without creating any image.");
            Assert.AreEqual(original, File.ReadAllText(recordPath));
        }

        [TestMethod]
        [TestCategory("Elevated")]
        [DataRow(false)]
        [DataRow(true)]
        public void CustomShortDirectoryWithoutTilde_CannotBypassUnresolvedRequests(bool storedAlias) {
            ScratchDisk.RequireElevation();
            var (longPath, aliasPath) = CustomAliasImagePaths();
            var stored = storedAlias ? aliasPath : longPath;
            var requested = storedAlias ? longPath : aliasPath;
            var id = Guid.NewGuid().ToString("D");
            var recordPath = JournalPath(id);
            var original = Record(id, stored, "running");
            File.WriteAllText(recordPath, original);
            Assert.AreNotEqual(Path.GetFullPath(longPath), Path.GetFullPath(aliasPath), true,
                "This fixture must exercise native short names that Path.GetFullPath does not expand.");

            using var automation = new AutomationOperations(Folder, 2 * ImageSize, true);
            var result = Execute(automation, "image_detach", new { path = requested });
            Assert.IsFalse(result.Success);
            Assert.AreEqual("outcome_unknown", result.Error?.Code, result.Error?.Message);
            Assert.AreEqual(original, File.ReadAllText(recordPath));
            Assert.IsFalse(File.Exists(longPath));
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void CustomShortDirectoryWithoutTilde_ReplaysTheSameCompletedRequest() {
            ScratchDisk.RequireElevation();
            var (longPath, aliasPath) = CustomAliasImagePaths();
            var id = Guid.NewGuid().ToString("D");
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                operation = "image_create", path = longPath.ToUpperInvariant(), size = ImageSize,
                fixedSize = false, prepare = true, readOnly = true, expectedId = (string)null,
            }, JsonOptions)));
            var original = Record(id, longPath, "succeeded", Success(id, longPath), fingerprint);
            var recordPath = JournalPath(id);
            File.WriteAllText(recordPath, original);

            using var automation = new AutomationOperations(Folder, 2 * ImageSize, true);
            var result = Execute(automation, "image_create", new { path = aliasPath, sizeBytes = ImageSize, requestId = id });
            Assert.IsTrue(result.Success, result.Error?.Code + ": " + result.Error?.Message);
            Assert.AreEqual(id, result.RequestId);
            Assert.IsFalse(File.Exists(longPath), "Replay must not create an image.");
            Assert.AreEqual(original, File.ReadAllText(recordPath));
        }

        private (string LongPath, string AliasPath) AliasImagePaths() {
            var child = Directory.CreateDirectory(Path.Combine(Folder, "workspace-directory-with-a-long-name-" + Guid.NewGuid().ToString("N")));
            var buffer = new StringBuilder(32768);
            var length = GetShortPathName(child.FullName, buffer, buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity) { Assert.Inconclusive("This volume does not provide an 8.3 directory alias."); }
            var shortName = Path.GetFileName(Path.TrimEndingDirectorySeparator(buffer.ToString()));
            if (string.Equals(shortName, child.Name, StringComparison.OrdinalIgnoreCase)) {
                Assert.Inconclusive("8.3 directory names are disabled on this volume; its policy was left unchanged.");
            }
            // Preserve the root's long spelling so this specifically tests an alias inside the allowed root.
            var aliasDirectory = Path.Combine(Folder, shortName);
            Assert.AreEqual(PathGuard.GetLongPath(child.FullName), PathGuard.GetLongPath(aliasDirectory), true);
            return (Path.Combine(child.FullName, "workspace.vhdx"), Path.Combine(aliasDirectory, "workspace.vhdx"));
        }

        private (string LongPath, string AliasPath) CustomAliasImagePaths() {
            var child = Directory.CreateDirectory(Path.Combine(Folder, "workspace-directory-custom-short-name-" + Guid.NewGuid().ToString("N")));
            Assert.AreEqual(Folder, Path.GetDirectoryName(Path.GetFullPath(child.FullName)), true,
                "Only this test's newly created directory may have its short name changed.");
            var start = new ProcessStartInfo("fsutil.exe") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "file", "setshortname", child.FullName, "WRKALIAS" }) { start.ArgumentList.Add(argument); }
            using (var process = Process.Start(start)) {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(); Assert.Fail("Setting this test directory's short name timed out."); }
                Assert.AreEqual(0, process.ExitCode, "Could not set this test directory's short name: " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            }
            var aliasDirectory = Path.Combine(Folder, "WRKALIAS");
            Assert.IsTrue(Directory.Exists(aliasDirectory));
            Assert.AreEqual(PathGuard.GetLongPath(child.FullName), PathGuard.GetLongPath(aliasDirectory), true);
            return (Path.Combine(child.FullName, "workspace.vhdx"), Path.Combine(aliasDirectory, "workspace.vhdx"));
        }

        private string JournalPath(string id) => Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, ".vhdstudio-operations")).FullName, id + ".json");
        private static string Record(string id, string image, string state, AutomationResult result = null, string fingerprint = null) => JsonSerializer.Serialize(new {
            requestId = id, operation = "image_create", path = image, fingerprint = fingerprint ?? new string('A', 64),
            state, startedUtc = DateTimeOffset.UtcNow, result,
        }, JsonOptions);
        private static AutomationResult Success(string id, string image) => new() {
            Success = true, RequestId = id, Operation = "image_create", Data = new { path = image },
        };
        private static AutomationResult Execute(AutomationOperations automation, string operation, object arguments)
            => automation.Execute(operation, JsonSerializer.SerializeToElement(arguments));

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string path, StringBuilder shortPath, int bufferLength);
    }
}
