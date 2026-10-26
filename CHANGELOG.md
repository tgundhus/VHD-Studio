# Changelog

## 5.2.3

### Fixed
- Volume refreshes now close their inspection handles immediately, including on errors.
  Those handles could block VHD Studio's own safe **Detach & lock**. Other Windows volume
  blockers can still prevent safe detach and need investigation if the refusal persists.

## 5.2.2

### Improved
- After a successful password-based VSUX Attach, **Detach & lock** can reuse that user's
  unlock session without asking for the password again. The Windows-protected session is
  bound to that exact disk and its password information, retained if safe relock fails, and
  removed after confirmed lock or conversion back to VHDX. Other actions still require the password.

### Fixed
- VSUX operations show the password prompt followed by one progress window. The settings
  dialog and the unelevated waiting window no longer stack behind the elevated operation.
- New disk preparation does not assign a temporary drive letter before detach, preventing
  Explorer/AutoPlay from opening a drive that disappears when creation finishes.
- **Protect as VSUX…** and **Convert back to VHDX…** go straight to the password prompt and
  use the matching filename with the new extension in the same folder. If that destination
  exists, the action reports **File already exists** without replacing it.
- Normal VSUX screens describe password information stored in the disk and optional recovery
  files.

## 5.2.1

### New
- **New → Create one NTFS partition** is enabled by default for VHD, VHDX and VSUX. It prepares
  one data volume on the newly created disk. Clear the checkbox for a blank disk and a custom
  layout. Prepared VSUX images are safely detached and locked; Attach then makes the volume
  available as a drive. This option requires a disk of at least 64 MiB and administrator approval.

### Fixed
- Attached VSUX images now expose their volumes and **Drive** controls. **Open in Explorer**
  is available for volumes with a drive letter; setup and letter changes open **Disk Manager**
  with the attached VSUX disk selected.
- An attached image without a filesystem volume now shows setup guidance. Attaching a blank
  disk does not create a partition or format it; these remain explicit Disk Manager actions.

## 5.2.0

### New
- New and newly protected **VSUX** images are single files. Their per-disk password salt,
  verifier and derivation parameters are stored in VHDX user metadata through the Windows
  virtual-disk API; a separate recovery file is no longer created or needed for normal use.
- **Soft protection** is available from the toolbar for VSUX images. **Make self-contained…**
  upgrades an earlier recovery-file-based image after password verification; the original recovery
  file is kept. Until that explicit upgrade, earlier images still require their recovery file.
- **Convert back to VHDX…** verifies the password, removes the embedded VSUX password record
  and renames a detached image to a new `.vhdx` filename in the same folder without copying the
  payload. Existing files are refused. Recovery export writes optional diagnostic files;
  standalone recovery still requires neither a recovery file nor a password.

### Safety
- The raw signature writer still changes only bytes 0–7. Initial protection, conversion of the
  earlier format and conversion back now also update VHDX container metadata through Windows,
  including its normal header/log bookkeeping. Disk contents remain unencrypted. These actions
  require a clean, detached image and do not create a full backup.
- Password Attach, safe **Detach & lock**, busy-file refusal and independent eight-byte recovery
  remain available. Standard Maintenance and auto-mount stay disabled for protected images.

## 5.1.2

### Improved
- **New → VSUX (soft protected drive)** creates a blank, locked `.vsux` image and its
  `.vsux.json` recovery file, with password setup during creation. Click **Attach** and enter the
  password, then use **Disk Manager** to initialize and format the new blank disk.
- The **Maintenance** window now has a visible **Soft protection** page with **Protect as VSUX…**
  for an existing detached standalone VHDX. After protection, the main window opens the renamed
  VSUX image. Existing image and recovery files are never overwritten.
- Both entry points explain that VSUX leaves the data unencrypted and deters ordinary mounting;
  specialists and the independent recovery tool can bypass the password. **Detach & lock** and
  standalone eight-byte recovery remain available.

## 5.1.1

### Fixed
- Setup no longer starts the previous service executable and waits indefinitely for it. A hidden
  .NET startup error could block the upgrade before its service-stop fallback was reached. Setup
  now manages the service directly through Windows, waits for its process to exit, and reports a
  failure if it cannot stop it. Uninstall uses the same path and retires the old logged helper actions.
