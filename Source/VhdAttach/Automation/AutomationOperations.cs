using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using VhdAttachCommon;

namespace VhdAttach.Automation {
    public sealed class AutomationError {
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public sealed class AutomationResult {
        public bool Success { get; set; }
        public string RequestId { get; set; }
        public string Operation { get; set; }
        public object Data { get; set; }
        public AutomationError Error { get; set; }
    }

    public sealed class AutomationVolume {
        public string VolumePath { get; set; }
        public string DriveLetter { get; set; }
    }

    public sealed class AutomationImage {
        public string Path { get; set; }
        public string ImageId { get; set; }
        public string Format { get; set; }
        public string Kind { get; set; }
        public long? VirtualSizeBytes { get; set; }
        public long? PhysicalSizeBytes { get; set; }
        public bool Attached { get; set; }
        public string PhysicalDrive { get; set; }
        public bool NeedsLogReplay { get; set; }
        public AutomationVolume[] Volumes { get; set; } = Array.Empty<AutomationVolume>();
    }

    /// <summary>
    /// Headless VHDX operations shared by CLI and MCP. The root and write grant are cooperative
    /// API guardrails; Windows caller access checks remain in the privileged service.
    /// </summary>
    public sealed class AutomationOperations : IDisposable {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly PathGuard RootGuard;
        private readonly string Root;
        private readonly long MaxSizeBytes;
        private readonly bool AllowWrite;
        private readonly string MutexName;
        private bool Disposed;
        private const long MinimumSize = 64L * 1024 * 1024;

        public AutomationOperations(string root, long maxSizeBytes, bool allowWrite) {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) {
                throw new ArgumentException("--root must name an existing full local directory.", nameof(root));
            }
            ValidateLocalPath(root);
            if (maxSizeBytes < MinimumSize || maxSizeBytes % 4096 != 0) {
                throw new ArgumentOutOfRangeException(nameof(maxSizeBytes), "The size limit must be at least 64 MiB and aligned to 4096 bytes.");
            }
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem) { throw new UnauthorizedAccessException("Run automation as a Windows user, not Local System."); }
            RootGuard = PathGuard.Open(root, directory: true, rejectHardLinks: false);
            Root = Path.TrimEndingDirectorySeparator(RootGuard.FinalPath);
            MaxSizeBytes = maxSizeBytes;
            AllowWrite = allowWrite;
            // ponytail: serialize this user's automation calls; use per-image locks if parallel workloads require them.
            MutexName = @"Global\VhdStudio.Automation." + identity.User.Value;
        }

