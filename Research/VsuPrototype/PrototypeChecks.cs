using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VhdStudio.Vsux;
using PrototypeOperations = VhdStudio.Vsux.VsuxOperations;

namespace VsuPrototype;

/// <summary>Dependency-free checks. Structural fixtures never count as native VHDX tests.</summary>
internal static class PrototypeChecks {
    private const string Password = "prototype disposable check password";
    private const int HeaderSize = 4096;
    private const int MetadataOffset = 1048576;
    private static readonly Guid DiskId = new("15400BEB-5D76-4D9C-96CE-605FE916AB11");
    private static readonly Guid MetadataRegionId = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid VirtualDiskId = new("BECA12AB-B2E6-4523-93EF-C309E000C746");
    private static readonly Guid FileParametersId = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");

    public static int Run(bool native = false) {
        var directory = Path.Combine(Path.GetTempPath(), "VsuPrototypeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        Console.WriteLine($"Disposable check directory: {directory}");
        Console.WriteLine("Structural fixtures verify guards and byte preservation; they are not mountable native test images.");

        void Check(string name, Action<string> check) {
            var path = Path.Combine(directory, name + ".vhdx");
            File.WriteAllBytes(path, CreateStructuralFixture());
            check(path);
            passed++;
            Console.WriteLine($"PASS {name}");
        }

        try {
            Assert(Crc32C("123456789"u8) == 0xE3069283, "Fixture CRC32C known vector failed.");
            Assert(PrototypeOperations.IsVsuxPath("portable.VSUX") && !PrototypeOperations.IsVsuxPath("ordinary.vhdx"), "VSUX extension recognition failed.");
            Check("eight-byte-round-trip", path => {
                var original = File.ReadAllBytes(path);
                using (var disk = SignatureFile.Open(path)) {
                    Assert(!disk.IsLocked && disk.DiskId == DiskId && disk.Length == original.Length, "Validated fixture identity mismatch.");
                    disk.SetLocked(true);
                }
                AssertOnlySignatureChanged(original, File.ReadAllBytes(path));
                using (var disk = SignatureFile.Open(path)) {
                    Assert(disk.IsLocked, "Locked signature was not recognized.");
                    disk.SetLocked(false);
                }
                AssertSameHash(original, File.ReadAllBytes(path), "Signature round trip changed the image.");
            });

            Check("password-and-portability", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                PrototypeOperations.Protect(path, container, Password);
                Assert(!File.Exists(path) && File.Exists(container + ".json"), "Protect did not create the portable container pair.");
                var info = PrototypeOperations.GetInfo(container);
                Assert(info.IsLocked && !info.IsAttached && info.AttachedPath == null && info.DiskId == DiskId && info.Length == original.Length,
                    "Locked container information did not match the validated image.");
                var locked = File.ReadAllBytes(container);
                var metadata = File.ReadAllBytes(container + ".json");
                AssertOnlySignatureChanged(original, locked);
                ExpectRefusal(() => PrototypeOperations.Unlock(container, "wrong password"));
                AssertSameHash(locked, File.ReadAllBytes(container), "Wrong password changed image bytes.");
                AssertSameHash(metadata, File.ReadAllBytes(container + ".json"), "Wrong password changed credential metadata.");
                var moved = Path.Combine(directory, "relocated.vsux");
                File.Move(container, moved);
                File.Move(container + ".json", moved + ".json");
                PrototypeOperations.Unlock(moved, Password);
                AssertSameHash(original, File.ReadAllBytes(moved), "Portable unlock changed the original image.");
                // These incomplete structural fixtures cannot be opened by Windows.
                // A failed native state query must propagate rather than report detached.
                ExpectRefusal(() => PrototypeOperations.GetInfo(moved));
            });

            Check("missing-sidecar-recovery", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                PrototypeOperations.Protect(path, container, Password);
                File.Delete(container + ".json");
                var locked = File.ReadAllBytes(container);
                ExpectRefusal(() => PrototypeOperations.Unlock(container, Password));
                AssertSameHash(locked, File.ReadAllBytes(container), "Missing sidecar changed image bytes.");
                SignatureFile.Restore(container);
                AssertSameHash(original, File.ReadAllBytes(container), "Password-independent recovery failed without metadata.");
            });

            Check("tampered-sidecar-recovery", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                PrototypeOperations.Protect(path, container, Password);
                File.WriteAllText(container + ".json", "{\"Version\":999}");
                var locked = File.ReadAllBytes(container);
                ExpectRefusal(() => PrototypeOperations.Unlock(container, Password));
                AssertSameHash(locked, File.ReadAllBytes(container), "Tampered sidecar changed image bytes.");
                SignatureFile.Restore(container);
                AssertSameHash(original, File.ReadAllBytes(container), "Recovery depended on a valid sidecar.");
            });