- Installing the shared-runtime package over a standalone installation could leave runtime files
  that caused the application and service to ignore the installed .NET runtime and show
  "You must install or update .NET". Shared-runtime executables now explicitly use the globally
  installed runtime. Standalone packages continue using their embedded runtime.
- The shared-runtime prerequisite check now requires a stable .NET 10 Desktop Runtime; .NET 11
  alone or a preview runtime no longer satisfies it.

## 5.1.0

### New
- **VSUX password prompt (experimental).** Protect a detached standalone VHDX with
  **Maintenance → Protect as VSUX…**, then enter its password when choosing **Attach**.
  The data stays unencrypted: only the eight signature bytes change, blocking ordinary mounting
  while locked, including on another PC. This convenience barrier can be bypassed by restoring
  the public signature. Keep the original `.vsux.json` recovery file beside the image.
- **Detach & lock** flushes, locks and dismounts VSUX volumes before detaching and restoring the
  mount lock. Open files stop the operation; there is no force-detach option. VSUX requests
  administrator approval and is excluded from auto-mount and Maintenance.
- Setup includes the independent, self-contained **VsuRecover.exe**. It validates a detached image
  and restores only the eight signature bytes, without a password or recovery file. An interrupted
  session can leave a disk unlocked, and signature writes still carry power-loss risk; keep a full backup.

### Release
- The application, service, VSUX library, recovery tool and both installers are Authenticode-signed
  using `sign-file`. The standard installer uses the shared .NET 10 Desktop Runtime; the standalone
  installer embeds it. Recovery embeds its runtime in both packages.

## 5.0.3

### Improved
- A disk gets its last drive letter back. Windows forgets a disk's letter as soon as another drive (for
  example a USB stick) takes it, so the disk came back on a different letter. VHD Studio now remembers
  the letter of each volume per disk and restores it on every attach, also auto-mount at startup. If
  another drive has that letter at the moment, the disk gets a free letter this time and its usual one
  again next time. Changing the letter in **Drive → Change drive letter** makes the new one the usual one.

