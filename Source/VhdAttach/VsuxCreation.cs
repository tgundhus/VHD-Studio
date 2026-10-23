using System;
using System.IO;
using System.Threading;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttach {
    /// <summary>Creates a blank VHDX, then applies the existing eight-byte VSUX mount lock.</summary>
    internal static class VsuxCreation {
        internal static void Create(string containerPath, long size, bool fixedSize, string password) {
            // Validate the secret before creating even the temporary image or its companion.
            if (password == null || password.Length < 8 || password.Length > 1024) {
                throw new ArgumentException("Use a password between 8 and 1024 characters.", nameof(password));
            }
            var target = Validate(containerPath, size);
            var folder = Path.GetDirectoryName(target);
            // Keep the destination directory pinned while creating and renaming the new image.
            using (PathGuard.Open(folder, directory: true, rejectHardLinks: false)) {
                var temporary = Path.Combine(folder, ".vsux-new-" + Guid.NewGuid().ToString("N") + ".vhdx");
                try {
                    VirtualDiskImage.Create(temporary, size, fixedSize, null, CancellationToken.None);
                    VsuxOperations.Protect(temporary, target, password);
                    var result = VsuxOperations.GetInfo(target);
                    if (!result.IsLocked || result.IsAttached) {
                        throw new IOException("The new image did not report a locked, detached state.");
                    }
                } catch (Exception error) {
                    // Never roll back a rename or delete a destination whose identity is unknown.
                    throw new IOException("VSUX creation did not finish. Keep any remaining files; do not assume the image is locked."
                        + Environment.NewLine + "Temporary VHDX: " + temporary
                        + Environment.NewLine + "Container: " + target
                        + Environment.NewLine + error.Message, error);
                }
            }
        }

        /// <summary>Checks a new destination without creating or changing files.</summary>
        internal static string Validate(string containerPath, long size) {
            if (size < 8L * 1024 * 1024 || size % 4096 != 0) {
                throw new ArgumentOutOfRangeException(nameof(size), "Use a disk size of at least 8 MiB, aligned to 4096 bytes.");
            }
            if (string.IsNullOrWhiteSpace(containerPath) || !Path.IsPathFullyQualified(containerPath)) {
                throw new ArgumentException("Choose a full local path for the new .vsux file.", nameof(containerPath));
            }
            var target = Path.GetFullPath(containerPath);
            if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.AsSpan(2).Contains(':')) {
                throw new ArgumentException("Use a local file, not a network path, device path or alternate data stream.", nameof(containerPath));
            }
            if (!VsuxOperations.IsVsuxPath(target)) { throw new ArgumentException("Choose a .vsux container.", nameof(containerPath)); }
            foreach (var part in target.Substring(Path.GetPathRoot(target).Length).Split(Path.DirectorySeparatorChar)) {
                if (part.EndsWith(' ') || part.EndsWith('.')) { throw new IOException("Trailing spaces or dots in a path are not supported."); }
            }
            var folder = Path.GetDirectoryName(target);
            using (PathGuard.Open(folder, directory: true, rejectHardLinks: false)) { }
            if ((File.GetAttributes(folder) & (FileAttributes.Compressed | FileAttributes.Encrypted)) != 0) {
                throw new IOException("Choose an uncompressed, unencrypted folder for the new VSUX image.");
            }
            if (File.Exists(target) || Directory.Exists(target)) {
                throw new IOException("The destination already exists. Nothing will be overwritten.");
            }
            return target;
        }
    }
}
