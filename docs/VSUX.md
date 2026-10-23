# VSUX password-gated mounting

VSUX keeps the disk contents unencrypted and blocks ordinary VHDX mounting while locked.
It is an experimental convenience barrier: someone who restores the published VHDX signature,
including with the supplied recovery tool, can bypass the password. It does not provide
BitLocker-equivalent protection.

## Create a VSUX image

1. Click **New** and select **VSUX (soft protected drive)**, the size and dynamic or fixed type.
2. Leave **Create one NTFS partition** checked for a ready-to-use data volume, or clear it for
   a blank disk and a custom partition layout. The checked option needs at least 64 MiB.
   Click **Create** and choose a new `.vsux` filename on a local physical drive.
3. Approve elevation if requested, then enter and confirm the password.

Creation produces one locked, detached image. The password salt, verifier and derivation
parameters are stored inside the VHDX container; no `.vsux.json` companion is created or needed.
With the default checkbox selected, creation initializes the new disk as GPT and formats one
NTFS data partition before applying its mount lock. Click **Attach** and enter the password to
use the drive. With the checkbox cleared, creation leaves the disk blank: after attaching, use
**Disk Manager** to initialize it and create and format a partition before storing files.
When finished, use **Detach & lock**. Creation never replaces an existing file, and Attach never
initializes or formats an existing image automatically.

If Attach succeeds but no drive appears in Explorer, open **Drive → Disk Manager…**
or **Disk Manager**. VHD Studio selects the attached VSUX disk. Verify its backing filename,
bring it online if needed, and initialize it only if its style is **Not initialized**. An
already initialized MBR or GPT disk with no partitions needs **Disk → Create partition…**;
choose a filesystem and drive letter there. For an existing formatted volume without a
letter, select the partition and use **Partition → Assign drive letter**. Initialization,
partition creation and formatting require explicit confirmation; Attach never performs them.
Volumes with a drive letter are available from **Drive → Open …**.

## Protect an existing VHDX

Start with a full backup and a detached standalone VHDX on a local physical drive. Open it
in VHD Studio, then open **Maintenance**, select **Soft protection** in the left sidebar and
click **Protect as VSUX…**. The command is also available from the main window's Maintenance
dropdown. Choose a new `.vsux` filename in the same folder, confirm the rename, approve
elevation if requested, and enter and confirm the password.

Protection accepts dynamic or fixed standalone VHDX images without a pending log. It renames
the image without copying its payload and stores its password record in VHDX user metadata
through the Windows virtual-disk API before masking the signature. The main window then opens
the renamed VSUX image. Existing destinations are refused. Protection does not make a full
backup; optional recovery-information export is diagnostic information only.

## Upgrade an earlier VSUX image

An image created by an earlier release still uses its adjacent `.vsux.json` companion. Keep both
files together, including when copying it to another PC, until you explicitly upgrade it.

With the disk detached, open it in VHD Studio, click **Soft protection** on the toolbar and choose
**Make self-contained…**. Enter the existing password. VHD Studio validates the companion and
the image, moves the password information into VHDX user metadata and leaves the image locked.
The original JSON is kept as a backup; it is no longer required for Attach after a successful
upgrade. Make a full backup before upgrading. Opening or attaching an earlier image does not
silently upgrade its format.

## Convert back to VHDX

Make a full backup, detach the image, then open **Soft protection → Convert back to VHDX…**.
Choose a new `.vhdx` filename in the same folder and enter the password. VHD Studio restores
the ordinary VHDX signature, removes its own embedded password record through the Windows
virtual-disk API and renames the file without copying the payload. Existing target files are
never replaced. The result is an ordinary detached, unprotected VHDX; the password no longer
gates mounting. Any existing legacy JSON or exported diagnostics remain untouched.

If the password is unavailable, use the standalone recovery tool described below. It restores
the signature without removing embedded metadata; after renaming, Windows can use the image
as an ordinary VHDX. This is why VSUX is a mounting deterrent rather than encryption.

## Optional recovery information

**Soft protection → Export recovery information…** saves JSON diagnostics to a new file. This
export contains no password verifier and is optional: normal use of a single-file VSUX and
standalone recovery need neither the export nor a saved header snapshot. It is not a copy of
your disk data or a substitute for a full backup. No recovery command writes a saved snapshot
over current VHDX metadata.

## Attach and detach

A single-file VSUX needs only `disk.vsux`, including on another PC. Earlier images still need
their JSON companion until upgraded as described above. The lock lives in the image, so
renaming a locked `.vsux` to `.vhdx` does not unlock it.

1. Open the `.vsux` file in VHD Studio.
2. Click **Attach**, approve Windows administrator elevation if requested, then enter the
   password in the masked prompt. Read-only mounting is available.
3. When finished, click **Detach & lock** and enter the password. The volumes must flush,
   lock and dismount before the image is detached and masked. If a file is open, close it and
   retry; there is no force-detach option for VSUX.

Explorer's VSUX context menu offers Open, Attach, Attach read-only and Detach and lock after
installation. The same interactive flows are available with:

```powershell
VhdStudio.exe /attach "D:\Disks\disk.vsux"
VhdStudio.exe /readonly /attach "D:\Disks\disk.vsux"
VhdStudio.exe /detach "D:\Disks\disk.vsux"
```

