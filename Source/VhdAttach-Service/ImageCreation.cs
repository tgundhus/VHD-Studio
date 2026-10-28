using System;
using System.IO;
using VhdAttachCommon;

namespace VhdAttachService {
    /// <summary>Creates only new, standalone VHDX files, with optional preparation of that exact new disk.</summary>
    internal static class ImageCreation {
        public static void Create(string path, long sizeBytes, bool fixedSize, bool createSinglePartition, PipeCaller caller) {
            if (caller == null) { throw new ArgumentNullException(nameof(caller)); }
            AuditLog.Started("Create image", path, "VHDX; bytes=" + sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; fixed=" + fixedSize + "; NTFS=" + createSinglePartition, caller.Name);
            try {
                Validate(path, sizeBytes);
                // Both path validation and native create run as the client. Checking directory
                // permissions first and later creating as SYSTEM would permit path-swap attacks.
                VhdAttach.NewDiskCreation.Create(path, sizeBytes, fixedSize, createSinglePartition, caller.RunAsCaller);
                AuditLog.Succeeded("Create image", path, "detached", caller.Name);
            } catch (Exception error) {
                AuditLog.Failed("Create image", path, error, caller.Name);
                throw new InvalidOperationException("The new VHDX image could not be created. " + error.Message, error);
            }
        }

        internal static void Validate(string path, long sizeBytes) {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\", StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentException("Choose a full local .vhdx path for the new image.", nameof(path));
            }
            // Automation uses one minimum for both blank and prepared workspaces.
            VhdAttach.NewDiskCreation.ValidateSize(sizeBytes, createSinglePartition: true);
        }
    }
}
