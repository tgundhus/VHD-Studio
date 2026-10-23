# VSUX mount-lock prototype

An independent Windows prototype and disposable-image test harness. Its core now
lives in `Source/VhdStudio.Vsux` and is shared with VHD Studio's integrated support.
See [the integrated VSUX guide](../../docs/VSUX.md) for normal application usage.
This harness provides its own Windows UI, masked **Attach** prompt and recovery tool.

## Format and behavior

`disk.vsux` contains the original VHDX with bytes 0–7 set to ASCII `vsuxlock` when
locked. Unlock restores ASCII `vhdxfile`. No other image bytes are written by the
prototype's raw writer. Windows performs ordinary virtual-disk and filesystem I/O
while the disk is mounted; a read/write session can naturally change other bytes.

New protection creates a single `.vsux` file. A per-image random salt, PBKDF2-HMAC-SHA256
password verifier (600,000 iterations) and derivation parameters are stored in VHDX
user metadata through Windows; no JSON companion is created or required. Initial
protection changes container metadata and normal header/log bookkeeping beyond the
eight-byte raw signature boundary. Disk payload contents remain unencrypted.

Earlier images still require their `disk.vsux.json` companion for password operations.
It also contains the stable disk identifier and a SHA256-checked snapshot of the
original first 1 MiB. Keep both files together until explicitly upgrading through
VHD Studio's **Soft protection → Make self-contained…**. The old JSON is retained
as a backup. Snapshots and optional recovery-information exports are diagnostic
information: no command restores them over current metadata. They are not full backups.

The password gates this application. This is mount prevention, **not encryption**:
the payload stays readable to someone who restores the published VHDX signature or
uses tools that ignore it. Masking the file signature violates the normal VHDX format
writing rules. An eight-byte write is not guaranteed atomic during power loss, and
metadata updates can also be interrupted. Use only disposable images in this harness.

## Build and run

Requires Windows and the .NET 10 SDK / Desktop Runtime. There are no new NuGet packages.

```powershell
dotnet build Research\VsuPrototype\VsuPrototype.csproj
dotnet build Research\VsuPrototype\Recovery\VsuRecover.csproj
Research\VsuPrototype\bin\Debug\net10.0-windows\VsuPrototype.exe
```

The prototype executable requests Windows administrator approval on startup so
**Attach** and **Detach and lock** can work directly. The separate recovery tool
does not require elevation beyond the file's access permissions. CLI operations can
also be invoked using `dotnet VsuPrototype.dll`.
The program does not install a service, register file associations, or change VHD Studio.

1. Create a disposable VHDX. It must be detached, local, uncompressed, unencrypted,
   non-sparse, without hard links, junctions or a differencing parent.
2. Click **Protect disposable VHDX**, choose a new `.vsux` name in the same folder,
   and set a password. This renames the image without copying its payload.
3. Click **Attach**, enter the password, and optionally select read-only access.
4. Click **Detach and lock** and enter the password. Open files prevent locking;
   close them and retry. There is no force-detach option.

Closing the app does not detach or lock. An app crash while attached or after
unlock leaves the ordinary VHDX signature available. After any failed operation,
check the displayed state; never assume the image is locked. If protection fails,
retain any original `.vhdx`, renamed `.vsux` and existing legacy JSON for diagnosis
and recovery. Do not assume a failed action rolled back its metadata changes.

## Standalone recovery

```powershell
Research\VsuPrototype\Recovery\bin\Debug\net10.0-windows\VsuRecover.exe --restore "D:\Test\disk.vsux"
```

This executable has no dependency on the main prototype, password, companion file,
saved snapshot or installed VHD Studio. It validates the remaining VHDX structures,
requires exclusive access, restores **only eight bytes**, flushes and verifies them,
and leaves the filename unchanged. Rename `.vsux` to `.vhdx` to stop using the lock.
It also accepts partially damaged signature bytes. Pending logs, differencing
disks and invalid metadata are refused for manual diagnosis.
Embedded password metadata remains because recovery deliberately changes only the
signature. VHD Studio's password-based **Convert back to VHDX…** action also removes
its own metadata item before renaming the file.

## Verification

```powershell
dotnet Research\VsuPrototype\bin\Debug\net10.0-windows\VsuPrototype.dll --self-test
# Windows will request UAC consent if necessary:
powershell -NoProfile -File Research\VsuPrototype\Run-NativeChecks.ps1
```

The first suite constructs the earlier companion-based format with a local test-only
helper restricted to its freshly created synthetic fixtures. It tests byte preservation,
password failure, portability, malformed/missing companion files, partial-write recovery,
invalid images and exclusive-access/no-overwrite guards. It does not claim those fixtures
can be mounted or use Windows' native user-metadata API.

The native suite creates its own uniquely named temporary VHDX images and fails if
Windows cannot create or mount them. It uses the current public protection API,
verifies embedded credentials without a JSON companion, and checks masked-image
rejection, password attach, safe detach, recovery, payload preservation and busy-file
refusal. Byte preservation is compared against the image after initial metadata creation;
raw signature changes and read-only mounting must preserve the other bytes. Only a
disk identified as this run's freshly created, 64 MiB, file-backed image may be
partitioned or formatted. No existing disk is selected. Failed runs retain their
disposable artifacts for diagnosis; successful runs remove only their own files.

Sandboxed processes may be unable to access Windows' VHDX provider (error
`0xC03A0014`) or rename files. Run these checks from an ordinary Windows terminal;
the native checks additionally need Administrator rights. Native results are
recorded in `TestResults/native-checks.log`.

To produce two independent portable executables with embedded runtimes:

```powershell
dotnet publish Research\VsuPrototype\VsuPrototype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish\VsuPrototype
dotnet publish Research\VsuPrototype\Recovery\VsuRecover.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish\VsuPrototype\Recovery
```

This harness does not install or configure VHD Studio. Auto-mount, automatic locking
on exit, differencing disks, network paths and custom filesystem drivers remain excluded.

## Original prototype verification, 23 October 2026

These initial results describe the earlier companion-based format, before embedded
password metadata was added. They do not verify the later metadata-writing paths.

Both self-contained Windows x64 executables were published successfully. All 19
checks passed in the elevated native run, including:

- Exact preservation of image bytes after offset 7, file length, and full SHA256
  recovery after a read-only Windows attach/detach cycle.
- Wrong passwords, malformed/missing/mismatched companion files, and partial
  signature writes; the independent recovery executable succeeds without a sidecar.
- Windows and installed 7-Zip accepting the original VHDX and rejecting its masked form.
- Real NTFS payload preservation across write, safe detach, lock, password attach,
  and read-only use; a deliberately open payload file prevents detach and masking.
- Exclusive-access, no-overwrite, pending-log, differencing-disk, checksum and bounds guards.

The GUI was also rendered and inspected at 100%, 150% and 200% DPI. Disposable disks
retained by failed development runs were safely detached; their diagnostic files
remain in their uniquely named temporary directories. VHD Studio source, service,
installer and existing user disk images were not changed during that initial prototype
verification. Subsequent application integration and its verification are documented
in [the integrated VSUX guide](../../docs/VSUX.md).

This verifies the listed scenarios, not resilience to real power failures,
hardware faults, every VHDX producer or every third-party extraction tool.
