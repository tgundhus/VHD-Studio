namespace VhdStudio.Vsux;

public static class VsuxOperations {
    public static bool IsVsuxPath(string path) => string.Equals(Path.GetExtension(path), ".vsux", StringComparison.OrdinalIgnoreCase);

    public static VsuxInfo GetInfo(string containerPath) {
        var path = ContainerPath(containerPath);
        using var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false);
        // A masked signature cannot be opened by the Windows VHDX provider.
        // For an ordinary signature, native errors must never imply detachment.
        var attachedPath = image.IsLocked ? null : NativeDisk.GetAttachedPath(path);
        return new VsuxInfo(image.IsLocked, attachedPath != null, attachedPath, image.DiskId, image.Length);
    }

    public static void Protect(string imagePath, string containerPath, string password) {
        PasswordDescriptor.CheckNewPassword(password);
        var source = SignatureFile.ValidatePath(imagePath);
        var target = ContainerPath(containerPath);
        if (!source.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a standalone .vhdx image."); }
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Save the .vsux file in the same directory as the VHDX. Protection renames it without copying its data.");
        }
        if (File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".json") || Directory.Exists(target + ".json")) {
            throw new IOException("The destination or companion file already exists. Nothing will be overwritten.");
        }
        string fileIdentity;
        Guid diskId;
        using (var image = SignatureFile.Open(source)) { // Attached or otherwise open images fail this exclusive open.
            if (image.IsLocked) { throw new InvalidDataException("The source image is already masked."); }
            fileIdentity = image.FileIdentity;
            diskId = image.DiskId;
            var descriptor = PasswordDescriptor.Create(image, password);
            descriptor.SaveNew(target + ".json"); // Commit and re-read recovery information before touching the image.
            PasswordDescriptor.Load(target + ".json").Verify(password);
        }
        File.Move(source, target, overwrite: false);
        try {
            using var image = SignatureFile.Open(target);
            if (image.FileIdentity != fileIdentity || image.DiskId != diskId) { throw new IOException("The image identity changed during rename. Its signature was not changed."); }
            image.SetLocked(true);
        } catch (Exception ex) {
            throw new IOException($"Protection did not finish. Keep '{target}' and its .json file; check status or use standalone recovery. Do not assume the file is locked. {ex.Message}", ex);
        }
    }

    public static void Unlock(string containerPath, string password) {
        var path = ContainerPath(containerPath);
        var descriptor = PasswordDescriptor.Load(path + ".json");
        descriptor.Verify(password); // Wrong passwords cannot reach any image write.
        using var image = SignatureFile.Open(path);
        descriptor.Match(image);
        if (image.IsLocked) { image.SetLocked(false); }
    }

    public static void Attach(string containerPath, string password, bool readOnly = false) {
        NativeDisk.RequireElevation(); // Fail before unmasking if native attach is not permitted.
        var path = ContainerPath(containerPath);
        NativeDisk.EnsurePhysicalBackingVolume(path);
        Unlock(path, password);
        try { NativeDisk.Mount(path, readOnly); }
        catch (Exception ex) {
            // Do not mask automatically: Windows may have completed attachment before reporting an error.
            throw new IOException("Attach failed after the signature was restored. The image may be unlocked or attached; use Status, then Detach & lock. " + ex.Message, ex);
        }
    }

    public static void Lock(string containerPath, string password) {
        var path = ContainerPath(containerPath);
        var descriptor = PasswordDescriptor.Load(path + ".json");
        descriptor.Verify(password);
        string identity;
        using (var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false)) {
            descriptor.Match(image);
            identity = image.FileIdentity;
            if (image.IsLocked) {
                // Re-check exclusivity and clean metadata before reporting an already locked file.
            } else {
                NativeDisk.Detach(path); // Checked flush/volume locks; no force option exists.
            }
        }
        using var detached = SignatureFile.Open(path);
        descriptor.Match(detached);
        if (detached.FileIdentity != identity) { throw new IOException("The file identity changed. The signature was not modified."); }
        if (!detached.IsLocked) { detached.SetLocked(true); }
    }

    public static string Status(string containerPath) {
        var path = ContainerPath(containerPath);
        using var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false);
        if (image.IsLocked) { return "Locked VSUX signature. Data is not encrypted."; }
        try {
            var attached = NativeDisk.GetAttachedPath(path);
            return attached == null ? "Unlocked and detached. The standard VHDX signature is present." : $"Attached at {attached}. Use Detach & lock to lock it.";
        } catch (Exception ex) { return "Unlocked signature; Windows attachment state is unknown: " + ex.Message; }
    }

    private static string ContainerPath(string path) {
        var full = SignatureFile.ValidatePath(path);
        if (!full.EndsWith(".vsux", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a .vsux container."); }
        return full;
    }
}

public sealed record VsuxInfo(bool IsLocked, bool IsAttached, string? AttachedPath, Guid DiskId, long Length);