        public AutomationResult Execute(string operation, JsonElement arguments) {
            string requestId = null;
            try {
                ObjectDisposedException.ThrowIf(Disposed, this);
                ValidateArguments(operation, arguments);
                if (operation == "image_list") { return Ok(operation, null, ListImages()); }
                if (operation == "image_inspect") {
                    var path = ResolvePath(RequiredString(arguments, "path"));
                    using var guard = GuardImage(path);
                    return Ok(operation, null, Inspect(path));
                }
                if (operation == "operation_status") {
                    requestId = RequestId(RequiredString(arguments, "requestId"));
                    return Status(requestId);
                }

                var target = ResolvePath(RequiredString(arguments, "path"));
                long size = operation == "image_create" ? RequiredInt64(arguments, "sizeBytes") : 0;
                if (operation == "image_create" && (size < MinimumSize || size > MaxSizeBytes || size % 4096 != 0)) {
                    throw new ArgumentOutOfRangeException("sizeBytes", "Use 64 MiB or more, within --max-size-bytes, aligned to 4096 bytes.");
                }
                var fixedSize = OptionalBool(arguments, "fixedSize", false);
                var prepare = OptionalBool(arguments, "prepare", true);
                var readOnly = OptionalBool(arguments, "readOnly", true);
                var expectedId = OptionalString(arguments, "expectedImageId");
                if (expectedId != null) { expectedId = RequestId(expectedId); }
                requestId = RequestId(OptionalString(arguments, "requestId") ?? Guid.NewGuid().ToString("D"));
                if (!AllowWrite) { throw new AutomationException("approval_required", "Create, attach and detach require an explicit --allow-write grant for this root."); }

                using var mutex = new Mutex(false, MutexName);
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { throw new AutomationException("operation_busy", "Another automation operation is running for this Windows user."); }
                try {
                    var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                        operation, path = target.ToUpperInvariant(), size, fixedSize, prepare, readOnly, expectedId,
                    }, JsonOptions)));
                    using var journalGuard = OpenJournalDirectory(create: true);
                    var previous = ReadJournal(requestId);
                    if (previous != null) {
                        if (previous.Fingerprint != fingerprint) { throw new AutomationException("request_conflict", "This request ID was already used with different arguments."); }
                        return previous.Result ?? Unknown(operation, requestId);
                    }
                    RefuseUnresolved(target);
                    using var parentGuard = PathGuard.Open(Path.GetDirectoryName(target), directory: true, rejectHardLinks: false);
                    using var imageGuard = operation == "image_create" ? null : GuardImage(target);
                    AutomationImage before = null;
                    Guid? serviceExpectedId = null;
                    if (operation == "image_create") {
                        if (File.Exists(target) || Directory.Exists(target)) { throw new AutomationException("already_exists", "The destination already exists. Nothing will be overwritten."); }
                    } else {
                        before = Inspect(target);
                        if (Guid.TryParse(before.ImageId, out var inspectedId)) { serviceExpectedId = inspectedId; }
                        if (expectedId != null && !string.Equals(expectedId, before.ImageId, StringComparison.OrdinalIgnoreCase)) {
                            throw new AutomationException("image_changed", "The image identifier changed. Inspect it again before acting.");
                        }
                        if (operation == "image_attach") {
                            if (before.Attached) { throw new AutomationException("already_attached", "The image is already attached. Inspect its current volumes before using it."); }
                            if (before.NeedsLogReplay) { throw new AutomationException("log_replay_required", "Repair the pending VHDX log interactively before automation attaches the image."); }
                            if (before.Kind == "Differencing") { throw new AutomationException("unsupported_format", "Automation currently attaches standalone VHDX images only."); }
                            if (before.VirtualSizeBytes == null || before.VirtualSizeBytes > MaxSizeBytes) {
                                throw new AutomationException("size_limit", "The image capacity exceeds --max-size-bytes or could not be verified.");
                            }
                        }
                        if ((operation == "image_attach" || before.Attached) && serviceExpectedId == null) {
                            throw new AutomationException("image_identity_unknown", "The image identifier could not be verified. Inspect it in the desktop UI.");
                        }
                    }
                    // An older service must not silently ignore the exact-handle identity/log guards.
                    var capabilities = PipeClient.AutomationCapabilities();
                    if (capabilities.IsError && capabilities.ErrorCode == "ServiceUnavailable") {
                        throw new AutomationException("service_unavailable", capabilities.Message);
                    }
                    if (capabilities.IsError || capabilities.AutomationProtocol != "vhdx-workspaces-v1") {
                        throw new AutomationException("service_upgrade_required", "Install the VHD Studio service from the same build before using automation mutations. No disk operation was submitted.");
                    }
                    var record = new OperationRecord {
                        RequestId = requestId, Operation = operation, Path = target, Fingerprint = fingerprint,
                        StartedUtc = DateTimeOffset.UtcNow, State = "running",
                    };
                    WriteJournal(record); // Persist intent before a request can reach the service.
                    AutomationResult result;
                    try {
                        PipeResponse response = null;
                        if (operation == "image_create") { response = PipeClient.CreateImage(target, size, fixedSize, prepare); }
                        else if (operation == "image_attach") {
                            response = PipeClient.Attach(target, readOnly, initializeDisk: false, avoidLetters: DriveLetters.GetSessionLetters(), refuseLogReplay: true, expectedImageId: serviceExpectedId);
                        } else if (before.Attached) { response = PipeClient.Detach(target, force: false, expectedImageId: serviceExpectedId); }
                        if (response?.IsError == true) {
                            var code = ServiceError(response.ErrorCode);
                            result = Fail(operation, requestId, code, response.Message);
                        } else {
                            var after = Inspect(target);
                            if ((operation == "image_attach" && !after.Attached) || (operation != "image_attach" && after.Attached)) {
                                result = Unknown(operation, requestId);
                            } else { result = Ok(operation, requestId, after); }
                        }
                    } catch (Exception error) {
                        // Once sent, even a broken pipe or failed refresh may follow a successful disk operation.
                        result = Fail(operation, requestId, "outcome_unknown", error.Message + " Inspect the image and request status before taking further action.");
                    }
                    record.State = result.Success ? "succeeded" : result.Error.Code == "outcome_unknown" ? "unknown" : "failed";
                    record.Result = result;
                    try { WriteJournal(record); }
                    catch (Exception error) { return Fail(operation, requestId, "outcome_unknown", "The final operation record could not be saved: " + error.Message + " Inspect the image before taking further action."); }
                    return result;
                } finally { mutex.ReleaseMutex(); }
            } catch (Exception error) {
                return Fail(operation, requestId, ErrorCode(error), error.Message);
            }
        }

        private object ListImages() {
            var images = new List<AutomationImage>();
            var errors = new List<object>();
            var paths = Directory.EnumerateFiles(Root, "*.vhdx", SearchOption.TopDirectoryOnly).Take(101).ToArray();
            foreach (var path in paths.Take(100)) {
                try { using var guard = GuardImage(path); images.Add(Inspect(path)); }
                catch (Exception error) { errors.Add(new { path, code = ErrorCode(error), message = error.Message }); }
            }
            return new { root = Root, images, errors, truncated = paths.Length > 100 };
        }

        private static AutomationImage Inspect(string path) {
            ValidateSignature(path);
            // A pending log is reported without opening the provider, which could require log replay.
            if (VhdxHeader.NeedsLogReplay(path)) {
                // Confirm state independently instead of treating a refused provider open as detachment.
                return new AutomationImage { Path = path, Format = "VHDX", NeedsLogReplay = true, Attached = VirtualDiskImage.IsAttached(path) };
            }
            var details = VirtualDiskImage.GetDetails(path);
            EnsureKnownAttachmentState(details);
            if (details.Format != "VHDX") { throw new AutomationException("unsupported_format", "Only VHDX images are supported by automation."); }
            var image = new AutomationImage {
                Path = path, ImageId = details.VirtualDiskId?.ToString("D"), Format = details.Format,
                Kind = details.Kind.ToString(), VirtualSizeBytes = details.VirtualSize, PhysicalSizeBytes = details.PhysicalSize,
                Attached = details.AttachedPath != null, PhysicalDrive = details.AttachedPath, NeedsLogReplay = details.NeedsLogReplay,
            };
            if (image.Attached) {
                image.Volumes = Volume.GetVolumesOnPhysicalDrive(image.PhysicalDrive)
                    .Select(v => new AutomationVolume { VolumePath = v.VolumeName, DriveLetter = v.DriveLetter3 }).ToArray();
            }
            return image;
        }

        internal static void EnsureKnownAttachmentState(VirtualDiskDetails details) {
            if (details.AttachedPathError != 0 && details.AttachedPathError != 55 /* ERROR_DEV_NOT_EXIST */) {
                throw new Win32Exception(details.AttachedPathError, "Windows could not verify whether the image is attached. No detached state is assumed.");
            }
            if (details.AttachedPathError == 0 && string.IsNullOrEmpty(details.AttachedPath)) {
                throw new IOException("Windows returned an empty virtual-disk device path. Attachment state could not be verified.");
            }
        }

        private AutomationResult Status(string requestId) {
            using var guard = OpenJournalDirectory(create: false);
            var record = ReadJournal(requestId) ?? throw new AutomationException("not_found", "No operation with this request ID is recorded in this root.");
            var path = ResolvePath(record.Path);
            AutomationResult current;
            try { using var imageGuard = GuardImage(path); current = Ok("image_inspect", null, Inspect(path)); }
            catch (Exception error) { current = Fail("image_inspect", null, ErrorCode(error), error.Message); }
            return Ok("operation_status", requestId, new {
                state = record.State == "running" ? "unknown" : record.State,
                record.Operation, record.Path, record.StartedUtc,
                result = record.Result ?? Unknown(record.Operation, requestId), current,
            });
        }

        private string ResolvePath(string path) {
            if (string.IsNullOrWhiteSpace(path) || (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))) {
                throw new ArgumentException("path must be relative to --root or a full local filename.");
            }
            var full = CanonicalImagePath(Path.GetFullPath(path, Root));
            var prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                throw new AutomationException("outside_root", "The image must be inside the configured --root directory.");
            }
            if (!Path.GetExtension(full).Equals(".vhdx", StringComparison.OrdinalIgnoreCase)) {
                throw new AutomationException("unsupported_format", "Automation supports .vhdx files. VSUX retains its interactive password flow.");
            }
            return full;
        }

        private static string CanonicalImagePath(string path) {
            var full = Path.GetFullPath(path);
            ValidateLocalPath(full);
            var parent = Path.GetDirectoryName(full);
            if (parent == null) { return full; }
            // Expand the parent independently: the new image itself may not exist yet.
            // GetFullPath only expands short names containing '~'; custom short names need this too.
            return PathGuard.GetLongPath(Path.Combine(PathGuard.GetLongPath(parent), Path.GetFileName(full)));
        }

        private static void ValidateLocalPath(string path) {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 3 || full.AsSpan(2).Contains(':')) {
                throw new ArgumentException("Use a local drive path without device namespaces or alternate data streams.");
            }
            foreach (var part in path.Replace('/', '\\').Split('\\')) {
                if (part.EndsWith(' ') || part.EndsWith('.')) { throw new ArgumentException("Trailing dots or spaces in paths are not supported."); }
            }
        }

        private static PathGuard GuardImage(string path) => PathGuard.Open(path, directory: false, rejectHardLinks: true);

        private static void ValidateSignature(string path) {
            Span<byte> signature = stackalloc byte[8];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual("vhdxfile"u8)) {
                throw new AutomationException("unsupported_format", "The image is not an ordinary unlocked VHDX. VSUX password operations remain interactive.");
            }
        }

        private string JournalDirectory => Path.Combine(Root, ".vhdstudio-operations");
        private PathGuard OpenJournalDirectory(bool create) {
            PathGuard.EnsureNoReparsePoints(JournalDirectory, includeLast: true);
            if (create) { Directory.CreateDirectory(JournalDirectory); }
            if (!Directory.Exists(JournalDirectory)) { throw new AutomationException("not_found", "This root has no operation records."); }
            return PathGuard.Open(JournalDirectory, directory: true, rejectHardLinks: false);
        }

        private OperationRecord ReadJournal(string requestId) {
            var path = Path.Combine(JournalDirectory, requestId + ".json");
            if (!File.Exists(path)) { return null; }
            using var guard = PathGuard.Open(path, directory: false, rejectHardLinks: true);
            using var stream = OpenJournalReader(path);
            if (stream.Length > 128 * 1024) { throw new IOException("The operation record is too large."); }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var record = JsonSerializer.Deserialize<OperationRecord>(reader.ReadToEnd(), JsonOptions);
            if (record == null || record.RequestId != requestId || record.Path == null || !Path.IsPathFullyQualified(record.Path) || record.Fingerprint == null
                || record.Operation is not ("image_create" or "image_attach" or "image_detach")
                || record.State is not ("running" or "succeeded" or "failed" or "unknown")
                || (record.State == "succeeded" && record.Result?.Success != true)
                || (record.State is "failed" or "unknown" && (record.Result == null || record.Result.Success || record.Result.Error == null))) {
                throw new IOException("The operation record is invalid. Inspect the image before taking further action.");
            }
            return record;
        }

        // Readers keep a complete old snapshot while a writer atomically replaces the directory entry.
        internal static FileStream OpenJournalReader(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        private void WriteJournal(OperationRecord record) {
            var path = Path.Combine(JournalDirectory, record.RequestId + ".json");
            var temporary = Path.Combine(JournalDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    JsonSerializer.Serialize(stream, record, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                CommitJournal(temporary, path);
            } finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }

        internal static void CommitJournal(string temporary, string destination) {
            // MoveFileEx cannot replace an open destination even when readers share deletion.
            if (File.Exists(destination)) { File.Replace(temporary, destination, null); }
            else { File.Move(temporary, destination); }
        }

        private void RefuseUnresolved(string path) {
            foreach (var file in Directory.EnumerateFiles(JournalDirectory, "*.json")) {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) { continue; }
                var record = ReadJournal(id.ToString("D"));
                if ((record.State == "running" || record.State == "unknown") && string.Equals(CanonicalImagePath(record.Path), path, StringComparison.OrdinalIgnoreCase)) {
                    throw new AutomationException("outcome_unknown", "A previous request for this image has an unresolved outcome. Inspect operation_status and the image; use the desktop UI for recovery.");
                }
            }
        }

        private static void ValidateArguments(string operation, JsonElement arguments) {
            string[] allowed = operation switch {
                "image_list" => Array.Empty<string>(),
                "image_inspect" => new[] { "path" },
                "image_create" => new[] { "path", "sizeBytes", "fixedSize", "prepare", "requestId" },
                "image_attach" => new[] { "path", "readOnly", "requestId", "expectedImageId" },
                "image_detach" => new[] { "path", "requestId", "expectedImageId" },
                "operation_status" => new[] { "requestId" },
                _ => throw new ArgumentException("Unknown operation. Use image_list, image_inspect, image_create, image_attach, image_detach or operation_status."),
            };
            if (arguments.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Arguments must be a JSON object."); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in arguments.EnumerateObject()) {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) {
                    throw new ArgumentException("Unknown or duplicate argument: " + property.Name);
                }
            }
        }

        private static string RequiredString(JsonElement arguments, string name) => OptionalString(arguments, name) ?? throw new ArgumentException(name + " is required.");
        private static string OptionalString(JsonElement arguments, string name) {
            if (!arguments.TryGetProperty(name, out var value)) { return null; }
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString().Length > 4096) {
                throw new ArgumentException(name + " must be a nonempty string of at most 4096 characters.");
            }
            return value.GetString();
        }
        private static long RequiredInt64(JsonElement arguments, string name) {
            if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) {
                throw new ArgumentException(name + " must be an integer number of bytes.");
            }
            return number;
        }
        private static bool OptionalBool(JsonElement arguments, string name, bool fallback) {
            if (!arguments.TryGetProperty(name, out var value)) { return fallback; }
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) { throw new ArgumentException(name + " must be a boolean."); }
            return value.GetBoolean();
        }
        private static string RequestId(string value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : throw new ArgumentException("requestId and expectedImageId must be GUIDs in D format.");
        private static string ServiceError(string code) => code switch {
            "InUse" => "volume_busy", "AccessDenied" => "access_denied", "NotFound" => "not_found",
            "InvalidArgument" => "invalid_argument", "AlreadyExists" => "already_exists",
            "LogReplayRequired" => "log_replay_required",
            "ImageChanged" => "image_changed",
            "Timeout" => "outcome_unknown", "ServiceUnavailable" => "service_unavailable", _ => "operation_failed",
        };
        private static string ErrorCode(Exception error) => error switch {
            AutomationException known => known.Code,
            ArgumentException or JsonException => "invalid_argument",
            UnauthorizedAccessException => "access_denied",
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            Win32Exception native when native.NativeErrorCode == 5 || native.NativeErrorCode == 1314 => "access_denied",
            Win32Exception native when native.NativeErrorCode == 2 || native.NativeErrorCode == 3 => "not_found",
            Win32Exception native when native.NativeErrorCode == 32 || native.NativeErrorCode == 33 => "volume_busy",
            _ => "operation_failed",
        };
        private static AutomationResult Ok(string operation, string id, object data) => new() { Success = true, RequestId = id, Operation = operation, Data = data };
        private static AutomationResult Fail(string operation, string id, string code, string message) => new() {
            Operation = operation, RequestId = id, Error = new AutomationError { Code = code, Message = message },
        };
        private static AutomationResult Unknown(string operation, string id) => Fail(operation, id, "outcome_unknown", "The operation may have completed. Inspect operation_status and the image before taking further action; this request will never be automatically repeated.");

        public void Dispose() { if (!Disposed) { RootGuard.Dispose(); Disposed = true; } }

        private sealed class AutomationException : Exception {
            public AutomationException(string code, string message) : base(message) { Code = code; }
            public string Code { get; }
        }
        private sealed class OperationRecord {
            public string RequestId { get; set; }
            public string Operation { get; set; }
            public string Path { get; set; }
            public string Fingerprint { get; set; }
            public DateTimeOffset StartedUtc { get; set; }
            public string State { get; set; }
            public AutomationResult Result { get; set; }
        }
    }
}
