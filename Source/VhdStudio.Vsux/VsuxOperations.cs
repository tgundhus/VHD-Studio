using System.Security.Cryptography;
using System.Text.Json;

namespace VhdStudio.Vsux;

public static class VsuxOperations {
    public static bool IsVsuxPath(string path) => string.Equals(Path.GetExtension(path), ".vsux", StringComparison.OrdinalIgnoreCase);

    public static VsuxInfo GetInfo(string containerPath) {
        var path = ContainerPath(containerPath);
        using var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false);
        if (image.EmbeddedPasswordRecord is { } record) { PasswordDescriptor.FromEmbeddedRecord(record).Match(image); }
        var attachedPath = image.IsLocked ? null : NativeDisk.GetAttachedPath(path);
        return new VsuxInfo(image.IsLocked, attachedPath != null, attachedPath, image.DiskId, image.Length) {
            HasEmbeddedPassword = image.EmbeddedPasswordRecord != null
        };
    }

    public static void Protect(string imagePath, string containerPath, string password) {
        PasswordDescriptor.CheckNewPassword(password);
        var source = SignatureFile.ValidatePath(imagePath);
        var target = ContainerPath(containerPath);
        if (!source.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a standalone .vhdx image."); }
        SameDirectory(source, target);
        RequireNew(target);
        using var folder = SignatureFile.PinDirectory(source);
        string identity;
        Guid diskId;
        byte[] record;
        using (var image = SignatureFile.Open(source)) {
            if (image.IsLocked) { throw new InvalidDataException("The source image is already masked."); }
            if (image.EmbeddedPasswordRecord != null) { throw new InvalidDataException("The image already contains VSUX credentials. Use its VSUX operations or standalone recovery."); }
            identity = image.FileIdentity;
            diskId = image.DiskId;
            record = PasswordDescriptor.CreateEmbedded(image, password).ToEmbeddedRecord();
        }
        NativeDisk.EnsureDetached(source);
        try {
            WriteEmbedded(source, identity, diskId, record);
            using var image = SignatureFile.Open(source, allowRename: true);
            MatchIdentity(image, identity, diskId);
            RequireEmbedded(image, record).Verify(password);
            image.MoveTo(target); // Rename this verified handle, never a subsequently substituted path.
            image.SetLocked(true);
        } catch (Exception ex) {
            throw Incomplete("Protection", source, target, ex);
        }
    }

    public static void Unlock(string containerPath, string password) => UnlockAuthenticated(ContainerPath(containerPath), password);

    private static UnlockSessionReceipt UnlockAuthenticated(string path, string password) {
        using var folder = SignatureFile.PinDirectory(path);
        using var image = SignatureFile.Open(path);
        var descriptor = PasswordDescriptor.ForImage(image, path);
        descriptor.Verify(password); // Raw bounded read and password verification happen before any write.
        var receipt = UnlockSessionReceipt.Capture(image, descriptor);
        if (image.IsLocked) { image.SetLocked(false); }
        return receipt;
    }

    public static void Attach(string containerPath, string password, bool readOnly = false) {
        NativeDisk.RequireElevation();
        var path = ContainerPath(containerPath);
        using var folder = SignatureFile.PinDirectory(path);
        NativeDisk.EnsurePhysicalBackingVolume(path);
        var receipt = UnlockAuthenticated(path, password);
        try {
            // Hold the authenticated file in place while Windows opens it for mounting.
            using var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false);
            if (image.IsLocked || !receipt.Matches(image, PasswordDescriptor.ForImage(image, path))) {
                throw new IOException("The authenticated image changed before mounting.");
            }
            NativeDisk.Mount(path, readOnly);
            UnlockSessionStore.Save(receipt);
        }
        catch (Exception ex) {
            throw new IOException("Attach failed after the signature was restored. The image may be unlocked or attached; use Status, then Detach & lock. " + ex.Message, ex);
        }
    }

    public static bool HasUnlockSession(string containerPath) {
        try {
            var path = ContainerPath(containerPath);
            using var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false);
            var receipt = UnlockSessionStore.Read(image.DiskId);
            if (image.IsLocked) { UnlockSessionStore.Clear(image.DiskId, image.FileIdentity); return false; }
            return receipt != null && receipt.Matches(image, PasswordDescriptor.ForImage(image, path));
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or CryptographicException or JsonException or System.ComponentModel.Win32Exception or System.Security.SecurityException) {
            // An unavailable or unverifiable receipt falls back to the password prompt.
        }
        return false;
    }

    public static void Lock(string containerPath, string password) {
        ArgumentNullException.ThrowIfNull(password);
        LockAuthenticated(ContainerPath(containerPath), password);
    }

    public static void LockUsingSession(string containerPath) => LockAuthenticated(ContainerPath(containerPath), password: null);

    private static void LockAuthenticated(string path, string? password) {
        using var folder = SignatureFile.PinDirectory(path);
        UnlockSessionReceipt receipt;
        using (var image = SignatureFile.Open(path, allowPendingLog: true, exclusive: false)) {
            var descriptor = PasswordDescriptor.ForImage(image, path);
            if (password != null) {
                descriptor.Verify(password);
                receipt = UnlockSessionReceipt.Capture(image, descriptor);
            } else {
                receipt = UnlockSessionStore.Read(image.DiskId) ?? throw new UnauthorizedAccessException("Enter the drive password to detach and lock this image.");
                if (!receipt.Matches(image, descriptor)) { throw new UnauthorizedAccessException("The unlock session does not match this image. Enter the drive password."); }
            }
            if (!image.IsLocked) { NativeDisk.Detach(path); }
        }
        using var detached = SignatureFile.Open(path);
        var currentDescriptor = PasswordDescriptor.ForImage(detached, path);
        if (!receipt.Matches(detached, currentDescriptor)) { throw new IOException("The file identity or password information changed. The signature was not modified."); }
        // Recheck authentication after safe detach and under the exclusive write handle.
        if (password != null) { currentDescriptor.Verify(password); }
        if (!detached.IsLocked) { detached.SetLocked(true); }
        UnlockSessionStore.Clear(detached.DiskId, receipt.FileIdentity);
    }

    public static void MakeSelfContained(string containerPath, string password) {
        var path = ContainerPath(containerPath);
        using var folder = SignatureFile.PinDirectory(path);
        string identity;
        Guid diskId;
        bool wasLocked;
        byte[] record;
        using (var image = SignatureFile.Open(path)) {
            PasswordDescriptor.ForImage(image, path).Verify(password);
            if (image.EmbeddedPasswordRecord != null) { return; }
            identity = image.FileIdentity;
            diskId = image.DiskId;
            wasLocked = image.IsLocked;
            record = PasswordDescriptor.CreateEmbedded(image, password).ToEmbeddedRecord();
        }
        if (!wasLocked) { NativeDisk.EnsureDetached(path); }
        try {
            using (var image = SignatureFile.Open(path)) {
                MatchIdentity(image, identity, diskId);
                PasswordDescriptor.ForImage(image, path).Verify(password);
                if (wasLocked) { image.SetLocked(false); }
            }
            WriteEmbedded(path, identity, diskId, record);
            using var result = SignatureFile.Open(path);
            MatchIdentity(result, identity, diskId);
            RequireEmbedded(result, record).Verify(password);
            if (wasLocked) { result.SetLocked(true); }
            // The original recovery file is retained; it is no longer consulted.
        } catch (Exception ex) { throw Incomplete("Making VSUX self-contained", path, path, ex); }
    }

    public static void ConvertToVhdx(string containerPath, string destination, string password) {
        var path = ContainerPath(containerPath);
        var target = SignatureFile.ValidatePath(destination);
        if (!target.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a new .vhdx filename."); }
        SameDirectory(path, target);
        RequireNew(target); // Destination collisions never reach authentication or an image write.
        using var folder = SignatureFile.PinDirectory(path);
        string identity;
        Guid diskId;
        bool embedded;
        using (var image = SignatureFile.Open(path, allowRename: true)) {
            PasswordDescriptor.ForImage(image, path).Verify(password);
            identity = image.FileIdentity;
            diskId = image.DiskId;
            embedded = image.EmbeddedPasswordRecord != null;
            // The exclusive file open rejects an attached image. Renaming first
            // means even a destination created after preflight cannot cause a write.
            image.MoveTo(target);
            try { if (image.IsLocked) { image.SetLocked(false); } }
            catch (Exception ex) { throw Incomplete("Conversion", path, target, ex); }
        }
        try {
            NativeDisk.EnsureDetached(target);
            if (embedded) {
                using (var guard = SignatureFile.Open(target, exclusive: false)) {
                    MatchIdentity(guard, identity, diskId);
                    PasswordDescriptor.ForImage(guard, path).Verify(password);
                    NativeDisk.DeletePasswordMetadata(target);
                }
            }
            using var result = SignatureFile.Open(target);
            MatchIdentity(result, identity, diskId);
            if (result.IsLocked || result.EmbeddedPasswordRecord != null) { throw new IOException("The converted image did not pass verification."); }
            UnlockSessionStore.Clear(diskId, identity);
            // Never delete or overwrite an original recovery file or a user's backup.
        } catch (Exception ex) { throw Incomplete("Conversion", path, target, ex); }
    }

    public static void ExportRecoveryBackup(string containerPath, string destination) {
        var path = ContainerPath(containerPath);
        var target = SignatureFile.ValidatePath(destination);
        if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a new recovery filename with the .json extension."); }
        RequireNew(target);
        using var folder = SignatureFile.PinDirectory(target);
        using var image = SignatureFile.Open(path); // A stable diagnostic snapshot requires detachment and exclusivity.
        var snapshot = image.Snapshot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            format = "VSUX-recovery-backup-2", diskId = image.DiskId, imageLength = image.Length,
            createdUtc = DateTimeOffset.UtcNow, isLocked = image.IsLocked,
            headerSnapshot = snapshot, snapshotSha256 = SHA256.HashData(snapshot),
            note = "Optional diagnostics only. Never restore this snapshot over a current image. Standalone recovery does not require this file."
        }, new JsonSerializerOptions { WriteIndented = true });
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        output.Position = 0;
        var readBack = new byte[bytes.Length];
        output.ReadExactly(readBack);
        if (output.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(readBack))) { throw new IOException("Recovery file read-back verification failed. The image was not changed."); }
    }

    public static string Status(string containerPath) {
        var info = GetInfo(containerPath);
        var credentials = info.HasEmbeddedPassword ? " Password information is stored inside this file." : " Earlier format: keep the original recovery file for password use until you make the image self-contained.";
        if (info.IsLocked) { return "Locked VSUX signature. Data is not encrypted." + credentials; }
        return (info.IsAttached ? $"Attached at {info.AttachedPath}. Use Detach & lock to lock it." : "Unlocked and detached. The standard VHDX signature is present.") + credentials;
    }

    private static void WriteEmbedded(string path, string identity, Guid diskId, byte[] record) {
        using (var guard = SignatureFile.Open(path, exclusive: false)) {
            MatchIdentity(guard, identity, diskId);
            if (guard.IsLocked) { throw new IOException("Windows metadata requires the ordinary VHDX signature."); }
            if (guard.EmbeddedPasswordRecord != null) { throw new IOException("The image acquired an embedded VSUX record during the operation. It was not overwritten."); }
            NativeDisk.SetPasswordMetadata(path, record);
        }
        using var verified = SignatureFile.Open(path);
        MatchIdentity(verified, identity, diskId);
        RequireEmbedded(verified, record);
    }

    private static PasswordDescriptor RequireEmbedded(SignatureFile image, byte[] expected) {
        if (image.EmbeddedPasswordRecord is not { } bytes || !bytes.AsSpan().SequenceEqual(expected)) { throw new IOException("The embedded VSUX record did not pass raw read-back verification."); }
        var descriptor = PasswordDescriptor.FromEmbeddedRecord(bytes);
        descriptor.Match(image);
        return descriptor;
    }

    private static void MatchIdentity(SignatureFile image, string identity, Guid diskId) {
        if (image.FileIdentity != identity || image.DiskId != diskId) { throw new IOException("The image identity changed. The operation was stopped."); }
    }

    private static void SameDirectory(string source, string target) {
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a destination in the same folder. This operation renames the image without copying its data."); }
    }

    private static void RequireNew(string target) {
        if (File.Exists(target) || Directory.Exists(target)) { throw new IOException("The destination already exists. Nothing will be overwritten."); }
    }

    private static IOException Incomplete(string operation, string source, string target, Exception error) => new(
        $"{operation} did not finish. Keep any files at '{source}' and '{target}' and any original recovery file. Do not assume the image is locked; check its signature/status or use standalone recovery. {error.Message}", error);

    private static string ContainerPath(string path) {
        var full = SignatureFile.ValidatePath(path);
        if (!full.EndsWith(".vsux", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Choose a .vsux container."); }
        return full;
    }
}

public sealed record VsuxInfo(bool IsLocked, bool IsAttached, string? AttachedPath, Guid DiskId, long Length) {
    public bool HasEmbeddedPassword { get; init; }
    public bool RequiresCompanion => !HasEmbeddedPassword;
}
