# VSUX password-gated mounting

VSUX keeps the disk contents unencrypted and blocks ordinary VHDX mounting while locked.
It is an experimental convenience barrier: someone who restores the published VHDX signature,
including with the supplied recovery tool, can bypass the password. It does not provide
BitLocker-equivalent protection.

## Create a VSUX image

1. Click **New** and select **VSUX (soft protected drive)**, the size and dynamic or fixed type.
2. Click **Create** and choose a new `.vsux` filename on a local physical drive.
3. Approve elevation if requested, then enter and confirm the password.

Creation produces a blank, locked, detached image and its `.vsux.json` companion. It does not
automatically attach or initialize the disk. Click **Attach**, enter the password, then use
**Disk Manager** to initialize the new blank disk and create and format a partition before
storing files. When finished, use **Detach & lock**.

## Protect an existing VHDX

Start with a full backup and a detached standalone VHDX on a local physical drive. Open it
in VHD Studio, then open **Maintenance**, select **Soft protection** in the left sidebar and
click **Protect as VSUX…**. The command is also available from the main window's Maintenance
dropdown. Choose a new `.vsux` filename in the same folder, confirm the rename, approve
elevation if requested, and enter and confirm the password.

Protection accepts dynamic or fixed standalone VHDX images without a pending log. It renames
the image without copying its payload and creates its `.vsux.json` companion before masking
the signature. The main window then opens the renamed VSUX image. Existing target images and
companions are refused. Soft protection retains recovery diagnostics rather than making a
full backup.

## Attach and detach

Keep `disk.vsux` and `disk.vsux.json` in the same folder. Copy both when moving to another PC.
The lock lives in the image, so renaming a locked `.vsux` to `.vhdx` does not unlock it.

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
outside the attach/auto-mount service. Auto-mount, folder-mount automation and Maintenance are
unavailable for VSUX. Network paths, linked files, compressed/encrypted/sparse backing files,
differencing disks and images with pending logs are refused by the VSUX engine.

Closing VHD Studio does not detach or relock a disk. A crash after unlock or during an attached
session can leave the normal VHDX signature available. After any interrupted or failed action,
check the state and use **Detach & lock**. Never assume that an image is locked because it
has a `.vsux` filename.

## Format and write boundary

The image is the original VHDX with only bytes 0–7 replaced with ASCII `vsuxlock` when locked.
Unlock restores ASCII `vhdxfile`. The raw writer never overwrites other image bytes or changes
the file length. Windows performs its ordinary virtual-disk and filesystem I/O while attached.
For read-only use, the mounted payload is read-only, but unlocking and relocking still require
write access to the eight signature bytes.

The companion contains a random salt, a PBKDF2-HMAC-SHA256 password verifier (600,000
iterations), the VHDX disk identifier and a SHA256-checked snapshot of the original first MiB.
That snapshot is diagnostic information, not a full backup; no command writes it back over
current disk metadata. Preserve a full backup before adopting the experimental format.

Changing the VHDX identifier violates the normal VHDX writing rules. Eight-byte writes are not
guaranteed atomic during power loss, and no software check makes this design risk-free.

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

## Disposable-image verification

The original prototype and independent recovery checks remain available:

```powershell
dotnet build Research\VsuPrototype\VsuPrototype.csproj
dotnet build Research\VsuPrototype\Recovery\VsuRecover.csproj
dotnet Research\VsuPrototype\bin\Debug\net10.0-windows\VsuPrototype.dll --self-test
powershell -NoProfile -File Research\VsuPrototype\Run-NativeChecks.ps1
```

The native script requests elevation and creates uniquely named temporary VHDX files. Only a
disk verified as this run's newly created 64 MiB file-backed image may be formatted. It checks
Windows mounting/rejection, wrong passwords, safe detach with an open file, payload integrity,
byte preservation, companion failure and independent recovery. No existing disk is selected.
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