Passwords are never command-line arguments. VSUX operations use a dedicated elevated helper,
outside the attach/auto-mount service. Auto-mount, folder-mount automation and ordinary
Maintenance operations are unavailable for VSUX; use its dedicated **Soft protection** actions.
Network paths, linked files, compressed/encrypted/sparse backing files, differencing disks and
images with pending logs are refused by the VSUX engine.

Closing VHD Studio does not detach or relock a disk. A crash after unlock or during an attached
session can leave the normal VHDX signature available. After any interrupted or failed action,
check the state and use **Detach & lock**. Never assume that an image is locked because it
has a `.vsux` filename.

## Format and write boundary

The mount lock replaces bytes 0–7 with ASCII `vsuxlock` when locked. Unlock restores ASCII
`vhdxfile`. The raw signature writer never overwrites other image bytes or changes the file
length. This eight-byte boundary does **not** apply to every VSUX action: initial protection and
an explicit upgrade store a password record in VHDX user metadata, and conversion back removes
that record through Windows. Those API calls can also change ordinary VHDX header and log
bookkeeping. Windows performs its ordinary virtual-disk and filesystem I/O while attached.
For read-only use, the mounted payload is read-only, but unlocking and relocking still require
write access to the eight signature bytes.

The embedded password record contains a unique random salt, a PBKDF2-HMAC-SHA256 verifier
(600,000 iterations) and the derivation parameters. The salt is public, not an application secret;
storing it per image keeps the file portable. The disk identifier is read from VHDX structures.
The disk payload stays unencrypted.

An earlier companion additionally contains a SHA256-checked snapshot of the original first
MiB. That snapshot and optional recovery exports are diagnostic information, not a full backup;
no command writes them back over current disk metadata. Preserve a full backup before adopting
the experimental format or changing an existing image.

Masking the VHDX file signature violates the normal VHDX writing rules. Eight-byte writes are not
guaranteed atomic during power loss. Metadata updates can also be interrupted, and no software
check makes this design risk-free.

## Standalone recovery

Setup includes `Recovery\VsuRecover.exe`. It is a self-contained single-file Windows x64 tool
that can be copied to another PC and run without VHD Studio, a shared .NET runtime, a password
or the `.vsux.json` companion:

```powershell
& "$env:ProgramFiles\VHD Studio\Recovery\VsuRecover.exe" --restore "D:\Disks\disk.vsux"
```

Recovery requires exclusive access to a detached image. It validates the remaining VHDX
structures, restores only bytes 0–7, flushes and verifies the write, and leaves the name and all
other bytes unchanged. It accepts partially written signatures but refuses pending logs,
differencing disks and invalid metadata. It does not repair disk contents or restore a header
snapshot. After successful recovery, rename the detached `.vsux` to `.vhdx` to use it normally.
An empty, oversized or damaged VSUX password record does not block recovery when the generic
VHDX structures remain valid. Embedded VSUX metadata may remain; recovery deliberately changes only the eight signature
bytes. Use the password-based **Convert back to VHDX…** action for normal removal of that record.

## Disposable-image verification

The original prototype and independent recovery checks remain available:

```powershell
dotnet build Research\VsuPrototype\VsuPrototype.csproj
dotnet build Research\VsuPrototype\Recovery\VsuRecover.csproj
dotnet Research\VsuPrototype\bin\Debug\net10.0-windows\VsuPrototype.dll --self-test
powershell -NoProfile -File Research\VsuPrototype\Run-NativeChecks.ps1
```

The structural checks build the earlier companion-based format locally in disposable synthetic
fixtures. They retain password, malformed/missing-companion and independent eight-byte recovery
coverage without treating incomplete fixtures as native VHDX images. The native checks use the
current public protection API to create single-file VSUX images without JSON companions.

The native script requests elevation and creates uniquely named temporary VHDX files. Only a
disk verified as this run's newly created 64 MiB file-backed image may be formatted. It checks
Windows mounting/rejection, wrong passwords, safe detach with an open file, payload integrity,
byte preservation and independent recovery. Its native byte-preservation baseline is captured
after the initial Windows metadata update; subsequent raw signature changes and read-only
mounting must preserve all other bytes. No existing disk is selected.
Successful runs clean only their own files; failed runs retain diagnostics. The prototype
check suite alone does not verify VHD Studio's UI or installer integration.

### Initial integration verification, 23 October 2026

The complete elevated product suite passed: 80 tests succeeded, no failures, and
two opt-in diagnostic/benchmark tests were skipped. The new integration tests cover
ordinary service-route refusal, exact-byte public-core password round trips,
locked/unlocked toolbar states, and the real Attach button opening a masked
password dialog whose cancellation preserves the complete locked image.

All 19 additional VSUX checks passed, including Windows/7-Zip rejection while
locked, safe NTFS busy-file refusal, payload integrity and the independent published
recovery executable. The final check found no attached disposable test disks.
The application, service and self-contained recovery binaries were published to
`Publish\VhdStudio`.

### 5.1.0 packaging verification, 23 October 2026

Both the shared-runtime and standalone installers compiled successfully with the official
portable Inno Setup 7.1.0 compiler. The application, service, VSUX library, recovery tool and
both installers have valid timestamped Authenticode signatures from xGND (`CN=xGND, O=xGND`),
applied using `sign-file`. The installers' uninstallers were signed through `sign-file` too.
The self-contained recovery executable reports version 5.1.0 and its `--help` smoke check passed.
The uploaded installer hashes matched the local signed files.
