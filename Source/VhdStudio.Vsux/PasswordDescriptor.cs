using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VhdStudio.Vsux;

internal sealed class PasswordDescriptor {
    private const int WorkFactor = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true
    };

    public required string Format { get; init; }
    public required Guid DiskId { get; init; }
    public required int Iterations { get; init; }
    public required byte[] Salt { get; init; }
    public required byte[] PasswordHash { get; init; }
    public required byte[] HeaderSnapshot { get; init; }
    public required byte[] SnapshotSha256 { get; init; }

    public static PasswordDescriptor Create(SignatureFile image, string password) {
        CheckNewPassword(password);
        var snapshot = image.Snapshot();
        var salt = RandomNumberGenerator.GetBytes(32);
        return new PasswordDescriptor {
            Format = "VSUX-prototype-1", DiskId = image.DiskId, Iterations = WorkFactor,
            Salt = salt, PasswordHash = Derive(password, salt, WorkFactor),
            HeaderSnapshot = snapshot, SnapshotSha256 = SHA256.HashData(snapshot)
        };
    }

    public static void CheckNewPassword(string password) {
        if (password.Length < 8 || password.Length > 1024) { throw new ArgumentException("Use a password between 8 and 1024 characters."); }
    }

    public void Verify(string password) {
        if (password.Length > 1024) { throw new UnauthorizedAccessException("Incorrect password."); }
        var candidate = Derive(password, Salt, Iterations);
        try {
            if (!CryptographicOperations.FixedTimeEquals(candidate, PasswordHash)) { throw new UnauthorizedAccessException("Incorrect password. The image was not changed."); }
        } finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    public void Match(SignatureFile image) {
        if (image.DiskId != DiskId) { throw new InvalidDataException("The password companion file belongs to a different virtual disk."); }
    }

    public void SaveNew(string path) {
        SignatureFile.ValidatePath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
        file.Position = 0;
        var reread = new byte[bytes.Length];
        file.ReadExactly(reread);
        if (file.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(reread))) {
            throw new IOException("The companion file did not pass read-back verification. The image has not been masked.");
        }
    }

    public static PasswordDescriptor Load(string path) {
        SignatureFile.ValidatePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 3 * 1024 * 1024) { throw new InvalidDataException("Invalid VSUX companion file size."); }
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var result = JsonSerializer.Deserialize<PasswordDescriptor>(bytes, JsonOptions) ?? throw new InvalidDataException("Missing VSUX descriptor.");
        if (result.Format != "VSUX-prototype-1" || result.DiskId == Guid.Empty || result.Iterations != WorkFactor ||
            result.Salt?.Length != 32 || result.PasswordHash?.Length != 32 || result.HeaderSnapshot?.Length != SignatureFile.SnapshotSize || result.SnapshotSha256?.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(result.HeaderSnapshot), result.SnapshotSha256)) {
            throw new InvalidDataException("The VSUX companion file or recovery snapshot is invalid. Standalone recovery does not need this file.");
        }
        return result;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) {
        var encoded = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(encoded, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}
