using Microsoft.Win32;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace VhdStudio.Vsux;

// Proof of a completed password-authorized Attach, never a password or password verifier.
internal sealed record UnlockSessionReceipt(Guid DiskId, string FileIdentity, byte[] CredentialFingerprint) {
    public static UnlockSessionReceipt Capture(SignatureFile image, PasswordDescriptor descriptor) =>
        new(image.DiskId, image.FileIdentity, SHA256.HashData(descriptor.ToEmbeddedRecord()));

    public bool Matches(SignatureFile image, PasswordDescriptor descriptor) =>
        DiskId == image.DiskId && string.Equals(FileIdentity, image.FileIdentity, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(CredentialFingerprint, SHA256.HashData(descriptor.ToEmbeddedRecord()));
}

internal static class UnlockSessionStore {
    private const string RegistryPath = @"Software\xGND Software\VHD Studio\VSUX\UnlockedSessions";

    public static void Save(UnlockSessionReceipt receipt) {
        try {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
            try {
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(receipt.DiskId.ToString("D"), protectedBytes, RegistryValueKind.Binary);
            } finally { CryptographicOperations.ZeroMemory(bytes); }
        } catch (Exception error) when (OptionalFailure(error)) {
            // Session caching is optional. A successful mount must stay successful.
        }
    }

    public static UnlockSessionReceipt? Read(Guid diskId) {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue(diskId.ToString("D")) is not byte[] protectedBytes || protectedBytes.Length is < 1 or > 4096) { return null; }
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            try {
                if (bytes.Length > 2048) { return null; }
                var receipt = JsonSerializer.Deserialize<UnlockSessionReceipt>(bytes);
                return receipt is { FileIdentity.Length: > 0 and <= 128, CredentialFingerprint.Length: 32 } && receipt.DiskId == diskId ? receipt : null;
            } finally { CryptographicOperations.ZeroMemory(bytes); }
        } catch (Exception error) when (OptionalFailure(error)) { return null; }
    }

    public static void Clear(Guid diskId, string fileIdentity) {
        try {
            var receipt = Read(diskId);
            if (receipt != null && !string.Equals(receipt.FileIdentity, fileIdentity, StringComparison.Ordinal)) { return; }
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(diskId.ToString("D"), throwOnMissingValue: false);
        } catch (Exception error) when (OptionalFailure(error)) {
            // A stale receipt cannot unlock a file and is checked against its current state.
        }
    }

    private static bool OptionalFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or CryptographicException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
}