            Check("wrong-disk-sidecar", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                PrototypeOperations.Protect(path, container, Password);
                var sidecar = JsonNode.Parse(File.ReadAllText(container + ".json"))!;
                sidecar["diskId"] = Guid.NewGuid().ToString();
                File.WriteAllText(container + ".json", sidecar.ToJsonString());
                var locked = File.ReadAllBytes(container);
                ExpectRefusal(() => PrototypeOperations.Unlock(container, Password));
                AssertSameHash(locked, File.ReadAllBytes(container), "A companion for another disk changed image bytes.");
                SignatureFile.Restore(container);
                AssertSameHash(original, File.ReadAllBytes(container), "Recovery depended on a matching disk identifier in the sidecar.");
            });

            Check("corrupt-recovery-snapshot", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                PrototypeOperations.Protect(path, container, Password);
                var sidecar = JsonNode.Parse(File.ReadAllText(container + ".json"))!;
                var snapshot = Convert.FromBase64String(sidecar["headerSnapshot"]!.GetValue<string>());
                snapshot[^1] ^= 1;
                sidecar["headerSnapshot"] = Convert.ToBase64String(snapshot);
                File.WriteAllText(container + ".json", sidecar.ToJsonString());
                var locked = File.ReadAllBytes(container);
                ExpectRefusal(() => PrototypeOperations.Unlock(container, Password));
                AssertSameHash(locked, File.ReadAllBytes(container), "An invalid recovery snapshot changed image bytes.");
                SignatureFile.Restore(container);
                AssertSameHash(original, File.ReadAllBytes(container), "Standalone recovery used the corrupt saved snapshot.");
            });

            Check("partial-signature-recovery", path => {
                var original = File.ReadAllBytes(path);
                for (var prefix = 1; prefix <= 8; prefix++) {
                    var damaged = (byte[])original.Clone();
                    for (var index = 0; index < prefix; index++) { damaged[index] = (byte)(0x90 + index); }
                    File.WriteAllBytes(path, damaged);
                    ExpectRefusal(() => { using var disk = SignatureFile.Open(path); });
                    SignatureFile.Restore(path);
                    AssertSameHash(original, File.ReadAllBytes(path), $"Recovery failed after a {prefix}-byte interrupted signature write.");
                }
            });

            Check("exclusive-open", path => {
                var original = File.ReadAllBytes(path);
                using (var busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                    ExpectRefusal(() => { using var disk = SignatureFile.Open(path); });
                    ExpectRefusal(() => SignatureFile.Restore(path));
                    ExpectRefusal(() => PrototypeOperations.Protect(path, Path.ChangeExtension(path, ".vsux"), Password));
                }
                AssertSameHash(original, File.ReadAllBytes(path), "An in-use refusal changed image bytes.");
            });

            Check("no-container-overwrite", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                byte[] existing = [0x12, 0x34, 0x56];
                File.WriteAllBytes(container, existing);
                ExpectRefusal(() => PrototypeOperations.Protect(path, container, Password));
                AssertSameHash(original, File.ReadAllBytes(path), "Overwrite refusal changed source image.");
                AssertSameHash(existing, File.ReadAllBytes(container), "Protect overwrote an existing container.");
            });

            Check("no-sidecar-overwrite", path => {
                var original = File.ReadAllBytes(path);
                var container = Path.ChangeExtension(path, ".vsux");
                byte[] existing = [0x12, 0x34, 0x56];
                File.WriteAllBytes(container + ".json", existing);
                ExpectRefusal(() => PrototypeOperations.Protect(path, container, Password));
                AssertSameHash(original, File.ReadAllBytes(path), "Sidecar overwrite refusal changed source image.");
                AssertSameHash(existing, File.ReadAllBytes(container + ".json"), "Protect overwrote an existing sidecar.");
            });

            Check("foreign-file", path => {
                File.WriteAllBytes(path, new byte[2 * 1048576]);
                AssertRefusedWithoutChange(path);
            });
            Check("truncated-file", path => {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) { file.SetLength(1024); }
                AssertRefusedWithoutChange(path);
            });
            Check("invalid-header-checksums", path => {
                var bytes = File.ReadAllBytes(path);
                bytes[65536 + 4] ^= 1;
                bytes[131072 + 4] ^= 1;
                File.WriteAllBytes(path, bytes);
                AssertRefusedWithoutChange(path);
            });
            Check("pending-log", path => {
                var bytes = File.ReadAllBytes(path);
                Guid.NewGuid().TryWriteBytes(bytes.AsSpan(131072 + 48, 16));
                Seal(bytes, 131072, HeaderSize);
                File.WriteAllBytes(path, bytes);
                AssertRefusedWithoutChange(path);
            });
            Check("differencing-disk", path => {
                var bytes = File.ReadAllBytes(path);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(MetadataOffset + 65536 + 4), 2);
                File.WriteAllBytes(path, bytes);
                AssertRefusedWithoutChange(path);
            });
            Check("region-outside-file", path => {
                var bytes = File.ReadAllBytes(path);
                foreach (var region in new[] { 196608, 262144 }) {
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(region + 16 + 16), (ulong)bytes.Length);
                    Seal(bytes, region, 65536);
                }
                File.WriteAllBytes(path, bytes);
                AssertRefusedWithoutChange(path);
            });
            Check("missing-disk-id", path => {
                var bytes = File.ReadAllBytes(path);
                bytes.AsSpan(MetadataOffset + 65536 + 8, 16).Clear();
                File.WriteAllBytes(path, bytes);
                AssertRefusedWithoutChange(path);
            });

            var recoveryExe = Environment.GetEnvironmentVariable("VSUX_RECOVERY_EXE");
            if (!string.IsNullOrWhiteSpace(recoveryExe)) {
                Assert(File.Exists(recoveryExe), "VSUX_RECOVERY_EXE does not name an existing standalone recovery executable.");
                Check("independent-recovery-executable", path => {
                    var original = File.ReadAllBytes(path);
                    var container = Path.ChangeExtension(path, ".vsux");
                    PrototypeOperations.Protect(path, container, Password);
                    File.Delete(container + ".json");
                    var result = RunTool(recoveryExe, ["--restore", container], directory);
                    Assert(result.ExitCode == 0, "Standalone recovery failed: " + result.Error + result.Output);
                    AssertSameHash(original, File.ReadAllBytes(container), "Standalone recovery executable changed bytes outside the signature.");
                    Assert(!File.Exists(container + ".json"), "Standalone recovery unexpectedly required or created a companion file.");
                });
            } else {
                Console.WriteLine("Set VSUX_RECOVERY_EXE to include the independent recovery executable check.");
            }

            if (native) {
                NativeDisk.RequireElevation();
                RunNative(directory);
                passed++;
                Console.WriteLine("PASS actual-Windows-VHDX-round-trip-and-busy-volume");
            } else {
                Console.WriteLine("Native attach/detach checks were not requested. Run --self-test-native as Administrator for real Windows VHDX checks.");
            }
            Console.WriteLine($"PASS {passed} checks.");
            foreach (var file in Directory.GetFiles(directory)) { File.Delete(file); }
            Directory.Delete(directory); // Only this run's flat directory; never recursive deletion.
            return 0;
        } catch {
            Console.Error.WriteLine($"FAIL; disposable artifacts retained at {directory}");
            throw;
        }
    }

    private static void RunNative(string directory) {
        var image = Path.Combine(directory, "actual-Windows.vhdx");
        var container = Path.ChangeExtension(image, ".vsux");
        Exception? failure = null;
        try {
            NativeDisk.CreateDisposable(image);
            var original = File.ReadAllBytes(image);
            const string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
            if (File.Exists(sevenZip)) {
                var normalReader = RunTool(sevenZip, ["l", "-tVHDX", "--", image]);
                Assert(normalReader.ExitCode == 0, "7-Zip could not read the normal disposable VHDX: " + normalReader.Error + normalReader.Output);
            }
            PrototypeOperations.Protect(image, container, Password);
            var locked = File.ReadAllBytes(container);
            AssertOnlySignatureChanged(original, locked);
            if (File.Exists(sevenZip)) {
                var lockedReader = RunTool(sevenZip, ["l", "-tVHDX", "--", container]);
                Assert(lockedReader.ExitCode != 0, "7-Zip unexpectedly opened the masked disposable VHDX.");
                AssertSameHash(locked, File.ReadAllBytes(container), "7-Zip changed the locked disposable image.");
                Console.WriteLine("PASS installed 7-Zip normal VHDX read and masked VSUX refusal");
            } else {
                Console.WriteLine("7-Zip generic reader check not run: C:\\Program Files\\7-Zip\\7z.exe is not installed.");
            }
            ExpectRefusal(() => NativeDisk.Mount(container, readOnly: true, noDriveLetter: true));
            AssertSameHash(locked, File.ReadAllBytes(container), "Windows refusal changed locked image bytes.");
            ExpectRefusal(() => PrototypeOperations.Attach(container, "wrong password", readOnly: true));
            AssertSameHash(locked, File.ReadAllBytes(container), "Wrong-password native attach changed image bytes.");
            PrototypeOperations.Attach(container, Password, readOnly: true);
            Assert(NativeDisk.IsAttached(container), "Password attach did not produce a native disk.");
            var attachedInfo = PrototypeOperations.GetInfo(container);
            Assert(!attachedInfo.IsLocked && attachedInfo.IsAttached && attachedInfo.AttachedPath != null, "Attached container information did not match Windows.");
            ExpectRefusal(() => { using var disk = SignatureFile.Open(container); });
            PrototypeOperations.Lock(container, Password);
            using (var disk = SignatureFile.Open(container)) { Assert(disk.IsLocked, "Safe detach did not re-lock the image."); }
            Assert(PrototypeOperations.GetInfo(container) is { IsLocked: true, IsAttached: false, AttachedPath: null }, "Detach did not update container information.");
            AssertOnlySignatureChanged(original, File.ReadAllBytes(container));
            SignatureFile.Restore(container);
            AssertSameHash(original, File.ReadAllBytes(container), "Native read-only attach/detach/recovery changed bytes outside the signature.");
        } catch (Exception error) {
            failure = error;
            throw;
        } finally {
            CleanupNativeImages(failure, image, container);
        }
        RunNativeBusyVolume(directory);
    }

    private static void RunNativeBusyVolume(string directory) {
        var image = Path.Combine(directory, "actual-payload.vhdx");
        var container = Path.ChangeExtension(image, ".vsux");
        var payload = RandomNumberGenerator.GetBytes(65536);
        Exception? failure = null;
        try {
            NativeDisk.CreateDisposable(image);
            var physicalPath = NativeDisk.Mount(image, noDriveLetter: true);
            var volumePath = FormatDisposableVolume(image, physicalPath);
            var payloadPath = volumePath + "prototype-payload.bin";
            using (var file = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(payload);
                file.Flush(flushToDisk: true);
            }
            NativeDisk.Detach(image);
            var beforeProtection = File.ReadAllBytes(image);
            PrototypeOperations.Protect(image, container, Password);
            AssertOnlySignatureChanged(beforeProtection, File.ReadAllBytes(container));

            PrototypeOperations.Attach(container, Password);
            using (var busy = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                ExpectRefusal(() => PrototypeOperations.Lock(container, Password));
                Assert(NativeDisk.IsAttached(container), "An in-use filesystem was detached.");
                using var signature = SignatureFile.Open(container, allowPendingLog: true, exclusive: false);
                Assert(!signature.IsLocked, "An in-use filesystem had its signature masked.");
                var actual = new byte[payload.Length];
                busy.ReadExactly(actual);
                AssertSameHash(payload, actual, "The busy-volume refusal changed payload contents.");
            }
            PrototypeOperations.Lock(container, Password);
            using (var signature = SignatureFile.Open(container)) { Assert(signature.IsLocked, "Closing the payload did not permit safe detach and lock."); }
            PrototypeOperations.Attach(container, Password, readOnly: true);
            AssertSameHash(payload, File.ReadAllBytes(payloadPath), "Payload contents changed across detach, lock and password attach.");
            PrototypeOperations.Lock(container, Password);
            Console.WriteLine("PASS Windows NTFS open-file refusal and payload integrity");
        } catch (Exception error) {
            failure = error;
            throw;
        } finally {
            CleanupNativeImages(failure, image, container);
        }
    }

    private static void CleanupNativeImages(Exception? testFailure, params string[] paths) {
        var failures = new List<Exception>();
        foreach (var path in paths) {
            try {
                if (!File.Exists(path)) { continue; }
                var signature = new byte[8];
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { file.ReadExactly(signature); }
                // Query actual Windows state even when Attach threw before returning.
                // Only these exact disposable image paths may be detached by cleanup.
                if (signature.AsSpan().SequenceEqual("vhdxfile"u8) && NativeDisk.GetAttachedPath(path) != null) {
                    NativeDisk.Detach(path);
                }
            } catch (Exception error) { failures.Add(new IOException($"Could not clean up disposable image '{path}'.", error)); }
        }
        if (failures.Count == 0) { return; }
        var cleanupFailure = new AggregateException("Disposable native image cleanup did not finish; retain the artifacts.", failures);
        if (testFailure == null) { throw cleanupFailure; }
        Console.Error.WriteLine(cleanupFailure); // Preserve the original failing assertion as the primary exception.
    }

    private static string FormatDisposableVolume(string image, string physicalPath) {
        const string prefix = @"\\.\PhysicalDrive";
        Assert(physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(physicalPath.AsSpan(prefix.Length), out _), "Unexpected disposable disk path.");
        var number = uint.Parse(physicalPath.AsSpan(prefix.Length));
        // Every destructive cmdlet re-checks this run's exact VHDX path and physical
        // device. It cannot select a physical user disk or an existing formatted disk.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $imagePath = '{{image.Replace("'", "''")}}'
            $physicalPath = '{{physicalPath.Replace("'", "''")}}'
            function Assert-DisposableDisk {
                $matches = @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Disk -Filter 'Number = {{number}}' -ErrorAction Stop)
                if ($matches.Count -ne 1) { throw 'The disposable disk could not be identified uniquely.' }
                $rawDisk = $matches[0]
                # Storage type data formats BusType; read the underlying CIM slots.
                $properties = $rawDisk.CimInstanceProperties
                $rawNumber = $properties['Number'].Value
                $rawLocation = $properties['Location'].Value
                $rawSize = $properties['Size'].Value
                if ($rawNumber -ne {{number}} -or ('\\.\PhysicalDrive' + $rawNumber) -ine $physicalPath -or
                    $rawLocation -ine $imagePath -or [int]$properties['BusType'].Value -ne 15 -or $rawSize -ne 67108864 -or
                    $properties['IsSystem'].Value -ne $false -or $properties['IsBoot'].Value -ne $false) {
                    throw 'Refusing to format a disk that is not the exact disposable 64MiB file-backed image.'
                }
                $disk = Get-Disk -Number {{number}} -ErrorAction Stop
                if ($disk.Number -ne $rawNumber -or $disk.Location -ine $imagePath -or $disk.Size -ne $rawSize) {
                    throw 'The typed storage disk identity changed.'
                }
                return $disk
            }
            $raw = Assert-DisposableDisk
            if ($raw.PartitionStyle -ne 'RAW') { throw 'Disposable image already contains partitions.' }
            Initialize-Disk -InputObject $raw -PartitionStyle GPT -Confirm:$false -ErrorAction Stop | Out-Null
            $partition = New-Partition -InputObject (Assert-DisposableDisk) -UseMaximumSize -AssignDriveLetter:$false -ErrorAction Stop
            Assert-DisposableDisk | Out-Null
            if ($partition.DiskNumber -ne {{number}}) { throw 'The new partition belongs to an unexpected disk.' }
            $volume = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'VSUX_DISPOSABLE' -Confirm:$false -ErrorAction Stop
            $volume.Path | ConvertTo-Json -Compress
            """;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = RunTool(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        if (result.ExitCode != 0) { throw new IOException("Disposable volume preparation failed: " + result.Error); }
        var path = JsonSerializer.Deserialize<string>(result.Output.Trim()) ?? "";
        Assert(path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.EndsWith(@"}\", StringComparison.Ordinal) &&
            Guid.TryParse(path.AsSpan(11, path.Length - 13), out _), "Windows returned an unexpected disposable volume path.");
        return path;
    }

    private static (int ExitCode, string Output, string Error) RunTool(string executable, string[] arguments, string? directory = null) {
        var start = new ProcessStartInfo {
            FileName = executable, WorkingDirectory = directory ?? Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + Path.GetFileName(executable));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) {
            process.Kill(entireProcessTree: true);
            throw new IOException(Path.GetFileName(executable) + " timed out.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static void AssertRefusedWithoutChange(string path) {
        var original = File.ReadAllBytes(path);
        ExpectRefusal(() => { using var disk = SignatureFile.Open(path); });
        ExpectRefusal(() => SignatureFile.Restore(path));
        AssertSameHash(original, File.ReadAllBytes(path), "Invalid image refusal changed image bytes.");
    }

    private static void ExpectRefusal(Action action) {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or
            CryptographicException or JsonException or ArgumentException or Win32Exception) { return; }
        throw new InvalidOperationException("An unsafe or unauthorized operation unexpectedly succeeded.");
    }

    private static void AssertOnlySignatureChanged(byte[] original, byte[] actual) {
        Assert(actual.Length == original.Length, "The image length changed.");
        Assert(actual.AsSpan(8).SequenceEqual(original.AsSpan(8)), "Bytes outside the eight-byte signature changed.");
        Assert(actual.AsSpan(0, 8).SequenceEqual("vsuxlock"u8), "Unexpected locked signature.");
    }

    private static void AssertSameHash(byte[] expected, byte[] actual, string message) =>
        Assert(SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual)), message);

    private static void Assert(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static byte[] CreateStructuralFixture() {
        var bytes = new byte[2 * 1048576];
        "vhdxfile"u8.CopyTo(bytes);
        foreach (var header in new[] { 65536, 131072 }) {
            "head"u8.CopyTo(bytes.AsSpan(header));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(header + 8), header == 65536 ? 1u : 2u);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header + 66), 1);
            Seal(bytes, header, HeaderSize);
        }
        foreach (var region in new[] { 196608, 262144 }) {
            "regi"u8.CopyTo(bytes.AsSpan(region));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 8), 1);
            MetadataRegionId.TryWriteBytes(bytes.AsSpan(region + 16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(region + 32), MetadataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 40), 1048576);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 44), 1);
            Seal(bytes, region, 65536);
        }
        "metadata"u8.CopyTo(bytes.AsSpan(MetadataOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(MetadataOffset + 10), 2);
        WriteItem(bytes, 0, FileParametersId, 65536, 8, 4);
        WriteItem(bytes, 1, VirtualDiskId, 65544, 16, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(MetadataOffset + 65536), 2 * 1048576);
        DiskId.TryWriteBytes(bytes.AsSpan(MetadataOffset + 65544, 16));
        return bytes;
    }

    private static void WriteItem(byte[] bytes, int index, Guid id, uint offset, uint length, uint flags) {
        var entry = bytes.AsSpan(MetadataOffset + 32 + 32 * index, 32);
        id.TryWriteBytes(entry[..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], flags);
    }

    private static void Seal(byte[] bytes, int offset, int length) {
        bytes.AsSpan(offset + 4, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), Crc32C(bytes.AsSpan(offset, length)));
    }

    private static uint Crc32C(ReadOnlySpan<byte> bytes) {
        uint crc = uint.MaxValue;
        foreach (var value in bytes) {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u); }
        }
        return ~crc;
    }
}