### Fixed
- An attached disk could get a drive letter you already use for a mapped network drive or a `subst`
  drive. The service attaches disks for all users, so it couldn't see drives that exist only in your
  sign-in session, and that letter kept showing the other drive. VHD Studio now tells the service which
  letters you use (including remembered network drives that aren't connected yet), and the disk moves to
  a free letter for good. You're told once.
- **Drive → Change drive letter** no longer offers letters of remembered network drives.
- Changing to a letter that is already in use removed the disk's current letter and left it without one.
  The change is now refused up front, and the old letter is put back if Windows rejects the new one.

## 5.0.2

### Fixed
- A backup made in a compressed or encrypted folder inherited that setting, and Windows refuses such
  files as virtual disks ("must be uncompressed and unencrypted and must not be sparse"). Backups are
  now always stored plainly. After the checksum matches, Windows must also open the backup as a virtual
  disk, or the backup counts as failed and the original is not changed.
- Files that Windows refuses for this reason (for example earlier backups) can be fixed with
  **Repair → Make usable as a virtual disk**. When opening such a file fails, the main window offers
  the same fix. It works like `compact /u`; the content does not change.
- The error message now explains the cause and the fix instead of showing the raw Windows text.

## 5.0.1

### Fixed
- Installing over, or uninstalling, 5.0.0 could fail with "DeleteFile failed; code 5. Access is denied"
  (for example on `clrjit.dll`). The 5.0.0 service process did not exit after being stopped, because its
  request loop could wait forever and .NET 10 no longer supports the old `Thread.Abort` fallback.
  The loop now ends on stop or after 10 seconds without data (which also stops a silent client from
  hanging the service). The service thread no longer keeps the process alive, and setup and uninstall
  make sure the service process has exited before files are replaced.
- Restarting the service (for example during an upgrade) re-ran auto-mount. That re-attached disks
  you had detached on purpose and logged false failures for disks still attached. Auto-mount now runs
  once per boot and skips disks that are already attached. Attached disks are never touched by
  install, upgrade or uninstall.
- Maintenance and Disk Manager could hide in the toolbar's overflow menu at the default window width.
  The window is now always wide enough for every toolbar button.

### Improved
- Backups are about 3.4× faster (3 GB: 7.2 s → 2.1 s on NVMe). Reading, checksumming and writing are
  pipelined, large files are written unbuffered like Explorer does (so a big backup no longer flushes
  everything else out of memory), and data is flushed to disk once.
- Backup verification now re-reads the copy from disk, bypassing the cache, so it checks what was
  really written. It uses a 128-bit XXH128 checksum instead of SHA-256, which was the bottleneck at
  about 2 GB/s.

## 5.0.0: VHD Studio

First release under the VHD Studio name, by Tobias Gundhus (xGND Software). It continues
VHD Attach 1.0–4.22, created and maintained 2009–2020 by Josip Medved
([@medo64](https://github.com/medo64/VhdAttach)).

### New
- **Maintenance window**: compact (file-system aware or zeroed blocks only, with a WSL/Docker
  helper), resize and shrink, convert (VHD↔VHDX, dynamic↔fixed, 512/4K sectors), create
  differencing disks, merge into parent, replay the VHDX log, fix the parent path, reset the
  identifier, and detailed information. No Hyper-V module needed.
- **Disk Manager** (DiskPart GUI): initialize, create, format, extend, shrink and delete partitions;
  manage drive letters and folder mounts; online/offline, read-only, active flag, retrim,
  scan/spot-fix, and clean. Every change first shows the equivalent PowerShell command. System
  and boot disks are always protected.
- Auto-mount and attach can **mount into an empty folder** instead of a drive letter.
- An Explorer context-menu entry **Maintenance…** and a Start-menu shortcut for Disk Manager.
- Command line: `/maintain "file" [/task=...]` and `/diskmanager [/disk=N]`.

### Fixed
- VHDX files that weren't closed cleanly failed with "Access denied". The pending log is now
  replayed for read/write attach (upstream #11). Read-only attach never writes and explains the problem.
- **New disk** permanently deleted an existing file when you picked its name. Existing files are now
  never replaced.
- The service's "initialize disk" step could overwrite the partition table of an existing disk. It now
  only initializes verifiably blank disks.
- Detaching cut off open files. Volumes are now flushed and locked first, and the user can retry
  instead of losing unsaved data.
- New-disk partition entries were written with the wrong layout (one-byte BOOLEAN fields marshalled as
  four bytes), and the attach parameter structure was too small.
- Cancelling a fixed-size VHDX creation left a partial file. The old code also passed a movable managed
  OVERLAPPED to the kernel; creation now uses the hardened engine.
- Uninstall no longer deletes the auto-mount list.
- `.avhd`/`.avhdx` differencing disks, and files with the wrong extension, could not be opened.
- Auto-mount entries with more than one option (`/readonly,nodriveletter/`) applied none of them.
- Files on mapped network drives could not be attached by the service.
- Uninstall left the default context-menu verb behind, which broke other tools (upstream #5).
- Removed a code path that started a `VhdAttachExecutor.exe` that no longer exists.

### Data safety
- Verified backups (128-bit checksum, re-read from disk) before every in-place change, enabled by default.
- Operations refuse attached or in-use disks, disks changed since selection, and protected disks
  (system, boot, cluster, page file, partitions holding attached images). Format never forces a dismount.
- Resize and merge cannot be interrupted midway. A cancelled or failed convert removes only the file
  it created.
- Fixing a parent path verifies the parent and restores the previous path on a mismatch.
- Differencing disks mark their parent read-only. A merged child goes to the Recycle Bin.
- Audit log of every change in `%ProgramData%\xGND Software\VHD Studio\Logs`.
- End-to-end data-safety test suite on real virtual disks (`Setup\Test-DataSafety.ps1`, runs in CI).

### Security
- The service, which runs as Local System, now checks that the calling user can access a file
  before attaching, detaching or auto-mounting it, or before changing a drive letter.
- Disks are opened under the caller's own token, so access is checked by Windows against the exact
  file being opened (no path races). Paths through junctions or links, and hard-linked files, are refused.
- The command pipe denies network access, rejects remote clients, cannot be squatted, and is verified
  by the client. Identification-only clients are rejected.
- "Detach anyway" and mount folders require an administrator. The audit log folder is created with a
  restricted ACL.

### Changed
- Runtime moved from .NET Framework 4.0 to **.NET 10**. Minimum OS is Windows 10 1809 (x64).
- New name, icon and look; published by xGND Software. Settings live under `Software\xGND Software\VHD Studio`.
  Update checks and bug reports go to this project's GitHub page.
- Setup removes VHD Attach 4.x and keeps its auto-mount list.
- The build uses `dotnet` and GitHub Actions instead of Visual Studio 2019 batch scripts.
